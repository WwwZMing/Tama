using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Tama.Avalonia;
using Tama.Avalonia.Desktop.Platforms;
using Tama.Core.Interfaces;
using Tama.Services.Bridge;

namespace Tama.Avalonia.Desktop;

internal static class Program
{
    // Avalonia 初始化前不要碰任何 Avalonia / 第三方 API。
    [STAThread]
    public static int Main(string[] args)
    {
        // 主机必须在 Avalonia 起来之前建好：AppPaths.Initialize()（含旧目录迁移）与 Serilog
        // 都要求"任何文件读写之前"完成，而 XAML 加载本身就会读资源、写日志。
        var environment = AppHost.Build(services =>
        {
            // Windows 平台能力：DPAPI 保护的密钥 + Windows Hello 门禁
            services.AddSingleton<IBiometricService, WindowsBiometricService>();
            services.AddSingleton<IPasskeyPlatformService, WindowsPasskeyPlatformService>();
            // 浏览器扩展的 native host 靠它把 WebAuthn 调用送进来（回环 HTTP + Bearer token）
            services.AddSingleton<LocalBridgeServer>();
        });

        // 回环 bridge 是 fire-and-forget：起不来只记日志，绝不能挡住主界面
        _ = Task.Run(() => environment.Host.Services.GetRequiredService<LocalBridgeServer>().StartAsync());

        App.AttachEnvironment(environment);

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            environment.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
