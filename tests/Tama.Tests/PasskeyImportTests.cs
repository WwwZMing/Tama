using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tama.Core.Contracts;
using Tama.Core.Exceptions;
using Tama.Core.Interfaces;
using Tama.Core.Models;
using Tama.Data.Database;
using Tama.Services.Passkey;
using Tama.Services.WebAuthn;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Tama.Tests;

/// <summary>
/// Bitwarden 通行密钥的**导入 / 启用**回归。
///
/// 这一块全是"格式事实"，几乎每条都只能靠实测确定，所以这里逐条钉死：
///   • Bitwarden 的 `credentialId` 是**标准 UUID 字符串**，rawId 是它解出来的 16 字节；
///     Tama 内部一律存 `base64url(rawId)`（页面 `allowCredentials[].id` 就是这串，
///     扩展 `b64ToBuf(cred.rawId)` 还原的也是这串——两边编码不一致会**静默**匹配不上，
///     表现为"库里有这枚凭据，但网站登录时被放行给了原生 Windows Hello"）。
///   • `keyValue` 是 **base64url 的 PKCS#8 私钥**（不是公钥），导进来就能直接签名；
///   • 导出文件里 `counter` / `discoverable` 是**字符串**（"0" / "true"），API 响应里是数字；
///   • counter==0（Bitwarden 的软件认证器恒定 0）断言时**不许自增**成 1。
///
/// 最关键的用例是 <see cref="Imported_Key_Produces_Verifiable_Assertion"/>：导入后真的走一遍
/// WebAuthn 断言并用公钥验签——"能存"和"能用"是两件事，只测前者等于没测。
/// </summary>
public class PasskeyImportTests : IDisposable
{
    private static readonly byte[] DbKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private readonly SqliteConnection _vaultConn, _authConn;
    private readonly TamaDbContext _vault;
    private readonly AuthDbContext _auth;
    private readonly DatabaseKeyService _keyService = new();
    private readonly FakePasskeyPlatform _platform = new();

    public PasskeyImportTests()
    {
        _vaultConn = new SqliteConnection("DataSource=:memory:");
        _vaultConn.Open();
        _authConn = new SqliteConnection("DataSource=:memory:");
        _authConn.Open();

        _vault = new TamaDbContext(new DbContextOptionsBuilder<TamaDbContext>().UseSqlite(_vaultConn).Options);
        _auth = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseSqlite(_authConn).Options);
        _vault.Database.EnsureCreated();
        _auth.Database.EnsureCreated();

        _keyService.SetKey(DbKey);
    }

    // ───────────────────────────── 夹具 ─────────────────────────────

    private PasskeyService Service() => new(_auth, _vault, _keyService, _platform);

    private WebAuthnService WebAuthn(PasskeyService passkeys) =>
        new(_vault, _keyService, _platform, passkeys);

    /// <summary>真造一对 P-256 密钥，返回 (PKCS#8 私钥, 期望的 SPKI 公钥)。</summary>
    private static (byte[] pkcs8, byte[] spki) NewP256Key()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ecdsa.ExportPkcs8PrivateKey(), ecdsa.ExportSubjectPublicKeyInfo());
    }

    private static string B64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string GuidCredentialId() => Guid.NewGuid().ToString();

    private static byte[] GuidBytes(string guid) => Convert.FromHexString(guid.Replace("-", ""));

    /// <summary>造一份"未加密导出"的 Bitwarden JSON（形状照抄真实导出：counter/discoverable 是字符串）。</summary>
    private static byte[] Export(params object[] items) =>
        JsonSerializer.SerializeToUtf8Bytes(new { encrypted = false, folders = Array.Empty<object>(), items });

    private static object LoginItem(string name, string userId, string userName, byte[] pkcs8, string credentialId,
        string rpId, string? rpName = null, string? userHandle = null, int counter = 0) => new
        {
            id = userId,
            name,
            type = 1,
            login = new
            {
                username = userName,
                fido2Credentials = new[]
                {
                    new
                    {
                        credentialId,
                        keyType = "public-key",
                        keyAlgorithm = "ECDSA",
                        keyCurve = "P-256",
                        keyValue = B64Url(pkcs8),
                        rpId,
                        rpName = rpName ?? rpId,
                        userHandle = userHandle ?? B64Url(Encoding.UTF8.GetBytes(userName)),
                        userName,
                        userDisplayName = userName,
                        counter = counter.ToString(),          // ← 实测：导出里是字符串
                        discoverable = "true",                 // ← 同上
                        creationDate = "2026-03-02T06:36:14.379Z",
                    },
                },
            },
        };

    // ───────────────────────────── 导入 ─────────────────────────────

    [Fact]
    public async Task Import_Maps_Guid_CredentialId_To_RawId_Base64Url()
    {
        var (pkcs8, spki) = NewP256Key();
        var guid = "01234567-89ab-cdef-0123-456789abcdef";
        var file = Export(LoginItem("Microsoft", "item-1", "me@example.com", pkcs8, guid,
            "login.microsoft.com", "Microsoft"));

        var result = await Service().ImportFromBitwardenJson(file);

        Assert.Equal(1, result.Imported);
        Assert.Equal(0, result.Failed);

        var record = await _vault.PasskeyCredentials.AsNoTracking().SingleAsync();
        // rawId 的 base64url —— 不是那个 GUID 字符串本身
        Assert.Equal(B64Url(GuidBytes(guid)), record.CredentialId);
        Assert.NotEqual(guid, record.CredentialId);
        Assert.Equal("login.microsoft.com", record.RpId);
        Assert.Equal("Microsoft", record.RpName);
        Assert.Equal("me@example.com", record.UserName);
        Assert.Equal(0, record.Counter);

        // 公钥由私钥现算（导出文件里没有公钥），私钥密文能解回原始 PKCS#8
        Assert.Equal(Convert.ToBase64String(spki), record.PublicKey);
        Assert.Equal(pkcs8, PasskeyService.DecryptKey(record.EncryptedPrivateKey, DbKey));
        // 断言里要原样回给 RP 的 userHandle，保持 base64url 原样
        Assert.Equal(B64Url(Encoding.UTF8.GetBytes("me@example.com")), record.UserHandle);
    }

    [Fact]
    public async Task Import_Accepts_B64_Prefixed_CredentialId()
    {
        var (pkcs8, _) = NewP256Key();
        var raw = RandomNumberGenerator.GetBytes(16);
        var file = Export(LoginItem("GitHub", "item-2", "alice", pkcs8, "b64." + B64Url(raw), "github.com"));

        var result = await Service().ImportFromBitwardenJson(file);

        Assert.Equal(1, result.Imported);
        var record = await _vault.PasskeyCredentials.AsNoTracking().SingleAsync();
        Assert.Equal(B64Url(raw), record.CredentialId);
    }

    [Fact]
    public async Task Import_Is_Idempotent()
    {
        var (pkcs8, _) = NewP256Key();
        var guid = GuidCredentialId();
        var file = Export(LoginItem("Google", "item-3", "me@gmail.com", pkcs8, guid, "google.com", "Google"));

        var first = await Service().ImportFromBitwardenJson(file);
        var second = await Service().ImportFromBitwardenJson(file);

        Assert.Equal(1, first.Imported);
        Assert.Equal(0, second.Imported);
        Assert.Equal(1, second.Skipped);
        Assert.Equal(1, await _vault.PasskeyCredentials.CountAsync());
    }

    [Fact]
    public async Task Import_Keeps_Going_When_One_Item_Is_Broken()
    {
        var (goodKey, _) = NewP256Key();
        var (brokenKey, _) = NewP256Key();
        var broken = Export(
            LoginItem("Bad", "item-4", "bad", brokenKey, GuidCredentialId(), "broken.example"),
            LoginItem("Good", "item-5", "good", goodKey, GuidCredentialId(), "good.example"));
        // 把第一条的私钥换成合法 base64url 但不是 PKCS#8 的字节
        var json = Encoding.UTF8.GetString(broken).Replace(B64Url(brokenKey), B64Url("not-a-real-key"u8.ToArray()));
        broken = Encoding.UTF8.GetBytes(json);

        var result = await Service().ImportFromBitwardenJson(broken);

        Assert.Equal(1, result.Imported);
        Assert.Equal(1, result.Failed);
        Assert.Contains("broken.example", result.Warnings[0]);
        Assert.Equal("good.example", (await _vault.PasskeyCredentials.AsNoTracking().SingleAsync()).RpId);
    }

    [Fact]
    public async Task Encrypted_Export_Is_Rejected_With_Actionable_Message()
    {
        var file = JsonSerializer.SerializeToUtf8Bytes(new { encrypted = true, data = "2.abc|def" });

        var ex = await Assert.ThrowsAsync<TamaException>(() => Service().ImportFromBitwardenJson(file));

        Assert.Contains("加密导出", ex.Message);
        Assert.Contains("不要", ex.Message);   // 告诉用户怎么办，而不是只说"不支持"
    }

    [Fact]
    public async Task Non_Bitwarden_Json_Is_Rejected()
    {
        await Assert.ThrowsAsync<TamaException>(() =>
            Service().ImportFromBitwardenJson(Encoding.UTF8.GetBytes("not json at all")));

        await Assert.ThrowsAsync<TamaException>(() =>
            Service().ImportFromBitwardenJson(Encoding.UTF8.GetBytes("""{"hello":"world"}""")));
    }

    [Fact]
    public async Task Keeps_Original_Counter_When_NonZero()
    {
        var (pkcs8, _) = NewP256Key();
        var file = Export(LoginItem("Bank", "item-6", "me", pkcs8, GuidCredentialId(), "bank.example", counter: 5));

        await Service().ImportFromBitwardenJson(file);

        Assert.Equal(5, (await _vault.PasskeyCredentials.AsNoTracking().SingleAsync()).Counter);
    }

    // ──────────────────── 导入后真的能断言（本轮的核心保证） ────────────────────

    [Fact]
    public async Task Imported_Key_Produces_Verifiable_Assertion()
    {
        var (pkcs8, _) = NewP256Key();
        var guid = GuidCredentialId();
        var handle = B64Url(Encoding.UTF8.GetBytes("GOOGLE_ACCOUNT:100000000000000000000"));
        var file = Export(LoginItem("Google", "item-7", "me@gmail.com", pkcs8, guid,
            "google.com", "Google", userHandle: handle));

        var passkeys = Service();
        await passkeys.ImportFromBitwardenJson(file);
        var credentialId = B64Url(GuidBytes(guid));

        // 扩展先探测（allowCredentials 里就是页面给的那串 base64url），再走断言
        var webauthn = WebAuthn(passkeys);
        var probe = await webauthn.Probe(new WebAuthnProbeRequest
        {
            RpId = "google.com",
            AllowCredentialIds = new List<string> { credentialId },
        });
        Assert.True(probe.HasMatch);
        Assert.False((await webauthn.Probe(new WebAuthnProbeRequest { RpId = "other.example" })).HasMatch);

        var challenge = B64Url(RandomNumberGenerator.GetBytes(32));
        var response = await webauthn.Get(new WebAuthnGetRequest
        {
            RpId = "google.com",
            Challenge = challenge,
            Origin = "https://accounts.google.com",
            CredentialId = credentialId,
        });

        Assert.Null(response.Success);              // 成功路径不带 success 字段
        Assert.Equal(credentialId, response.Id);    // 页面据此造 rawId
        Assert.Equal(credentialId, response.RawId);
        Assert.NotNull(response.Response);

        var authData = Convert.FromBase64String(response.Response!.AuthenticatorData);
        var clientDataJson = Convert.FromBase64String(response.Response.ClientDataJSON);
        var signature = Convert.FromBase64String(response.Response.Signature);

        // authenticatorData = SHA256(rpId) | flags | counter
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes("google.com")), authData[..32]);
        Assert.Equal(0x05, authData[32]);                    // UP|UV，无 AT
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, authData[33..37]);   // counter==0 保持 0（Bitwarden 口径）

        // clientDataJSON 是标准结构，challenge 必须原样（base64url）
        using (var cd = JsonDocument.Parse(clientDataJson))
        {
            Assert.Equal("webauthn.get", cd.RootElement.GetProperty("type").GetString());
            Assert.Equal(challenge, cd.RootElement.GetProperty("challenge").GetString());
            Assert.Equal("https://accounts.google.com", cd.RootElement.GetProperty("origin").GetString());
        }

        // userHandle 按 base64url 还原成原始字节
        Assert.Equal("GOOGLE_ACCOUNT:100000000000000000000",
            Encoding.UTF8.GetString(Convert.FromBase64String(response.Response.UserHandle)));

        // ★ 真正的证明：用**导入的那把私钥对应的公钥**验签，
        //   签名对象是 authenticatorData || SHA256(clientDataJSON)（规范顺序，写反过就验不过）
        var signedData = authData.Concat(SHA256.HashData(clientDataJson)).ToArray();
        using (var ecdsa = ECDsa.Create())
        {
            ecdsa.ImportPkcs8PrivateKey(pkcs8, out _);
            Assert.True(ecdsa.VerifyData(signedData, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence));
        }

        // 断言后 counter 仍为 0，没有把同步型凭据写成一个像被克隆过的计数器
        Assert.Equal(0, (await _vault.PasskeyCredentials.AsNoTracking().SingleAsync()).Counter);
    }

    [Fact]
    public async Task Counter_Advances_For_NonZero_Credentials()
    {
        var (pkcs8, _) = NewP256Key();
        var file = Export(LoginItem("Bank", "item-8", "me", pkcs8, GuidCredentialId(), "bank.example", counter: 5));
        var passkeys = Service();
        await passkeys.ImportFromBitwardenJson(file);
        var credentialId = (await _vault.PasskeyCredentials.AsNoTracking().SingleAsync()).CredentialId;

        var webauthn = WebAuthn(passkeys);
        for (var expected = 6; expected <= 7; expected++)
        {
            await webauthn.Get(new WebAuthnGetRequest
            {
                RpId = "bank.example",
                Challenge = B64Url(RandomNumberGenerator.GetBytes(32)),
                Origin = "https://bank.example",
                CredentialId = credentialId,
            });
            Assert.Equal(expected, (await _vault.PasskeyCredentials.AsNoTracking().SingleAsync()).Counter);
        }
    }

    // ───────────────────────── 启用同步缓存里的凭据 ─────────────────────────

    [Fact]
    public async Task Adopt_Synced_Passkeys_Makes_Them_Usable_And_List_Does_Not_Duplicate()
    {
        var (pkcs8, _) = NewP256Key();
        var guid = GuidCredentialId();
        SeedSyncedVault(pkcs8, guid, "google.com", "me@gmail.com", "Google");

        var passkeys = Service();

        // 启用前：列表里只有缓存那一份，且明确是"未启用"
        var before = await passkeys.List();
        var syncedRow = Assert.Single(before);
        Assert.Equal("bitwarden", syncedRow.Source);
        Assert.Equal(B64Url(GuidBytes(guid)), syncedRow.CredentialId);

        var result = await passkeys.AdoptSyncedPasskeys();
        Assert.Equal(1, result.Imported);

        // 启用后：仍然只有一行（本地那份取代了缓存那份，不是并列两行），并且可断言
        var after = await passkeys.List();
        var row = Assert.Single(after);
        Assert.Equal("local", row.Source);
        Assert.Equal("Google", row.RpName);

        Assert.Equal(1, (await passkeys.AdoptSyncedPasskeys()).Skipped);   // 幂等

        var webauthn = WebAuthn(passkeys);
        var response = await webauthn.Get(new WebAuthnGetRequest
        {
            RpId = "google.com",
            Challenge = B64Url(RandomNumberGenerator.GetBytes(32)),
            Origin = "https://accounts.google.com",
            AllowCredentialIds = new List<string> { row.CredentialId },
        });
        Assert.Equal(row.CredentialId, response.Id);
        Assert.NotNull(response.Response);

        // 缓存里那份私钥本来就是明文（同步时就解开了），所以断言必须真的签得出来：验一遍
        var authData = Convert.FromBase64String(response.Response!.AuthenticatorData);
        var clientDataJson = Convert.FromBase64String(response.Response.ClientDataJSON);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(pkcs8, out _);
        Assert.True(ecdsa.VerifyData(authData.Concat(SHA256.HashData(clientDataJson)).ToArray(),
            Convert.FromBase64String(response.Response.Signature), HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence));
    }

    [Fact]
    public async Task Adopt_Single_Credential_Only_Touches_That_One()
    {
        var (key1, _) = NewP256Key();
        var (key2, _) = NewP256Key();
        var guid1 = GuidCredentialId();
        var guid2 = GuidCredentialId();
        SeedSyncedVault(key1, guid1, "a.example", "a@example.com", "A");
        AddSyncedCredential(key2, guid2, "b.example", "b@example.com", "B");

        var passkeys = Service();
        var result = await passkeys.AdoptSyncedPasskeys(B64Url(GuidBytes(guid2)));

        Assert.Equal(1, result.Imported);
        var local = Assert.Single(await _vault.PasskeyCredentials.AsNoTracking().ToListAsync());
        Assert.Equal(B64Url(GuidBytes(guid2)), local.CredentialId);
    }

    [Fact]
    public async Task Adopt_With_Encrypted_Cache_Value_Reports_The_Reason()
    {
        // 旧版本写下的缓存：keyValue 还是 EncString 密文
        SeedSyncedVault([1, 2, 3], GuidCredentialId(), "old.example", "old@example.com", "Old", rawKeyValue: "2.abc|def");

        var result = await Service().AdoptSyncedPasskeys();

        Assert.Equal(0, result.Imported);
        Assert.Equal(1, result.Failed);
        Assert.Contains("重新同步", result.Warnings[0]);
    }

    [Fact]
    public async Task Adopt_Without_Linked_Account_Throws()
    {
        await Assert.ThrowsAsync<TamaException>(() => Service().AdoptSyncedPasskeys());
    }

    [Fact]
    public async Task Delete_Unknown_Credential_Throws()
    {
        await Assert.ThrowsAsync<TamaException>(() => Service().Delete("does-not-exist"));
    }

    // ───────────────────────────── 辅助 ─────────────────────────────

    private void SeedSyncedVault(byte[] pkcs8, string guid, string rpId, string userName, string rpName,
        string? rawKeyValue = null)
    {
        _auth.Accounts.Add(new AccountData
        {
            Id = "acc-1",
            Email = "me@example.com",
            Type = "bitwarden",
            EncryptionKey = "user-key",
        });
        _auth.SyncCaches.Add(new SyncCache { Id = "default", AccountId = "acc-1", CiphersJson = "[]" });
        _auth.SaveChanges();
        AddSyncedCredential(pkcs8, guid, rpId, userName, rpName, rawKeyValue);
    }

    private void AddSyncedCredential(byte[] pkcs8, string guid, string rpId, string userName, string rpName,
        string? rawKeyValue = null)
    {
        var cache = _auth.SyncCaches.Single();
        var ciphers = JsonSerializer.Deserialize<List<BitwardenCipherResponse>>(cache.CiphersJson) ?? new();
        ciphers.Add(new BitwardenCipherResponse
        {
            Id = Guid.NewGuid().ToString(),
            Name = rpName,
            Type = 1,
            Fido2Credentials = new List<BitwardenFido2Credential>
            {
                new()
                {
                    CredentialId = guid,
                    KeyValue = rawKeyValue ?? B64Url(pkcs8),
                    RpId = rpId,
                    RpName = rpName,
                    UserName = userName,
                    UserHandle = B64Url(Encoding.UTF8.GetBytes(userName)),
                    Counter = 0,
                    CreationDate = new DateTime(2026, 3, 2, 6, 36, 14, DateTimeKind.Utc),
                },
            },
        });
        cache.CiphersJson = JsonSerializer.Serialize(ciphers);
        _auth.SaveChanges();
    }

    public void Dispose()
    {
        _vault.Dispose();
        _auth.Dispose();
        _vaultConn.Dispose();
        _authConn.Dispose();
    }

    /// <summary>平台确认框的假实现：一律"用户确认了"，否则断言流程根本走不到签名那一步。</summary>
    private sealed class FakePasskeyPlatform : IPasskeyPlatformService
    {
        public bool IsAvailable() => true;
        public Task<bool> AuthenticateAsync(string title, string subtitle) => Task.FromResult(true);
    }
}
