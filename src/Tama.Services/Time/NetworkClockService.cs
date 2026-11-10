using Serilog;
using Tama.Core.Contracts;
using Tama.Core.Exceptions;

namespace Tama.Services.Time;

/// <summary>
/// 联网校时：并发读几个站点的 HTTP <c>Date</c> 响应头，取中位数作为网络时间，
/// 算出"本机 − 网络"的偏差并缓存。TOTP 用 <see cref="UnixNow"/> 计算时间步，
/// 于是系统时钟慢了/快了几小时，验证码仍然是对的；偏差过大时由界面提示用户去修时钟。
///
/// 为什么不用 NTP：TOTP 只需要秒级精度，而 HTTP Date 头到处都有、不需要额外端口/权限；
/// 也不依赖某个具体服务可用（三个源，任一个活着就行）。
/// </summary>
public sealed class NetworkClockService : IClockApi, IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<NetworkClockService>();

    /// <summary>
    /// 校时站点：国内、国外各取一个，避免单点被墙/被限流。
    /// ⚠ 不要换成 <c>api.github.com</c>：它对 HEAD 回 403 且**不带 Date 头**（实测），当源等于白给。
    /// </summary>
    private static readonly string[] DefaultSources =
    {
        "https://www.baidu.com",
        "https://www.bing.com",
        "https://www.cloudflare.com",
    };

    /// <summary>单个源最多等多久——校时不该让界面等超过半秒级，慢源直接放弃。</summary>
    private static readonly TimeSpan PerSourceTimeout = TimeSpan.FromSeconds(4);

    /// <summary>进程内共享的 HttpClient：校时请求很短，但每次 new 一个会耗光连接。</summary>
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(4),
    })
    {
        Timeout = TimeSpan.FromSeconds(6),
    };

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly string[] _sources;
    private readonly NetworkClockOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>volatile：写在校时线程，读在 UI 线程；快照本身不可变，换引用即可。</summary>
    private volatile ClockSyncSnapshot? _snapshot;

    public NetworkClockService(
        HttpClient? http = null,
        TimeProvider? time = null,
        IEnumerable<string>? sources = null,
        NetworkClockOptions? options = null)
    {
        _http = http ?? SharedHttp;
        _time = time ?? TimeProvider.System;
        _options = options ?? new NetworkClockOptions();

        var list = (sources ?? DefaultSources).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        _sources = list.Length > 0 ? list : DefaultSources;
    }

    /// <summary>本次运行的校时策略（测试里可以调短 TTL）。</summary>
    public NetworkClockOptions Options => _options;

    public ClockSyncSnapshot? LastSnapshot => _snapshot;

    /// <summary>校时成功事件；订阅方（横幅 / 设置页）在后台线程收到，需要自己切回 UI 线程。</summary>
    public event Action<ClockSyncSnapshot>? Synced;

    /// <summary>
    /// 网络校正后的当前 Unix 秒。快照把"本机 − 网络"记成常数偏差，之后用本机流逝时间推算，
    /// 所以校时之后即使系统时钟被再次拨动，推算结果也只在偏差被改动的瞬间失效
    /// （下一次 <see cref="SyncAsync"/> 会纠正）。
    /// </summary>
    public long UnixNow
    {
        get
        {
            var snapshot = _snapshot;
            var now = _time.GetUtcNow();
            return (snapshot is null ? now : now - snapshot.Skew).ToUnixTimeSeconds();
        }
    }

    public async Task<ClockSyncSnapshot> SyncAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        var cached = _snapshot;
        if (!force && IsFresh(cached)) return cached!;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 等锁期间别人可能刚校完：再判一次，避免并发请求把站点打两遍。
            cached = _snapshot;
            if (!force && IsFresh(cached)) return cached!;

            var localBefore = _time.GetUtcNow();
            var samples = await Task.WhenAll(_sources.Select(s => FetchDateAsync(s, cancellationToken)))
                .ConfigureAwait(false);

            var networkTimes = samples
                .Where(t => t.HasValue)
                .Select(t => t!.Value)
                .OrderBy(t => t)
                .ToArray();

            if (networkTimes.Length == 0)
                throw new TamaException("无法联网获取时间：请检查网络后重试");

            // 取中位数：单个站点响应慢/被缓存/时钟本身有鬼都不会把结果带偏。
            var networkUtc = networkTimes[networkTimes.Length / 2];
            var localAfter = _time.GetUtcNow();
            // 请求是并发的，网络时间约等于"发出与收到之间的中点"这一刻的服务器时间。
            var localMid = localBefore + (localAfter - localBefore) / 2;

            var snapshot = new ClockSyncSnapshot(
                NetworkUtc: networkUtc,
                LocalUtc: localMid,
                Skew: localMid - networkUtc,
                CheckedAt: localAfter,
                Sources: networkTimes.Length);

            _snapshot = snapshot;

            // 通知界面（横幅 / 设置页）：复制验证码这类"强制校时"之后，提示要立刻更新。
            // 订阅方抛异常不能打断校时——它只是展示层的 bug。
            try { Synced?.Invoke(snapshot); }
            catch (Exception ex) { Log.Warning(ex, "Clock sync subscriber failed"); }

            if (snapshot.IsDrifted)
            {
                // 文件日志只收 Information 及以上，所以这条必须用 Warning：
                // "验证码为什么全错"这种事故，日志里必须留得下证据。
                Log.Warning(
                    "System clock skew: local {LocalUtc:o} vs network {NetworkUtc:o} = {SkewSeconds:F0}s ({Sources}/{Total} sources)",
                    snapshot.LocalUtc, snapshot.NetworkUtc, snapshot.Skew.TotalSeconds, snapshot.Sources, _sources.Length);
            }
            else
            {
                // 正常校时每 5 分钟一次，只进控制台，不落盘（避免日志噪音）。
                Log.Debug("Network time synced: skew {SkewMs:F0}ms ({Sources}/{Total} sources)",
                    snapshot.Skew.TotalMilliseconds, snapshot.Sources, _sources.Length);
            }

            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsFresh(ClockSyncSnapshot? snapshot) =>
        snapshot is not null && _time.GetUtcNow() - snapshot.CheckedAt < _options.CacheTtl;

    /// <summary>读一个站点的 <c>Date</c> 头；超时/网络错误都只当"这个源不可用"，不抛给上层。</summary>
    private async Task<DateTimeOffset?> FetchDateAsync(string url, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(PerSourceTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            // 405/403 也照样带 Date 头，所以不检查状态码。
            return response.Headers.Date;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Debug("Clock source timed out: {Url}", url);
            return null;
        }
        catch (HttpRequestException ex)
        {
            Log.Debug(ex, "Clock source unavailable: {Url}", url);
            return null;
        }
    }

    public void Dispose() => _gate.Dispose();
}
