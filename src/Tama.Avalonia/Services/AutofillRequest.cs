namespace Tama.Avalonia.Services;

/// <summary>
/// Android 自动填充服务的"待处理目标"：用户在别的应用里点了「打开 Tama 自动填充」，
/// 系统把我们的 Activity 拉起来，这里记下当时的域名（或包名），解锁后由保险库页消费一次。
///
/// 为什么是一个独立的小服务而不是静态字段：宿主注入、可读、可测；语义只有"推一条 / 取一条"，
/// 取出即消费（<see cref="Take"/>），不会在每次切页时重复预填。
///
/// 为什么要过期：自动填充在**用户点开下拉菜单的那一刻**就推了目标，但用户可能点了别的凭据、
/// 或者干脆没进 Tama。没有过期的话，下次（哪怕是几小时后）解锁时搜索框会莫名其妙被塞一个域名。
/// 超过 <see cref="MaxAge"/> 的目标按"没有目标"处理。
///
/// 拿不到值的正常原因：进程刚起、还没解锁、用户是从桌面图标进来的——都按"没有目标"处理。
/// </summary>
public sealed class AutofillRequest
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

    private readonly object _gate = new();
    private string? _pending;
    private DateTime _pushedAtUtc;

    /// <summary>登记目标（自动填充服务调用，来自 binder 线程）。空值等于清空。</summary>
    public void Push(string? target)
    {
        var value = string.IsNullOrWhiteSpace(target) ? null : target.Trim();
        lock (_gate)
        {
            _pending = value;
            _pushedAtUtc = DateTime.UtcNow;
        }
    }

    /// <summary>取走并清空目标（UI 调用）。没有就返回 null。</summary>
    public string? Take()
    {
        lock (_gate)
        {
            var value = _pending;
            _pending = null;
            if (value == null) return null;
            return DateTime.UtcNow - _pushedAtUtc > MaxAge ? null : value;
        }
    }
}
