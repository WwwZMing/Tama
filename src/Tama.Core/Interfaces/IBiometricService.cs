namespace Tama.Core.Interfaces;

/// <summary>
/// 平台生物识别服务抽象：MAUI Android 用 AndroidKeyStore + BiometricHelper 实现，
/// MAUI Windows 用 DPAPI + Windows Hello（WindowsBiometricService），
/// 浏览器模式用 NullBiometricService 降级（返回不可用）。
/// 前端（Blazor 页面）直接注入本接口调用，不再依赖 WebView 注入对象，也没有 DTO 包装。
/// </summary>
public interface IBiometricService
{
    /// <summary>设备是否支持生物识别（Android：BiometricManager 可认证且已注册生物特征）</summary>
    bool IsAvailable();

    /// <summary>是否已用生物识别保存过加密的主密码（prefs 中有 encrypted_password）</summary>
    bool IsConfigured();

    /// <summary>初始化 AndroidKeyStore 中的 AES 密钥（不存在则生成，存在则复用）</summary>
    void SetupKey();

    /// <summary>用 KeyStore 密钥加密主密码，返回 "iv.ct" 格式；不可用时返回 null（绝不回退为明文）</summary>
    string? EncryptPassword(string password);

    /// <summary>解密 "iv.ct" 格式密文；密钥缺失/解密失败返回 null</summary>
    string? DecryptPassword(string encrypted);

    /// <summary>把加密密码写入应用私有 prefs</summary>
    void SaveEncryptedPassword(string encrypted);

    /// <summary>从 prefs 读取加密密码，未保存返回 null</summary>
    string? GetEncryptedPassword();

    /// <summary>清除 prefs 中的加密密码（如关闭生物识别解锁）</summary>
    void ClearEncryptedPassword();

    /// <summary>
    /// 弹出系统生物识别确认（Android BiometricPrompt / Windows Hello）。
    /// Android 实现（AndroidBiometricService）用 Xamarin.AndroidX.Biometric 弹 BiometricPrompt，
    /// 允许 BIOMETRIC_STRONG | DEVICE_CREDENTIAL（无生物识别硬件时降级锁屏 PIN），真机已验证。
    /// Windows 实现（WindowsBiometricService）用 UserConsentVerifier 弹 Windows Hello 验证窗。
    /// 浏览器模式用 NullBiometricService 降级（返回 false）。
    /// </summary>
    Task<bool> AuthenticateAsync(string title, string subtitle);
}

/// <summary>Passkey 平台能力抽象：Android 反射检测 Credential Manager + BiometricPrompt，Windows 用 Windows Hello（UserConsentVerifier）</summary>
public interface IPasskeyPlatformService
{
    bool IsAvailable();

    /// <summary>弹出平台身份验证确认（Windows Hello / Android BiometricPrompt），通过返回 true</summary>
    Task<bool> AuthenticateAsync(string title, string subtitle);
}
