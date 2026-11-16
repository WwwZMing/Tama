using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tama.Core;
using Tama.Core.Interfaces;
using Tama.Services.Crypto;

namespace Tama.Services.Auth;

/// <summary>
/// vault-access.json 的唯一读写点。文件里只有保险库密钥的加密封装，没有主密码、应急明文或设备明文。
/// </summary>
public sealed class VaultAccessStore
{
    private const int CurrentVersion = 1;
    private const int PasswordIterations = 310_000;
    private const int RecoveryPassphraseIterations = 600_000;
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IVaultCrypto _crypto;
    private readonly string _path;
    private readonly string _deviceIdPath;
    private readonly object _gate = new();

    public VaultAccessStore(IVaultCrypto crypto)
        : this(crypto, AppPaths.VaultAccessPath, AppPaths.DeviceTrustIdPath) { }

    internal VaultAccessStore(IVaultCrypto crypto, string path, string? deviceIdPath = null)
    {
        _crypto = crypto;
        _path = path;
        _deviceIdPath = deviceIdPath ?? System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(path)!, "device-trust.id");
    }

    public string PathOnDisk => _path;
    public bool Exists => File.Exists(_path);

    public string GetOrCreateDeviceId()
    {
        if (File.Exists(_deviceIdPath))
        {
            var existing = File.ReadAllText(_deviceIdPath).Trim();
            if (existing.Length is >= 16 and <= 128) return existing;
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_deviceIdPath)!);
        var id = Guid.NewGuid().ToString("N");
        File.WriteAllText(_deviceIdPath, id);
        return id;
    }

    public VaultAccessFile? Load()
    {
        lock (_gate)
        {
            return LoadCore();
        }
    }

    public AccessStatusSnapshot Status()
    {
        var file = Load();
        return new AccessStatusSnapshot(
            file is not null,
            file?.VaultId,
            file?.Recovery is not null,
            file?.Recovery?.CreatedAt,
            file?.RecoveryPassphrase is not null,
            file?.RecoveryPassphrase?.CreatedAt,
            file?.TrustedDevices.Count ?? 0,
            file?.TrustedDevices.Count(device => device.OfflineRecovery is not null) ?? 0);
    }

    /// <summary>新库：生成独立随机保险库密钥，并只把密码包装写进访问元数据。</summary>
    public (VaultAccessFile File, byte[] VaultKey) CreateForNewVault(string password)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("Password required", nameof(password));
        var vaultId = Guid.NewGuid().ToString("N");
        var vaultKey = RandomNumberGenerator.GetBytes(KeySize);
        var file = new VaultAccessFile
        {
            Version = CurrentVersion,
            VaultId = vaultId,
            CreatedAt = DateTimeOffset.UtcNow,
            Password = WrapWithPassword(password, vaultId, vaultKey),
        };
        return (file, vaultKey);
    }

    /// <summary>旧库迁移：数据库密钥保持不变，只是从“密码直接派生”改成可包装的稳定密钥。</summary>
    public VaultAccessFile CreateFromLegacyKey(string password, byte[] vaultKey)
    {
        var vaultId = Guid.NewGuid().ToString("N");
        return new VaultAccessFile
        {
            Version = CurrentVersion,
            VaultId = vaultId,
            CreatedAt = DateTimeOffset.UtcNow,
            Password = WrapWithPassword(password, vaultId, vaultKey),
        };
    }

    public bool TryUnwrapPassword(string password, out byte[] vaultKey)
    {
        vaultKey = [];
        var file = Load();
        if (file?.Password is null) return false;
        return TryUnwrapPassword(file, password, out vaultKey);
    }

    public bool TryUnwrapPassword(VaultAccessFile file, string password, out byte[] vaultKey)
    {
        vaultKey = [];
        try
        {
            var envelope = file.Password ?? throw new CryptographicException("Missing password envelope");
            var salt = Decode(envelope.Salt);
            var kek = _crypto.DeriveKey(password, salt, envelope.Iterations);
            try
            {
                vaultKey = Unwrap(kek, envelope.Nonce, envelope.Ciphertext, PasswordAad(file.VaultId));
                return vaultKey.Length == KeySize;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kek);
            }
        }
        catch
        {
            return false;
        }
    }

    public void SetRecoveryPassphrase(string passphrase, byte[] vaultKey)
    {
        if (string.IsNullOrWhiteSpace(passphrase))
            throw new ArgumentException("Recovery passphrase required", nameof(passphrase));

        lock (_gate)
        {
            var file = LoadCore() ?? throw new InvalidOperationException("Vault access metadata not found");
            file.RecoveryPassphrase = WrapWithRecoveryPassphrase(passphrase, file.VaultId, vaultKey);
            file.UpdatedAt = DateTimeOffset.UtcNow;
            SaveCore(file);
        }
    }

    public bool TryUnwrapRecoveryPassphrase(string passphrase, out byte[] vaultKey)
    {
        vaultKey = [];
        var file = Load();
        var envelope = file?.RecoveryPassphrase;
        if (file is null || envelope is null) return false;

        try
        {
            var salt = Decode(envelope.Salt);
            var kek = _crypto.DeriveKey(passphrase, salt, envelope.Iterations);
            try
            {
                vaultKey = Unwrap(kek, envelope.Nonce, envelope.Ciphertext,
                    RecoveryPassphraseAad(file.VaultId));
                return vaultKey.Length == KeySize;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kek);
            }
        }
        catch
        {
            return false;
        }
    }

    public void ClearRecoveryPassphrase()
    {
        lock (_gate)
        {
            var file = LoadCore();
            if (file is null || file.RecoveryPassphrase is null) return;
            file.RecoveryPassphrase = null;
            file.UpdatedAt = DateTimeOffset.UtcNow;
            SaveCore(file);
        }
    }

    internal void AddOfflineTrustedDevice(
        string deviceId, string deviceName, string offerId, string vaultTag,
        OfflineTrustedDeviceCodec.SealedPairingKey envelope)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(offerId))
            throw new ArgumentException("Trusted device identity is required");

        lock (_gate)
        {
            var file = LoadCore() ?? throw new InvalidOperationException("Vault access metadata not found");
            var existing = file.TrustedDevices.FirstOrDefault(device => device.Id == deviceId);
            if (existing is null)
            {
                existing = new TrustedDeviceRecord { Id = deviceId };
                file.TrustedDevices.Add(existing);
            }

            existing.Name = string.IsNullOrWhiteSpace(deviceName) ? "未命名设备" : deviceName.Trim();
            existing.Kind = "offline-qr";
            existing.RegisteredAt = DateTimeOffset.UtcNow;
            existing.LastSeenAt = DateTimeOffset.UtcNow;
            existing.OfflineRecovery = new OfflineRecoveryEnvelope
            {
                OfferId = offerId,
                VaultTag = vaultTag,
                Salt = envelope.Salt,
                Nonce = envelope.Nonce,
                Ciphertext = envelope.Ciphertext,
            };

            file.UpdatedAt = DateTimeOffset.UtcNow;
            SaveCore(file);
        }
    }

    public IReadOnlyList<TrustedDeviceRecord> GetOfflineTrustedDevices()
    {
        var file = Load();
        return file?.TrustedDevices
            .Where(device => device.OfflineRecovery is not null)
            .Select(CloneTrustedDevice)
            .ToArray() ?? [];
    }

    public bool TryUnwrapOfflineTrustedDevice(
        string pairingCode, out TrustedDeviceRecord? matchedDevice, out byte[] vaultKey)
    {
        matchedDevice = null;
        vaultKey = [];
        var file = Load();
        if (file is null) return false;

        foreach (var device in file.TrustedDevices)
        {
            var recovery = device.OfflineRecovery;
            if (recovery is null) continue;

            var envelope = new OfflineTrustedDeviceCodec.SealedPairingKey(
                recovery.Salt, recovery.Nonce, recovery.Ciphertext);
            if (!OfflineTrustedDeviceCodec.TryOpenVaultKey(
                    envelope, pairingCode, recovery.VaultTag, recovery.OfferId, out var candidate))
                continue;

            matchedDevice = CloneTrustedDevice(device);
            vaultKey = candidate;
            return true;
        }

        return false;
    }

    public void RemoveTrustedDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;
        lock (_gate)
        {
            var file = LoadCore();
            if (file is null) return;
            if (file.TrustedDevices.RemoveAll(device => device.Id == deviceId) == 0) return;
            file.UpdatedAt = DateTimeOffset.UtcNow;
            SaveCore(file);
        }
    }

    public void UpdatePassword(VaultAccessFile file, string newPassword, byte[] vaultKey)
    {
        file.Password = WrapWithPassword(newPassword, file.VaultId, vaultKey);
        file.UpdatedAt = DateTimeOffset.UtcNow;
        Save(file);
    }

    public RecoveryKitDto CreateRecoveryKit(byte[] vaultKey)
    {
        var file = Load() ?? throw new InvalidOperationException("Vault access metadata not found");
        var recoverySecret = RandomNumberGenerator.GetBytes(32);
        try
        {
            var shares = SecretSharing.Split(recoverySecret, threshold: 2, shareCount: 3);
            var setId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            var salt = RandomNumberGenerator.GetBytes(32);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var kek = Hkdf(recoverySecret, salt, $"Tama.Recovery.v1|{file.VaultId}|{setId}");
            try
            {
                var ciphertext = Wrap(kek, nonce, vaultKey, RecoveryAad(file.VaultId, setId));
                var envelope = new RecoveryEnvelope
                {
                    SetId = setId,
                    Salt = Encode(salt),
                    Nonce = Encode(nonce),
                    Ciphertext = Encode(ciphertext),
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                var textShares = shares.Select(s =>
                    RecoveryShareCodec.Encode(setId, s.Index, 2, 3, s.Data)).ToArray();
                return new RecoveryKitDto(textShares, JsonSerializer.Serialize(envelope, JsonOptions));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kek);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(recoverySecret);
        }
    }

    public void CommitRecoveryKit(string envelopeJson)
    {
        var envelope = JsonSerializer.Deserialize<RecoveryEnvelope>(envelopeJson, JsonOptions)
                       ?? throw new CryptographicException("应急包数据无效");
        ValidateEnvelope(envelope);

        lock (_gate)
        {
            var file = LoadCore() ?? throw new InvalidOperationException("Vault access metadata not found");
            if (envelope.SetId.Length > 64 || string.IsNullOrWhiteSpace(envelope.SetId))
                throw new CryptographicException("应急包集合编号无效");
            file.Recovery = envelope;
            file.UpdatedAt = DateTimeOffset.UtcNow;
            SaveCore(file);
        }
    }

    public byte[] UnwrapRecovery(params string[] shareTexts)
    {
        var parsed = shareTexts.Select(RecoveryShareCodec.Parse).ToArray();
        if (parsed.Length < 2) throw new CryptographicException("至少需要两份恢复份额");
        var first = parsed[0];
        if (parsed.Any(s => s.SetId != first.SetId || s.Total != first.Total || s.Threshold != first.Threshold))
            throw new CryptographicException("恢复份额来自不同应急包");
        if (parsed.Length < first.Threshold)
            throw new CryptographicException("恢复份额数量不足");

        var secret = SecretSharing.Combine(parsed.Take(first.Threshold)
            .Select(s => new SecretSharing.Share(s.Index, s.Data)));
        try
        {
            var file = Load() ?? throw new CryptographicException("找不到保险库访问元数据");
            var envelope = file.Recovery ?? throw new CryptographicException("此保险库没有配置应急包");
            if (envelope.SetId != first.SetId)
                throw new CryptographicException("恢复份额不属于当前保险库");

            var salt = Decode(envelope.Salt);
            var kek = Hkdf(secret, salt, $"Tama.Recovery.v1|{file.VaultId}|{envelope.SetId}");
            try
            {
                var key = Unwrap(kek, envelope.Nonce, envelope.Ciphertext,
                    RecoveryAad(file.VaultId, envelope.SetId));
                if (key.Length != KeySize) throw new CryptographicException("保险库密钥长度无效");
                return key;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kek);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>
    /// 受信设备份额必须属于当前已提交的应急包。否则 UI 会显示“本机已保存”，
    /// 真正恢复时才发现它是上一套已经作废的份额。
    /// </summary>
    public bool IsCurrentRecoveryShare(RecoveryShareCodec.ParsedShare share)
    {
        var recovery = Load()?.Recovery;
        return recovery is not null
               && recovery.SetId == share.SetId
               && share.Threshold == 2
               && share.Total == 3;
    }

    public void ClearRecovery()
    {
        lock (_gate)
        {
            var file = LoadCore();
            if (file is null) return;
            file.Recovery = null;
            file.UpdatedAt = DateTimeOffset.UtcNow;
            SaveCore(file);
        }
    }

    public void RegisterTrustedDevice(string id, string name, string kind)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_gate)
        {
            var file = LoadCore();
            if (file is null) return;
            var existing = file.TrustedDevices.FirstOrDefault(d => d.Id == id);
            if (existing is null)
            {
                file.TrustedDevices.Add(new TrustedDeviceRecord
                {
                    Id = id,
                    Name = string.IsNullOrWhiteSpace(name) ? "未命名设备" : name.Trim(),
                    Kind = kind,
                    RegisteredAt = DateTimeOffset.UtcNow,
                    LastSeenAt = DateTimeOffset.UtcNow,
                });
            }
            else
            {
                existing.Name = string.IsNullOrWhiteSpace(name) ? existing.Name : name.Trim();
                existing.Kind = kind;
                existing.LastSeenAt = DateTimeOffset.UtcNow;
            }
            file.UpdatedAt = DateTimeOffset.UtcNow;
            SaveCore(file);
        }
    }

    public void Save(VaultAccessFile file)
    {
        lock (_gate)
        {
            SaveCore(file);
        }
    }

    private VaultAccessFile? LoadCore()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var json = File.ReadAllText(_path);
            var file = JsonSerializer.Deserialize<VaultAccessFile>(json, JsonOptions);
            if (file is null || file.Version != CurrentVersion || string.IsNullOrWhiteSpace(file.VaultId)
                || file.Password is null)
                throw new CryptographicException("Vault access metadata is invalid");
            return file;
        }
        catch (Exception primary)
        {
            var backup = _path + ".bak";
            if (!File.Exists(backup)) throw new CryptographicException("保险库访问元数据损坏", primary);
            try
            {
                var json = File.ReadAllText(backup);
                return JsonSerializer.Deserialize<VaultAccessFile>(json, JsonOptions)
                       ?? throw new CryptographicException("Backup is empty");
            }
            catch (Exception backupError)
            {
                throw new CryptographicException("保险库访问元数据损坏，备份也无法读取",
                    new AggregateException(primary, backupError));
            }
        }
    }

    private void SaveCore(VaultAccessFile file)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var json = JsonSerializer.Serialize(file, JsonOptions);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, json);
        if (File.Exists(_path))
            File.Copy(_path, _path + ".bak", overwrite: true);
        File.Move(temp, _path, overwrite: true);
    }

    private PasswordEnvelope WrapWithPassword(string password, string vaultId, byte[] vaultKey)
    {
        var salt = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var kek = _crypto.DeriveKey(password, salt, PasswordIterations);
        try
        {
            return new PasswordEnvelope
            {
                Salt = Encode(salt),
                Iterations = PasswordIterations,
                Nonce = Encode(nonce),
                Ciphertext = Encode(Wrap(kek, nonce, vaultKey, PasswordAad(vaultId))),
                CreatedAt = DateTimeOffset.UtcNow,
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    private PasswordEnvelope WrapWithRecoveryPassphrase(string passphrase, string vaultId, byte[] vaultKey)
    {
        var salt = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var kek = _crypto.DeriveKey(passphrase, salt, RecoveryPassphraseIterations);
        try
        {
            return new PasswordEnvelope
            {
                Salt = Encode(salt),
                Iterations = RecoveryPassphraseIterations,
                Nonce = Encode(nonce),
                Ciphertext = Encode(Wrap(kek, nonce, vaultKey, RecoveryPassphraseAad(vaultId))),
                CreatedAt = DateTimeOffset.UtcNow,
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    private static byte[] Wrap(byte[] key, byte[] nonce, byte[] plaintext, byte[] aad)
    {
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        return [.. ciphertext, .. tag];
    }

    private static byte[] Unwrap(byte[] key, string nonce64, string ciphertext64, byte[] aad)
    {
        var nonce = Decode(nonce64);
        var combined = Decode(ciphertext64);
        if (nonce.Length != NonceSize || combined.Length < TagSize + 1)
            throw new CryptographicException("Envelope length invalid");
        var ciphertext = combined.AsSpan(0, combined.Length - TagSize);
        var tag = combined.AsSpan(combined.Length - TagSize);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        return plaintext;
    }

    private static byte[] Hkdf(byte[] ikm, byte[] salt, string info)
    {
        var prk = HMACSHA256.HashData(salt, ikm);
        try
        {
            return HMACSHA256.HashData(prk, Encoding.UTF8.GetBytes(info));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(prk);
        }
    }

    private static byte[] PasswordAad(string vaultId) =>
        Encoding.UTF8.GetBytes($"Tama.VaultAccess.Password.v1|{vaultId}");

    private static byte[] RecoveryAad(string vaultId, string setId) =>
        Encoding.UTF8.GetBytes($"Tama.VaultAccess.Recovery.v1|{vaultId}|{setId}");

    private static byte[] RecoveryPassphraseAad(string vaultId) =>
        Encoding.UTF8.GetBytes($"Tama.VaultAccess.RecoveryPassphrase.v1|{vaultId}");

    private static TrustedDeviceRecord CloneTrustedDevice(TrustedDeviceRecord device) => new()
    {
        Id = device.Id,
        Name = device.Name,
        Kind = device.Kind,
        RegisteredAt = device.RegisteredAt,
        LastSeenAt = device.LastSeenAt,
        OfflineRecovery = device.OfflineRecovery,
    };

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes);

    private static byte[] Decode(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new CryptographicException("Envelope field missing");
        return Convert.FromBase64String(value);
    }

    private static void ValidateEnvelope(RecoveryEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(envelope.SetId)
            || Decode(envelope.Salt).Length < 16
            || Decode(envelope.Nonce).Length != NonceSize
            || Decode(envelope.Ciphertext).Length < TagSize + KeySize)
            throw new CryptographicException("应急包数据无效");
    }
}

public sealed class VaultAccessFile
{
    public int Version { get; set; }
    public string VaultId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public PasswordEnvelope? Password { get; set; }
    public RecoveryEnvelope? Recovery { get; set; }
    /// <summary>用户输入的恢复口令只派生 KEK；这里保存的是保险库密钥的 AEAD 密文。</summary>
    public PasswordEnvelope? RecoveryPassphrase { get; set; }
    public List<TrustedDeviceRecord> TrustedDevices { get; set; } = [];
}

public sealed class PasswordEnvelope
{
    public string Salt { get; set; } = "";
    public int Iterations { get; set; }
    public string Nonce { get; set; } = "";
    public string Ciphertext { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class RecoveryEnvelope
{
    public string SetId { get; set; } = "";
    public string Salt { get; set; } = "";
    public string Nonce { get; set; } = "";
    public string Ciphertext { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TrustedDeviceRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    /// <summary>纯离线扫码配对生成的保险库密钥封装；不包含配对码明文。</summary>
    public OfflineRecoveryEnvelope? OfflineRecovery { get; set; }
}

public sealed class OfflineRecoveryEnvelope
{
    public string OfferId { get; set; } = "";
    public string VaultTag { get; set; } = "";
    public string Salt { get; set; } = "";
    public string Nonce { get; set; } = "";
    public string Ciphertext { get; set; } = "";
}

public sealed record AccessStatusSnapshot(
    bool HasAccessFile,
    string? VaultId,
    bool HasRecovery,
    DateTimeOffset? RecoveryCreatedAt,
    bool HasRecoveryPassphrase,
    DateTimeOffset? RecoveryPassphraseCreatedAt,
    int TrustedDeviceCount,
    int OfflineTrustedDeviceCount);
