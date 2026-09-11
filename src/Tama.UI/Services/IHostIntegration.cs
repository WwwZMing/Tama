namespace Tama.UI.Services;

/// <summary>
/// 宿主集成出口：Blazor 组件能触达平台外壳的**全部**能力（原生标题栏配色、Android 自动填充推送）。
///
/// 这是 UI 依赖宿主的唯一接口——业务数据一律走 <c>Tama.Core.Contracts</c> 里的领域契约接口
/// （进程内强类型直调），两者都不含任何 JSON，也没有 method 字符串。
///
/// 实现：
///   MAUI（Tama.App）        → MauiHostIntegration（缓存主题 + 转发原生外壳事件）
///   Blazor Server（Tama.Api）→ NoOpHostIntegration（无原生外壳，全部静默降级）
/// </summary>
public interface IHostIntegration
{
    /// <summary>把生效主题（"dark"/"light"）推给宿主：由平台外壳刷新原生标题栏/状态栏配色。</summary>
    void ApplyTheme(string resolvedMode);

    /// <summary>生效主题变化（平台外壳订阅：Windows 标题栏、Android 状态栏）。</summary>
    event Action<string>? ThemeChanged;

    /// <summary>宿主带来、待 UI 消费的自动填充目标（域名 / Android 包名）；无则 null。</summary>
    string? AutofillUrl { get; }

    /// <summary>自动填充目标变化（含清空）。宿主线程触发，订阅方自行 InvokeAsync 回渲染线程。</summary>
    event Action<string?>? AutofillUrlChanged;

    /// <summary>消费后清空自动填充目标（banner 关闭）。</summary>
    void ClearAutofillUrl();
}
