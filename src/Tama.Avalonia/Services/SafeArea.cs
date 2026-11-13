namespace Tama.Avalonia.Services;

/// <summary>
/// 系统栏（状态栏 / 手势条）占用的边距，单位是 **DIP**（跨平台，和 Avalonia 的布局单位一致）。
///
/// 谁写：只有平台头（Android 的 MainActivity）在 window insets 变化时写。
/// 谁读：壳（ShellViewModel）——顶部内容让开状态栏，底部浮条让开手势条。
///
/// 为什么不做成"内容画到状态栏下面": 手机上标题/搜索框会被时间和信号图标压住。
/// 现在的口径是 edge-to-edge **背景**（窗口铺满整屏，状态栏/手势条区域透出的是应用底色），
/// 内容仍然让开系统栏。桌面宿主从不写它，所以两边共用同一套布局代码。
/// </summary>
public static class SafeArea
{
    private static double _top;
    private static double _bottom;

    public static double Top
    {
        get => _top;
        private set => _top = Math.Max(0, value);
    }

    public static double Bottom
    {
        get => _bottom;
        private set => _bottom = Math.Max(0, value);
    }

    /// <summary>insets 变化（旋转、手势条显示/隐藏、全屏切换）。</summary>
    public static event Action? Changed;

    public static void Update(double top, double bottom)
    {
        if (Math.Abs(_top - top) < 0.5 && Math.Abs(_bottom - bottom) < 0.5) return;

        Top = top;
        Bottom = bottom;
        Changed?.Invoke();
    }
}
