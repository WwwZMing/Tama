using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Tama.Avalonia.Desktop.Platforms;

/// <summary>
/// Windows 11 的"沉浸式标题栏"：用 DWM 给系统 caption / 边框 / 标题文字上主题色，
/// 而不是让标题栏保持系统默认的白或黑。
///
/// 几个硬事实：
///   · 只有 **Windows 11 22000+** 支持着色（Win10 上这些属性会被忽略），所以每个调用都容忍失败；
///   · 颜色是 COLORREF（0x00BBGGRR），不是 RGB，直接传 0xRRGGBB 会红蓝互换；
///   · 深色模式下还要额外打开 <c>DWMWA_USE_IMMERSIVE_DARK_MODE</c>，否则标题栏上的
///     最小化/关闭按钮图标是黑的，压在深色底上看不见。
///
/// 颜色取自共享层的主题画刷（TamaBgBrush / TamaTextBrush / TamaBorderBrush），
/// 所以切主题时重新调一次就能跟着变。
/// </summary>
internal static class Win32WindowChrome
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static bool _logged;

    public static void Apply(TopLevel? topLevel)
    {
        if (topLevel is not Window window) return;

        var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;

        var dark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

        // 主题画刷走资源查找，找不到才用兜底常量（防止主题资源改名后整条着色静默失效）
        var caption = ReadColor("TamaBgBrush", dark ? "#0B0B10" : "#F5F5FA");
        var text = ReadColor("TamaTextBrush", dark ? "#ECECF2" : "#16161D");
        var border = ReadColor("TamaBorderBrush", dark ? "#262633" : "#E1E1EC");

        var darkMode = dark ? 1 : 0;
        var captionRef = ToColorRef(caption);
        var textRef = ToColorRef(text);
        var borderRef = ToColorRef(border);

        var ok = TrySet(hwnd, DwmwaUseImmersiveDarkMode, darkMode);
        ok &= TrySet(hwnd, DwmwaCaptionColor, captionRef);
        ok &= TrySet(hwnd, DwmwaTextColor, textRef);
        ok &= TrySet(hwnd, DwmwaBorderColor, borderRef);

        if (!_logged)
        {
            _logged = true;
            Serilog.Log.Information(
                "Window chrome tint applied={Ok} dark={Dark} caption=#{Caption:X6} (Win10 上不支持着色，返回 false 属正常)",
                ok, dark, caption.ToUInt32() & 0xFFFFFF);
        }
    }

    private static bool TrySet(IntPtr hwnd, int attribute, int value) =>
        DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)) == 0;

    /// <summary>COLORREF = 0x00BBGGRR（注意不是 RGB 顺序）。</summary>
    private static int ToColorRef(Color color) => (color.B << 16) | (color.G << 8) | color.R;

    private static Color ReadColor(string key, string fallback)
    {
        try
        {
            if (Application.Current is { } app
                && app.TryFindResource(key, app.ActualThemeVariant, out var value)
                && value is ISolidColorBrush brush)
            {
                return brush.Color;
            }
        }
        catch
        {
            // 资源查找失败不值得抛：用兜底色，标题栏至少不会是刺眼的白
        }

        return Color.Parse(fallback);
    }
}
