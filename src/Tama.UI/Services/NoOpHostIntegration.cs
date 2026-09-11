namespace Tama.UI.Services;

/// <summary>
/// 无原生外壳的宿主（Blazor Server / 单元测试）：宿主集成全部降级为空操作。
/// 浏览器模式没有原生标题栏可通知，也没有 Android Autofill 通道，静默吞掉即正确行为。
/// </summary>
public sealed class NoOpHostIntegration : IHostIntegration
{
    public string? AutofillUrl => null;

    // 永不触发的空事件：显式 add/remove 既表达"没有宿主"，也避免 CS0067（event 从未使用）告警
    public event Action<string>? ThemeChanged
    {
        add { }
        remove { }
    }

    public event Action<string?>? AutofillUrlChanged
    {
        add { }
        remove { }
    }

    public void ApplyTheme(string resolvedMode) { }

    public void ClearAutofillUrl() { }
}
