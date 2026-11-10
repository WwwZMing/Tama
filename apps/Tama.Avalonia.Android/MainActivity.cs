using Android.App;
using Android.Content.PM;
using Android.Views;
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
    // AdjustResize：不写这个的话软键盘弹出来会**盖住**输入框（主密码、搜索、TOTP），
    // 而且整个窗口不会跟着缩，用户看不到自己在打什么。
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
}
