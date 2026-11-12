using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Microsoft.Extensions.DependencyInjection;
using Tama.Avalonia.Android.Platforms;
using Tama.Core.Interfaces;

namespace Tama.Avalonia.Android;

[Application]
public class TamaApplication : AvaloniaAndroidApplication<App>
{
    protected TamaApplication(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // 主机要在 Avalonia 初始化之前建好：AppPaths.Initialize()（含旧目录迁移）与 Serilog
        // 都要求"任何文件读写之前"完成。
        var environment = AppHost.Build(services =>
        {
            // 生物识别：AndroidKeyStore 里的 AES 密钥加密主密码 + BiometricPrompt（指纹/面容，
            // 无硬件时降级锁屏 PIN）验证。与 Windows 头的 Windows Hello 一一对应。
            // 千万别在这里退回 Null 实现：那等于"指纹入口不存在"，而且不会报错——
            // 正是 Windows Hello 当年被 TryAdd 兜底盖掉的那种静默故障（CLAUDE.md 陷阱 19）。
            services.AddSingleton<IBiometricService, AndroidBiometricService>();
            services.AddSingleton<IPasskeyPlatformService, AndroidPasskeyPlatformService>();
        });

        App.AttachEnvironment(environment);

        return base.CustomizeAppBuilder(builder).WithInterFont();
    }
}
