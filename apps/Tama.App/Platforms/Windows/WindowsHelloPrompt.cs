using Microsoft.Maui.ApplicationModel;
using Windows.Security.Credentials.UI;

namespace Tama.Platforms.Windows;

/// <summary>
/// Windows Hello 门禁助手（生物识别解锁 / Passkey 共用）：
/// - 可用性 = 系统已配置 Windows Hello（UserConsentVerifier.CheckAvailabilityAsync）
/// - 认证 = 弹系统验证窗（UserConsentVerifierInterop，桌面应用必须走 interop + UI 线程 + HWND）
/// </summary>
public static class WindowsHelloPrompt
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext(typeof(WindowsHelloPrompt));

    public static bool IsAvailable()
    {
        try
        {
            // bridge 回调常在 UI 线程执行，GetResult() 同步阻塞 + SynchronizationContext 会死锁；
            // 用 Task.Run 移到线程池。异常记日志，不吞。
            return Task.Run(async () =>
                    await UserConsentVerifier.CheckAvailabilityAsync())
                .GetAwaiter()
                .GetResult() == UserConsentVerifierAvailability.Available;
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
            // 桌面应用（WinUI 3 unpackaged）必须用 UserConsentVerifierInterop.RequestVerificationForWindowAsync：
            // 直接 RequestVerificationAsync 是 UWP API，桌面应用弹不出验证窗。
            // 且弹窗必须在 UI 线程调用，因此整体调度到 MainThread。
            return await MainThread.InvokeOnMainThreadAsync(async () =>
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
        var window = Application.Current?.Windows?.FirstOrDefault();
        if (window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window nativeWindow)
        {
            return WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
        }
        return IntPtr.Zero;
    }
}
