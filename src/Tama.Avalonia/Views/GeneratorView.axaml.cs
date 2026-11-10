using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Tama.Avalonia.Views;

public partial class GeneratorView : UserControl
{
    public GeneratorView() => AvaloniaXamlLoader.Load(this);
}
