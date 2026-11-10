namespace Tama.Core.Contracts;

/// <summary>
/// 网络校时：TOTP 依赖真实时间，而本机时钟可能被关掉的同步/手动改时间带偏
/// （2026-10-17 实测本机慢 24 小时 → 所有网站的验证码都被拒绝）。
///
/// 约定：凡是"现在几点"的领域判断（TOTP、日志时间线…）都用 <see cref="UnixNow"/>，
/// 不要直接 <c>DateTimeOffset.UtcNow</c>。实现：<c>Tama.Services.Time.NetworkClockService</c>。
/// </summary>
public interface IClockApi
{
    /// <summary>
    /// 每次校时成功都会触发（可能在后台线程——订阅方自行 <c>InvokeAsync</c>）。
    /// 用来让"时钟偏差横幅 / 设置页状态"在复制验证码等强制校时之后立刻刷新，
    /// 而不是等下一次定时轮询。⚠ 回调里不要再调 <see cref="SyncAsync"/>（会撞上校时锁）。
    /// </summary>
    event Action<ClockSyncSnapshot>? Synced;

    /// <summary>最近一次成功校时的快照；从未成功过（还没查 / 离线）返回 null。</summary>
    ClockSyncSnapshot? LastSnapshot { get; }

    /// <summary>
    /// 按网络时间计算的当前 Unix 秒。没有校时快照时回落到本机 UTC——
    /// 所以调用方不需要为空值分支，但界面上应当提示"未校时"。
    /// </summary>
    long UnixNow { get; }

    /// <summary>
    /// 联网校时（读多个站点的 HTTP <c>Date</c> 头取中位数）。默认在
    /// <see cref="NetworkClockOptions.CacheTtl"/> 内复用上次结果；<paramref name="force"/> 强制重查。
    /// 所有站点都失败时抛 <c>TamaException</c>（调用方决定提示方式）。
    /// </summary>
    Task<ClockSyncSnapshot> SyncAsync(bool force = false, CancellationToken cancellationToken = default);
}

/// <summary>一次校时的结果。<see cref="Skew"/> = 本机 − 网络（负值 = 本机慢）。</summary>
public sealed record ClockSyncSnapshot(
    DateTimeOffset NetworkUtc,
    DateTimeOffset LocalUtc,
    TimeSpan Skew,
    DateTimeOffset CheckedAt,
    int Sources)
{
    /// <summary>TOTP 只容忍 ±1 个 30 秒窗口，超过这个偏差就必须提示用户。</summary>
    public const int DriftThresholdSeconds = 30;

    /// <summary>本机时钟是否已经偏到"验证码必错"的程度。</summary>
    public bool IsDrifted => Math.Abs(Skew.TotalSeconds) > DriftThresholdSeconds;
}

/// <summary>校时策略（配置项少，单独放一个记录，便于以后加开关/自定义源）。</summary>
public sealed record NetworkClockOptions
{
    /// <summary>多久之内复用上次校时结果。偏差是慢变量，不需要每次算验证码都联网。</summary>
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromMinutes(5);
}
