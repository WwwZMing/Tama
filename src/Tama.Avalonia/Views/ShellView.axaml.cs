using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Tama.Avalonia.Views;

public partial class ShellView : UserControl
{
    public ShellView() => AvaloniaXamlLoader.Load(this);
}
