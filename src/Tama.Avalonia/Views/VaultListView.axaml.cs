using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Tama.Avalonia.ViewModels;

namespace Tama.Avalonia.Views;

public partial class VaultListView : UserControl
{
    public VaultListView() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// 整行点击 = 展开 / 收起（窄屏手风琴）。
    ///
    /// 只处理"空白处"的点击：行里还有验证码按钮、箭头按钮、详情里的复制按钮，
    /// 它们自己的点击不能顺带把行收起来（否则点个复制就折叠了）。
    /// Avalonia 的 Button 会在 PointerPressed 里把事件标记为已处理，所以这里用 Tapped
    /// 并向上找最近的 Button，找到就跳过。
    /// </summary>
    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not VaultViewModel vm) return;
        if (sender is not Control { DataContext: CipherRow row }) return;
        if (IsInsideInteractive(e.Source as Visual)) return;

        vm.ToggleRow(row);
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
