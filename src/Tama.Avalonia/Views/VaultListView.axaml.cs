using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Tama.Avalonia.ViewModels;

namespace Tama.Avalonia.Views;

public partial class VaultListView : UserControl
{
    private const double TapSlop = 10;

    private CipherRow? _pressedRow;
    private Point _pressedAt;
    private bool _wasExpandedOnPress;

    public VaultListView() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// 整行点击 = 展开 / 收起（窄屏手风琴）。
    ///
    /// ListBox 自己会在按下时改 SelectedItem，进而把行展开；这里不能再无条件 Toggle，
    /// 否则同一次点击会"选中展开 → Tapped 收起"，表现成有时点不开。
    /// 所以按下时记住当时状态，抬起时再决定：
    ///   · 按下前已展开 → 本次点击负责收起；
    ///   · 按下前未展开 → 让 ListBox 的选中逻辑展开；若它没展开，再由这里补一次。
    /// 行里的验证码、箭头、复制等按钮会自行处理指针事件，这里也做祖先检查兜底。
    /// </summary>
    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ResetPress();
        if (sender is not Control { DataContext: CipherRow row } control) return;
        if (IsInsideInteractive(e.Source as Visual)) return;

        _pressedRow = row;
        _pressedAt = e.GetPosition(control);
        _wasExpandedOnPress = row.IsExpanded;
    }

    private void OnRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var row = _pressedRow;
        var expanded = _wasExpandedOnPress;
        var pressedAt = _pressedAt;
        ResetPress();

        if (row is null || sender is not Control control || control.DataContext != row) return;
        if (IsInsideInteractive(e.Source as Visual)) return;

        // 手指纵向滚动时也会从行上按下并抬起；位移超过轻触容差就不再当成展开。
        var releasedAt = e.GetPosition(control);
        if (Math.Abs(releasedAt.X - pressedAt.X) > TapSlop ||
            Math.Abs(releasedAt.Y - pressedAt.Y) > TapSlop)
            return;

        if (DataContext is not VaultViewModel vm) return;

        if (expanded)
        {
            vm.ToggleRow(row);
            e.Handled = true;
            return;
        }

        // 正常情况下 ListBox 已经在按下/抬起时选中并展开。若平台没有触发选择变化，
        // 这里补上；已经展开时绝不再调 ToggleRow，避免双路逻辑互相抵消。
        if (!row.IsExpanded) vm.ToggleRow(row);
        e.Handled = true;
    }

    private void ResetPress()
    {
        _pressedRow = null;
        _pressedAt = default;
        _wasExpandedOnPress = false;
    }

    private static bool IsInsideInteractive(Visual? source)
    {
        for (var v = source; v is not null; v = v.GetVisualParent())
        {
            if (v is Button or TextBox or ComboBox or Slider or CheckBox) return true;
        }
        return false;
    }
}
