using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace Tama.Avalonia.Views;

public partial class UnlockView : UserControl
{
    public UnlockView() => AvaloniaXamlLoader.Load(this);

    private async void OnCodeFieldGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (sender is not Control control) return;

        // AdjustResize 与输入法动画不是同时结束。多次 BringIntoView 才能覆盖
        // Android 键盘从弹出到最终高度稳定的过程，避免验证码框被键盘盖住。
        foreach (var delay in new[] { 0, 80, 220, 450 })
        {
            if (delay > 0) await Task.Delay(delay);
            control.BringIntoView();
        }
    }
}
