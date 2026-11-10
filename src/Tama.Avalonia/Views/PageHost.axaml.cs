using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Tama.Avalonia.Views;

public partial class PageHost : UserControl
{
    public PageHost() => AvaloniaXamlLoader.Load(this);
}
