using Microsoft.JSInterop;

namespace Tama.UI.Services;

/// <summary>
/// 主题状态三态（dark/light/system，对齐原版 settingsStore.theme，默认 dark）：
/// Mode = 用户选择的持久化值；Resolved = 实际生效值（system 经 matchMedia 解析为 dark/light）。
/// DOM/localStorage 走 theme.js；宿主标题栏由 MainLayout 监听 ResolvedChanged 后调 IHostIntegration.ApplyTheme。
/// system 模式下系统外观变化经 tamaTheme.watchSystem → Watcher 回流到 UpdateResolved。
/// </summary>
public static class ThemeState
{
    /// <summary>默认 dark（2026-09-06 用户拍板：深色为默认观感）；已持久化的用户选择优先。</summary>
    public static string Mode { get; private set; } = "dark";

    public static string Resolved { get; private set; } = "dark";

    /// <summary>用户选择变化（raw：dark/light/system）</summary>
    public static event Action<string>? Changed;

    /// <summary>实际生效主题变化（resolved：dark/light），含 system 模式下系统外观切换</summary>
    public static event Action<string>? ResolvedChanged;

    private static DotNetObjectReference<Watcher>? _watcherRef;

    public static void SetMode(string mode)
    {
        var m = mode is "light" or "dark" or "system" ? mode : "system";
        if (Mode == m) return;
        Mode = m;
        Changed?.Invoke(m);
        // system 的实际值由 JS 端 apply/watch 回报；dark/light 即 resolved
        if (m != "system") UpdateResolved(m);
    }

    private static void UpdateResolved(string resolved)
    {
        var r = resolved == "light" ? "light" : "dark";
        if (Resolved == r) return;
        Resolved = r;
        ResolvedChanged?.Invoke(r);
    }

    /// <summary>首次渲染后从 localStorage 恢复并同步 DOM（早于 Blazor 首帧的 index.html 内联脚本已设 data-theme 防闪）。
    /// 必须走事件路径（Changed/ResolvedChanged）：此前直接赋值导致订阅者（MainLayout 的 MudThemeProvider、
    /// 宿主标题栏补发）停在默认值——界面"碰巧深色"、标题栏却被真实值推成亮色，两者打架。
    /// <para>apply 传 animate: false —— 启动恢复绝不能播过渡动画（那会让每次开 app 都整屏淡一下，
    /// 且发生在首帧、Blazor 还没渲染完的时候）。</para></summary>
    public static async Task InitAsync(IJSRuntime js)
    {
        try
        {
            var raw = await js.InvokeAsync<string>("tamaTheme.get");
            var mode = raw is "light" or "dark" or "system" ? raw : "dark";
            // apply 会重设 DOM + localStorage 并返回解析结果（幂等）
            var resolved = await js.InvokeAsync<string>("tamaTheme.apply", mode, false);
            if (Mode != mode) { Mode = mode; Changed?.Invoke(mode); }
            UpdateResolved(resolved); // 内部判等：变化才发 ResolvedChanged
            if (Mode == "system") await StartWatchAsync(js);
        }
        catch { /* 非 WebView 环境静默回退 */ }
    }

    /// <summary>应用主题：改 DOM + localStorage + 广播；宿主标题栏由 MainLayout 监听 ResolvedChanged 统一处理。
    /// <para>apply 传 animate: true —— 只有用户主动切换才做 220ms 颜色过渡（CSS 见 tama.css 的 .tm-theme-anim）。</para></summary>
    public static async Task ApplyAsync(IJSRuntime js, string mode)
    {
        SetMode(mode);
        try
        {
            var resolved = await js.InvokeAsync<string>("tamaTheme.apply", mode, true);
            UpdateResolved(resolved);
            if (Mode == "system") await StartWatchAsync(js);
        }
        catch { }
    }

    /// <summary>侧栏按钮循环切换：light → dark → system → light（对齐原版 ThemeContext.toggleTheme）</summary>
    public static string NextMode() => Mode switch
    {
        "light" => "dark",
        "dark" => "system",
        _ => "light",
    };

    private static async Task StartWatchAsync(IJSRuntime js)
    {
        if (_watcherRef != null) return;
        _watcherRef = DotNetObjectReference.Create(new Watcher());
        try { await js.InvokeVoidAsync("tamaTheme.watchSystem", _watcherRef); } catch { }
    }

    /// <summary>JS 回调宿主：system 模式下系统外观变化（matchMedia change）</summary>
    private sealed class Watcher
    {
        [JSInvokable]
        public Task OnSystemThemeChanged(string resolved)
        {
            if (Mode == "system") UpdateResolved(resolved);
            return Task.CompletedTask;
        }
    }

    public static void Toggle() => SetMode(NextMode());
}
