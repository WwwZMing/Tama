using Tama.Core.Interfaces;

namespace Tama.Platforms.Windows;

/// <summary>
/// Windows Passkey 能力：Windows Hello（人脸 / 指纹 / PIN）验证。
/// 可用性 = 系统已配置 Windows Hello；认证 = 弹系统验证确认。
/// 验证窗逻辑抽到 WindowsHelloPrompt（与 WindowsBiometricService 共用）。
/// </summary>
public class WindowsPasskeyPlatformService : IPasskeyPlatformService
{
    public bool IsAvailable() => WindowsHelloPrompt.IsAvailable();

    public Task<bool> AuthenticateAsync(string title, string subtitle) =>
        WindowsHelloPrompt.AuthenticateAsync(title, subtitle);
}
