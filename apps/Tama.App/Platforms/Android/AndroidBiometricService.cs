using Tama.Core.Interfaces;
using Microsoft.Maui.ApplicationModel;

namespace Tama.Platforms.Droid;

/// <summary>
/// Android 生物识别实现：包装 BiometricHelper（AndroidKeyStore AES + 私有 prefs）。
/// Activity/Context 从 MAUI 的 Platform.CurrentActivity 获取。
/// </summary>
public class AndroidBiometricService : IBiometricService
{
    public bool IsAvailable()
    {
        var activity = Platform.CurrentActivity;
        return activity != null && BiometricHelper.IsAvailable(activity);
    }

    public bool IsConfigured()
    {
        var context = Platform.CurrentActivity;
        return context != null && BiometricHelper.IsConfigured(context);
    }

    public void SetupKey()
    {
        BiometricHelper.SetupKey();
    }

    public string? EncryptPassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return null;
        return BiometricHelper.EncryptPassword(password);
    }

    public string? DecryptPassword(string encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return null;
        return BiometricHelper.DecryptPassword(encrypted);
    }

    public void SaveEncryptedPassword(string encrypted)
    {
        var context = Platform.CurrentActivity;
        if (context != null) BiometricHelper.SaveEncryptedPassword(context, encrypted);
    }

    public string? GetEncryptedPassword()
    {
        var context = Platform.CurrentActivity;
        return context != null ? BiometricHelper.GetEncryptedPassword(context) : null;
    }

    public void ClearEncryptedPassword()
    {
        var context = Platform.CurrentActivity;
        if (context != null) BiometricHelper.ClearEncryptedPassword(context);
    }

    /// <summary>
    /// 弹出系统 BiometricPrompt（指纹/面部/设备凭据）做用户验证。
    /// 有生物识别硬件（如 K80 Ultra 屏下指纹）→ 弹指纹；无硬件但设了锁屏 PIN → 自动降级弹 PIN 输入
    /// （allowedAuthenticators 含 DeviceCredential）。
    /// </summary>
    public Task<bool> AuthenticateAsync(string title, string subtitle)
    {
        return BiometricPromptHelper.Show(title, subtitle);
    }
}

/// <summary>
/// Android Passkey 能力。通行密钥是 Tama 自闭环（软密钥 + BiometricPrompt 指纹验证），
/// 不依赖系统 Credential Manager——可用性即"系统有可用于验证的认证器"（指纹/面部/锁屏 PIN）。
/// </summary>
public class AndroidPasskeyPlatformService : IPasskeyPlatformService
{
    public bool IsAvailable()
    {
        var activity = Platform.CurrentActivity;
        return activity != null && BiometricHelper.IsAvailable(activity);
    }

    /// <summary>与 AndroidBiometricService 相同：弹系统 BiometricPrompt（指纹/设备凭据）验证。</summary>
    public Task<bool> AuthenticateAsync(string title, string subtitle)
    {
        return BiometricPromptHelper.Show(title, subtitle);
    }
}
