using System.Diagnostics;

namespace Tama.Services.Platforms.Linux;

/// <summary>
/// 跑外部命令并拿结果——Linux 平台胶水的共用底座。
///
/// 为什么用 CLI 而不是引 D-Bus / libsecret 的托管库：
///   · 这几个能力都有**随系统自带**且稳定的命令行客户端（busctl 属 systemd、fprintd-verify 属 fprintd、
///     secret-tool 属 libsecret），依赖为零；
///   · 本项目是密码管理器，少一个第三方原生依赖就少一份供应链风险；
///   · 探测结果都取**机器可读**的形态（busctl 用 --json=short，其余看退出码），
///     不解析任何给人看的文本——fprintd-list 的文本是 gi18n 翻译过的，中文环境下根本没法解析。
/// </summary>
internal static class LinuxShell
{
    public readonly record struct Result(int ExitCode, string StdOut, string StdErr)
    {
        public bool Ok => ExitCode == 0;
    }

    /// <summary>
    /// 长命令（要等用户操作的那种）的执行结果。
    ///
    /// <see cref="Elapsed"/> 是必需的，不是顺手加的：调用方要靠"退出得太快"来区分
    /// 「根本没起来 / 没能 claim 到资源」和「用户真的没匹配上」——两者都是非 0 退出码，
    /// 但处理方式完全相反（前者应当重试，后者必须如实报失败）。
    /// </summary>
    public readonly record struct CheckResult(bool Ok, TimeSpan Elapsed, string StdOut, string StdErr);

    /// <summary>命令是否存在（走 PATH，避免依赖 shell 的 `which`）。</summary>
    public static bool Exists(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return false;

        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(dir, fileName))) return true;
            }
            catch
            {
                // 单个目录不可读（权限/悬空软链）不该让整次探测失败
            }
        }
        return false;
    }

    /// <summary>同步跑一个短命令（毫秒级探测用）。超时或启动失败返回 false。</summary>
    public static bool TryRun(string fileName, string[] args, out Result result,
        string? stdin = null, TimeSpan? timeout = null)
    {
        result = default;
        if (!OperatingSystem.IsLinux()) return false;

        try
        {
            using var proc = Start(fileName, args, stdin);
            if (proc is null) return false;

            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();

            if (stdin is not null)
            {
                proc.StandardInput.Write(stdin);
                proc.StandardInput.Close();
            }

            var limit = timeout ?? TimeSpan.FromSeconds(5);
            if (!proc.WaitForExit((int)limit.TotalMilliseconds))
            {
                Kill(proc);
                return false;
            }

            result = new Result(proc.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
            return true;
        }
        catch
        {
            // 命令不存在 / 无执行权限 / 环境异常：一律当"不可用"，由调用方降级
            return false;
        }
    }

    /// <summary>
    /// 异步跑一个**可能很久**的命令（用户要慢慢按指纹），退出码 0 = 成功。
    ///
    /// 超时或取消都会杀掉子进程。注意：这里**只能**杀掉——.NET 的 Process.Kill 在 Unix 上是 SIGKILL，
    /// 没法优雅退出；实测 SIGTERM 也一样不会让 fprintd 更快释放设备（释放是由 D-Bus 连接断开驱动的，
    /// 且有一段随机延迟）。所以"杀完之后下一次马上用同一个设备会失败"是**调用方**必须处理的事，
    /// 见 <see cref="CheckResult.Elapsed"/> 的注释与 LinuxFprintd 的重试。
    /// </summary>
    public static async Task<CheckResult> RunCheckAsync(string fileName, string[] args,
        TimeSpan timeout, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        if (!OperatingSystem.IsLinux())
            return new CheckResult(false, sw.Elapsed, "", "not linux");

        try
        {
            using var proc = Start(fileName, args, stdin: null);
            if (proc is null)
                return new CheckResult(false, sw.Elapsed, "", "process start failed");

            // 必须把两条流读干净：目标程序会逐条打印进度，
            // 管道写满（64KB）时子进程会阻塞在 write 上，我们却永远等不到它退出。
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(proc);
                try { await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
                catch { /* 已杀，忽略 */ }

                // 被杀的程序输出常随连接关闭一起丢，能读多少算多少（读不回来不该让整次调用抛异常）
                return new CheckResult(false, sw.Elapsed, await TryRead(stdout), await TryRead(stderr));
            }

            return new CheckResult(proc.ExitCode == 0, sw.Elapsed,
                await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return new CheckResult(false, sw.Elapsed, "", ex.Message);
        }
    }

    /// <summary>尽力读回一条可能永远不会完成的读取任务（进程已被杀、管道已断的情况）。</summary>
    private static async Task<string> TryRead(Task<string> task)
    {
        try
        {
            return await task.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }
        catch
        {
            return "";
        }
    }

    private static Process? Start(string fileName, string[] args, string? stdin)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi);
    }

    private static void Kill(Process proc)
    {
        try { proc.Kill(entireProcessTree: true); } catch { /* 已经退了 */ }
        try { proc.WaitForExit(2000); } catch { /* 同上 */ }
    }
}
