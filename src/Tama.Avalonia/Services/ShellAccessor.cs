namespace Tama.Avalonia.Services;

/// <summary>
/// 壳的**访问点**：页面 VM 由 DI 从会话作用域里造出来，而壳是进程级单例、不进容器
/// （它持有 AppSession，容器里的生命周期表达不了这件事）。
/// 用一个只放引用的单例把两边接上：页面注入它，壳在构造时把 <see cref="Value"/> 填好。
///
/// 为什么不给页面注入事件、回调之类的替代品：页面几乎都需要"提示一句 + 跳一页 + 锁定"，
/// 三件事都走 <see cref="ViewModels.IShell"/>，多一条路径就多一种不一致。
/// </summary>
public sealed class ShellAccessor
{
    public ViewModels.IShell? Value { get; set; }

    /// <summary>壳一定在页面之前建好；为 null 说明宿主接线错了，早点炸比静默失效好。</summary>
    public ViewModels.IShell Shell => Value
        ?? throw new InvalidOperationException("Shell is not attached yet — platform host must create ShellViewModel first.");
}
