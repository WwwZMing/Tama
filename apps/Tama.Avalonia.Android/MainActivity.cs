using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AndroidX.Core.View;
using Avalonia.Android;
using Tama.Avalonia.Services;
using System.Runtime.Versioning;

namespace Tama.Avalonia.Android;

/// <summary>
/// Avalonia 12 的 <c>AvaloniaMainActivity</c> 是**非泛型**的：具体 App 类型由
/// <see cref="TamaApplication"/>（<c>AvaloniaAndroidApplication&lt;App&gt;</c>）指定。
/// 别再写成 <c>AvaloniaMainActivity&lt;App&gt;</c>——那是 11.x 时代的写法，编译直接 CS0308。
///
/// 它同时承担"给平台服务提供当前 Activity"的职责：生物识别弹窗（BiometricPrompt）必须拿到
/// 一个活着的 <c>FragmentActivity</c>，而 <c>AvaloniaActivity : AppCompatActivity : FragmentActivity</c>，
/// 正好满足。实例在这里登记/注销，见 <see cref="AndroidPlatform"/>。
///
/// ⚠ 系统栏 / edge-to-edge **不在这里做**：Avalonia 12 有 <c>TopLevel.InsetsManager</c>
/// （<c>DisplayEdgeToEdgePreference</c> / <c>SystemBarColor</c> / <c>SafeAreaPadding</c>），
/// 那套是平台无关的、写在 <c>App.ConfigureMobileInsets</c> 里。这里手搓
/// <c>SetDecorFitsSystemWindows</c> 会被 Avalonia 自己的 insets 逻辑覆盖掉，实测状态栏仍是一条黑。
/// </summary>
[Activity(
    Label = "Tama",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/Icon",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    // AdjustResize：不写这个的话软键盘弹出来会**盖住**输入框（主密码、搜索、TOTP），
    // 而且整个窗口不会跟着缩，用户看不到自己在打什么。
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataScheme = "tama",
    DataHost = "pair")]
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataScheme = "tama",
    DataHost = "login")]
public class MainActivity : AvaloniaMainActivity
{
    private const float TargetRefreshRate = 120f;
    private KeyboardLayoutListener? _keyboardLayoutListener;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // 先登记再走 base：Avalonia 的主机在 base 里建界面，期间可能就有平台调用
        AndroidPlatform.CurrentActivity = this;
        AppDeepLinkRouter.Receive(Intent?.DataString);
        base.OnCreate(savedInstanceState);

        _keyboardLayoutListener = new KeyboardLayoutListener(this);
        Window?.DecorView?.ViewTreeObserver?.AddOnGlobalLayoutListener(_keyboardLayoutListener);
        Window?.DecorView?.Post(UpdateKeyboardInset);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        AppDeepLinkRouter.Receive(intent?.DataString);
    }

    protected override void OnResume()
    {
        // 从后台回来/自动填充跳回来时会走这里：Activity 还是同一个实例，重新登记一次更保险
        AndroidPlatform.CurrentActivity = this;
        base.OnResume();
        RequestHighRefreshRate();
        AndroidPlatform.MarkResumed(this);
        Window?.DecorView?.Post(UpdateKeyboardInset);
    }

    protected override void OnPause()
    {
        AndroidPlatform.MarkPaused(this);
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        // 只注销自己：Activity 重建（旋转屏等）时新实例已经登记过了，别把新的清掉
        AndroidPlatform.MarkPaused(this);
        if (ReferenceEquals(AndroidPlatform.CurrentActivity, this))
            AndroidPlatform.CurrentActivity = null;
        var observer = Window?.DecorView?.ViewTreeObserver;
        if (observer is { IsAlive: true } && _keyboardLayoutListener is not null)
            observer.RemoveOnGlobalLayoutListener(_keyboardLayoutListener);
        _keyboardLayoutListener = null;
        base.OnDestroy();
    }

    private void UpdateKeyboardInset()
    {
        var decor = Window?.DecorView;
        if (decor is null) return;

        var rootInsets = ViewCompat.GetRootWindowInsets(decor);
        var ime = rootInsets?.GetInsets(WindowInsetsCompat.Type.Ime());
        var verticalPixels = ime?.Bottom ?? 0;
        var density = Resources?.DisplayMetrics?.Density ?? 1;
        var dip = verticalPixels / Math.Max(1, density);
        KeyboardInset.Update(dip);
    }

    private sealed class KeyboardLayoutListener(MainActivity owner)
        : Java.Lang.Object, ViewTreeObserver.IOnGlobalLayoutListener
    {
        public void OnGlobalLayout() => owner.UpdateKeyboardInset();
    }

    /// <summary>
    /// Avalonia 的 Android SurfaceView 默认向系统报 NoPreference，MIUI 会按省电策略
    /// 回落到 60Hz。这里明确请求 120Hz；60Hz 设备与不支持的无缝切换会由系统自动选最近档。
    /// </summary>
    private void RequestHighRefreshRate()
    {
        if (Window is not { } window) return;

        var attributes = window.Attributes;
        if (attributes is not null)
        {
            attributes.PreferredRefreshRate = TargetRefreshRate;
            window.Attributes = attributes;
        }

        // Avalonia Android 的实际画面在独立 SurfaceView 上；只改 Window attributes，
        // SurfaceFlinger 仍会看到 NoPreference。直接给该 Surface 设置帧率。
        ApplySurfaceFrameRate(window.DecorView, attemptsRemaining: 5);
    }

    private static void ApplySurfaceFrameRate(View? root, int attemptsRemaining)
    {
        if (root is null || !OperatingSystem.IsAndroidVersionAtLeast(30)) return;

        if (root is SurfaceView surfaceView)
        {
            var surface = surfaceView.Holder?.Surface;
            if (surface is { IsValid: true })
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(30))
                    SetSurfaceFrameRate(surface);
                return;
            }

            if (attemptsRemaining > 0)
                surfaceView.PostDelayed(
                    () => ApplySurfaceFrameRate(surfaceView, attemptsRemaining - 1), 100);
            return;
        }

        if (root is not ViewGroup group) return;
        for (var i = 0; i < group.ChildCount; i++)
            ApplySurfaceFrameRate(group.GetChildAt(i), attemptsRemaining);
    }

    [SupportedOSPlatform("android30.0")]
    private static void SetSurfaceFrameRate(Surface surface)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(36))
            SetSurfaceFrameRateAtLeast(surface);
        else
            SetSurfaceFrameRateDefault(surface);
    }

    [SupportedOSPlatform("android30.0")]
    private static void SetSurfaceFrameRateDefault(Surface surface)
        => surface.SetFrameRate(TargetRefreshRate, (int)SurfaceFrameRateCompatibility.Default);

    [SupportedOSPlatform("android36.0")]
    private static void SetSurfaceFrameRateAtLeast(Surface surface)
        => surface.SetFrameRate(TargetRefreshRate, (int)SurfaceFrameRateCompatibility.AtLeast);
}
