using Android.OS;
using AndroidX.Biometric;
using AndroidX.Core.Content;
using AndroidX.Fragment.App;
using Microsoft.Maui.ApplicationModel;

namespace Tama.Platforms.Droid;

/// <summary>
/// Android 系统生物识别弹窗（BiometricPrompt）封装。
/// allowedAuthenticators = BIOMETRIC_STRONG | DEVICE_CREDENTIAL：
///   - 有生物识别硬件（指纹/面部）→ 弹指纹/面部验证
///   - 无硬件但设置了锁屏 PIN/图案/密码 → 自动降级弹"设备凭据"输入界面
/// 这让"没有生物识别设备的模拟器/真机"也能完整走通认证流程。
/// </summary>
public static class BiometricPromptHelper
{
    /// <summary>弹系统生物识别框，成功返回 true，取消/失败/不可用返回 false。必须在主线程调用。</summary>
    public static Task<bool> Show(string title, string subtitle)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(() => LaunchPrompt(title, subtitle, tcs));
        }
        else
        {
            LaunchPrompt(title, subtitle, tcs);
        }

        return tcs.Task;
    }

    private static void LaunchPrompt(string title, string subtitle, TaskCompletionSource<bool> tcs)
    {
        try
        {
            if (Platform.CurrentActivity is not FragmentActivity activity || activity.IsFinishing)
            {
                tcs.TrySetResult(false);
                return;
            }

            // 1) 先查可用性：无可用的认证器（既无生物识别也无锁屏凭据）直接失败。
            // CanAuthenticate 返回 0 = BIOMETRIC_SUCCESS（androidx.biometric 绑定无 BiometricError 枚举，直接比较）
            var manager = BiometricManager.From(activity);
            int authResult = manager.CanAuthenticate(BiometricManager.Authenticators.BiometricStrong
                                                     | BiometricManager.Authenticators.DeviceCredential);
            if (authResult != 0)
            {
                tcs.TrySetResult(false);
                return;
            }

            var executor = ContextCompat.GetMainExecutor(activity);
            var callback = new AuthCallback(tcs);
            var prompt = new BiometricPrompt(activity, executor, callback);

            // API 29+：BIOMETRIC_STRONG + DEVICE_CREDENTIAL 组合（无生物识别硬件时自动降级到锁屏 PIN/图案）。
            // 注：androidx.biometric 新版绑定已移除 SetNegativeButton/SetDeviceCredentialAllowed（API 26-28 分支），
            // 目标设备 API 35（K80 Ultra），统一走组合认证器。
            var promptInfo = new BiometricPrompt.PromptInfo.Builder()
                .SetTitle(string.IsNullOrEmpty(title) ? "Tama" : title)
                .SetSubtitle(subtitle ?? string.Empty)
                .SetAllowedAuthenticators(BiometricManager.Authenticators.BiometricStrong
                                         | BiometricManager.Authenticators.DeviceCredential)
                .Build();

            prompt.Authenticate(promptInfo);
        }
        catch (Exception)
        {
            // 任何异常（Activity 不可用、无认证器、权限缺失）都视为失败
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
            => _tcs.TrySetResult(false);

        // 指纹匹配失败（非致命，可重试）——不结束，等待再次尝试或用户取消
        public override void OnAuthenticationFailed()
        {
        }
    }
}
