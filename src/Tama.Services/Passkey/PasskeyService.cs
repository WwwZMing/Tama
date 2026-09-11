using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tama.Core.Contracts;
using Tama.Core.Interfaces;
using Tama.Core.Models;
using Tama.Data.Database;
using Tama.Services.Bitwarden;
using Tama.Services.Crypto;
using Microsoft.EntityFrameworkCore;
using Serilog;

using Tama.Core.Exceptions;

namespace Tama.Services.Passkey;

/// <summary>
/// 通行密钥（FIDO2）管理：本机软凭据（ECDSA P-256 或 RSA-2048 私钥，用主密码派生密钥 AES-GCM
/// 加密落库）+ Bitwarden 凭据的**导入 / 启用**。
/// 平台验证：Windows Hello（UserConsentVerifier）；Android 走原生反射实现；浏览器宿主降级为不可用。
///
/// **凭据 id 的口径全链路只有一个，改之前先读这里**：
/// <c>PasskeyCredentialRecord.CredentialId</c> = **rawId 字节的 base64url**。
///   • Tama 自己生成（网站发起的注册）→ rawId = SHA256(公钥)，见 <see cref="CreateOrGetCredentialAsync"/>；
///   • 从 Bitwarden 导入 → rawId = 那边的 credentialId 字符串解出来的字节
///     （标准 UUID 字符串 = 16 字节；新格式 "b64." 前缀 = base64url），见 <see cref="DecodeCredentialIdBytes"/>。
/// 为什么必须统一：扩展把响应交给页面时是 `rawId: b64ToBuf(cred.rawId)`，而页面的
/// `allowCredentials[].id` 也是同一串 base64url（extensions/tama-webauthn/content-main.js）——
/// 两边编码不一致就会出现"库里有、却永远匹配不上、被放行给原生认证器"的静默失败。
///
/// 早先的"自闭环"入口（CreateRequest/Authenticate/Save：自己造挑战、自己签名、没有 RP 参与）
/// 已于 2026-09-13 整体删除：它产出的凭据任何真实网站都不认识，只会误导用户。
/// </summary>
public class PasskeyService : IPasskeyApi
{
    private readonly AuthDbContext _db;
    private readonly TamaDbContext _dbVault;
    private readonly DatabaseKeyService _dbKeyService;
    private readonly IPasskeyPlatformService _platform;
    private static readonly ILogger Log = Serilog.Log.ForContext<PasskeyService>();

    /// <summary>导入结果里最多带回几条逐条原因（够定位问题即可，别把整个文件摊到界面上）。</summary>
    private const int MaxWarnings = 5;

    public PasskeyService(AuthDbContext db, TamaDbContext dbVault, DatabaseKeyService dbKeyService, IPasskeyPlatformService platform)
    {
        _db = db;
        _dbVault = dbVault;
        _dbKeyService = dbKeyService;
        _platform = platform;
    }

    /// <summary>设备是否支持 Passkey：Windows 查 Windows Hello，Android 反射检测 Credential Manager</summary>
    public bool Available() => _platform.IsAvailable();

    public async Task Delete(string credentialId)
    {
        if (string.IsNullOrEmpty(credentialId))
            throw new TamaException("credentialId required");

        var record = await _dbVault.PasskeyCredentials
            .FirstOrDefaultAsync(p => p.CredentialId == credentialId);
        // 删不存在的凭据不许静默成功（与 CipherService 的 Update/Delete 同口径）：
        // 否则界面上"删掉了但还在"这类问题会没有任何线索。
        if (record == null)
            throw new TamaException("找不到这枚通行密钥（可能已经被删掉了）");

        _dbVault.PasskeyCredentials.Remove(record);
        await _dbVault.SaveChangesAsync();
        Log.Information("Deleted passkey {CredentialId} ({RpId})", record.CredentialId, record.RpId);
    }

    /// <summary>
    /// 合并列表：本机可用凭据 + Bitwarden 同步缓存里**尚未启用**的那些。
    /// 两边都归一化成同一套 credentialId（rawId 的 base64url），所以启用过的那枚不会在
    /// 缓存那一半里再出现一次（否则同一枚凭据会有两行，一行能删、一行能启用，很乱）。
    /// </summary>
    public async Task<List<PasskeyEntryDto>> List(string? accountId = null)
    {
        var local = await _dbVault.PasskeyCredentials.AsNoTracking()
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new PasskeyEntryDto(p.CredentialId, p.RpId, p.RpName, p.UserName, p.UserHandle, p.Counter, "local"))
            .ToListAsync();

        var synced = await LoadBitwardenPasskeys(accountId);
        var seen = local.Select(x => x.CredentialId).ToHashSet(StringComparer.Ordinal);
        return local.Concat(synced.Where(s => seen.Add(s.CredentialId))).ToList();
    }

    // ───────────────────────────── 导入 / 启用 ─────────────────────────────

    /// <summary>
    /// 导入 Bitwarden 的**未加密 JSON 导出**。
    /// 文件里的 `keyValue` 就是私钥本身（base64url 的 PKCS#8），所以导入后即可直接用于登录断言。
    /// </summary>
    public async Task<PasskeyImportResult> ImportFromBitwardenJson(byte[] file)
    {
        if (file == null || file.Length == 0)
            throw new TamaException("文件是空的");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(file);
        }
        catch (JsonException ex)
        {
            throw new TamaException("这不是有效的 JSON 文件：" + ex.Message, ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new TamaException("JSON 顶层不是对象，不像是 Bitwarden 的导出格式");

            if (root.TryGetProperty("encrypted", out var encrypted) && encrypted.ValueKind == JsonValueKind.True)
                throw new TamaException(
                    "这是「加密导出」——私钥在文件里是密文，Tama 解不开。请在 Bitwarden 里重新导出：" +
                    "工具 → 导出 vault → 文件格式选 .json，且**不要**勾选「使用密码保护 / 加密导出」。");

            if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new TamaException("这个 JSON 里没有 items 数组，不像是 Bitwarden 的导出文件");

            var pending = new List<PendingPasskey>();
            var warnings = new List<string>();
            var failed = 0;

            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("login", out var login) || login.ValueKind != JsonValueKind.Object) continue;
                if (!login.TryGetProperty("fido2Credentials", out var creds) || creds.ValueKind != JsonValueKind.Array) continue;

                var itemName = GetString(item, "name");
                foreach (var c in creds.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Object) continue;
                    try
                    {
                        pending.Add(BuildPending(
                            credentialId: GetString(c, "credentialId"),
                            keyValue: GetString(c, "keyValue"),
                            rpId: GetString(c, "rpId"),
                            rpName: GetString(c, "rpName") ?? itemName,
                            userName: GetString(c, "userName"),
                            userHandle: GetString(c, "userHandle"),
                            counter: GetCounter(c),
                            createdAt: GetCreatedAt(c)));
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        AddWarning(warnings, $"{itemName ?? "?"}: {ex.Message}");
                    }
                }
            }

            Log.Information("Bitwarden JSON import: {Count} passkey(s) found, {Failed} unusable", pending.Count, failed);
            return await StoreAsync(pending, warnings, failed);
        }
    }

    /// <summary>
    /// 把同步缓存里已经解密的 Bitwarden 通行密钥转成本机可用凭据。
    /// 缓存里的 `keyValue` 由 <c>BitwardenApiClient</c> 在同步时就解开了（见 ApplyDecryptedSections），
    /// 所以这一步**不需要联网、也不需要账号密码**，纯本地搬运。
    /// </summary>
    public async Task<PasskeyImportResult> AdoptSyncedPasskeys(string? credentialId = null)
    {
        var account = await _db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Type == "bitwarden");
        if (account == null)
            throw new TamaException("还没有关联 Bitwarden 账号，没有可启用的云端通行密钥");

        var cache = await _db.SyncCaches.AsNoTracking().FirstOrDefaultAsync(c => c.AccountId == account.Id);
        if (cache == null)
            throw new TamaException("本地还没有同步缓存，请先在账号页点一次「立即同步」");

        var ciphers = JsonSerializer.Deserialize<List<BitwardenCipherResponse>>(cache.CiphersJson) ?? new();
        var pending = new List<PendingPasskey>();
        var warnings = new List<string>();
        var failed = 0;

        foreach (var cipher in ciphers)
        {
            if (cipher.Fido2Credentials is not { Count: > 0 }) continue;
            foreach (var f in cipher.Fido2Credentials)
            {
                string? normalizedId = null;
                try
                {
                    normalizedId = f.CredentialId == null ? null : ToCredentialId(f.CredentialId);
                }
                catch (Exception ex)
                {
                    failed++;
                    AddWarning(warnings, $"{f.RpId ?? cipher.Name ?? "?"}: {ex.Message}");
                    continue;
                }
                if (normalizedId == null) continue;
                if (credentialId != null && !string.Equals(normalizedId, credentialId, StringComparison.Ordinal)) continue;

                try
                {
                    pending.Add(BuildPending(
                        credentialId: f.CredentialId,
                        keyValue: f.KeyValue,
                        rpId: f.RpId,
                        rpName: f.RpName ?? cipher.Name,
                        userName: f.UserName,
                        userHandle: f.UserHandle,
                        counter: f.Counter,
                        createdAt: f.CreationDate));
                }
                catch (Exception ex)
                {
                    failed++;
                    AddWarning(warnings, $"{f.RpId ?? cipher.Name ?? "?"}: {ex.Message}");
                }
            }
        }

        return await StoreAsync(pending, warnings, failed);
    }

    /// <summary>
    /// 落库：逐条**先把密钥材料准备齐全再 Add**（密钥解不开时数据库里什么都不会留下），
    /// 已存在的按跳过算（幂等），最后一次性 SaveChanges。
    /// </summary>
    private async Task<PasskeyImportResult> StoreAsync(List<PendingPasskey> pending, List<string> warnings, int failed)
    {
        if (pending.Count == 0)
            return new PasskeyImportResult(0, 0, failed, warnings);

        var existing = (await _dbVault.PasskeyCredentials.AsNoTracking()
                .Select(p => p.CredentialId)
                .ToListAsync())
            .ToHashSet(StringComparer.Ordinal);

        var imported = 0;
        var skipped = 0;

        foreach (var p in pending)
        {
            // 本机已有（含同一个文件里的重复项）→ 幂等跳过，绝不覆盖现有凭据
            if (!existing.Add(p.CredentialId)) { skipped++; continue; }

            try
            {
                var spki = ExportSubjectPublicKeyInfo(p.PrivateKeyPkcs8);
                _dbVault.PasskeyCredentials.Add(new PasskeyCredentialRecord
                {
                    CredentialId = p.CredentialId,
                    RpId = p.RpId,
                    RpName = p.RpName,
                    UserName = p.UserName,
                    UserHandle = p.UserHandle,
                    PublicKey = Convert.ToBase64String(spki),
                    EncryptedPrivateKey = EncryptKey(p.PrivateKeyPkcs8, _dbKeyService.GetKey()),
                    Counter = p.Counter,
                    CreatedAt = p.CreatedAt,
                });
                imported++;
            }
            catch (Exception ex)
            {
                failed++;
                AddWarning(warnings, $"{p.RpId}: {ex.Message}");
            }
        }

        if (imported > 0)
            await _dbVault.SaveChangesAsync();

        Log.Information("Passkey import: {Imported} imported, {Skipped} already present, {Failed} failed",
            imported, skipped, failed);
        return new PasskeyImportResult(imported, skipped, failed, warnings);
    }

    /// <summary>待落库的一枚凭据。两个来源（JSON 导出 / 同步缓存）都归到这里。</summary>
    private sealed record PendingPasskey(
        string CredentialId,
        string RpId,
        string? RpName,
        string? UserName,
        string? UserHandle,
        int Counter,
        DateTime CreatedAt,
        byte[] PrivateKeyPkcs8);

    /// <summary>
    /// 校验并归拢一枚凭据（两个来源共用）。任何一项不合格都抛 <see cref="TamaException"/>，
    /// 由调用方计成"失败一条"，不影响其余凭据。
    /// </summary>
    private static PendingPasskey BuildPending(
        string? credentialId, string? keyValue, string? rpId, string? rpName,
        string? userName, string? userHandle, int counter, DateTime createdAt)
    {
        if (string.IsNullOrWhiteSpace(credentialId))
            throw new TamaException("缺少 credentialId");
        if (string.IsNullOrWhiteSpace(keyValue))
            throw new TamaException("缺少 keyValue（私钥）");
        if (string.IsNullOrWhiteSpace(rpId))
            throw new TamaException("缺少 rpId");
        // 同步缓存若是旧版本写的，keyValue 还是 EncString 密文——说清楚该怎么办，别让人猜
        if (keyValue.StartsWith("2.", StringComparison.Ordinal))
            throw new TamaException("私钥还是密文（缓存来自旧版本），请重新同步一次 Bitwarden 或改用未加密导出");

        byte[] pkcs8;
        try
        {
            pkcs8 = DecodeBase64Url(keyValue.TrimEnd('='));
        }
        catch (FormatException ex)
        {
            throw new TamaException("keyValue 不是合法的 base64url 私钥", ex);
        }

        if (!string.IsNullOrWhiteSpace(userHandle))
        {
            try
            {
                DecodeBase64Url(userHandle.TrimEnd('='));
            }
            catch (FormatException ex)
            {
                // userHandle 是断言里要原样回给 RP 的值，解不开说明这枚凭据没法忠实断言
                throw new TamaException("userHandle 不是合法的 base64url", ex);
            }
        }

        return new PendingPasskey(
            ToCredentialId(credentialId),
            rpId,
            string.IsNullOrWhiteSpace(rpName) ? rpId : rpName,
            string.IsNullOrWhiteSpace(userName) ? null : userName,
            string.IsNullOrWhiteSpace(userHandle) ? null : userHandle,
            Math.Max(0, counter),
            createdAt == default ? DateTime.UtcNow : createdAt,
            pkcs8);
    }

    /// <summary>
    /// Bitwarden 的 credentialId（字符串）→ Tama 的 credentialId（rawId 字节的 base64url）。
    /// Bitwarden 用标准 UUID 字符串表示；新版可能给 "b64." 前缀的 base64url；
    /// 其它工具可能直接给 base64url —— 三种都认。
    /// </summary>
    internal static string ToCredentialId(string bitwardenCredentialId) =>
        Base64Url(DecodeCredentialIdBytes(bitwardenCredentialId));

    internal static byte[] DecodeCredentialIdBytes(string credentialId)
    {
        if (credentialId.StartsWith("b64.", StringComparison.Ordinal))
            return DecodeCredentialIdFallback(credentialId[4..]);

        var hex = credentialId.Replace("-", "");
        if (hex.Length == 32)
        {
            try
            {
                return Convert.FromHexString(hex);
            }
            catch (FormatException)
            {
                // 不是十六进制 → 当 base64url 处理
            }
        }
        return DecodeCredentialIdFallback(credentialId);
    }

    private static byte[] DecodeCredentialIdFallback(string value)
    {
        try
        {
            var bytes = DecodeBase64Url(value.TrimEnd('='));
            if (bytes.Length == 0) throw new FormatException("empty");
            return bytes;
        }
        catch (FormatException ex)
        {
            throw new TamaException($"无法解析 credentialId：{value}", ex);
        }
    }

    /// <summary>从 PKCS#8 私钥导出 SPKI 公钥（EC P-256 或 RSA；其它曲线一律拒绝——签名算法对不上）。</summary>
    internal static byte[] ExportSubjectPublicKeyInfo(byte[] pkcs8)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportPkcs8PrivateKey(pkcs8, out _);
            if (ecdsa.KeySize != 256)
                throw new TamaException($"只支持 P-256 的 EC 私钥（这是 {ecdsa.KeySize} 位）");
            return ecdsa.ExportSubjectPublicKeyInfo();
        }
        catch (CryptographicException)
        {
            // 不是 EC → 试试 RSA
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(pkcs8, out _);
            return rsa.ExportSubjectPublicKeyInfo();
        }
        catch (CryptographicException ex)
        {
            throw new TamaException("私钥不是 ECDSA/RSA 的 PKCS#8，无法导入", ex);
        }
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s
            ? s
            : null;

    /// <summary>
    /// counter：Bitwarden 的**导出文件**里是字符串（实测 "0"），API 响应里是数字 —— 两种都收。
    /// </summary>
    private static int GetCounter(JsonElement obj)
    {
        if (!obj.TryGetProperty("counter", out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var n) ? n : 0,
            JsonValueKind.String => int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0,
            _ => 0,
        };
    }

    private static DateTime GetCreatedAt(JsonElement obj)
    {
        if (!obj.TryGetProperty("creationDate", out var v) || v.ValueKind != JsonValueKind.String) return DateTime.UtcNow;
        return DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)
            ? dt
            : DateTime.UtcNow;
    }

    private static void AddWarning(List<string> warnings, string message)
    {
        if (warnings.Count < MaxWarnings) warnings.Add(message);
    }

    // ───────────────────────────── 同步缓存里的通行密钥 ─────────────────────────────

    /// <summary>Bitwarden 同步缓存里带通行密钥的条目（只读展示用；启用请走 AdoptSyncedPasskeys）。</summary>
    private async Task<List<PasskeyEntryDto>> LoadBitwardenPasskeys(string? accountId)
    {
        var account = !string.IsNullOrEmpty(accountId)
            ? await _db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId && a.Type == "bitwarden")
            : await _db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Type == "bitwarden");
        if (account == null) return new List<PasskeyEntryDto>();

        var cache = await _db.SyncCaches.AsNoTracking().FirstOrDefaultAsync(c => c.AccountId == account.Id);
        if (cache == null) return new List<PasskeyEntryDto>();

        var ciphers = JsonSerializer.Deserialize<List<BitwardenCipherResponse>>(cache.CiphersJson) ?? new();
        var result = new List<PasskeyEntryDto>();
        foreach (var c in ciphers)
        {
            if (c.Fido2Credentials is not { Count: > 0 }) continue;
            foreach (var f in c.Fido2Credentials)
            {
                if (string.IsNullOrEmpty(f.CredentialId))
                {
                    Log.Warning("Skipped synced passkey without credentialId (rpId={RpId})", f.RpId);
                    continue;
                }

                string id;
                try
                {
                    id = ToCredentialId(f.CredentialId);
                }
                catch (TamaException ex)
                {
                    // 解析不了的条目不值得让整个列表炸掉；但也不能给它一个空 id ——
                    // 空 id 在页面的 @key 上会互相撞（同一次渲染里两个 "" 直接抛异常）
                    Log.Warning(ex, "Skipped synced passkey with unusable credentialId {Id} (rpId={RpId})",
                        f.CredentialId, f.RpId);
                    continue;
                }
                result.Add(new PasskeyEntryDto(
                    id,
                    f.RpId ?? string.Empty,
                    f.RpName ?? c.Name,
                    f.UserName,
                    f.UserHandle,
                    f.Counter,
                    "bitwarden"));
            }
        }
        return result;
    }

    // ───────────────────────────── WebAuthn 复用（internal） ─────────────────────────────

    internal ECDsa LoadPrivateKey(PasskeyCredentialRecord record)
    {
        var keyBytes = DecryptKey(record.EncryptedPrivateKey, _dbKeyService.GetKey());
        var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(keyBytes, out _);
        return ecdsa;
    }

    /// <summary>加载 RSA 私钥（RSA-2048/RS256 凭据；WebAuthn 真实站点用）。</summary>
    internal RSA LoadRsaPrivateKey(PasskeyCredentialRecord record)
    {
        var keyBytes = DecryptKey(record.EncryptedPrivateKey, _dbKeyService.GetKey());
        var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(keyBytes, out _);
        return rsa;
    }

    /// <summary>从存储的 SPKI 检测密钥类型（RSA vs EC）。模型无算法列，按 ASN.1 AlgorithmIdentifier OID 判定（见 KeyEncoding）。</summary>
    internal static bool IsRsaKey(PasskeyCredentialRecord record)
    {
        var spki = Convert.FromBase64String(record.PublicKey);
        return KeyEncoding.IsRsa(spki);
    }

    /// <summary>
    /// 按 rpId+userName 查找或创建软凭据（生成 ECDSA P-256 或 RSA-2048 密钥对并加密落库）。
    /// 供 WebAuthn 注册流程复用。
    /// </summary>
    /// <param name="keyType">"ec" = ECDSA P-256（ES256/-7，Tama 自闭环默认）；"rsa" = RSA-2048（RS256/-257，
    /// 微软 MSA 等真实站点的通行密钥用 RSA——原生 Windows Hello 断言签名就是 256 字节 RSA-2048，ES256 会被拒）。</param>
    internal async Task<PasskeyCredentialRecord> CreateOrGetCredentialAsync(string rpId, string userName, string? userHandle = null, string keyType = "ec")
    {
        var existing = await _dbVault.PasskeyCredentials
            .FirstOrDefaultAsync(p => p.RpId == rpId && p.UserName == userName);

        if (existing != null)
        {
            // RP 的 user.id 与已存 userHandle 不一致时更新（首次注册若忽略 user.id 会存成邮箱，
            // 导致登录断言 userHandle 与服务器存储不符被拒）
            if (!string.IsNullOrEmpty(userHandle) && existing.UserHandle != userHandle)
            {
                existing.UserHandle = userHandle;
                await _dbVault.SaveChangesAsync();
                Log.Information("Updated userHandle for {RpId} user {User}", rpId, userName);
            }
            return existing;
        }

        byte[] publicKey, privateKey;
        if (keyType == "rsa")
        {
            using var rsa = RSA.Create(2048);
            publicKey = rsa.ExportSubjectPublicKeyInfo();
            privateKey = rsa.ExportPkcs8PrivateKey();
        }
        else
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            publicKey = ecdsa.ExportSubjectPublicKeyInfo();
            privateKey = ecdsa.ExportPkcs8PrivateKey();
        }

        var record = new PasskeyCredentialRecord
        {
            CredentialId = Base64Url(SHA256.HashData(publicKey)),
            RpId = rpId,
            RpName = rpId,
            UserName = userName,
            UserHandle = !string.IsNullOrEmpty(userHandle)
                ? userHandle
                : Base64Url(Encoding.UTF8.GetBytes(userName)),
            PublicKey = Convert.ToBase64String(publicKey),
            EncryptedPrivateKey = EncryptKey(privateKey, _dbKeyService.GetKey()),
            Counter = 1,
        };
        _dbVault.PasskeyCredentials.Add(record);
        await _dbVault.SaveChangesAsync();
        Log.Information("Created soft passkey for {RpId} user {User} keyType={KeyType}", rpId, userName, keyType);
        return record;
    }

    // === 私钥 AES-GCM 加密（与 DatabaseKeyService 主密码派生密钥绑定） ===
    // internal：WebAuthnService（真实 WebAuthn 协议层）复用同一套密钥管理

    internal static string EncryptKey(byte[] plaintext, byte[] key)
    {
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return $"enc:{Convert.ToBase64String(nonce)}.{Convert.ToBase64String(ciphertext)}.{Convert.ToBase64String(tag)}";
    }

    internal static byte[] DecryptKey(string encrypted, byte[] key)
    {
        var parts = encrypted.Split('.');
        if (parts.Length != 3 || !parts[0].StartsWith("enc:", StringComparison.Ordinal))
            throw new CryptographicException("Invalid encrypted key format");
        var nonce = Convert.FromBase64String(parts[0][4..]);
        var ciphertext = Convert.FromBase64String(parts[1]);
        var tag = Convert.FromBase64String(parts[2]);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] DecodeBase64Url(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }
}
