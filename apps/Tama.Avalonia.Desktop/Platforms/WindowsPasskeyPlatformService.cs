using Tama.Core.Interfaces;

namespace Tama.Avalonia.Desktop.Platforms;

/// <summary>
/// Windows 的通行密钥平台能力：可用性 = 系统已配置 Windows Hello；
/// 认证 = 弹系统验证窗（与生物识别解锁共用 <see cref="WindowsHelloPrompt"/>）。
/// </summary>
public sealed class WindowsPasskeyPlatformService : IPasskeyPlatformService
{
    public bool IsAvailable() => WindowsHelloPrompt.IsAvailable();

    public Task<bool> AuthenticateAsync(string title, string subtitle) =>
        WindowsHelloPrompt.AuthenticateAsync(title, subtitle);
}
