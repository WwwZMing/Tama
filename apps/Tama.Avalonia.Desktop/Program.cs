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
        // --gc-check：**发布版自证用**。Release 的日志 sink 只收 Error 及以上，
        // 启动那行 "satori=..." 在发布版里根本不会落盘（这就是"发布版看不到 GC 状态"的原因）。
        // 这个开关不建主机、不碰数据目录，把结论打到标准输出并写一份到 exe 旁边，然后直接退出。
        if (args.Contains("--gc-check"))
        {
            var text = GcDiagnostics.Describe();
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "gc-check.txt"), text); }
            catch { /* 只读目录里就只打 stdout */ }
            Console.WriteLine(text);
            return 0;
        }

        // 主机必须在 Avalonia 起来之前建好：AppPaths.Initialize()（含旧目录迁移）与 Serilog
        // 都要求"任何文件读写之前"完成，而 XAML 加载本身就会读资源、写日志。
        var environment = AppHost.Build(services =>
        {
            // 沉浸式标题栏：DWM 给系统 caption 上主题色（Win11 22000+；老系统自动退化为默认色）
            PlatformHooks.ApplyWindowChrome = Win32WindowChrome.Apply;

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
