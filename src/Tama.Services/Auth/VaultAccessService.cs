using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Tama.Core;
using Tama.Core.Contracts;
using Tama.Core.Exceptions;
using Tama.Core.Interfaces;
using Tama.Data.Database;

namespace Tama.Services.Auth;

/// <summary>
/// 把应急份额、受信设备和系统 Passkey 统一接到同一条保险库密钥上。
/// 主密码、系统验证与应急份额是三条互相独立的入口；任何一条都不会重加密数据库。
/// </summary>
public sealed class VaultAccessService : IVaultAccessApi
{
    private const string PasskeyPrefix = "v2:";
    private const string VaultKeyPrefix = "vault-key-v1:";
    private const string DeviceSharePrefix = "recovery-share-v1:";

    private readonly VaultAccessStore _store;
    private readonly IAuthSession _auth;
    private readonly IBiometricService _bio;
    private readonly DatabaseKeyService _dbKey;
    private readonly OfflineTrustedDeviceLinkStore _trustedDeviceLinks;
    private readonly object _pairingGate = new();
    private readonly Dictionary<string, PendingTrustedDeviceOffer> _pendingPairingOffers = new();

    public VaultAccessService(VaultAccessStore store, IAuthSession auth,
        IBiometricService bio, DatabaseKeyService dbKey,
        OfflineTrustedDeviceLinkStore trustedDeviceLinks)
    {
        _store = store;
        _auth = auth;
        _bio = bio;
        _dbKey = dbKey;
        _trustedDeviceLinks = trustedDeviceLinks;
    }

    public Task<VaultAccessStatus> Status()
    {
        var access = _store.Status();
        bool available;
        bool configured;
        try
        {
            available = _bio.IsAvailable();
            configured = _bio.IsConfigured();
        }
        catch
        {
            available = false;
            configured = false;
        }

        return Task.FromResult(new VaultAccessStatus(
            access.HasRecovery,
            access.RecoveryCreatedAt,
            access.HasRecoveryPassphrase,
            access.RecoveryPassphraseCreatedAt,
            available,
            configured,
            File.Exists(AppPaths.DeviceRecoverySharePath),
            access.TrustedDeviceCount,
            access.OfflineTrustedDeviceCount));
    }

    public Task<RecoveryKitDto> CreateRecoveryKit()
    {
        if (!_auth.IsUnlocked) throw new TamaException("请先解锁保险库");
        var key = _dbKey.CopyKey();
        try
        {
            return Task.FromResult(_store.CreateRecoveryKit(key));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public Task CommitRecoveryKit(CommitRecoveryKitRequest request)
    {
        if (!_auth.IsUnlocked) throw new TamaException("请先解锁保险库");
        if (request is null || string.IsNullOrWhiteSpace(request.EnvelopeJson))
            throw new TamaException("应急包数据无效");
        _store.CommitRecoveryKit(request.EnvelopeJson);

        // 新应急包一旦提交，本机保存的旧份额就不再属于当前集合。
        // 留着它只会让界面继续显示“已保存”，恢复时却必然失败。
        ClearDeviceShare();
        return Task.CompletedTask;
    }

    public async Task Recover(RecoverVaultRequest request)
    {
        if (request is null) throw new TamaException("恢复请求无效");
        if (string.IsNullOrEmpty(request.NewPassword) || request.NewPassword.Length < 6)
            throw new TamaException("新主密码至少 6 位");
        if (string.IsNullOrWhiteSpace(request.FirstShare) || string.IsNullOrWhiteSpace(request.SecondShare))
            throw new TamaException("请提供两份不同的应急份额");

        try
        {
            var key = _store.UnwrapRecovery(request.FirstShare, request.SecondShare);
            try
            {
                await _auth.ApplyVaultKeyAsync(key, request.NewPassword);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

            // 恢复成功等同于应急包已被使用：旧份额、旧 Passkey 与旧设备份额全部失效，
            // 用户必须重新生成并分发，避免“已经暴露的旧份额”长期保留后门。
            _store.ClearRecovery();
            ClearPasskey();
            ClearDeviceShare();
        }
        catch
        {
            // ApplyVaultKeyAsync 内部会回滚新密钥；这里再兜底，确保恢复流程任何一步失败后
            // 都不会把应用留在“锁屏界面 + 内存已解锁”的半状态。
            _auth.Lock();
            throw;
        }
    }

    public Task SetRecoveryPassphrase(SetRecoveryPassphraseRequest request)
    {
        if (!_auth.IsUnlocked) throw new TamaException("请先解锁保险库");
        var passphrase = request?.Passphrase ?? "";
        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length < 12)
            throw new TamaException("恢复口令至少 12 个字符");
        if (passphrase.Length > 1024)
            throw new TamaException("恢复口令过长");

        var key = _dbKey.CopyKey();
        try
        {
            _store.SetRecoveryPassphrase(passphrase, key);
            return Task.CompletedTask;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task RecoverWithPassphrase(RecoverWithPassphraseRequest request)
    {
        if (request is null) throw new TamaException("恢复请求无效");
        if (string.IsNullOrWhiteSpace(request.Passphrase))
            throw new TamaException("请输入恢复口令");
        if (string.IsNullOrEmpty(request.NewPassword) || request.NewPassword.Length < 6)
            throw new TamaException("新主密码至少 6 位");
        if (!_store.TryUnwrapRecoveryPassphrase(request.Passphrase, out var key))
            throw new TamaException("恢复口令不正确");

        try
        {
            await _auth.ApplyVaultKeyAsync(key, request.NewPassword);
        }
        catch
        {
            _auth.Lock();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        // 恢复成功后作废本次口令，避免已经暴露的旧口令长期成为后门。
        // 用户登录后可以在设置页设置一段新的恢复口令。
        try { _store.ClearRecoveryPassphrase(); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Clear consumed recovery passphrase failed"); }
    }

    public void ClearRecoveryPassphrase()
    {
        if (!_auth.IsUnlocked) throw new TamaException("请先解锁保险库");
        _store.ClearRecoveryPassphrase();
    }

    public Task<TrustedDevicePairingOffer> CreateTrustedDevicePairingOffer(
        CreateTrustedDevicePairingOfferRequest request)
    {
        if (!_auth.IsUnlocked) throw new TamaException("请先解锁保险库");

        var vault = _store.Load() ?? throw new TamaException("保险库访问元数据不存在");
        var offerId = Guid.NewGuid().ToString("N");
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        var pairingCode = OfflineTrustedDeviceCodec.CreatePairingCode();
        var normalizedCode = OfflineTrustedDeviceCodec.NormalizePairingCode(pairingCode);
        var vaultTag = OfflineTrustedDeviceCodec.ComputeVaultTag(vault.VaultId);
        var key = _dbKey.CopyKey();
        try
        {
            var envelope = OfflineTrustedDeviceCodec.SealVaultKey(
                key, normalizedCode, vaultTag, offerId);
            var mutualCode = OfflineTrustedDeviceCodec.ComputeMutualCode(
                key, vaultTag, offerId);
            var sourceName = string.IsNullOrWhiteSpace(request?.DeviceName)
                ? Environment.MachineName
                : request.DeviceName.Trim();
            var query = string.Join('&',
                "ver=1",
                $"id={Uri.EscapeDataString(offerId)}",
                $"name={Uri.EscapeDataString(OfflineTrustedDeviceCodec.EncodeDeviceName(sourceName))}",
                $"exp={expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}",
                $"vt={Uri.EscapeDataString(vaultTag)}",
                $"salt={Uri.EscapeDataString(envelope.Salt)}",
                $"nonce={Uri.EscapeDataString(envelope.Nonce)}",
                $"ct={Uri.EscapeDataString(envelope.Ciphertext)}");
            var qrPayload = "tama://pair?" + query;

            lock (_pairingGate)
            {
                foreach (var expired in _pendingPairingOffers
                             .Where(pair => pair.Value.ExpiresAt <= DateTimeOffset.UtcNow)
                             .Select(pair => pair.Key)
                             .ToArray())
                    _pendingPairingOffers.Remove(expired);

                _pendingPairingOffers[offerId] = new PendingTrustedDeviceOffer(
                    offerId, sourceName, normalizedCode, mutualCode, vaultTag, envelope, expiresAt);
            }

            return Task.FromResult(new TrustedDevicePairingOffer(
                offerId,
                qrPayload,
                OfflineTrustedDeviceCodec.FormatCode(normalizedCode),
                mutualCode,
                expiresAt));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public Task CommitTrustedDevicePairing(CommitTrustedDevicePairingRequest request)
    {
        if (!_auth.IsUnlocked) throw new TamaException("请先解锁保险库");
        if (request is null || string.IsNullOrWhiteSpace(request.OfferId))
            throw new TamaException("扫码配对请求无效");

        if (string.IsNullOrWhiteSpace(request.MutualCode)
            || request.MutualCode.Length != 6
            || !request.MutualCode.All(char.IsAsciiDigit))
            throw new TamaException("请输入手机显示的 6 位互认码");

        PendingTrustedDeviceOffer? matched;
        lock (_pairingGate)
        {
            if (!_pendingPairingOffers.TryGetValue(request.OfferId, out matched)
                || matched is null)
                throw new TamaException("扫码配对已过期，请重新生成");

            if (matched.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _pendingPairingOffers.Remove(request.OfferId);
                throw new TamaException("扫码配对已过期，请重新生成");
            }

            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(request.MutualCode),
                    Encoding.UTF8.GetBytes(matched.MutualCode)))
                throw new TamaException("互认码不正确，请核对手机显示的数字");

            _pendingPairingOffers.Remove(request.OfferId);
        }

        var pending = matched;
        _store.AddOfflineTrustedDevice(
            "offline-" + pending.OfferId,
            "扫码设备 " + pending.OfferId[..6].ToUpperInvariant(),
            pending.OfferId,
            pending.VaultTag,
            pending.Envelope);
        return Task.CompletedTask;
    }

    public void CancelTrustedDevicePairing(string offerId)
    {
        if (string.IsNullOrWhiteSpace(offerId)) return;
        lock (_pairingGate)
            _pendingPairingOffers.Remove(offerId);
    }

    public async Task<TrustedDeviceImportResult> ImportTrustedDevicePairing(
        ImportTrustedDevicePairingRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.QrPayload))
            throw new TamaException("二维码内容为空");

        var query = ParseDeepLinkQuery(request.QrPayload, "pair");
        Require(query, "ver", "1");
        var offerId = Required(query, "id");
        if (!long.TryParse(Required(query, "exp"), NumberStyles.None,
                CultureInfo.InvariantCulture, out var expiration))
            throw new TamaException("二维码有效期无效");
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiration)
            throw new TamaException("二维码已过期，请在 Windows 上重新生成");

        var sourceName = OfflineTrustedDeviceCodec.DecodeDeviceName(Required(query, "name"));
        var envelope = new OfflineTrustedDeviceCodec.SealedPairingKey(
            Required(query, "salt"),
            Required(query, "nonce"),
            Required(query, "ct"));
        var vaultTag = Required(query, "vt");
        if (!OfflineTrustedDeviceCodec.TryOpenVaultKey(
                envelope, request.PairingCode, vaultTag, offerId, out var vaultKey))
            throw new TamaException("配对码不正确");

        try
        {
            var mutualCode = OfflineTrustedDeviceCodec.ComputeMutualCode(
                vaultKey, vaultTag, offerId);
            var linkId = await _trustedDeviceLinks.Save(
                offerId, sourceName, vaultTag, mutualCode, request.PairingCode);
            return new TrustedDeviceImportResult(
                linkId,
                sourceName,
                mutualCode);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(vaultKey);
        }
    }

    public Task<IReadOnlyList<TrustedDeviceLink>> ListTrustedDeviceLinks()
        => Task.FromResult(_trustedDeviceLinks.List());

    public Task<TrustedDeviceLoginOffer> CreateTrustedDeviceLoginOffer(
        CreateTrustedDeviceLoginOfferRequest request)
    {
        var device = _store.GetOfflineTrustedDevices()
            .Where(candidate => candidate.OfflineRecovery is not null)
            .OrderByDescending(candidate => candidate.RegisteredAt)
            .FirstOrDefault()
            ?? throw new TamaException("尚无已配对手机，请先在设置中完成扫码配对");

        var recovery = device.OfflineRecovery!;
        var sourceName = string.IsNullOrWhiteSpace(request?.DeviceName)
            ? Environment.MachineName
            : request.DeviceName.Trim();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        var query = string.Join('&',
            "ver=1",
            $"id={Uri.EscapeDataString(recovery.OfferId)}",
            $"name={Uri.EscapeDataString(OfflineTrustedDeviceCodec.EncodeDeviceName(sourceName))}",
            $"exp={expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}",
            $"vt={Uri.EscapeDataString(recovery.VaultTag)}");

        return Task.FromResult(new TrustedDeviceLoginOffer(
            "tama://login?" + query,
            sourceName,
            expiresAt));
    }

    public Task<TrustedDeviceLoginPrompt> ReadTrustedDeviceLoginRequest(string qrPayload)
    {
        var query = ParseDeepLinkQuery(qrPayload, "login");
        Require(query, "ver", "1");
        var sourceDeviceId = Required(query, "id");
        var vaultTag = Required(query, "vt");
        if (!long.TryParse(Required(query, "exp"), NumberStyles.None,
                CultureInfo.InvariantCulture, out var expiration))
            throw new TamaException("登录二维码有效期无效");
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiration)
            throw new TamaException("登录二维码已过期，请在电脑上重新刷新");

        var sourceName = OfflineTrustedDeviceCodec.DecodeDeviceName(Required(query, "name"));
        var link = _trustedDeviceLinks.List()
            .FirstOrDefault(candidate =>
                string.Equals(candidate.SourceDeviceId, sourceDeviceId, StringComparison.Ordinal)
                && (string.IsNullOrWhiteSpace(candidate.VaultTag)
                    || string.Equals(candidate.VaultTag, vaultTag, StringComparison.Ordinal)))
            ?? throw new TamaException("这台手机没有保存该电脑的登录码，请先完成扫码配对");

        return Task.FromResult(new TrustedDeviceLoginPrompt(
            link.Id,
            sourceDeviceId,
            string.IsNullOrWhiteSpace(sourceName) ? link.SourceDeviceName : sourceName));
    }

    public async Task UnlockWithTrustedDeviceCode(UnlockWithTrustedDeviceCodeRequest request)
    {
        if (request is null) throw new TamaException("扫码登录请求无效");
        var normalizedCode = OfflineTrustedDeviceCodec.NormalizePairingCode(request.LoginCode);
        if (normalizedCode.Length != 16)
            throw new TamaException("登录码应为 16 个字符");
        if (!_store.TryUnwrapOfflineTrustedDevice(normalizedCode, out _, out var key))
            throw new TamaException("登录码不正确或该设备关系已移除");

        try
        {
            await _auth.ApplyVaultKeyAsync(key);
        }
        catch
        {
            _auth.Lock();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public Task<IReadOnlyList<PairedTrustedDevice>> ListPairedTrustedDevices()
    {
        var authorized = _store.GetOfflineTrustedDevices()
            .Where(device => device.OfflineRecovery is not null)
            .Select(device => new PairedTrustedDevice(
                device.Id,
                PairedTrustedDeviceSide.AuthorizedDevice,
                string.IsNullOrWhiteSpace(device.Name) ? "另一台设备" : device.Name,
                device.RegisteredAt));
        var saved = _trustedDeviceLinks.List()
            .Select(link => new PairedTrustedDevice(
                link.Id,
                PairedTrustedDeviceSide.SavedRecoveryLink,
                link.SourceDeviceName,
                link.PairedAt));

        return Task.FromResult<IReadOnlyList<PairedTrustedDevice>>(
            authorized.Concat(saved).OrderByDescending(device => device.PairedAt).ToArray());
    }

    public Task RemovePairedTrustedDevice(PairedTrustedDeviceId device)
    {
        if (!_auth.IsUnlocked) throw new TamaException("请先解锁保险库");
        if (device is null || string.IsNullOrWhiteSpace(device.Id))
            throw new TamaException("可信设备记录无效");

        switch (device.Side)
        {
            case PairedTrustedDeviceSide.AuthorizedDevice:
                _store.RemoveTrustedDevice(device.Id);
                break;
            case PairedTrustedDeviceSide.SavedRecoveryLink:
                _trustedDeviceLinks.Remove(device.Id);
                break;
            default:
                throw new TamaException("可信设备记录类型无效");
        }

        return Task.CompletedTask;
    }

    public Task<TrustedDeviceRecoveryCode> RevealTrustedDeviceRecoveryCode(string linkId)
        => _trustedDeviceLinks.Reveal(linkId);

    public async Task RecoverWithTrustedDeviceCode(RecoverWithTrustedDeviceCodeRequest request)
    {
        if (request is null) throw new TamaException("可信设备恢复请求无效");
        if (string.IsNullOrEmpty(request.NewPassword) || request.NewPassword.Length < 6)
            throw new TamaException("新主密码至少 6 位");

        var normalizedCode = OfflineTrustedDeviceCodec.NormalizePairingCode(request.RecoveryCode);
        if (normalizedCode.Length != 16)
            throw new TamaException("恢复码应为 16 个字符");
        if (!_store.TryUnwrapOfflineTrustedDevice(normalizedCode, out var matched, out var key))
            throw new TamaException("恢复码不正确或已经使用");

        try
        {
            await _auth.ApplyVaultKeyAsync(key, request.NewPassword);
        }
        catch
        {
            _auth.Lock();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        if (matched is not null)
            _store.RemoveTrustedDevice(matched.Id);
    }

    public void RemoveTrustedDevice(string deviceId)
    {
        if (!_auth.IsUnlocked) throw new TamaException("请先解锁保险库");
        _store.RemoveTrustedDevice(deviceId);
    }

    public async Task ConfigurePasskey(ConfigurePasskeyRequest request)
    {
        if (!_auth.IsUnlocked) throw new TamaException("请先解锁保险库");
        if (string.IsNullOrEmpty(request.CurrentPassword))
            throw new TamaException("请输入当前主密码");

        byte[] verifiedKey = [];
        try
        {
            if (!_store.TryUnwrapPassword(request.CurrentPassword, out verifiedKey))
                throw new TamaException("主密码不正确");
            CryptographicOperations.ZeroMemory(verifiedKey);

            if (!_bio.IsAvailable())
                throw new TamaException("当前设备没有可用的系统验证");
            if (!await _bio.AuthenticateAsync("启用 Passkey 解锁", "验证系统身份以绑定当前设备"))
                throw new TamaException("系统验证未通过");

            var key = _dbKey.CopyKey();
            try
            {
                _bio.SetupKey();
                var encrypted = _bio.EncryptPassword(VaultKeyPrefix + Convert.ToBase64String(key));
                if (string.IsNullOrEmpty(encrypted))
                    throw new TamaException("系统密钥不可用，Passkey 未保存");
                _bio.SaveEncryptedPassword(PasskeyPrefix + encrypted);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

            var id = _store.GetOrCreateDeviceId();
            var name = string.IsNullOrWhiteSpace(request.DeviceName)
                ? Environment.MachineName
                : request.DeviceName.Trim();
            _store.RegisterTrustedDevice(id, name, "passkey");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(verifiedKey);
        }
    }

    public async Task<bool> UnlockWithPasskey()
    {
        var stored = _bio.GetEncryptedPassword();
        if (string.IsNullOrEmpty(stored)) return false;
        if (!await _bio.AuthenticateAsync("解锁 Tama", "使用 Passkey / 系统身份解锁保险库"))
            return false;

        if (stored.StartsWith(PasskeyPrefix, StringComparison.Ordinal))
        {
            var encrypted = stored[PasskeyPrefix.Length..];
            var plain = _bio.DecryptPassword(encrypted);
            if (plain?.StartsWith(VaultKeyPrefix, StringComparison.Ordinal) != true)
            {
                ClearPasskey();
                return false;
            }

            byte[] key;
            try
            {
                key = Convert.FromBase64String(plain[VaultKeyPrefix.Length..]);
            }
            catch
            {
                ClearPasskey();
                return false;
            }

            try
            {
                await _auth.ApplyVaultKeyAsync(key);
                return true;
            }
            catch
            {
                ClearPasskey();
                _auth.Lock();
                return false;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }

        // 旧版 biometric.dat 里缓存的是主密码。成功解锁一次后，
        // AuthSessionService 会自动改写成包装保险库密钥的 v2 凭据。
        var legacyPassword = _bio.DecryptPassword(stored);
        return !string.IsNullOrEmpty(legacyPassword) && await _auth.UnlockAsync(legacyPassword);
    }

    public void ClearPasskey()
    {
        try { _bio.ClearEncryptedPassword(); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Clear passkey credential failed"); }
    }

    public async Task ImportDeviceShare(ImportDeviceShareRequest request)
    {
        if (request is null) throw new TamaException("受信份额请求无效");
        var parsed = RecoveryShareCodec.Parse(request.ShareText);
        if (!_store.IsCurrentRecoveryShare(parsed))
            throw new TamaException("这份份额不属于当前应急包，请使用最新生成的份额");
        if (!_bio.IsAvailable())
            throw new TamaException("当前设备没有可用的系统验证，不能保存受信设备份额");
        if (!await _bio.AuthenticateAsync("添加受信设备", "验证系统身份以保存一份受信份额"))
            throw new TamaException("系统验证未通过");

        _bio.SetupKey();
        var encrypted = _bio.EncryptPassword(DeviceSharePrefix + request.ShareText)
                        ?? throw new TamaException("系统密钥不可用，份额未保存");
        Directory.CreateDirectory(AppPaths.DataDir);
        WriteAtomic(AppPaths.DeviceRecoverySharePath, "sharev1:" + encrypted);

        var id = _store.GetOrCreateDeviceId();
        var name = string.IsNullOrWhiteSpace(request.DeviceName)
            ? Environment.MachineName
            : request.DeviceName.Trim();
        _store.RegisterTrustedDevice(id, name, "recovery-share");
    }

    public async Task<string?> ExportDeviceShare()
    {
        if (!File.Exists(AppPaths.DeviceRecoverySharePath)) return null;
        if (!await _bio.AuthenticateAsync("使用受信设备", "验证系统身份以取出本机受信份额"))
            throw new TamaException("系统验证未通过");

        var stored = File.ReadAllText(AppPaths.DeviceRecoverySharePath);
        if (!stored.StartsWith("sharev1:", StringComparison.Ordinal))
            throw new TamaException("受信设备份额格式无效");
        var plain = _bio.DecryptPassword(stored["sharev1:".Length..]);
        if (plain?.StartsWith(DeviceSharePrefix, StringComparison.Ordinal) != true)
            throw new TamaException("受信设备份额无法解密");

        var share = plain[DeviceSharePrefix.Length..];
        _ = RecoveryShareCodec.Parse(share);
        return share;
    }

    public void ClearDeviceShare()
    {
        try
        {
            if (File.Exists(AppPaths.DeviceRecoverySharePath))
                File.Delete(AppPaths.DeviceRecoverySharePath);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Clear trusted device share failed");
        }
    }

    private static void WriteAtomic(string path, string text)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, Encoding.UTF8);
        File.Move(temp, path, overwrite: true);
    }

    private static Dictionary<string, string> ParseDeepLinkQuery(string qrPayload, string host)
    {
        if (!Uri.TryCreate(qrPayload, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, "tama", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase))
            throw new TamaException(host == "login"
                ? "这不是 Tama 扫码登录二维码"
                : "这不是 Tama 扫码配对二维码");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0) continue;
            values[Uri.UnescapeDataString(pair[..separator])] =
                Uri.UnescapeDataString(pair[(separator + 1)..]);
        }
        return values;
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new TamaException("二维码缺少必要字段");

    private static void Require(IReadOnlyDictionary<string, string> values, string key, string expected)
    {
        if (!values.TryGetValue(key, out var value)
            || !string.Equals(value, expected, StringComparison.Ordinal))
            throw new TamaException("二维码版本不受支持");
    }

    private sealed record PendingTrustedDeviceOffer(
        string OfferId,
        string SourceDeviceName,
        string PairingCode,
        string MutualCode,
        string VaultTag,
        OfflineTrustedDeviceCodec.SealedPairingKey Envelope,
        DateTimeOffset ExpiresAt);
}
