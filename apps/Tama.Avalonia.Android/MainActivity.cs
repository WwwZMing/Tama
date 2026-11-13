using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Avalonia.Android;

namespace Tama.Avalonia.Android;

/// <summary>
/// Avalonia 12 的 <c>AvaloniaMainActivity</c> 是**非泛型**的：具体 App 类型由
/// <see cref="TamaApplication"/>（<c>AvaloniaAndroidApplication&lt;App&gt;</c>）指定。
/// 别再写成 <c>AvaloniaMainActivity&lt;App&gt;</c>——那是 11.x 时代的写法，编译直接 CS0308。
///
/// 它同时承担"给平台服务提供当前 Activity"的职责：生物识别弹窗（BiometricPrompt）必须拿到
/// 一个活着的 <c>FragmentActivity</c>，而 <c>AvaloniaActivity : AppCompatActivity : FragmentActivity</c>，
/// 正好满足。实例在这里登记/注销，见 <see cref="AndroidPlatform"/>。
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
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // 先登记再走 base：Avalonia 的主机在 base 里建界面，期间可能就有平台调用
        AndroidPlatform.CurrentActivity = this;
        base.OnCreate(savedInstanceState);
    }

    protected override void OnResume()
    {
        // 从后台回来/自动填充跳回来时会走这里：Activity 还是同一个实例，重新登记一次更保险
        AndroidPlatform.CurrentActivity = this;
        base.OnResume();
    }

    protected override void OnDestroy()
    {
        // 只注销自己：Activity 重建（旋转屏等）时新实例已经登记过了，别把新的清掉
        if (ReferenceEquals(AndroidPlatform.CurrentActivity, this))
            AndroidPlatform.CurrentActivity = null;
        base.OnDestroy();
    }
}
