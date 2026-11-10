using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace Tama.Avalonia.Services;

/// <summary>
/// 剪贴板：复制密码后 **30 秒自动清空**（PRD 的硬要求，不是可选项）。
///
/// 只清"还是自己写进去的那一份"：用户中途复制了别的东西，30 秒一到就把别人的内容抹掉
/// 是很讨厌的行为——所以清空前先比对当前文本。
/// </summary>
public sealed class ClipboardService
{
    private const int ClearSeconds = 30;

    /// <summary>由 App 在窗口建好后塞进来（没有窗口就拿不到 TopLevel）。</summary>
    public Func<TopLevel?>? TopLevelProvider { get; set; }

    private CancellationTokenSource? _clearCts;

    public async Task CopyAsync(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var topLevel = TopLevelProvider?.Invoke();
        if (topLevel?.Clipboard is not { } clipboard) return;

        try
        {
            await clipboard.SetTextAsync(text);
        }
        catch
        {
            // 剪贴板被别的进程占着：不算致命错误，静默失败（界面别为此弹窗）
            return;
        }

        _clearCts?.Cancel();
        var cts = new CancellationTokenSource();
        _clearCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(ClearSeconds), cts.Token);
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    try
                    {
                        // 只清自己写的那一份
                        if (await clipboard.TryGetTextAsync() == text) await clipboard.ClearAsync();
                    }
                    catch { /* 已经不可用就算了 */ }
                });
            }
            catch (OperationCanceledException)
            {
                // 又被复制了一次，计时重置
            }
        }, cts.Token);
    }
}
