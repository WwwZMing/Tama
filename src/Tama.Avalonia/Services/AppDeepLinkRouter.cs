using Avalonia.Threading;

namespace Tama.Avalonia.Services;

/// <summary>
/// Android 系统相机扫描二维码后会以 ACTION_VIEW 打开应用。Activity 可能先于
/// Avalonia 页面创建，所以这里先暂存 URI，等解锁页注册处理器后再投递到 UI 线程。
/// </summary>
public static class AppDeepLinkRouter
{
    private static readonly object Gate = new();
    private static Action<string>? _handler;
    private static string? _pendingUri;

    public static void Receive(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)
            || (!uri.StartsWith("tama://pair", StringComparison.OrdinalIgnoreCase)
                && !uri.StartsWith("tama://login", StringComparison.OrdinalIgnoreCase)))
            return;

        Action<string>? handler;
        lock (Gate)
        {
            handler = _handler;
            if (handler is null)
            {
                _pendingUri = uri;
                return;
            }
        }

        Dispatcher.UIThread.Post(() => handler(uri));
    }

    public static void Register(Action<string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        string? pending;
        lock (Gate)
        {
            _handler = handler;
            pending = _pendingUri;
            _pendingUri = null;
        }

        if (pending is not null)
            Dispatcher.UIThread.Post(() => handler(pending));
    }
}
