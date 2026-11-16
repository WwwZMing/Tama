using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Tama.Avalonia.ViewModels;

namespace Tama.Avalonia.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => AvaloniaXamlLoader.Load(this);

    private async void OnCodeFieldGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (sender is not Control control) return;

        if (DataContext is SettingsViewModel settings)
            settings.IsTrustedDeviceCodeEntryFocused = true;

        foreach (var delay in new[] { 0, 80, 220, 450, 700 })
        {
            if (delay > 0) await Task.Delay(delay);
            control.BringIntoView();
        }
    }

    private void OnCodeFieldLostFocus(object? sender, FocusChangedEventArgs e)
    {
        if (DataContext is SettingsViewModel settings)
            settings.IsTrustedDeviceCodeEntryFocused = false;
    }
}
