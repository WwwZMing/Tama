using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Tama.Avalonia.ViewModels;

namespace Tama.Avalonia.Views;

/// <summary>
/// 外壳视图。除了装载 XAML，这里还负责**窄屏左右滑动切页**的手势识别。
///
/// 为什么手写 Pointer 事件而不用 Gestures：Avalonia 内置的手势识别器只有 pinch / scroll / pull，
/// 没有水平 swipe；而这里要的判定规则很具体（位移阈值 + 轴向锁定 + 起手排除区），手写更可控。
/// 规则与 Blazor 版 <c>shell.js</c> 对齐：
///   · 横向位移 ≥ 56px 才触发；先看 12px 定轴向，避免纵向滚动被误判成横滑
///   · 从 TextBox / ComboBox / Slider 起手不算（选字、开下拉、拉滑块都在它们身上）
///   · 浮层（对话框）打开时不算
///   · 纵向手势一律放行给 ScrollViewer
/// </summary>
public partial class ShellView : UserControl
{
    private const double SwipeThreshold = 56;
    private const double AxisLock = 12;

    private const int AxisUndecided = 0;
    private const int AxisHorizontal = 1;
    private const int AxisVertical = 2;

    private Point? _startPoint;
    private int _axis = AxisUndecided;

    public ShellView()
    {
        AvaloniaXamlLoader.Load(this);

        // Tunnel：在子控件（列表项、按钮）之前拿到事件，但不阻止它们自己的处理
        AddHandler(PointerPressedEvent, OnPointerPressedTunnel, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMovedTunnel, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleasedTunnel, RoutingStrategies.Tunnel);
    }

    private void OnPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        ResetGesture();

        if (DataContext is not ShellViewModel vm) return;
        if (!vm.IsUnlocked || !vm.IsCompact) return;          // 只有窄屏才启用
        if (vm.OverlayContent is not null) return;            // 有对话框时不切页
        if (IsInteractiveControl(e.Source as Visual)) return; // 输入框 / 下拉 / 滑块

        _startPoint = e.GetPosition(this);
    }

    private void OnPointerMovedTunnel(object? sender, PointerEventArgs e)
    {
        if (_startPoint is not { } start) return;

        var current = e.GetPosition(this);
        var dx = current.X - start.X;
        var dy = current.Y - start.Y;

        // 先锁定轴向：一旦判成纵向，这次手势就彻底交给 ScrollViewer，不再回头
        if (_axis == AxisUndecided && (Math.Abs(dx) > AxisLock || Math.Abs(dy) > AxisLock))
            _axis = Math.Abs(dx) > Math.Abs(dy) ? AxisHorizontal : AxisVertical;
    }

    private void OnPointerReleasedTunnel(object? sender, PointerReleasedEventArgs e)
    {
        var start = _startPoint;
        var axis = _axis;
        ResetGesture();

        if (start is not { } origin || axis != AxisHorizontal) return;

        var dx = e.GetPosition(this).X - origin.X;
        if (Math.Abs(dx) < SwipeThreshold) return;

        // 手指左滑（dx < 0）→ 下一项；右滑 → 上一项
        if (DataContext is ShellViewModel vm) vm.SwipeNavigate(dx < 0 ? 1 : -1);
    }

    private void ResetGesture()
    {
        _startPoint = null;
        _axis = AxisUndecided;
    }

    /// <summary>
    /// 起手在这些控件上时不接管手势：在密码框里拖动是选字，在滑块/下拉上拖动是调值，
    /// 把这些当成切页会让人抓狂（Blazor 版同样排除了 input/textarea/select）。
    /// </summary>
    private static bool IsInteractiveControl(Visual? source)
    {
        if (source is null) return false;
        return source is TextBox or ComboBox or Slider
               || source.FindAncestorOfType<TextBox>() is not null
               || source.FindAncestorOfType<ComboBox>() is not null
               || source.FindAncestorOfType<Slider>() is not null;
    }
}
