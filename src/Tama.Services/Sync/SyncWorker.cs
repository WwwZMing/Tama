using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tama.Core.Interfaces;
using Tama.Data.Database;
using Tama.Services.Auth;
using Serilog;

namespace Tama.Services.Sync;

/// <summary>
/// 后台同步循环。宿主注册：MAUI 与 Tama.Api 都注册（NativeHost 不需要）。
///
/// 一轮做两件事：
///   ① **推** —— 消费离线写入队列（pending/failed 的条目与文件夹），每轮都推；
///   ② **拉** —— 全量拉一次云端库，让"别处改的东西"自己出现，每 <see cref="PullInterval"/> 一次。
///
/// ② 就是"本机与云端保持一致"的另一半：Bitwarden 既没有增量接口、我们也没有 WebSocket 通道
/// （官方客户端靠 Live sync 推单条增量），所以等价物只能是定时全量拉。拉取本身是安全的——
/// 拉取路径有两条护栏（未推送的本地改动整条跳过、孤儿只删已同步的），见 SyncSafetyTests。
/// </summary>
public class SyncWorker : BackgroundService
{
    /// <summary>
    /// 全量拉的间隔。一次约 471 KB / 1–2 秒（198 条实测）→ 10 分钟约 2.8 MB/小时。
    /// 再密就是拿流量与电量换新鲜度，而这个库的主要写入方就是用户自己。
    ///
    /// 可用环境变量 <c>TAMA_SYNC_PULL_MINUTES</c> 覆盖（分钟，支持小数；≤0 表示关掉定时拉）。
    /// 留这个口子是为了**能被实测**：不然"定时拉到底有没有跑"只能等十分钟。
    /// </summary>
    private static readonly TimeSpan PullInterval = ResolvePullInterval();

    private static TimeSpan ResolvePullInterval()
    {
        var raw = Environment.GetEnvironmentVariable("TAMA_SYNC_PULL_MINUTES");
        if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var minutes))
        {
            return minutes <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromMinutes(minutes);
        }
        return TimeSpan.FromMinutes(10);
    }

    private readonly IServiceProvider _services;
    private DateTime _lastPullUtc = DateTime.MinValue;
    private static readonly ILogger Log = Serilog.Log.ForContext<SyncWorker>();

    public SyncWorker(IServiceProvider services)
    {
        _services = services;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.Information("SyncWorker started (push every 5s, full pull every {Minutes}min)", PullInterval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "SyncWorker tick failed");
            }

            await Task.Delay(5000, stoppingToken);
        }

        Log.Information("SyncWorker stopped");
    }

    private async Task TickAsync(CancellationToken ct)
    {
        // 库未解锁（未 setup / 已 lock）时静默跳过：此时访问 vault DB 只会得到
        // "Vault is locked. Unlock first."，不需要每 5 秒刷一次错误日志。
        // 副作用正好是我们想要的：定时拉只在"应用正在用"的时候发生。
        var dbKeyService = _services.GetRequiredService<DatabaseKeyService>();
        if (!dbKeyService.IsUnlocked) return;

        using var scope = _services.CreateScope();

        // ① 推：队列的消费者（条目 + 文件夹，见 PendingSyncProcessor）
        var processor = scope.ServiceProvider.GetRequiredService<PendingSyncProcessor>();
        await processor.RunOnceAsync(ct);

        // ② 拉：定时全量
        if (DateTime.UtcNow - _lastPullUtc < PullInterval) return;

        // 时间戳先打上：一次拉失败不该退化成"每 5 秒重试一次全量拉"
        _lastPullUtc = DateTime.UtcNow;

        var accounts = scope.ServiceProvider.GetRequiredService<BitwardenAccountService>();
        var keys = await accounts.FindBitwardenAccount(null);
        if (keys == null)
        {
            Log.Debug("Periodic pull skipped: no linked Bitwarden account");
            return;
        }

        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
        var result = await auth.SyncNow(keys.Value.accountId);

        if (result.Busy)
        {
            // 另一次拉取（用户手动同步 / 解锁同步）正在跑，它拉完就是最新
            Log.Information("Periodic pull skipped: another vault sync is already running");
            return;
        }

        // 只在**真有变化**时广播：拉取是全量的，每 10 分钟弹一次"已从云端同步"纯属噪音。
        // （Updated 只在服务器 RevisionDate 变过时才计数，见 AuthRefresh 里跳过未变化行那段。）
        if (result.Imported + result.Updated + result.Removed > 0)
        {
            Log.Information("Periodic pull found changes: {Imported} new, {Updated} updated, {Removed} removed",
                result.Imported, result.Updated, result.Removed);
            scope.ServiceProvider.GetRequiredService<IVaultSyncNotifier>().Raise(result);
        }
        else
        {
            // 这一行是"定时拉还活着"的唯一直接证据（10 分钟一条，不算吵）
            Log.Information("Periodic pull: nothing changed");
        }
    }
}
