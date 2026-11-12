using Tama.Core.Interfaces;

namespace Tama.Avalonia.Android.Platforms;

/// <summary>
/// Android 生物识别实现：AndroidKeyStore 加密主密码 + BiometricPrompt 验证。
/// 与 Windows 头的 <c>WindowsBiometricService</c> 语义一一对应（DPAPI + Windows Hello）。
/// </summary>
public sealed class AndroidBiometricService : IBiometricService
{
    public bool IsAvailable() => BiometricHelper.IsAvailable(AndroidPlatform.Context);

    public bool IsConfigured() => BiometricHelper.IsConfigured(AndroidPlatform.Context);

    public void SetupKey() => BiometricHelper.SetupKey();

    public string? EncryptPassword(string password)
        => string.IsNullOrEmpty(password) ? null : BiometricHelper.EncryptPassword(password);

    public string? DecryptPassword(string encrypted)
        => string.IsNullOrEmpty(encrypted) ? null : BiometricHelper.DecryptPassword(encrypted);

    public void SaveEncryptedPassword(string encrypted)
    {
        if (!string.IsNullOrEmpty(encrypted))
            BiometricHelper.SaveEncryptedPassword(AndroidPlatform.Context, encrypted);
    }

    public string? GetEncryptedPassword() => BiometricHelper.GetEncryptedPassword(AndroidPlatform.Context);

    public void ClearEncryptedPassword() => BiometricHelper.ClearEncryptedPassword(AndroidPlatform.Context);

    /// <summary>弹系统验证（指纹/面容，无硬件时降级锁屏 PIN）。</summary>
    public Task<bool> AuthenticateAsync(string title, string subtitle)
        => BiometricPromptHelper.Show(title, subtitle);
}

/// <summary>
/// Android 通行密钥平台能力：Tama 的软密钥 + BiometricPrompt 验证，不依赖系统 Credential Manager，
/// 所以"可用性"就等于"系统有能用来验证的认证器"。
/// </summary>
public sealed class AndroidPasskeyPlatformService : IPasskeyPlatformService
{
    public bool IsAvailable() => BiometricHelper.IsAvailable(AndroidPlatform.Context);

    public Task<bool> AuthenticateAsync(string title, string subtitle)
        => BiometricPromptHelper.Show(title, subtitle);
}
