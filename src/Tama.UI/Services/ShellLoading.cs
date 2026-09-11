namespace Tama.UI.Services;

/// <summary>
/// 解锁转场的加载层协调器（Scoped）。
///
/// 设计：解锁走"乐观切换"——点解锁立刻切进 shell 并升起半透明加载层，
/// 首屏数据落地后由页面调 <see cref="Ready"/> 收起，遮罩淡出、内容逐级淡入。
/// 这样密钥派生的几百毫秒发生在"界面里"，而不是"界面外"，感知上等于瞬间进入。
///
/// 页面不通知也不会永久遮罩：MainLayout 侧有 <see cref="WaitAsync"/> 超时兜底。
/// </summary>
public class ShellLoading
{
    private TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>是否正处于转场（加载层可见）</summary>
    public bool Busy { get; private set; }

    public event Action? Changed;

    /// <summary>升起加载层（解锁开始 / 重新进入转场）</summary>
    public void Begin()
    {
        Busy = true;
        _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Changed?.Invoke();
    }

    /// <summary>
    /// 首屏数据已就绪 → 收起加载层，并**无条件**广播一次状态。
    ///
    /// 为什么是无条件：进程内导航时 <see cref="Busy"/> 一直是 false，
    /// 按老写法（busy 早退）就永远不广播，而遮罩收起这件事需要通知布局。
    /// 重复调用安全：遮罩状态幂等。
    ///
    /// ⚠ **这条广播不再触发"内容进场"动画**（2026-09-12 修"切页一闪一闪"）：
    /// 每个页面首次渲染都会调它，如果每次广播都补放一次进场，一次导航就会连闪两下
    /// （路由一下 + 数据落地一下，实测相隔约 700ms）。现在只有**遮罩真的收起**那一次
    /// 才算进场时机，普通导航由 MainLayout 在路由变化时放。
    /// </summary>
    public void Ready()
    {
        Busy = false;
        _ready.TrySetResult();
        Changed?.Invoke();
    }

    /// <summary>等页面通知就绪，最多等 timeoutMs（页面没接入也不至于永久遮罩）。</summary>
    public Task WaitAsync(int timeoutMs) => Task.WhenAny(_ready.Task, Task.Delay(timeoutMs));
}
