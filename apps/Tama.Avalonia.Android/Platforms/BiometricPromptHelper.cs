using Android.OS;
using AndroidX.Biometric;
using AndroidX.Core.Content;
using AndroidX.Fragment.App;

namespace Tama.Avalonia.Android.Platforms;

/// <summary>
/// 系统生物识别弹窗（BiometricPrompt）封装。
///
/// allowedAuthenticators = BIOMETRIC_STRONG | DEVICE_CREDENTIAL：
///   · 有指纹/面容硬件 → 弹生物识别；
///   · 没硬件但设了锁屏 PIN/图案/密码 → 自动降级弹"设备凭据"，
///     于是模拟器与"只设了 PIN"的真机也能把流程走通。
///
/// 必须从主线程调用：这里统一 post 到主 Looper，不假设调用方线程。
/// </summary>
internal static class BiometricPromptHelper
{
    /// <summary>弹系统验证框：成功 true；取消 / 失败 / 不可用 / 没有 Activity 都是 false。</summary>
    public static async Task<bool> Show(string title, string subtitle)
    {
        var activity = await WaitForResumedActivityAsync();
        if (activity is null)
        {
            Serilog.Log.Warning("Biometric prompt skipped: no resumed Activity within 5s");
            return false;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var looper = Looper.MainLooper;
        if (looper == null)
        {
            tcs.TrySetResult(false);
            return false;
        }

        // 不 using 掉这个 Handler：Post 之后立刻 Dispose 会把 Java 侧的实例释放掉，
        // 而排队的消息还没执行（一次性小对象，交给 GC 更安全）。
        var handler = new Handler(looper);
        handler.Post(() => Launch(activity, title, subtitle, tcs));
        return await tcs.Task;
    }

    private static async Task<FragmentActivity?> WaitForResumedActivityAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (AndroidPlatform.CurrentActivity is FragmentActivity activity
                && !activity.IsFinishing
                && !activity.IsDestroyed
                && AndroidPlatform.IsResumed(activity))
                return activity;

            await Task.Delay(50);
        }

        return null;
    }

    private static void Launch(FragmentActivity activity, string title, string subtitle,
        TaskCompletionSource<bool> tcs)
    {
        try
        {
            // BiometricPrompt 要求 FragmentActivity（我们的 MainActivity 是
            // AvaloniaActivity : AppCompatActivity : FragmentActivity，满足）。
            if (activity.IsFinishing || activity.IsDestroyed || !AndroidPlatform.IsResumed(activity))
            {
                tcs.TrySetResult(false);
                return;
            }

            // 先查可用性：既无生物识别也无锁屏凭据时直接失败，不用弹一个转圈的窗。
            var manager = BiometricManager.From(activity);
            var authResult = manager.CanAuthenticate(
                BiometricManager.Authenticators.BiometricStrong
                | BiometricManager.Authenticators.DeviceCredential);
            if (authResult != 0)
            {
                Serilog.Log.Warning("Biometric prompt unavailable: CanAuthenticate={Status}", authResult);
                tcs.TrySetResult(false);
                return;
            }

            var executor = ContextCompat.GetMainExecutor(activity)
                           ?? throw new InvalidOperationException("no main executor");
            var prompt = new BiometricPrompt(activity, executor, new AuthCallback(tcs));

            // 组合认证器（API 30+ 语义，androidx 在低版本上也能降级）。
            // ⚠ 用了组合认证器就不能再调 SetNegativeButton / SetDeviceCredentialAllowed
            //    —— androidx 新版绑定会把它们当已弃用/互斥处理。
            var promptInfo = new BiometricPrompt.PromptInfo.Builder()
                .SetTitle(string.IsNullOrEmpty(title) ? "Tama" : title)
                .SetSubtitle(subtitle ?? string.Empty)
                .SetAllowedAuthenticators(BiometricManager.Authenticators.BiometricStrong
                                         | BiometricManager.Authenticators.DeviceCredential)
                .Build();

            prompt.Authenticate(promptInfo);
        }
        catch (Exception ex)
        {
            // Activity 不可用 / 没有认证器 / 权限缺失：一律当验证失败（绝不回退明文）
            Serilog.Log.Warning(ex, "Biometric prompt launch failed");
            tcs.TrySetResult(false);
        }
    }

    private sealed class AuthCallback : BiometricPrompt.AuthenticationCallback
    {
        private readonly TaskCompletionSource<bool> _tcs;

        public AuthCallback(TaskCompletionSource<bool> tcs) => _tcs = tcs;

        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult result)
            => _tcs.TrySetResult(true);

        public override void OnAuthenticationError(int errorCode, Java.Lang.ICharSequence? errString)
        {
            Serilog.Log.Information("Biometric prompt ended: code={Code}, message={Message}",
                errorCode, errString?.ToString());
            _tcs.TrySetResult(false);
        }

        // 指纹没匹配上（非致命，可再试）：不结束，继续等用户重试或取消
        public override void OnAuthenticationFailed()
        {
        }
    }
}
