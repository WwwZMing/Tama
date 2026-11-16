namespace Tama.Avalonia.Services;

/// <summary>
/// 平台提供的无对话框导出兜底。桌面返回 null，走系统保存对话框；
/// Android 10+ 可写入公共 Downloads，不需要存储权限。
/// </summary>
public interface IExportFileService
{
    bool CanSaveDirectly { get; }

    Task<string?> TrySaveAsync(string fileName, string mimeType, byte[] bytes,
        CancellationToken cancellationToken = default);
}

/// <summary>没有平台专用落盘能力时的稳定降级。</summary>
public sealed class UnsupportedExportFileService : IExportFileService
{
    public bool CanSaveDirectly => false;

    public Task<string?> TrySaveAsync(string fileName, string mimeType, byte[] bytes,
        CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);
}
