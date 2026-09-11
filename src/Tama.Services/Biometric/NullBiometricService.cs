using Tama.Core.Interfaces;

namespace Tama.Services.Biometric;

/// <summary>
/// 生物识别的降级实现：浏览器模式（Tama.Api 无原生通道）使用。
/// 所有能力返回"不可用"，加密绝不回退为明文——保证无原生支持时行为安全。
/// </summary>
public class NullBiometricService : IBiometricService
{
    public bool IsAvailable() => false;
    public bool IsConfigured() => false;
    public void SetupKey() { }
    public string? EncryptPassword(string password) => null;
    public string? DecryptPassword(string encrypted) => null;
    public void SaveEncryptedPassword(string encrypted) { }
    public string? GetEncryptedPassword() => null;
    public void ClearEncryptedPassword() { }
    public Task<bool> AuthenticateAsync(string title, string subtitle) => Task.FromResult(false);
}

/// <summary>Passkey 平台能力降级实现：所有平台默认不可用（Android 用原生反射实现，Windows 用 Windows Hello）</summary>
public class NullPasskeyPlatformService : IPasskeyPlatformService
{
    public bool IsAvailable() => false;
    public Task<bool> AuthenticateAsync(string title, string subtitle) => Task.FromResult(false);
}
