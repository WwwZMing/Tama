using System.Text.Json;
using System.Text.Json.Serialization;
using Tama.Core;
using Tama.Core.Interfaces;

namespace Tama.Services.Auth;

/// <summary>
/// 扫码设备本地保存的“另一台设备”恢复凭据。恢复码用平台生物识别密钥再次加密，
/// 文件里没有可直接使用的明文；整个文件只在本机，不参与同步。
/// </summary>
public sealed class OfflineTrustedDeviceLinkStore
{
    private const string PayloadPrefix = "offline-device-link-v1:";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IBiometricService _bio;
    private readonly string _path;
    private readonly object _gate = new();

    public OfflineTrustedDeviceLinkStore(IBiometricService bio)
        : this(bio, AppPaths.TrustedDeviceLinksPath) { }

    internal OfflineTrustedDeviceLinkStore(IBiometricService bio, string path)
    {
        _bio = bio;
        _path = path;
    }

    public IReadOnlyList<TrustedDeviceLink> List()
    {
        lock (_gate)
        {
            return LoadCore().Links
                .OrderByDescending(link => link.PairedAt)
                .Select(ToPublic)
                .ToArray();
        }
    }

    public async Task<string> Save(
        string sourceDeviceId, string sourceDeviceName, string vaultTag,
        string mutualCode, string pairingCode)
    {
        if (!_bio.IsAvailable())
            throw new InvalidOperationException("当前设备没有可用的系统验证，不能保存离线恢复码");
        if (!await _bio.AuthenticateAsync(
                "保存离线恢复码", "验证系统身份以把这台设备加入可信设备"))
            throw new InvalidOperationException("系统验证未通过");

        var normalizedCode = OfflineTrustedDeviceCodec.NormalizePairingCode(pairingCode);
        if (normalizedCode.Length != 16)
            throw new InvalidOperationException("配对码格式无效");

        _bio.SetupKey();
        var encrypted = _bio.EncryptPassword(PayloadPrefix + normalizedCode)
                        ?? throw new InvalidOperationException("系统密钥不可用，恢复码未保存");

        lock (_gate)
        {
            var file = LoadCore();
            var existing = file.Links.FirstOrDefault(link => link.SourceDeviceId == sourceDeviceId);
            if (existing is null)
            {
                existing = new DeviceLinkRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    SourceDeviceId = sourceDeviceId,
                };
                file.Links.Add(existing);
            }

            existing.SourceDeviceName = string.IsNullOrWhiteSpace(sourceDeviceName)
                ? "另一台设备"
                : sourceDeviceName.Trim();
            existing.VaultTag = vaultTag;
            existing.PairedAt = DateTimeOffset.UtcNow;
            existing.MutualCode = mutualCode;
            existing.EncryptedPairingCode = encrypted;
            SaveCore(file);
            return existing.Id;
        }
    }

    public async Task<TrustedDeviceRecoveryCode> Reveal(string linkId)
    {
        DeviceLinkRecord? record;
        lock (_gate)
        {
            record = LoadCore().Links.FirstOrDefault(link => link.Id == linkId);
        }

        if (record is null)
            throw new InvalidOperationException("找不到这条可信设备记录");
        if (!await _bio.AuthenticateAsync(
                "查看离线恢复码", $"验证系统身份以恢复 {record.SourceDeviceName}"))
            throw new InvalidOperationException("系统验证未通过");

        var plain = _bio.DecryptPassword(record.EncryptedPairingCode);
        if (plain?.StartsWith(PayloadPrefix, StringComparison.Ordinal) != true)
            throw new InvalidOperationException("离线恢复码无法解密");

        var normalized = OfflineTrustedDeviceCodec.NormalizePairingCode(plain[PayloadPrefix.Length..]);
        if (normalized.Length != 16)
            throw new InvalidOperationException("离线恢复码格式无效");

        return new TrustedDeviceRecoveryCode(
            record.Id,
            record.SourceDeviceName,
            OfflineTrustedDeviceCodec.FormatCode(normalized));
    }

    public void Remove(string linkId)
    {
        lock (_gate)
        {
            var file = LoadCore();
            if (file.Links.RemoveAll(link => link.Id == linkId) == 0) return;
            SaveCore(file);
        }
    }

    private DeviceLinkFile LoadCore()
    {
        if (!File.Exists(_path)) return new DeviceLinkFile();
        try
        {
            var file = JsonSerializer.Deserialize<DeviceLinkFile>(
                File.ReadAllText(_path), JsonOptions);
            return file is { Version: 1 } ? file : new DeviceLinkFile();
        }
        catch
        {
            return new DeviceLinkFile();
        }
    }

    private void SaveCore(DeviceLinkFile file)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }

    private static TrustedDeviceLink ToPublic(DeviceLinkRecord link)
        => new(link.Id, link.SourceDeviceId, link.SourceDeviceName, link.VaultTag, link.PairedAt);

    private sealed class DeviceLinkFile
    {
        public int Version { get; set; } = 1;
        public List<DeviceLinkRecord> Links { get; set; } = [];
    }

    private sealed class DeviceLinkRecord
    {
        public string Id { get; set; } = "";
        public string SourceDeviceId { get; set; } = "";
        public string SourceDeviceName { get; set; } = "";
        public string VaultTag { get; set; } = "";
        public DateTimeOffset PairedAt { get; set; }
        public string MutualCode { get; set; } = "";
        public string EncryptedPairingCode { get; set; } = "";
    }
}
