using Tama.Core;
using System.Runtime.InteropServices;
using Tama.UI.Services;

namespace Tama;

public partial class MainPage : ContentPage
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowText(IntPtr hWnd, string lpString);

    public MainPage(IHostIntegration host)
    {
        InitializeComponent();

#if WINDOWS
        // 原生标题栏跟随生效主题：Blazor 侧 MainLayout 调 host.ApplyTheme(resolved) 时触发
        host.ThemeChanged += OnThemeChanged;
        this.HandlerChanged += (_, _) =>
        {
            this.Dispatcher.Dispatch(() =>
            {
                // 启动即按主题上色：读上次会话缓存的生效主题（ApplyTheme 时写入），
                // 首次运行/无缓存时读 Windows 系统主题兜底——不再写死 light。
                UpdateTitleBarColors(GetStartupThemeMode());
                SetWindowTitle("Tama");
            });
        };
#endif

        // Blazor 组件只依赖领域契约接口（业务）与 IHostIntegration（宿主能力），
        // 没有任何 method 字符串或 JSON 通道需要在这里转发。
        Serilog.Log.Information("=== Tama MAUI === {Framework}",
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
    }

#if WINDOWS
    private void SetWindowTitle(string title)
    {
        try
        {
            var window = this.GetParentWindow();
            if (window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window win)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(win);
                SetWindowText(hwnd, title);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Tama] Failed to set window title: {ex.Message}");
        }
    }

    private void OnThemeChanged(string mode)
    {
        this.Dispatcher.Dispatch(() => UpdateTitleBarColors(mode));
    }

    /// <summary>启动时标题栏该用什么主题：上次会话缓存的生效主题 &gt; Windows 系统主题 &gt; light。</summary>
    private static string GetStartupThemeMode()
    {
        try
        {
            var cache = AppPaths.ThemeModePath;
            var cached = File.ReadAllText(cache).Trim();
            if (cached is "dark" or "light")
                return cached;
        }
        catch { }

        try
        {
            // 首次运行兜底：HKCU\...\Themes\Personalize AppsUseLightTheme（1=亮，0=暗）
            var light = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return light is 0 ? "dark" : "light";
        }
        catch
        {
            return "light";
        }
    }

    private void UpdateTitleBarColors(string mode)
    {
        var window = this.GetParentWindow();
        if (window == null) return;

        if (window.TitleBar is TitleBar mauiTitleBar)
        {
            if (mode == "dark")
            {
                mauiTitleBar.BackgroundColor = Color.Parse("#0f172a");
                mauiTitleBar.ForegroundColor = Color.Parse("#f1f5f9");
            }
            else
            {
                mauiTitleBar.BackgroundColor = Color.Parse("#f8fafc");
                mauiTitleBar.ForegroundColor = Color.Parse("#1e293b");
            }
        }

        if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window win) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(win);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        var sysTitleBar = appWindow.TitleBar;

        if (mode == "dark")
        {
            sysTitleBar.ButtonBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x0F, 0x17, 0x2A);
            sysTitleBar.ButtonForegroundColor = Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xF1, 0xF5, 0xF9);
            sysTitleBar.ButtonHoverBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(0x20, 0xFF, 0xFF, 0xFF);
            sysTitleBar.ButtonPressedBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(0x40, 0xFF, 0xFF, 0xFF);
            sysTitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x0F, 0x17, 0x2A);
            sysTitleBar.ButtonInactiveForegroundColor = Microsoft.UI.ColorHelper.FromArgb(0x80, 0xF1, 0xF5, 0xF9);
        }
        else
        {
            sysTitleBar.ButtonBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xF8, 0xFA, 0xFC);
            sysTitleBar.ButtonForegroundColor = Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x1E, 0x29, 0x3B);
            sysTitleBar.ButtonHoverBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(0x20, 0x00, 0x00, 0x00);
            sysTitleBar.ButtonPressedBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(0x40, 0x00, 0x00, 0x00);
            sysTitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xF8, 0xFA, 0xFC);
            sysTitleBar.ButtonInactiveForegroundColor = Microsoft.UI.ColorHelper.FromArgb(0x80, 0x1E, 0x29, 0x3B);
        }
    }
#endif
}
