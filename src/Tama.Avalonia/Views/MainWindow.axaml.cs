using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Tama.Avalonia.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        // 背景材质只能在窗口真正创建之后才知道结果：TransparencyLevelHint 是"请求"，
        // 给不给要看系统（Windows 11 22H2+ 有 Mica；Win10 / 远程桌面 / 关掉了透明效果的机器
        // 会退回 AcrylicBlur 或干脆 None）。所以等 Opened 再读 ActualTransparencyLevel。
        Opened += OnOpenedForBackdrop;
    }

    private void OnOpenedForBackdrop(object? sender, EventArgs e)
    {
        Opened -= OnOpenedForBackdrop;
        ApplyBackdrop();
    }

    /// <summary>
    /// 拿到系统背景材质时给窗口挂 <c>.mica</c> 类：样式里把根网格与侧栏/顶栏/底栏让成半透明
    /// （内容区仍不透明），于是透出来的是**桌面壁纸**而不是应用自己的底色。
    ///
    /// 没拿到就什么都不做——所有元素保持原来的不透明样式，Android 与 Win10 上零变化。
    /// 这条是**探测而非假设**：以前踩过"以为系统支持"的坑，这类能力必须以 Actual 值为准。
    /// </summary>
    private void ApplyBackdrop()
    {
        // 逃生开关：有人不喜欢壁纸透过侧栏（或者壁纸太花导致对比度差），
        // 设 TAMA_DISABLE_MICA=1 就回到全不透明。也是截图核对"无玻璃"路径的入口。
        if (Environment.GetEnvironmentVariable("TAMA_DISABLE_MICA") == "1")
        {
            Serilog.Log.Information("Backdrop: disabled by TAMA_DISABLE_MICA");
            return;
        }

        var level = ActualTransparencyLevel;
        // WindowTransparencyLevel 在 Avalonia 12 里是带静态属性的类型（不是 enum），
        // 所以用相等比较而不是 is 模式匹配
        var supported = level == WindowTransparencyLevel.Mica
                        || level == WindowTransparencyLevel.AcrylicBlur
                        || level == WindowTransparencyLevel.Blur;

        Serilog.Log.Information("Backdrop: requested=Mica/AcrylicBlur, actual={Level}, glass={On}",
            level, supported);

        if (!supported) return;
        Classes.Add("mica");
    }
}
