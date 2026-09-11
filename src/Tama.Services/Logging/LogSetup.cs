using Serilog;
using Serilog.Events;
using Tama.Core;

namespace Tama.Services.Logging;

public static class LogSetup
{
    public static ILogger CreateLogger()
    {
        Directory.CreateDirectory(AppPaths.LogDir);

        // 文件名前缀跟着产品名走：以后再改名不会漏掉日志文件（原先硬编码 "tama-"）
        var logFile = Path.Combine(AppPaths.LogDir, $"{AppPaths.ProductName.ToLowerInvariant()}-.log");

        return new LoggerConfiguration()
#if DEBUG
            .MinimumLevel.Debug()
            .WriteTo.Console(LogEventLevel.Debug)
            // ⚠ 文件 sink 刻意压到 Information：**日志文件是明文的**（就在加密保险库旁边）。
            //   Debug 级里出现过"EF 把 SQL 参数值一起打出来"这类东西（实测 465 行带值，
            //   含用户名/密码/TOTP），所以 Debug 一律只进控制台——控制台不落盘。
            //   排查要值的时候用控制台看，别把它持久化。
            .WriteTo.File(logFile,
                restrictedToMinimumLevel: LogEventLevel.Information,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
#else
            .MinimumLevel.Error()
            .WriteTo.File(logFile,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 3,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
#endif
            .CreateLogger();
    }
}
