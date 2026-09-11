using Tama.Core.Interfaces;

namespace Tama.Core.Contracts;

/// <summary>
/// 账号与门禁：主密码的设置/解锁/锁定，以及 Bitwarden 账号管理。
/// 实现：<c>Tama.Services.Auth.AuthService</c>。
/// </summary>
public interface IAuthApi
{
    /// <summary>当前门禁状态：是否已设置主密码、是否已解锁、是否允许生物识别解锁。</summary>
    Task<AuthStatusResponse> Status();

    /// <summary>首次设置主密码：建库 + 写入指纹盐。已设置时抛 <see cref="Exceptions.TamaException"/>。</summary>
    Task Setup(SetupRequest req);

    /// <summary>用主密码解锁；密码错误抛 <see cref="Exceptions.TamaException"/>。</summary>
    Task Unlock(UnlockRequest req);

    /// <summary>立即锁定（清空内存中的数据库密钥）。</summary>
    void Lock();

    /// <summary>Bitwarden 登录：校验主密码 → 取 token → 首次全量同步并落缓存。</summary>
    Task<BitwardenLoginResponse> Login(LoginRequest req);

    /// <summary>已保存账号列表（不含 token 明文）。</summary>
    Task<List<AccountSummaryDto>> Accounts();

    /// <summary>手动添加账号（已有 access token 的场景）。</summary>
    Task<AuthAddAccountResponse> AddAccount(AddAccountRequest req);

    /// <summary>删除账号及其同步缓存。</summary>
    Task DeleteAccount(string accountId);

    /// <summary>
    /// 立即从服务器拉取整个密码库并落进本地（账号页的"立即同步"）。
    /// 这是**下载**方向的唯一入口：写入方向（本机改动 → 服务器）走 IVaultApi + 后台 SyncWorker。
    /// 只传账号 Id，密钥材料由服务自己从账号里解出来。
    /// </summary>
    Task<VaultSyncResult> SyncNow(string accountId);
}

// === 请求 ===

/// <summary>设置主密码。</summary>
public sealed record SetupRequest
{
    public string Password { get; init; } = "";
}

/// <summary>解锁。</summary>
public sealed record UnlockRequest
{
    public string Password { get; init; } = "";
}

/// <summary>Bitwarden 登录。</summary>
public sealed record LoginRequest
{
    public string Email { get; init; } = "";
    public string MasterPassword { get; init; } = "";

    /// <summary>两步验证码 / 新设备邮箱验证码（第一步不需要，服务端要求时在第二步带上）。</summary>
    public string? TwoFactorCode { get; init; }

    /// <summary>
    /// 两步验证的提供方 Id（0=验证器 App、1=邮箱…），来自第一步响应里的
    /// <c>TwoFactorProviders</c> 的键。提交验证码时要原样带回去。
    /// </summary>
    public int? TwoFactorProvider { get; init; }

    /// <summary>
    /// 这次的码是**"新设备登录验证"**的邮箱码，而不是账号自身的两步验证。
    /// 两者提交字段不同（<c>newDeviceOtp</c> vs <c>twoFactorToken</c>），搞混就是"输了码也登不上"。
    /// </summary>
    public bool NewDeviceVerification { get; init; }
}

/// <summary>手动添加账号：客户端已自行完成登录与密钥派生，这里直接落库。</summary>
public sealed record AddAccountRequest
{
    public string Email { get; init; } = "";
    public string Type { get; init; } = "bitwarden";
    public string? ServerUrl { get; init; }
    public string AccessToken { get; init; } = "";
    public string? RefreshToken { get; init; }
    public string EncryptionKey { get; init; } = "";
    public string? RawTokenKey { get; init; }
    public string? DerivedEncKey { get; init; }
    public string? DerivedMacKey { get; init; }
    public int KdfIterations { get; init; }
}

// === 响应 ===

/// <summary>门禁状态。<c>BioUnlockAllowed</c> 为 false 时解锁页不显示生物识别入口（重验证期/未配置）。</summary>
public sealed record AuthStatusResponse(bool IsSetup, bool IsUnlocked, bool BioUnlockAllowed);

/// <summary>账号列表项（不含任何密钥明文）。</summary>
public sealed record AccountSummaryDto(
    string Id,
    string Email,
    string Type,
    string? ServerUrl,
    bool HasToken,
    DateTime CreatedAt);

/// <summary>新增账号后返回其本地 Id。</summary>
public sealed record AuthAddAccountResponse(string Id);

// ============================================================================
// 拉取同步的契约。
//
// 2026-09-11 精简：原先这里有 5 个类型服务另外 5 个无调用方的方法，逐条核对后确认
// 与既有实现完全重复或纯转发，方法连同 DTO 一并删除。
// 2026-09-12：这条路径**接上了 UI**（账号页「立即同步」→ IAuthApi.SyncNow），
// 现在它是唯一的"下载"入口；RefreshRequest 是它的低层重载（吃密钥材料，方便测试与定时同步）。
// ============================================================================

/// <summary>
/// 一次拉取同步的结果，给 UI 报数用。
/// <c>Skipped</c> 不是"没有"而是"本地模型装不下、明确没导"的条目名——
/// 悄悄少几条比报个数难查得多（典型：身份信息里的多段地址，见 CipherMapper）。
/// </summary>
public sealed record VaultSyncResult(int Imported, int Updated, int Removed, List<string> Skipped)
{
    public int Total => Imported + Updated + Removed;

    /// <summary>
    /// 这一轮**什么都没做**：另一次全量拉取正在跑（进程级闸门挡住了），本次直接跳过。
    /// 为什么不用 <c>new VaultSyncResult(0,0,0,[])</c>：那看起来像"跑过了、结果是零"，
    /// 界面上就会显示一句骗人的"同步完成：新增 0 · 更新 0 · 删除 0"。
    /// </summary>
    public static VaultSyncResult AlreadyRunning { get; } = new(0, 0, 0, new List<string>()) { Busy = true };

    /// <summary>真见 <see cref="AlreadyRunning"/>。</summary>
    public bool Busy { get; init; }
}

/// <summary>拉取同步请求（低层重载用：直接携带密钥材料）。</summary>
public sealed record RefreshRequest
{
    public string AccountId { get; init; } = "";
    public string AccessToken { get; init; } = "";
    public string EncryptionKey { get; init; } = "";
    public string? DerivedEncKey { get; init; }
    public string? DerivedMacKey { get; init; }
}
