using Android.App;
using Android.Content.PM;
using Avalonia.Android;

namespace Tama.Avalonia.Android;

/// <summary>
/// Avalonia 12 的 <c>AvaloniaMainActivity</c> 是**非泛型**的：具体 App 类型由
/// <see cref="TamaApplication"/>（<c>AvaloniaAndroidApplication&lt;App&gt;</c>）指定。
/// 别再写成 <c>AvaloniaMainActivity&lt;App&gt;</c>——那是 11.x 时代的写法，编译直接 CS0308。
/// </summary>
[Activity(
    Label = "Tama",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/Icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
}
