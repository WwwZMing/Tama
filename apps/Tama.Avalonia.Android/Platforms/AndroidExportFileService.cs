using Android.Content;
using Android.OS;
using Android.Provider;
using System.Runtime.Versioning;
using Tama.Avalonia.Services;

namespace Tama.Avalonia.Android.Platforms;

/// <summary>
/// Android 10+ 的无权限直接导出：通过 MediaStore 写入公共“下载/Tama”。
/// 不使用 SAF 的 CREATE_DOCUMENT，因此不受 OEM 文件提供器对 "wt" 模式的兼容性影响。
/// </summary>
internal sealed class AndroidExportFileService : IExportFileService
{
    public bool CanSaveDirectly => OperatingSystem.IsAndroidVersionAtLeast(29);

    public Task<string?> TrySaveAsync(string fileName, string mimeType, byte[] bytes,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29))
            return Task.FromResult<string?>(null);

        return SaveApi29Async(fileName, mimeType, bytes, cancellationToken);
    }

    [SupportedOSPlatform("android29.0")]
    private static async Task<string?> SaveApi29Async(string fileName, string mimeType, byte[] bytes,
        CancellationToken cancellationToken)
    {
        var resolver = AndroidPlatform.Context.ContentResolver;
        if (resolver is null) return null;

        var values = new ContentValues();
        values.Put(MediaStore.IMediaColumns.DisplayName, fileName);
        values.Put(MediaStore.IMediaColumns.MimeType, mimeType);
        var downloads = global::Android.OS.Environment.DirectoryDownloads;
        values.Put(MediaStore.IMediaColumns.RelativePath, $"{downloads}/Tama");

        var collection = MediaStore.Downloads.GetContentUri(MediaStore.VolumeExternalPrimary);
        var uri = resolver.Insert(collection, values)
                  ?? throw new IOException("系统下载目录拒绝创建文件");

        try
        {
            await using var stream = resolver.OpenOutputStream(uri, "w")
                                     ?? throw new IOException("无法打开导出文件写入流");
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);

            return $"下载/Tama/{fileName}";
        }
        catch
        {
            resolver.Delete(uri, null, null);
            throw;
        }
    }
}
