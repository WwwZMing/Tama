namespace Tama.Core.Models;

/// <summary>
/// Watchtower 安全报告缓存（每个 Bitwarden 账号一条）。
/// 避免每次打开报告页都重新计算并批量查询 HIBP。
/// 在 AuthRefresh 同步成功后失效，另设 TTL 兜底。
/// </summary>
public class WatchtowerReportCache
{
    public string AccountId { get; set; } = "";

    public string ReportJson { get; set; } = "";

    public DateTime GeneratedAt { get; set; }
}
