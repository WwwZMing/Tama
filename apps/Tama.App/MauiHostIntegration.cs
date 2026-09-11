using Tama.Core;
using Tama.UI.Services;

namespace Tama;

/// <summary>
/// MAUI 宿主的 <see cref="IHostIntegration"/> 实现。
///
/// 单例：一个进程只有一棵 BlazorWebView 组件树，宿主状态天然全局唯一。
/// 取代重构前的三件套（TamaBridge 静态事件 + TamaBridge.PendingAutofillUrl + BridgeEvents 静态总线）——
/// 那些静态成员在 Blazor Server 多电路场景下会跨用户串味，而这里是一个可注入、可测试的普通服务。
/// </summary>
public sealed class MauiHostIntegration : IHostIntegration
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<MauiHostIntegration>();

    private string? _autofillUrl;
    private string? _cachedThemeMode;

    /// <summary>生效主题变化（原生外壳订阅：Windows 标题栏配色）。</summary>
    public event Action<string>? ThemeChanged;

    /// <summary>自动填充目标变化（Blazor 侧订阅；宿主线程触发）。</summary>
    public event Action<string?>? AutofillUrlChanged;

    public string? AutofillUrl => _autofillUrl;

    /// <summary>平台代码（Android Autofill 服务 / MainActivity）取当前实例；应用尚未启动时为 null。</summary>
    public static MauiHostIntegration? Current =>
        IPlatformApplication.Current?.Services.GetService<MauiHostIntegration>();

    public void ApplyTheme(string resolvedMode)
    {
        // 主题缓存只服务于"下次启动时先给标题栏上色"，与本次切换无关：
        // 丢到线程池写盘，**绝不**在 Blazor 渲染路径上做同步 I/O——切主题时 MainLayout 正在
        // 重渲染（Mud 调色板 + 全站配色过渡），在这里同步写文件会直接拖慢/打断那一次过渡。
        if (!string.Equals(_cachedThemeMode, resolvedMode, StringComparison.Ordinal))
        {
            _cachedThemeMode = resolvedMode;
            _ = Task.Run(() => WriteThemeCache(resolvedMode));
        }

        ThemeChanged?.Invoke(resolvedMode);
    }

    /// <summary>写入 %LOCALAPPDATA%\Tama\theme-mode.txt（供下次启动在 Blazor 电路起来前上色）。</summary>
    private static void WriteThemeCache(string resolvedMode)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(AppPaths.ThemeModePath, resolvedMode);
        }
        catch (Exception ex)
        {
            // 缓存失败不影响主题切换本身
            Log.Warning(ex, "写入 theme-mode.txt 失败");
        }
    }

    /// <summary>
    /// Android Autofill 带来的目标（域名 / 包名）：既作为"待消费"状态存下来，
    /// 又立即推给已在运行的 Blazor 组件树（banner）。两者原先是分开的两条路径，现已合一。
    /// </summary>
    public void SetAutofillUrl(string? url)
    {
        _autofillUrl = url;
        AutofillUrlChanged?.Invoke(url);
    }

    public void ClearAutofillUrl()
    {
        _autofillUrl = null;
        AutofillUrlChanged?.Invoke(null);
    }
}
