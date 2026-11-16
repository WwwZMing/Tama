using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace Tama.Avalonia.Views;

public partial class TrustedDeviceImportView : UserControl
{
    public TrustedDeviceImportView() => AvaloniaXamlLoader.Load(this);

    private async void OnCodeFieldGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (sender is not Control control) return;

        foreach (var delay in new[] { 0, 80, 220, 450 })
        {
            if (delay > 0) await Task.Delay(delay);
            control.BringIntoView();
        }
    }
}
