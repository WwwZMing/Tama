using Tama.Core.Contracts;

namespace Tama.Core.Interfaces;

/// <summary>
/// 一次**后台**拉取同步完成的通知面。解锁后会自动同步一次（见 <c>AuthService.Unlock</c>），
/// 那是后台跑的：页面在这之前就已经用本地数据渲染好了，所以同步回来得有人告诉它"该重读了"。
///
/// 为什么是接口 + 单例：发布方在 Tama.Services、订阅方在 Tama.UI，两边只能共同依赖 Tama.Core；
/// 它本身无状态（只是个事件汇总点），注册成 Singleton 是刻意的——参见架构规则 7 针对的是领域服务。
/// </summary>
public interface IVaultSyncNotifier
{
    /// <summary>后台同步完成（成功才触发；失败只记日志，不打扰界面）。</summary>
    event Action<VaultSyncResult>? Completed;

    /// <summary>由同步方调用（不在 UI 层）。</summary>
    void Raise(VaultSyncResult result);
}

/// <summary>默认实现：把事件转给订阅者，订阅者异常必须被吞掉（一个页面出错不能影响同步本身）。</summary>
public sealed class VaultSyncNotifier : IVaultSyncNotifier
{
    public event Action<VaultSyncResult>? Completed;

    public void Raise(VaultSyncResult result) => Completed?.Invoke(result);
}
