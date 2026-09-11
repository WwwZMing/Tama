using Tama.Core.Models;

namespace Tama.Services.Sync;

/// <summary>
/// 待推送条目的重试节奏。
///
/// 抽成纯函数是为了能被单测直接钉住——旧实现有两处会烂尾：
///   · `failed` 被排除在 worker 的查询之外，等于**永久放弃**（用户那条改动再也推不上去）；
///   · `RetryCount` 只增不减，用户重新编辑后一试就再次 failed，等于永远卡住。
/// 现在的约定：失败照退避继续重试，用户重新编辑 = 重置预算（见 CipherService.ApplyLocalUpdateAsync）。
/// </summary>
public static class SyncBackoff
{
    /// <summary>最大重试间隔。5 秒一轮的 tick 直接撞失败条目会把服务端打爆，日志也没法看。</summary>
    public static readonly TimeSpan Max = TimeSpan.FromHours(1);

    /// <summary>重试间隔：5s × 2^RetryCount，封顶 <see cref="Max"/>。</summary>
    public static TimeSpan Delay(int retryCount)
    {
        var seconds = 5.0 * Math.Pow(2, Math.Clamp(retryCount, 0, 20));
        return TimeSpan.FromSeconds(Math.Min(seconds, Max.TotalSeconds));
    }

    /// <summary>现在该不该再试一次。</summary>
    public static bool IsDue(Cipher cipher, DateTime nowUtc) => IsDue(cipher.RetryCount, cipher.LastAttempt, nowUtc);

    /// <summary>
    /// 现在该不该再试一次（文件夹队列同用；两边的退避语义必须一致，
    /// 否则"条目 5 秒、文件夹 5 分钟"这种差异只会让人误判成卡住）。
    /// </summary>
    public static bool IsDue(Folder folder, DateTime nowUtc) => IsDue(folder.RetryCount, folder.LastAttempt, nowUtc);

    public static bool IsDue(int retryCount, DateTime? lastAttempt, DateTime nowUtc)
    {
        if (lastAttempt == null) return true;   // 从没试过
        return nowUtc - lastAttempt.Value >= Delay(retryCount);
    }
}
