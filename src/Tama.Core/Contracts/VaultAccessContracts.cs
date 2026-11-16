namespace Tama.Core.Contracts;

/// <summary>
/// 多入口解锁与恢复。所有入口最终只解开同一条保险库密钥；数据库本身不因新增入口而重加密。
/// </summary>
public interface IVaultAccessApi
{
    Task<VaultAccessStatus> Status();

    /// <summary>
    /// 生成一套尚未生效的 2/3 应急包。返回的三份 share 必须由调用方分别保存；
    /// 全部保存成功后调用 <see cref="CommitRecoveryKit"/> 才会让旧应急包失效。
    /// </summary>
    Task<RecoveryKitDto> CreateRecoveryKit();

    Task CommitRecoveryKit(CommitRecoveryKitRequest request);

    /// <summary>用任意两份应急份额打开保险库，并立即设置新主密码。</summary>
    Task Recover(RecoverVaultRequest request);

    /// <summary>
    /// 用用户输入的恢复口令包装保险库密钥。磁盘上只保存派生密钥加密后的密文，
    /// 不保存口令明文，也不要求用户另外保存三份文件。
    /// </summary>
    Task SetRecoveryPassphrase(SetRecoveryPassphraseRequest request);

    /// <summary>用恢复口令解出保险库密钥，并立即设置新主密码。</summary>
    Task RecoverWithPassphrase(RecoverWithPassphraseRequest request);

    void ClearRecoveryPassphrase();

    /// <summary>
    /// 创建一次纯离线扫码配对。返回的二维码可被另一台设备的系统相机扫描；
    /// 配对码不进入二维码，必须由用户手动输入，双方再用互认码核对。
    /// </summary>
    Task<TrustedDevicePairingOffer> CreateTrustedDevicePairingOffer(
        CreateTrustedDevicePairingOfferRequest request);

    /// <summary>用户核对双方互认码后，把本次扫码配对正式写入可信设备列表。</summary>
    Task CommitTrustedDevicePairing(CommitTrustedDevicePairingRequest request);

    void CancelTrustedDevicePairing(string offerId);

    /// <summary>在扫码设备上输入配对码并保存离线恢复凭据；全程不联网。</summary>
    Task<TrustedDeviceImportResult> ImportTrustedDevicePairing(
        ImportTrustedDevicePairingRequest request);

    Task<IReadOnlyList<TrustedDeviceLink>> ListTrustedDeviceLinks();

    /// <summary>
    /// 在已锁定的设备上生成扫码登录二维码。二维码只包含设备关系编号和短期有效期，
    /// 不包含主密码、配对码或保险库密钥。
    /// </summary>
    Task<TrustedDeviceLoginOffer> CreateTrustedDeviceLoginOffer(
        CreateTrustedDeviceLoginOfferRequest request);

    /// <summary>在扫码设备上识别登录请求，并找到本机保存的对应可信设备关系。</summary>
    Task<TrustedDeviceLoginPrompt> ReadTrustedDeviceLoginRequest(string qrPayload);

    /// <summary>使用手机显示的离线登录码直接解锁；不修改主密码，也不消耗设备关系。</summary>
    Task UnlockWithTrustedDeviceCode(UnlockWithTrustedDeviceCodeRequest request);

    /// <summary>列出本机参与的全部扫码设备关系，供设置页查看和移除。</summary>
    Task<IReadOnlyList<PairedTrustedDevice>> ListPairedTrustedDevices();

    Task RemovePairedTrustedDevice(PairedTrustedDeviceId device);

    /// <summary>经系统生物识别后显示这台设备保存的恢复码。</summary>
    Task<TrustedDeviceRecoveryCode> RevealTrustedDeviceRecoveryCode(string linkId);

    /// <summary>在忘记主密码的源设备上输入另一台可信设备显示的一次性恢复码。</summary>
    Task RecoverWithTrustedDeviceCode(RecoverWithTrustedDeviceCodeRequest request);

    void RemoveTrustedDevice(string deviceId);

    /// <summary>把本机系统验证（Windows Hello / 指纹 / Android BiometricPrompt）绑定为 Passkey 解锁。</summary>
    Task ConfigurePasskey(ConfigurePasskeyRequest request);

    Task<bool> UnlockWithPasskey();

    void ClearPasskey();

    /// <summary>把一份应急份额写入当前设备的系统密钥保护存储，作为“受信设备份额”。</summary>
    Task ImportDeviceShare(ImportDeviceShareRequest request);

    /// <summary>经系统验证后导出本机保存的受信设备份额；没有时返回 null。</summary>
    Task<string?> ExportDeviceShare();

    void ClearDeviceShare();
}

public sealed record VaultAccessStatus(
    bool HasRecovery,
    DateTimeOffset? RecoveryCreatedAt,
    bool HasRecoveryPassphrase,
    DateTimeOffset? RecoveryPassphraseCreatedAt,
    bool PasskeyAvailable,
    bool PasskeyConfigured,
    bool HasDeviceShare,
    int TrustedDeviceCount,
    int OfflineTrustedDeviceCount);

public sealed record RecoveryKitDto(
    IReadOnlyList<string> Shares,
    string EnvelopeJson);

public sealed record CommitRecoveryKitRequest
{
    public string EnvelopeJson { get; init; } = "";
}

public sealed record RecoverVaultRequest
{
    public string FirstShare { get; init; } = "";
    public string SecondShare { get; init; } = "";
    public string NewPassword { get; init; } = "";
}

public sealed record SetRecoveryPassphraseRequest
{
    public string Passphrase { get; init; } = "";
}

public sealed record RecoverWithPassphraseRequest
{
    public string Passphrase { get; init; } = "";
    public string NewPassword { get; init; } = "";
}

public sealed record CreateTrustedDevicePairingOfferRequest
{
    public string DeviceName { get; init; } = "";
}

public sealed record TrustedDevicePairingOffer(
    string OfferId,
    string QrPayload,
    string PairingCode,
    string MutualCode,
    DateTimeOffset ExpiresAt);

public sealed record CommitTrustedDevicePairingRequest
{
    public string OfferId { get; init; } = "";
    public string MutualCode { get; init; } = "";
}

public sealed record ImportTrustedDevicePairingRequest
{
    public string QrPayload { get; init; } = "";
    public string PairingCode { get; init; } = "";
}

public sealed record TrustedDeviceImportResult(
    string LinkId,
    string SourceDeviceName,
    string MutualCode);

public sealed record TrustedDeviceLink(
    string Id,
    string SourceDeviceId,
    string SourceDeviceName,
    string VaultTag,
    DateTimeOffset PairedAt);

public sealed record CreateTrustedDeviceLoginOfferRequest
{
    public string DeviceName { get; init; } = "";
}

public sealed record TrustedDeviceLoginOffer(
    string QrPayload,
    string SourceDeviceName,
    DateTimeOffset ExpiresAt);

public sealed record TrustedDeviceLoginPrompt(
    string LinkId,
    string SourceDeviceId,
    string SourceDeviceName);

public sealed record UnlockWithTrustedDeviceCodeRequest
{
    public string LoginCode { get; init; } = "";
}

public enum PairedTrustedDeviceSide
{
    /// <summary>本机授权的设备，可以保存本机的离线登录码。</summary>
    AuthorizedDevice,

    /// <summary>本机保存了另一台来源设备的离线登录码。</summary>
    SavedRecoveryLink,
}

public sealed record PairedTrustedDevice(
    string Id,
    PairedTrustedDeviceSide Side,
    string Name,
    DateTimeOffset PairedAt);

public sealed record PairedTrustedDeviceId(
    string Id,
    PairedTrustedDeviceSide Side);

public sealed record TrustedDeviceRecoveryCode(
    string LinkId,
    string SourceDeviceName,
    string Code);

public sealed record RecoverWithTrustedDeviceCodeRequest
{
    public string RecoveryCode { get; init; } = "";
    public string NewPassword { get; init; } = "";
}

public sealed record ConfigurePasskeyRequest
{
    public string CurrentPassword { get; init; } = "";
    public string DeviceName { get; init; } = "";
}

public sealed record ImportDeviceShareRequest
{
    public string ShareText { get; init; } = "";
    public string DeviceName { get; init; } = "";
}
