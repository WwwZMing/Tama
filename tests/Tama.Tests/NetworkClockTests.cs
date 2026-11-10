using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Tama.Core.Contracts;
using Tama.Core.Exceptions;
using Tama.Services;
using Tama.Services.Time;
using Xunit;

namespace Tama.Tests;

/// <summary>
/// 网络校时：中位数、偏差语义（本机 − 网络）、TOTP 用的 UnixNow 回落到本机钟、缓存与失败分支。
/// 背景：2026-10-17 本机钟慢 24 小时，TOTP 全被网站拒绝——校时是 TOTP 正确性的前置条件。
/// </summary>
public class NetworkClockTests
{
    private static readonly DateTimeOffset TrueNow = new(2026, 10, 16, 16, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage WithDate(DateTimeOffset date)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Date = date;
        return response;
    }

    private static NetworkClockService CreateService(
        HttpMessageHandler handler, TimeProvider time, params string[] sources) =>
        new(new HttpClient(handler), time, sources, new NetworkClockOptions { CacheTtl = TimeSpan.FromMinutes(5) });

    [Fact]
    public async Task Sync_Takes_Median_And_Reports_Skew_As_Local_Minus_Network()
    {
        var localNow = TrueNow.AddMinutes(-2);          // 本机慢 2 分钟
        var offsets = new Dictionary<string, int>
        {
            ["a"] = -2, ["b"] = 0, ["c"] = 2,
        };
        var handler = new StubHandler(req => WithDate(TrueNow.AddSeconds(offsets[req.RequestUri!.Host])));
        var time = new FixedTime(localNow);
        using var svc = CreateService(handler, time, "https://a", "https://b", "https://c");

        var snapshot = await svc.SyncAsync();

        Assert.Equal(TrueNow, snapshot.NetworkUtc);     // 中位数，不是第一个/最后一个
        Assert.Equal(TimeSpan.FromMinutes(-2), snapshot.Skew);
        Assert.True(snapshot.IsDrifted);
        Assert.Equal(3, snapshot.Sources);
    }

    [Fact]
    public async Task UnixNow_Is_Corrected_By_Skew()
    {
        var localNow = TrueNow.AddHours(-24);           // 实测踩到的场景：慢整整一天
        var handler = new StubHandler(_ => WithDate(TrueNow));
        var time = new FixedTime(localNow);
        using var svc = CreateService(handler, time, "https://a");

        await svc.SyncAsync();

        Assert.Equal(TrueNow.ToUnixTimeSeconds(), svc.UnixNow);

        // 校时后本机钟又走了 10 秒：UnixNow 跟着走，且仍然对的是网络时间
        time.UtcNow = localNow.AddSeconds(10);
        Assert.Equal(TrueNow.AddSeconds(10).ToUnixTimeSeconds(), svc.UnixNow);
    }

    [Fact]
    public void UnixNow_Falls_Back_To_Local_Clock_Without_Snapshot()
    {
        var time = new FixedTime(TrueNow);
        using var svc = CreateService(new StubHandler(_ => WithDate(TrueNow)), time, "https://a");

        Assert.Null(svc.LastSnapshot);
        Assert.Equal(TrueNow.ToUnixTimeSeconds(), svc.UnixNow);
    }

    [Fact]
    public async Task Cached_Snapshot_Is_Reused_Until_Ttl_Expires_And_Force_Refetches()
    {
        var time = new FixedTime(TrueNow);
        var handler = new StubHandler(_ => WithDate(TrueNow));
        using var svc = CreateService(handler, time, "https://a");

        await svc.SyncAsync();
        await svc.SyncAsync();                          // TTL 内：不该再联网
        Assert.Equal(1, handler.Calls);

        time.UtcNow = TrueNow.AddMinutes(10);           // 超过 5 分钟缓存
        await svc.SyncAsync();
        Assert.Equal(2, handler.Calls);

        await svc.SyncAsync(force: true);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task One_Dead_Source_Does_Not_Fail_The_Sync()
    {
        var handler = new StubHandler(req =>
            req.RequestUri!.Host == "dead"
                ? throw new HttpRequestException("offline")
                : WithDate(TrueNow));
        using var svc = CreateService(handler, new FixedTime(TrueNow.AddSeconds(45)), "https://dead", "https://alive");

        var snapshot = await svc.SyncAsync();

        Assert.Equal(1, snapshot.Sources);
        Assert.Equal(TimeSpan.FromSeconds(45), snapshot.Skew);
    }

    [Fact]
    public async Task All_Sources_Failing_Throws_And_Keeps_Last_Snapshot()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("offline"));
        using var svc = CreateService(handler, new FixedTime(TrueNow), "https://a", "https://b");

        await Assert.ThrowsAsync<TamaException>(() => svc.SyncAsync());
        Assert.Null(svc.LastSnapshot);
    }

    [Theory]
    [InlineData(-30, false)]   // 正好 30 秒：TOTP 还容得下
    [InlineData(-31, true)]    // 超过 1 个窗口：网站必然拒绝
    [InlineData(31, true)]
    [InlineData(29, false)]
    public void Drift_Threshold_Is_Thirty_Seconds(int skewSeconds, bool drifted)
    {
        var snapshot = new ClockSyncSnapshot(
            NetworkUtc: TrueNow,
            LocalUtc: TrueNow.AddSeconds(skewSeconds),
            Skew: TimeSpan.FromSeconds(skewSeconds),
            CheckedAt: TrueNow,
            Sources: 3);

        Assert.Equal(drifted, snapshot.IsDrifted);
    }

    [Fact]
    public async Task Synced_Event_Fires_With_The_New_Snapshot()
    {
        using var svc = CreateService(
            new StubHandler(_ => WithDate(TrueNow)), new FixedTime(TrueNow), "https://a");
        ClockSyncSnapshot? received = null;
        svc.Synced += s => received = s;

        var snapshot = await svc.SyncAsync();

        Assert.Same(snapshot, received);
    }

    [Fact]
    public async Task A_Throwing_Subscriber_Does_Not_Break_Sync()
    {
        using var svc = CreateService(
            new StubHandler(_ => WithDate(TrueNow)), new FixedTime(TrueNow), "https://a");
        svc.Synced += _ => throw new InvalidOperationException("bad subscriber");

        var snapshot = await svc.SyncAsync();

        Assert.NotNull(snapshot);
        Assert.Same(snapshot, svc.LastSnapshot);
    }

    [Fact]
    public void Clock_Contract_Is_Registered_And_Resolvable()
    {
        // 守 DI 接线：NetworkClockService 的 HttpClient/TimeProvider 都是可选参数，
        // 容器必须能挑到这个构造函数（挑不到的话运行时直接炸，单测 Service 则看不出来）。
        using var provider = new ServiceCollection().AddTamaServices().BuildServiceProvider();
        using var scope = provider.CreateScope();

        var clock = scope.ServiceProvider.GetRequiredService<IClockApi>();
        Assert.IsType<NetworkClockService>(clock);
    }
}
