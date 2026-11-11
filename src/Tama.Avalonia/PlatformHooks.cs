using Avalonia.Controls;

namespace Tama.Avalonia;

/// <summary>
/// 平台钩子：共享 UI 层不该引用任何 Win32 / Android API，但有些"窗口外壳"的事只有平台头能做
/// （比如给系统标题栏上色）。平台头在启动时塞一个实现进来，共享层只负责在正确的时机调用。
///
/// 没塞实现（博客/测试宿主）= 什么都不做，不是错误。
/// </summary>
public static class PlatformHooks
{
    /// <summary>
    /// 给窗口外壳套上主题色（Windows：DWM 标题栏/边框着色；其他平台：通常为空实现）。
    /// 窗口打开后调一次，之后每次主题切换再调一次。
    /// </summary>
    public static Action<TopLevel>? ApplyWindowChrome { get; set; }

    public static void WindowChrome(TopLevel? topLevel)
    {
        if (topLevel is null) return;
        try
        {
            ApplyWindowChrome?.Invoke(topLevel);
        }
        catch (Exception ex)
        {
            // 窗口着色失败不该影响任何功能：最坏情况就是标题栏保持系统默认色
            Serilog.Log.Debug(ex, "Window chrome hook failed");
        }
    }
}
