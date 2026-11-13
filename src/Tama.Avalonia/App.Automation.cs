#if DEBUG
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Tama.Avalonia.ViewModels;
using Tama.Core.Interfaces;

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

            // TAMA_UI_SNAPSHOT_WAIT_UNLOCK=<秒>：真实库没法自动解锁（自动化明确拒绝在 TAMA_DATA_DIR
            // 之外解锁，避免碰用户真实密码），所以这个开关让人先解锁，解锁后自动截图再退出。
            var waitText = Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_WAIT_UNLOCK");
            if (string.IsNullOrEmpty(password) && int.TryParse(waitText, out var waitSeconds) && waitSeconds > 0)
            {
                var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
                while (!shell.IsUnlocked && DateTime.UtcNow < deadline) await Task.Delay(400);

                if (!shell.IsUnlocked)
                {
                    Serilog.Log.Error("Snapshot: waited {Seconds}s for unlock, giving up", waitSeconds);
                    lifetime.Shutdown();
                    return;
                }

                await Task.Delay(600);   // 等首屏数据落地
            }

            // TAMA_UI_SNAPSHOT_SEED_BIO=1：把当前密码加密存成"生物识别凭据"，好让下一次启动
            // 的解锁页真的出现 Hello 按钮（否则空库里永远没有 biometric.dat，截不到那个状态）。
            // 只在 TAMA_DATA_DIR 已重定向时执行 —— 不会碰真实数据目录。
            if (Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_SEED_BIO") == "1"
                && !string.IsNullOrEmpty(password)
                && Tama.Core.AppPaths.IsDataDirOverridden)
            {
                var bio = App.HostEnvironment?.Session.Services.GetRequiredService<IBiometricService>();
                if (bio is not null)
                {
                    bio.SetupKey();
                    var encrypted = bio.EncryptPassword(password);
                    if (!string.IsNullOrEmpty(encrypted)) bio.SaveEncryptedPassword(encrypted);
                    Serilog.Log.Information("Snapshot: biometric credential seeded (available={Available})", bio.IsAvailable());
                }
            }

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

            // TAMA_UI_SNAPSHOT_DIALOG=folders：打开文件夹管理对话框。
            // 对话框在手机上是底部抽屉、桌面是居中卡片，两种形态都得看一眼。
            var dialog = Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_DIALOG");
            if (dialog == "folders" && shell.CurrentPage is VaultViewModel vault)
            {
                vault.ManageFoldersCommand.Execute(null);
                await Task.Delay(900);
            }

            // TAMA_UI_SNAPSHOT_SWIPE=1 / -1：直接走一次滑动切页（验证手势背后的导航逻辑与 tab 高亮，
            // 手势识别本身靠真机验证）
            var swipe = Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_SWIPE");
            if (!string.IsNullOrWhiteSpace(swipe) && int.TryParse(swipe, out var swipeDir))
            {
                shell.SwipeNavigate(swipeDir);
                await Task.Delay(600);
            }

            // TAMA_UI_SNAPSHOT_NEW=login|note：走一次"新建条目"。
            // 窄屏下它是**壳内浮层**（新加的行为），桌面是右栏——两种形态都要能截出来核对。
            var newKind = Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_NEW");
            if (!string.IsNullOrEmpty(newKind) && shell.CurrentPage is VaultViewModel newVault)
            {
                if (newKind == "note") newVault.NewNoteCommand.Execute(null);
                else newVault.NewLoginCommand.Execute(null);
                await Task.Delay(900);
            }

            // TAMA_UI_SNAPSHOT_SCROLL=<px>：把页面内容滚到指定位置再截图。
            // 用来验证"内容到底能不能滚到"（用户报"设置里的导入导出看不到"，先确认是没滚还是滚不动）。
            var scroll = Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_SCROLL");
            if (!string.IsNullOrWhiteSpace(scroll) && double.TryParse(scroll, out var offsetY))
            {
                // 取"内容最长"的那个 ScrollViewer：第一个往往是 TextBox 模板里的，
                // 给它设 offset 等于什么都没发生（排查顶部浮层时踩过）
                var viewer = window.GetVisualDescendants().OfType<ScrollViewer>()
                    .OrderByDescending(v => v.Extent.Height)
                    .FirstOrDefault();
                if (viewer is not null)
                {
                    viewer.Offset = new Vector(viewer.Offset.X, offsetY);
                    await Task.Delay(350);
                }
            }

            // 等布局与首帧数据落地
            await Task.Delay(1000);

            // TAMA_UI_SNAPSHOT_TOAST=ok|err|info：演示提示条（位置/配色要单独看）
            ShowToastIfRequested(shell);

            Save(window, path);

            // TAMA_UI_SNAPSHOT_MOBILE=<png>：同一会话里再按手机尺寸抓一张。
            // 真实库解锁一次能拿到桌面 + 手机两版，省一次人工解锁。
            var mobilePath = Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_MOBILE");
            if (!string.IsNullOrWhiteSpace(mobilePath) && shell.IsUnlocked)
            {
                window.Width = 412;
                window.Height = 915;
                await Task.Delay(700);

                // 手机上详情是"就地展开"：切到窄屏后要重新点一次行，
                // 否则拍出来的还是没展开的列表（桌面阶段选中的那条进的是右栏）
                ShowToastIfRequested(shell);

                var mobilePick = Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_PICK");
                if (!string.IsNullOrEmpty(mobilePick))
                {
                    shell.OpenCipherForAutomation(mobilePick);
                    await Task.Delay(900);
                }

                Save(window, mobilePath);
            }
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

    private static void ShowToastIfRequested(ShellViewModel shell)
    {
        var kind = Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_TOAST");
        if (string.IsNullOrEmpty(kind)) return;

        var (text, toastKind) = kind switch
        {
            "ok" => ("已复制到剪贴板（30 秒后自动清空）", ToastKind.Success),
            "err" => ("导入失败：这个文件不是 Tama 备份", ToastKind.Error),
            _ => ("正在同步云端库…", ToastKind.Info),
        };
        ((IShell)shell).Toast(text, toastKind);
    }

    private static void Save(Window window, string path)
    {
        var size = new PixelSize(
            Math.Max(1, (int)Math.Round(window.Bounds.Width)),
            Math.Max(1, (int)Math.Round(window.Bounds.Height)));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
        Serilog.Log.Information("UI snapshot saved to {Path}", path);
    }
}
#endif
