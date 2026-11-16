using Android.App;
using Android.Content;

namespace Tama.Avalonia.Android;

/// <summary>
/// 进程级的 Android 平台入口：当前 Activity。
///
/// 为什么需要它：生物识别弹窗（<c>BiometricPrompt</c>）必须拿到一个**活着的 Activity**，
/// 而自动填充服务（<c>AutofillService</c>）是没有任何界面的组件；Avalonia 12 也没有公开
/// "当前 Activity" 的访问点（<c>AvaloniaActivity</c> 类型公开，实例不公开）。
/// 于是由 <see cref="MainActivity"/> 自己登记/注销一个静态引用——这比反射进 Avalonia 内部稳定得多。
///
/// 线程：Avalonia 的 UI 线程就是 Android 主线程，所以这里不需要额外编组。
/// </summary>
internal static class AndroidPlatform
{
    private static Activity? s_resumedActivity;

    /// <summary>前台 Activity（MainActivity 在 OnCreate/OnResume 登记，OnDestroy 注销）。</summary>
    public static Activity? CurrentActivity { get; set; }

    /// <summary>Activity 是否已经真正进入 Resumed，而不只是 OnCreate 里登记过。</summary>
    public static bool IsResumed(Activity activity)
        => ReferenceEquals(Volatile.Read(ref s_resumedActivity), activity);

    public static void MarkResumed(Activity activity)
        => Volatile.Write(ref s_resumedActivity, activity);

    public static void MarkPaused(Activity activity)
    {
        if (ReferenceEquals(Volatile.Read(ref s_resumedActivity), activity))
            Volatile.Write(ref s_resumedActivity, null);
    }

    /// <summary>
    /// 任何 Android 组件都能用的 Context：优先当前 Activity，否则退到 Application。
    /// SharedPreferences / KeyStore 这类只需要 Context 的调用一律用它，免得
    /// "Activity 还没建好"时拿不到上下文（自动填充服务就是这种时机）。
    /// </summary>
    public static Context Context => (Context?)CurrentActivity ?? Application.Context;
}
