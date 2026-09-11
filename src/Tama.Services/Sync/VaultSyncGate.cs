namespace Tama.Services.Sync;

/// <summary>
/// 进程级同步闩：**同一次全量拉取只能有一个在跑**。
///
/// 为什么必须是进程级：领域服务都是 Scoped，而"解锁时的后台同步""账号页的手动同步""定时轮询"
/// 可能分别在三个 DI scope（甚至两个浏览器电路 = 两个 scope）里同时发生。以前这个闩是
/// <c>AuthService</c> 的**实例字段**，只能挡住同一个电路里的重复解锁——两路全量拉取会同时
/// 往同一批行上写（后写的赢），界面上还会各弹一次"已从云端同步"。
///
/// 刻意不做成"等待"（没有 WaitAsync）：等一个正在跑的同步没有意义（它拉完就是最新），
/// 直接跳过这一轮才对。
/// </summary>
public sealed class VaultSyncGate
{
    private int _running;

    /// <summary>拿到闸门返回 true；已经有同步在跑返回 false（调用方应当直接跳过）。</summary>
    public bool TryEnter() => Interlocked.CompareExchange(ref _running, 1, 0) == 0;

    public void Exit() => Volatile.Write(ref _running, 0);
}
