using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;

namespace Tama.Avalonia.Android;

[Activity(
    Label = "Tama",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/Icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity<App>
{
}
