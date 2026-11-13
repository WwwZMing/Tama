using Avalonia.Media.Imaging;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
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
        // 对话框在手机上是底部抽屉、桌面是居中卡片，判定跟外壳一致
        services.GetRequiredService<DialogService>().IsCompactProvider = () => shell.IsCompact;

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

            // 切主题（侧栏那颗按钮 / 设置页）时标题栏跟着重新上色，
            // 否则会出现"内容变浅色、标题栏还是深色"的割裂
            if (Application.Current is { } app)
                app.ActualThemeVariantChanged += (_, _) => PlatformHooks.WindowChrome(window);
            window.Opened += async (_, _) =>
            {
                UpdateCompact();

                // 沉浸式标题栏：把系统 caption / 边框染成主题色（平台头实现，没实现就是空操作）
                PlatformHooks.WindowChrome(window);

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
            var singleViewRoot = new ShellView { DataContext = shell };
            singleView.MainView = singleViewRoot;

            // 挂到可视树之后才能拿到 TopLevel / InsetsManager
            singleViewRoot.AttachedToVisualTree += (_, _) => ConfigureMobileInsets(singleViewRoot);

            _ = shell.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 手机上的 edge-to-edge（2026-11-14）。
    ///
    /// 用 Avalonia 12 自己的 <see cref="IInsetsManager"/>，**不要**再在 Android 头里手搓
    /// <c>SetDecorFitsSystemWindows</c> + 自研 insets listener：
    ///   · <c>DisplayEdgeToEdgePreference = true</c> 让窗口画到系统栏底下（Android 15+ 默认也是这个）；
    ///   · <c>SystemBarColor</c> 必须给**实色**（= 应用底色）。已知问题：edge-to-edge 打开时给
    ///     Transparent 不生效，系统栏会停在半透明黑——表现就是"状态栏一条纯黑、不沉浸"；
    ///   · 真实安全区从 <c>SafeAreaPadding</c> 取（旋转/手势条显隐都会变），写进 SafeArea 给壳用，
    ///     这样不用再在 XAML 里写死 24；
    ///   · 同时把 <c>AutoSafeAreaPadding</c> 关掉：边距由壳自己按 SafeArea 给，避免被加两遍。
    /// </summary>
    private void ConfigureMobileInsets(Control root)
    {
        if (TopLevel.GetTopLevel(root) is not { } topLevel) return;
        if (topLevel.InsetsManager is not { } insets) return;

        try
        {
            insets.DisplayEdgeToEdgePreference = true;

            // ⚠ 单位陷阱（2026-11-14 实测）：Android 上 InsetsManager.SafeAreaPadding 给的是
            // **物理像素**，而 Avalonia 布局用 DIP。直接当 DIP 用会在 2.6x 屏上多让出两倍多
            // （实测顶部空出一大截）。这里统一除以 RenderScaling。
            var scaling = topLevel.RenderScaling;
            if (scaling <= 0) scaling = 1;

            void ApplySafeArea(Thickness pad)
                => SafeArea.Update(pad.Top / scaling, pad.Bottom / scaling);

            ApplySafeArea(insets.SafeAreaPadding);
            insets.SafeAreaChanged += (_, e) => ApplySafeArea(e.SafeAreaPadding);

            ApplySystemBarColor(insets);
            ActualThemeVariantChanged += (_, _) => ApplySystemBarColor(insets);

            // 边距**由壳自己给**（PageHost 的 Margin / 底栏的 Margin），
            // 所以关掉 Avalonia 的自动安全区 padding，避免加两遍。
            // 这样窗口背景铺满整屏（状态栏/手势条区域就是我们自己的底色），内容按真实 insets 让位。
            TopLevel.SetAutoSafeAreaPadding(root, false);

            Serilog.Log.Information("Mobile insets: edgeToEdge={Edge}, safeArea={Safe}, scaling={Scaling}",
                insets.DisplaysEdgeToEdge, insets.SafeAreaPadding, topLevel.RenderScaling);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Configure mobile insets failed");
        }
    }

    /// <summary>系统栏颜色 = 应用底色（给 Transparent 在 edge-to-edge 下不生效，见上面的注释）。</summary>
    private void ApplySystemBarColor(IInsetsManager insets)
    {
        var light = ActualThemeVariant == ThemeVariant.Light;
        insets.SystemBarColor = Color.Parse(light ? "#F5F5FA" : "#0B0B10");
    }

    /// <summary>桌面头在窗口出现前调用一次，保证 <c>ThemeVariant</c> 在首帧就正确。</summary>
    public static void AttachEnvironment(AppEnvironment env) => HostEnvironment = env;

    /// <summary>保留给测试/调试：把当前主题强制成某个变体。</summary>
    public void ApplyTheme(ThemeVariant variant) => RequestedThemeVariant = variant;
}
