namespace Tama.Core.Contracts;

/// <summary>
/// 安全报告（Watchtower）：弱密码 / 重复使用 / 撞库泄露 / 无 2FA / 明文 HTTP。
/// 实现：<c>Tama.Services.Watchtower.WatchtowerService</c>。
/// </summary>
public interface IWatchtowerApi
{
    /// <summary>
    /// 生成（或命中 12 小时缓存返回）安全报告。
    /// 分析范围 = 本地条目 ∪ Bitwarden 云端缓存；撞库检测走 HIBP k-anonymity（只发 SHA-1 前 5 位）。
    /// </summary>
    Task<WatchtowerReportResponse> Report(string? accountId = null);
}

/// <summary>报告主体。<c>Score</c> 为 0-100（无条目时满分）。</summary>
public sealed record WatchtowerReportResponse(double Score, List<SecurityIssueDto> Issues, WatchtowerStatsDto Stats);

/// <summary>
/// 单条问题。<c>Type</c> 取值：weak / reused / breached / no2fa / unsecure。
/// <c>CipherId</c> 保持字符串：报告会以 JSON 形式缓存进数据库，历史缓存里就是字符串。
/// </summary>
public sealed record SecurityIssueDto(
    string CipherId,
    string CipherName,
    string Type,
    string Severity,
    string Description);

/// <summary>问题计数（UI 顶部数字矩阵）。</summary>
public sealed record WatchtowerStatsDto(
    int TotalItems,
    int WeakPasswords,
    int ReusedPasswords,
    int BreachedPasswords,
    int No2Fa,
    int Unsecure);
