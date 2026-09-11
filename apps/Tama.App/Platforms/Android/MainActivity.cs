using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;

namespace Tama;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        if (Window?.DecorView != null)
        {
            Window.SetStatusBarColor(Android.Graphics.Color.ParseColor("#0f172a"));
            Window.SetNavigationBarColor(Android.Graphics.Color.ParseColor("#0f172a"));
            Window.ClearFlags(WindowManagerFlags.TranslucentStatus);
            Window.ClearFlags(WindowManagerFlags.TranslucentNavigation);
            Window.DecorView.SetFitsSystemWindows(true);
        }

        HandleAutofillIntent(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleAutofillIntent(intent);
    }

    private static void HandleAutofillIntent(Intent? intent)
    {
        var host = MauiHostIntegration.Current;

        // AutofillService 已直接写入目标（同一进程），这里只负责通知 WebView
        var url = host?.AutofillUrl;
        if (string.IsNullOrEmpty(url))
            url = intent?.GetStringExtra("autofill_url");

        if (!string.IsNullOrEmpty(url))
        {
            // 存入宿主状态并推给 Blazor 组件树（banner）
            host?.SetAutofillUrl(url);
        }
    }
}
