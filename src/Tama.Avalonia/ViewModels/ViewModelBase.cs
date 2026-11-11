using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Tama.Avalonia.ViewModels;

/// <summary>壳里可导航的页面。刻意是枚举而不是字符串路由：编译期就能查到"少了哪一页"。</summary>
public enum AppPage
{
    Vault,
    Generator,
    Watchtower,
    Accounts,
    Passkeys,
    Settings,
}

/// <summary>
/// 页面 VM 基类：统一的忙碌标志 / 错误文案。
///
/// 约定与 Blazor 版本一致——**业务失败一律是异常**（<c>TamaException</c>），
/// 页面 catch 住把 <c>ex.Message</c> 放进 <see cref="Error"/>，没有返回值信封。
/// </summary>
public abstract partial class PageViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _error;

    /// <summary>
    /// 窄屏（手机 / 窄窗口）。由壳统一下发（导航时 + 窗口宽度变化时），页面据此收窄边距、
    /// 隐藏与顶栏重复的标题、把分栏切成单栏。
    ///
    /// 放在基类而不是各页面自己定义：否则每个页面都要从壳那里知道一次，漏一个就是"某个页面在手机上还是桌面布局"。
    /// </summary>
    [ObservableProperty]
    private bool _isCompact;

    /// <summary>窄屏切换时的钩子（页面要重算自己的派生属性就覆盖它）。</summary>
    protected virtual void OnCompactChanged(bool isCompact) { }

    partial void OnIsCompactChanged(bool value) => OnCompactChanged(value);

    /// <summary>每次进入该页调用（刷新数据）。返回的 Task 由壳 await。</summary>
    public virtual Task OnActivatedAsync() => Task.CompletedTask;

    protected void ClearError() => Error = null;

    /// <summary>统一的失败落点：记日志（含堆栈）+ 把可读的话给界面。</summary>
    protected void Fail(Exception ex, string? fallback = null)
    {
        Serilog.Log.Error(ex, "{Page} failed", GetType().Name);
        Error = string.IsNullOrWhiteSpace(ex.Message) ? fallback ?? "操作失败" : ex.Message;
    }
}

/// <summary>页面能触达壳的**全部**能力：导航、提示、锁定、浮层对话框。没有别的入口。</summary>
/// <summary>提示的语气：决定颜色与图标。别再用"一条灰卡片"糊过去。</summary>
public enum ToastKind
{
    Info,
    Success,
    Error,
}

public interface IShell
{
    void Navigate(AppPage page);
    void Toast(string message, ToastKind kind = ToastKind.Info);
    void Lock();

    /// <summary>把内容推到全窗口浮层上，直到 <see cref="CloseOverlay"/> 被调用才返回。</summary>
    Task ShowOverlayAsync(Control content);

    void CloseOverlay();
}
