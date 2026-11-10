#if DEBUG
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Tama.Avalonia.ViewModels;

namespace Tama.Avalonia;

/// <summary>
/// **仅 Debug 构建存在**的界面快照工具（发布版里 #if DEBUG 整段消失）。
///
/// 为什么需要它：这台机器上没有可用的截屏手段（PowerShell 的 Add-Type 被安全策略拦掉），
/// 而"界面到底长什么样"不能靠猜。渲染走 Avalonia 的 RenderTargetBitmap，
/// 顺带也证明了控件树真的渲染出来了（不只是编译通过）。
///
/// 环境变量：
///   TAMA_UI_SNAPSHOT            png 输出路径（必填，否则什么都不做）
///   TAMA_UI_SNAPSHOT_PASSWORD   自动解锁用的主密码（只在 TAMA_DATA_DIR 已重定向时生效）
///   TAMA_UI_SNAPSHOT_NAV        导航到的页面名（Vault / Generator / Watchtower / Accounts / Passkeys / Settings）
///   TAMA_UI_SNAPSHOT_PICK       保险库页要选中的条目名（打开详情）
/// </summary>
internal static class SnapshotAutomation
{
    public static async Task CaptureAsync(Window window, ShellViewModel shell,
        IClassicDesktopStyleApplicationLifetime lifetime)
    {
        var path = Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT");
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var password = Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_PASSWORD");
            if (!string.IsNullOrEmpty(password)) await shell.TryUnlockForAutomationAsync(password);

            // TAMA_UI_SNAPSHOT_SIZE=412x915：把窗口调成手机尺寸，验证窄屏布局（IsCompact 由窗口宽度驱动）
            var sizeText = Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_SIZE");
            if (!string.IsNullOrWhiteSpace(sizeText))
            {
                var parts = sizeText.Split('x', 'X');
                if (parts.Length == 2 && double.TryParse(parts[0], out var w) && double.TryParse(parts[1], out var h))
                {
                    window.Width = w;
                    window.Height = h;
                    await Task.Delay(400);   // 等布局按新宽度重算（IsCompact 由 Bounds 变化触发）
                }
            }

            var nav = Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_NAV");
            if (Enum.TryParse<AppPage>(nav, out var page) && shell.IsUnlocked)
            {
                ((IShell)shell).Navigate(page);
                await Task.Delay(700);
            }

            var pick = Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_PICK");
            if (!string.IsNullOrEmpty(pick)) shell.OpenCipherForAutomation(pick);

            // 等布局与首帧数据落地
            await Task.Delay(1000);

            var size = new PixelSize(
                Math.Max(1, (int)Math.Round(window.Bounds.Width)),
                Math.Max(1, (int)Math.Round(window.Bounds.Height)));
            using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
            bitmap.Render(window);
            bitmap.Save(path, PngBitmapEncoderOptions.Default);
            Serilog.Log.Information("UI snapshot saved to {Path}", path);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "UI snapshot failed");
        }
        finally
        {
            lifetime.Shutdown();
        }
    }
}
#endif
