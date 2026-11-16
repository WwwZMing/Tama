namespace Tama.Avalonia.Services;

/// <summary>
/// Android 输入法当前占用的底部高度，单位是 DIP。
///
/// edge-to-edge 打开后，AdjustResize 不会自动缩短 Avalonia 的根布局；只看
/// InsetsManager.SafeAreaPadding 也拿不到键盘高度。平台头需要单独写入这里，
/// 壳据此缩短整个可用区域，所有页面的 ScrollViewer 就共享同一条键盘上边界。
/// </summary>
public static class KeyboardInset
{
    private static double _bottom;

    public static double Bottom => _bottom;
    public static bool IsVisible => _bottom > 0.5;

    public static event Action? Changed;

    public static void Update(double bottom)
    {
        bottom = Math.Max(0, bottom);
        if (Math.Abs(_bottom - bottom) < 0.5) return;

        _bottom = bottom;
        Changed?.Invoke();
    }
}
