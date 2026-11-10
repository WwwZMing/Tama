using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Microsoft.Extensions.DependencyInjection;
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
            // TODO(Android)：生物识别要接 AndroidKeyStore + BiometricPrompt（Xamarin.AndroidX.Biometric）。
            // 现在先用 Null 降级——解锁仍然可用（主密码），只是没有指纹入口。
            // 决不能在不可用时"回退明文"，这正是 NullBiometricService 的语义。
            services.AddSingleton<IBiometricService, Tama.Services.Biometric.NullBiometricService>();
            services.AddSingleton<IPasskeyPlatformService, Tama.Services.Biometric.NullPasskeyPlatformService>();
        });

        App.AttachEnvironment(environment);

        return base.CustomizeAppBuilder(builder).WithInterFont();
    }
}
