using Microsoft.Extensions.DependencyInjection;
using Tama.Core.Interfaces;

namespace Tama.Avalonia.Services;

/// <summary>
/// UI 会话 = 一个 DI 作用域（领域服务是 Scoped，DbContext 挂在上面）。
///
/// 锁定后 **重建**作用域：旧的 DbContext 连同里面缓存的实体一起丢弃，
/// 否则用户锁定之后，界面上的陈旧跟踪实体会在下次解锁时继续被引用（密码管理器里这是硬伤）。
/// 会话作用域始终存在（解锁前也有），这样解锁页可以直接用 <c>IAuthApi</c>，
/// 不需要为"登录前"再造第二套解析路径。
/// </summary>
public sealed class AppSession : IDisposable
{
    private readonly IServiceProvider _root;
    private readonly object _gate = new();
    private IServiceScope _scope;
    private bool _disposed;

    public AppSession(IServiceProvider root)
    {
        _root = root;
        _scope = root.CreateScope();
    }

    /// <summary>当前会话作用域。**页面一律从这里解析 Scoped 服务**，别用根容器。</summary>
    public IServiceProvider Services => _scope.ServiceProvider;

    /// <summary>锁定时重建作用域，并让单例的密钥服务清掉内存里的主密码。</summary>
    public void Reset()
    {
        IServiceScope old;
        lock (_gate)
        {
            if (_disposed) return;
            old = _scope;
            _scope = _root.CreateScope();
        }

        try { _root.GetRequiredService<IAuthSession>().Lock(); }
        finally { old.Dispose(); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _scope.Dispose();
    }
}
