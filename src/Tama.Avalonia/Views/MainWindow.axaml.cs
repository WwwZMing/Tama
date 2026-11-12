using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Tama.Avalonia.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        // Mica 是**显式开启**（TAMA_ENABLE_MICA=1）。不请求透明时窗口走不透明合成，
        // 拖动缩放 / 最小化还原不会出现合成层发白或空白（2026-11-12 实测：
        // 无论开不开 Mica 都请求 Mica/AcrylicBlur 会一直走透明合成，和"默认不透明"的
        // 设计意图不符）。
        if (Environment.GetEnvironmentVariable("TAMA_ENABLE_MICA") == "1")
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur };

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
        // Mica 改成**显式开启**（TAMA_ENABLE_MICA=1）。
        // 默认关：用户要的是"一整个简洁的主题色铺满窗口"，而 Mica 只有在窗口某处透明时
        // 才看得见——那会在侧栏四周露出一圈壁纸，反而又变成"多层底色"。
        // 想试的机器上设这个变量即可，代码路径保留着。
        if (Environment.GetEnvironmentVariable("TAMA_ENABLE_MICA") != "1")
        {
            Serilog.Log.Information("Backdrop: mica off (set TAMA_ENABLE_MICA=1 to enable)");
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
