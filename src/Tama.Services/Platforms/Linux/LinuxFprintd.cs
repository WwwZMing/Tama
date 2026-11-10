using System.Text.Json;
using Serilog;

namespace Tama.Services.Platforms.Linux;

/// <summary>
/// fprintd 交互——Linux 上指纹识别的唯一标准栈（libfprint + fprintd，走 **system** bus）。
///
/// 分工：
///   · 探测（有没有设备、当前用户录没录过指纹）走 <c>busctl --json=short</c> —— 输出是机器可读的 JSON；
///   · 验证动作委托给 <c>fprintd-verify</c> —— 它是 fprintd 官方的参考客户端，自己处理
///     claim / release / 重试，退出码 0 = 匹配。比我们手搓一遍 D-Bus 状态机可靠得多。
///
/// 注意：不传 <c>-f</c>，让 fprintd 自动挑一根**已录入**的手指 —— 用户用哪根都该能解锁。
/// </summary>
internal static class LinuxFprintd
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(LinuxFprintd));

    private const string Service = "net.reactivated.Fprint";
    private const string ManagerPath = "/net/reactivated/Fprint/Manager";
    private const string ManagerIface = "net.reactivated.Fprint.Manager";
    private const string DeviceIface = "net.reactivated.Fprint.Device";

    /// <summary>
    /// 同一时刻只允许一次验证。fprintd 的一个设备同时只能被一个客户端 claim，
    /// 并发验证会拿到 "Device was already claimed" 而不是让用户按两次。
    /// </summary>
    private static readonly SemaphoreSlim VerifyGate = new(1, 1);

    /// <summary>
    /// 退出得比这还快 = 压根没进入"等用户按手指"的阶段。实测这种秒退只有一个原因：
    /// 上一个验证进程刚被杀掉，fprintd 还没来得及释放设备，新的这次 claim 失败
    /// （<c>net.reactivated.Fprint.Error.AlreadyInUse: Device was already claimed</c>）。
    /// </summary>
    private static readonly TimeSpan FastFailThreshold = TimeSpan.FromSeconds(1);

    /// <summary>claim 冲突后的重试间隔。实测释放延迟在 0.3s 以内，留一倍余量。</summary>
    private static readonly TimeSpan ClaimRetryDelay = TimeSpan.FromMilliseconds(400);

    private const int MaxClaimAttempts = 3;

    private static string UserName => Environment.UserName;

    /// <summary>本机"现在就能用指纹"：有设备 + 当前用户录过至少一根手指。</summary>
    public static bool IsAvailable()
    {
        if (!OperatingSystem.IsLinux()) return false;
        if (!LinuxShell.Exists("busctl") || !LinuxShell.Exists("fprintd-verify")) return false;
        return GetEnrolledFingers().Count > 0;
    }

    /// <summary>当前用户已录入的手指名（如 right-index-finger）；任何异常都返回空表。</summary>
    public static IReadOnlyList<string> GetEnrolledFingers()
    {
        var device = GetDefaultDevice();
        if (device is null) return Array.Empty<string>();

        if (!LinuxShell.TryRun("busctl", new[]
            {
                "--system", "--json=short", "call",
                Service, device, DeviceIface,
                "ListEnrolledFingers", "s", UserName,
            }, out var result, timeout: TimeSpan.FromSeconds(5)))
        {
            Log.Debug("fprintd: ListEnrolledFingers 调用失败");
            return Array.Empty<string>();
        }

        // 形如 {"type":"as","data":[["right-index-finger"]]}
        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            var fingers = doc.RootElement.GetProperty("data")[0];
            var list = new List<string>();
            foreach (var f in fingers.EnumerateArray())
            {
                var name = f.GetString();
                if (!string.IsNullOrEmpty(name)) list.Add(name);
            }
            return list;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "fprintd: 解析 ListEnrolledFingers 输出失败：{Out}", result.StdOut);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// 弹指纹验证：等用户按手指，匹配返回 true。
    /// 超时/取消/不匹配/设备错误一律 false（**绝不**因为"验证通道有问题"就放行）。
    ///
    /// 为什么里面有个重试循环：<c>fprintd-verify</c> 非 0 退出有两种完全相反的含义 ——
    ///   · 跑满超时后才退 → 用户没按 / 没匹配上，如实返回 false；
    ///   · **秒退** → 根本没起来，是设备被上一个还挂在 fprintd 里的客户端占着
    ///     （实测：kill 掉上一次验证后立刻再验证，必然 AlreadyInUse）。
    /// 不区分就会把第二种误报成"验证未通过"，用户明明没按错却看到失败，而且白丢一次机会。
    /// </summary>
    public static async Task<bool> VerifyAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux()) return false;

        await VerifyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 1; attempt <= MaxClaimAttempts; attempt++)
            {
                // 不传 -f：让 fprintd 自动挑一根已录入的手指 —— 用户用哪根都该能解锁
                var r = await LinuxShell.RunCheckAsync("fprintd-verify", new[] { UserName }, timeout, ct)
                    .ConfigureAwait(false);

                if (r.Ok)
                {
                    Log.Information("fprintd verify: 匹配成功（{Ms}ms）", (int)r.Elapsed.TotalMilliseconds);
                    return true;
                }

                if (r.Elapsed < FastFailThreshold && attempt < MaxClaimAttempts)
                {
                    Log.Debug("fprintd 设备未释放（{Ms}ms 秒退，第 {N} 次），{Delay}ms 后重试：{Err}",
                        (int)r.Elapsed.TotalMilliseconds, attempt,
                        (int)ClaimRetryDelay.TotalMilliseconds, r.StdErr.Trim());
                    try
                    {
                        await Task.Delay(ClaimRetryDelay, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }
                    continue;
                }

                Log.Information("fprintd verify: 未匹配 / 超时（{Ms}ms，第 {N} 次）：{Err}",
                    (int)r.Elapsed.TotalMilliseconds, attempt, r.StdErr.Trim());
                return false;
            }
            return false;
        }
        finally
        {
            VerifyGate.Release();
        }
    }

    /// <summary>默认指纹设备对象路径；没有设备时返回 null。</summary>
    private static string? GetDefaultDevice()
    {
        if (!LinuxShell.TryRun("busctl", new[]
            {
                "--system", "--json=short", "call",
                Service, ManagerPath, ManagerIface, "GetDefaultDevice",
            }, out var result, timeout: TimeSpan.FromSeconds(5)))
        {
            return null;
        }

        // 形如 {"type":"o","data":["/net/reactivated/Fprint/Device/0"]}
        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            var data = doc.RootElement.GetProperty("data");
            return data.GetArrayLength() > 0 ? data[0].GetString() : null;
        }
        catch (Exception ex)
        {
            // 没有指纹设备时 busctl 打的是 "Call failed: ..." 这种给人看的文本，不是 JSON —— 预期路径
            Log.Debug(ex, "fprintd: 未取到默认设备（本机可能没有指纹阅读器）");
            return null;
        }
    }
}
