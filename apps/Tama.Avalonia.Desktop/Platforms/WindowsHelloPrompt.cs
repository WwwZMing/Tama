using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Windows.Security.Credentials.UI;

namespace Tama.Avalonia.Desktop.Platforms;

/// <summary>
/// Windows Hello 门禁（生物识别解锁 / 通行密钥共用）：
///   · 可用性 = 系统已配置 Windows Hello（<c>UserConsentVerifier.CheckAvailabilityAsync</c>）
///   · 认证 = 弹系统验证窗。桌面（非 UWP）应用**必须**走 <c>UserConsentVerifierInterop</c> +
///     UI 线程 + 窗口句柄，直接调 <c>RequestVerificationAsync</c> 是 UWP API，桌面应用弹不出来。
/// </summary>
internal static class WindowsHelloPrompt
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext(typeof(WindowsHelloPrompt));

    public static bool IsAvailable()
    {
        try
        {
            // 同步等在 UI 线程上会死锁（SynchronizationContext），挪到线程池
            var availability = Task.Run(async () => await UserConsentVerifier.CheckAvailabilityAsync())
                .GetAwaiter()
                .GetResult();
            // 打印原始枚举：NotConfigured / DeviceNotPresent / DisabledByPolicy 的处置方式完全不同，
            // 只记 "false" 的话排查时根本分不清是"没设 PIN"还是"被组策略关了"
            Log.Debug("Windows Hello availability = {Availability}", availability);
            return availability == UserConsentVerifierAvailability.Available;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Windows Hello availability check failed");
            return false;
        }
    }

    public static async Task<bool> AuthenticateAsync(string title, string subtitle)
    {
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var hwnd = GetMainWindowHandle();
                if (hwnd == IntPtr.Zero)
                {
                    Log.Error("Windows Hello prompt skipped: no main window handle");
                    return false;
                }

                var message = string.IsNullOrWhiteSpace(subtitle) ? title : subtitle;
                var result = await UserConsentVerifierInterop.RequestVerificationForWindowAsync(hwnd, message);
                Log.Information("Windows Hello verification result: {Result}", result);
                return result == UserConsentVerificationResult.Verified;
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Windows Hello verification failed");
            return false;
        }
    }

    private static IntPtr GetMainWindowHandle()
    {
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        return window?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
    }
}
