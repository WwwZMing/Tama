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
public interface IShell
{
    void Navigate(AppPage page);
    void Toast(string message);
    void Lock();

    /// <summary>把内容推到全窗口浮层上，直到 <see cref="CloseOverlay"/> 被调用才返回。</summary>
    Task ShowOverlayAsync(Control content);

    void CloseOverlay();
}
