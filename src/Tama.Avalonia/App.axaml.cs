using Avalonia.Media.Imaging;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Tama.Avalonia.Services;
using Tama.Avalonia.ViewModels;
using Tama.Avalonia.Views;

namespace Tama.Avalonia;

public partial class App : Application
{
    /// <summary>平台头在建好主机后塞进来（桌面头的 <c>Program.Main</c> / Android 的 Application）。</summary>
    public static AppEnvironment? HostEnvironment { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var env = HostEnvironment
                  ?? throw new InvalidOperationException("AppHost.Build 必须先于 Avalonia 启动执行。");

        var services = env.Host.Services;
        services.GetRequiredService<ThemeService>().Initialize();

        var shell = new ShellViewModel(env.Session, services.GetRequiredService<ThemeService>(),
            services.GetRequiredService<ShellAccessor>());

        // 剪贴板与文件选择器要用 TopLevel；两种生命周期取值方式不同，收在一个 lambda 里
        TopLevel? CurrentTopLevel() => ApplicationLifetime switch
        {
            IClassicDesktopStyleApplicationLifetime desktop => desktop.MainWindow,
            ISingleViewApplicationLifetime single => single.MainView is { } view ? TopLevel.GetTopLevel(view) : null,
            _ => null,
        };

        services.GetRequiredService<ClipboardService>().TopLevelProvider = CurrentTopLevel;
        services.GetRequiredService<DialogService>().TopLevelProvider = CurrentTopLevel;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopLifetime)
        {
            var window = new MainWindow { DataContext = shell };
            desktopLifetime.MainWindow = window;

            // 窄窗口切换成单栏（与手机同一套布局），阈值和 Blazor 版的 640px 断点不是一回事：
            // 这里是"列表 340 + 详情最少 420"的物理下限。
            void UpdateCompact() => shell.IsCompact = window.Bounds.Width < 780;
            window.PropertyChanged += (_, e) =>
            {
                if (e.Property == Visual.BoundsProperty) UpdateCompact();
            };
            window.Opened += async (_, _) =>
            {
                UpdateCompact();
                await shell.InitializeAsync();
#if DEBUG
                await SnapshotAutomation.CaptureAsync(window, shell, desktopLifetime);
#endif
            };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            // Android / 移动：单视图宿主，布局按窄屏处理
            shell.IsCompact = true;
            singleView.MainView = new ShellView { DataContext = shell };
            _ = shell.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>桌面头在窗口出现前调用一次，保证 <c>ThemeVariant</c> 在首帧就正确。</summary>
    public static void AttachEnvironment(AppEnvironment env) => HostEnvironment = env;

    /// <summary>保留给测试/调试：把当前主题强制成某个变体。</summary>
    public void ApplyTheme(ThemeVariant variant) => RequestedThemeVariant = variant;
}
