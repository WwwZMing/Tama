#if DEBUG
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Tama.Avalonia.Services;
using Tama.Avalonia.ViewModels;
using Tama.Core.Contracts;
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
///   TAMA_UI_SNAPSHOT_SEED_PASSPHRASE  隔离库上设置恢复口令
///   TAMA_UI_SNAPSHOT_RUN_PASSPHRASE   隔离库上用恢复口令完成一次恢复
///   TAMA_UI_SNAPSHOT_OPEN_TRUSTED_DEVICE  隔离库上生成离线扫码配对二维码
///   TAMA_UI_SNAPSHOT_SEED_TRUSTED_RECOVERY 隔离库上登记一台离线扫码可信设备
///   TAMA_UI_SNAPSHOT_OPEN_TRUSTED_LOGIN  锁屏后打开手机扫码登录二维码
///   TAMA_UI_SNAPSHOT_OPEN_TRUSTED_IMPORT   展示系统相机扫码后的导入界面
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

            // TAMA_UI_SNAPSHOT_SEED_RECOVERY=1：只在隔离库上提交一套应急包，
            // 用来截图“忘记主密码”面板。不会碰真实数据目录。
            if (Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_SEED_RECOVERY") == "1"
                && shell.IsUnlocked
                && Tama.Core.AppPaths.IsDataDirOverridden)
            {
                var access = App.HostEnvironment!.Session.Services.GetRequiredService<IVaultAccessApi>();
                var kit = await access.CreateRecoveryKit();
                await access.CommitRecoveryKit(new CommitRecoveryKitRequest
                {
                    EnvelopeJson = kit.EnvelopeJson,
                });
                await File.WriteAllTextAsync(
                    Path.Combine(Tama.Core.AppPaths.DataDir, "snapshot-recovery-shares.json"),
                    System.Text.Json.JsonSerializer.Serialize(kit.Shares));
                Serilog.Log.Information("Snapshot: isolated recovery kit committed");
            }

            // TAMA_UI_SNAPSHOT_SEED_PASSPHRASE=<口令>：只在隔离库上设置恢复口令，
            // 用来验证“输入口令 → 自动保存密文”的真实路径。
            var seedPassphrase = Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_SEED_PASSPHRASE");
            if (!string.IsNullOrEmpty(seedPassphrase)
                && shell.IsUnlocked
                && Tama.Core.AppPaths.IsDataDirOverridden)
            {
                var access = App.HostEnvironment!.Session.Services.GetRequiredService<IVaultAccessApi>();
                await access.SetRecoveryPassphrase(new SetRecoveryPassphraseRequest
                {
                    Passphrase = seedPassphrase,
                });
                Serilog.Log.Information("Snapshot: isolated recovery passphrase set");
            }

            // TAMA_UI_SNAPSHOT_SEED_TRUSTED_RECOVERY=1：只对隔离库提交一次扫码配对，
            // 让锁屏上的“用另一台可信设备恢复”入口真的出现。
            if (Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_SEED_TRUSTED_RECOVERY") == "1"
                && shell.IsUnlocked
                && Tama.Core.AppPaths.IsDataDirOverridden)
            {
                var access = App.HostEnvironment!.Session.Services.GetRequiredService<IVaultAccessApi>();
                var offer = await access.CreateTrustedDevicePairingOffer(
                    new CreateTrustedDevicePairingOfferRequest { DeviceName = "快照手机" });
                await access.CommitTrustedDevicePairing(
                    new CommitTrustedDevicePairingRequest
                    {
                        OfferId = offer.OfferId,
                        MutualCode = offer.MutualCode,
                    });
                Serilog.Log.Information("Snapshot: isolated trusted recovery device registered");
            }

            // TAMA_UI_SNAPSHOT_RUN_RECOVERY=1：在隔离库上真正走一遍
            // “两份份额 → 新密码 → 打开数据库”，随后进入设置页截图。
            var recoveryPassword = Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_RECOVERY_PASSWORD");
            if (Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_RUN_RECOVERY") == "1"
                && !shell.IsUnlocked
                && !string.IsNullOrEmpty(recoveryPassword)
                && Tama.Core.AppPaths.IsDataDirOverridden)
            {
                var sharesPath = Path.Combine(Tama.Core.AppPaths.DataDir, "snapshot-recovery-shares.json");
                var shares = System.Text.Json.JsonSerializer.Deserialize<string[]>(
                    await File.ReadAllTextAsync(sharesPath))
                    ?? throw new InvalidOperationException("Snapshot recovery shares are missing");
                var access = App.HostEnvironment!.Session.Services.GetRequiredService<IVaultAccessApi>();
                await access.Recover(new RecoverVaultRequest
                {
                    FirstShare = shares[0],
                    SecondShare = shares[2],
                    NewPassword = recoveryPassword,
                });

                var status = await access.Status();
                Serilog.Log.Information(
                    "Snapshot: recovery completed — hasRecovery={HasRecovery}, passkeyConfigured={PasskeyConfigured}",
                    status.HasRecovery, status.PasskeyConfigured);
                if (!await shell.TryUnlockForAutomationAsync(recoveryPassword))
                    throw new InvalidOperationException("Snapshot recovery password could not unlock the vault");
                File.Delete(sharesPath);   // 只存在于隔离目录，验证完立即移除份额明文
                await Task.Delay(500);
            }

            // TAMA_UI_SNAPSHOT_RUN_PASSPHRASE=<口令>：在隔离库里真实走一遍
            // “恢复口令 → 新主密码 → 打开数据库”，随后进入设置页截图。
            var runPassphrase = Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_RUN_PASSPHRASE");
            var passphraseNewPassword =
                Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_PASSPHRASE_NEW_PASSWORD");
            if (!string.IsNullOrEmpty(runPassphrase)
                && !string.IsNullOrEmpty(passphraseNewPassword)
                && !shell.IsUnlocked
                && Tama.Core.AppPaths.IsDataDirOverridden)
            {
                var access = App.HostEnvironment!.Session.Services.GetRequiredService<IVaultAccessApi>();
                await access.RecoverWithPassphrase(new RecoverWithPassphraseRequest
                {
                    Passphrase = runPassphrase,
                    NewPassword = passphraseNewPassword,
                });
                if (!await shell.TryUnlockForAutomationAsync(passphraseNewPassword))
                    throw new InvalidOperationException("Snapshot passphrase recovery password could not unlock the vault");
                await Task.Delay(500);
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

            // TAMA_UI_SNAPSHOT_LOCK_RECOVERY=1：退回锁屏并展开应急恢复面板。
            if (Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_LOCK_RECOVERY") == "1"
                && shell.IsUnlocked)
            {
                ((IShell)shell).Lock();
                await Task.Delay(700);
                await shell.Unlock.RefreshAsync();
                shell.Unlock.OpenRecoveryCommand.Execute(null);
                await Task.Delay(500);
            }
            else if (Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_OPEN_RECOVERY") == "1"
                     && !shell.IsUnlocked)
            {
                await shell.Unlock.RefreshAsync();
                shell.Unlock.OpenRecoveryCommand.Execute(null);

                // TAMA_UI_SNAPSHOT_FILL_RECOVERY=1：把隔离库刚生成的份额直接填进两个槽位，
                // 用来截图“第二份就绪 → 新密码”这一段。仍强制要求 TAMA_DATA_DIR。
                if (Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_FILL_RECOVERY") == "1"
                    && Tama.Core.AppPaths.IsDataDirOverridden)
                {
                    var sharesPath = Path.Combine(Tama.Core.AppPaths.DataDir, "snapshot-recovery-shares.json");
                    if (File.Exists(sharesPath))
                    {
                        var shares = System.Text.Json.JsonSerializer.Deserialize<string[]>(
                            await File.ReadAllTextAsync(sharesPath));
                        if (shares is { Length: >= 2 })
                        {
                            shell.Unlock.RecoveryShareA = shares[0];
                            shell.Unlock.RecoveryShareB = shares[1];
                        }
                    }
                }

                await Task.Delay(500);
            }
            else if (Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_OPEN_PASSPHRASE_RECOVERY") == "1"
                     && !shell.IsUnlocked)
            {
                await shell.Unlock.RefreshAsync();
                shell.Unlock.OpenPassphraseRecoveryCommand.Execute(null);
                var fillPassphrase = Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_FILL_PASSPHRASE");
                if (!string.IsNullOrEmpty(fillPassphrase))
                    shell.Unlock.Passphrase = fillPassphrase;
                await Task.Delay(500);
            }

            // TAMA_UI_SNAPSHOT_LOCK_TRUSTED_RECOVERY=1：退回锁屏，验证可信设备入口；
            // 再加 TAMA_UI_SNAPSHOT_OPEN_TRUSTED_RECOVERY=1 展开输入面板。
            if (Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_LOCK_TRUSTED_RECOVERY") == "1"
                && shell.IsUnlocked)
            {
                ((IShell)shell).Lock();
                await Task.Delay(700);
                await shell.Unlock.RefreshAsync();
                if (Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_OPEN_TRUSTED_RECOVERY") == "1")
                    shell.Unlock.OpenTrustedDeviceRecoveryCommand.Execute(null);
                await Task.Delay(500);
            }

            // TAMA_UI_SNAPSHOT_OPEN_TRUSTED_LOGIN=1：先用
            // TAMA_UI_SNAPSHOT_SEED_TRUSTED_RECOVERY=1 在隔离库里造出已配对的来源设备，
            // 再锁屏并打开真正的“手机扫码登录”二维码；不会走恢复或更换主密码。
            if (Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_OPEN_TRUSTED_LOGIN") == "1"
                && Tama.Core.AppPaths.IsDataDirOverridden)
            {
                if (shell.IsUnlocked)
                {
                    ((IShell)shell).Lock();
                    await Task.Delay(700);
                }

                await shell.Unlock.RefreshAsync();
                await shell.Unlock.BeginTrustedDeviceLoginCommand.ExecuteAsync(null);
                await Task.Delay(700);
            }

            // TAMA_UI_SNAPSHOT_OPEN_TRUSTED_IMPORT=1：模拟系统相机把 tama://pair
            // 交给应用，核对锁屏与已解锁壳内两种落点。
            if (Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_OPEN_TRUSTED_IMPORT") == "1"
                && Tama.Core.AppPaths.IsDataDirOverridden)
            {
                var payload = "tama://pair?ver=1&id=snapshot&name=Windows%20PC&exp=4102444800&vt=snapshot&salt=x&nonce=x&ct=x&check=000000";
                if (shell.IsUnlocked) AppDeepLinkRouter.Receive(payload);
                else shell.Unlock.OpenTrustedDeviceImport(payload);
                await Task.Delay(700);
            }

            var pick = Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_PICK");
            if (!string.IsNullOrEmpty(pick)) shell.OpenCipherForAutomation(pick);

            // TAMA_UI_SNAPSHOT_RESTORE=<json 路径>：把一份备份"选中"，走的是点完"选择备份文件…"
            // 之后**同一条 VM 路径**（系统文件选择器本身没法脚本化）。再给 -RESTORE_RUN=1 就会
            // 接着点"开始还原"，于是"预览态按钮是不是灰的""还原完到底有没有完成反馈"都能截图。
            var restorePath = Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_RESTORE");
            if (!string.IsNullOrEmpty(restorePath) && shell.CurrentPage is SettingsViewModel restoreSettings)
            {
                var bytes = File.ReadAllBytes(restorePath);
                var importer = App.HostEnvironment!.Session.Services.GetRequiredService<IVaultImportApi>();
                var backupPreview = importer.Preview(bytes);
                restoreSettings.ApplyPickedBackup(Path.GetFileName(restorePath), bytes, backupPreview);
                Serilog.Log.Information(
                    "Snapshot: backup picked — isTama={IsTama}, total={Total}, HasPreview={HasPreview}, CanRestore={CanRestore}",
                    backupPreview.IsTamaVault, backupPreview.Total, restoreSettings.HasPreview, restoreSettings.CanRestore);
                await Task.Delay(500);

                if (Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_RESTORE_RUN") == "1")
                {
                    Serilog.Log.Information(
                        "Snapshot: before restore — canExecute={CanExecute}, canRestore={CanRestore}",
                        restoreSettings.RestoreCommand.CanExecute(null), restoreSettings.CanRestore);
                    await restoreSettings.RestoreCommand.ExecuteAsync(null);
                    Serilog.Log.Information(
                        "Snapshot: restore finished — restoring={Restoring}, hasResult={HasResult}, summary={Summary}, detail={Detail}, error={Error}",
                        restoreSettings.IsRestoring, restoreSettings.HasRestoreResult,
                        restoreSettings.RestoreSummary, restoreSettings.RestoreDetail, restoreSettings.Error);
                    await Task.Delay(800);
                }
            }

            // TAMA_UI_SNAPSHOT_START_RECOVERY_KIT=1：在隔离库里创建一个**不提交**的临时应急包，
            // 只用于核对“三份文件怎么保存”的展开态；不会碰真实数据目录。
            if (Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_START_RECOVERY_KIT") == "1"
                && Tama.Core.AppPaths.IsDataDirOverridden
                && shell.CurrentPage is SettingsViewModel recoverySettings)
            {
                await recoverySettings.StartRecoveryKitCommand.ExecuteAsync(null);
                await Task.Delay(500);
            }

            // TAMA_UI_SNAPSHOT_OPEN_PASSPHRASE_SETTINGS=1：展开恢复口令输入区，
            // 用于核对密码框、说明和确认按钮在手机宽度下的布局。
            if (Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_OPEN_PASSPHRASE_SETTINGS") == "1"
                && shell.CurrentPage is SettingsViewModel passphraseSettings)
            {
                passphraseSettings.BeginSetRecoveryPassphraseCommand.Execute(null);
                await Task.Delay(500);
            }

            // TAMA_UI_SNAPSHOT_OPEN_TRUSTED_DEVICE=1：生成一张真实的离线扫码配对二维码，
            // 用于核对二维码、配对码和互认码在桌面/窄屏下的布局。仍只作用于隔离库。
            if (Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_OPEN_TRUSTED_DEVICE") == "1"
                && Tama.Core.AppPaths.IsDataDirOverridden
                && shell.CurrentPage is SettingsViewModel trustedDeviceSettings)
            {
                await trustedDeviceSettings.BeginTrustedDevicePairingCommand.ExecuteAsync(null);
                await Task.Delay(700);
            }

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

            // TAMA_UI_SNAPSHOT_EDIT=1：选中条目后进入编辑态（验证窄屏底部保存栏与密码揭示）
            if (Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_EDIT") == "1"
                && shell.CurrentPage is VaultViewModel editVault
                && editVault.Selected is { } editing)
            {
                editing.BeginEditCommand.Execute(null);
                await Task.Delay(600);
            }

            // TAMA_UI_SNAPSHOT_REVEAL=1：把密码切成"显示"状态（验证眼睛按钮与明文渲染）
            if (Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_REVEAL") == "1"
                && shell.CurrentPage is VaultViewModel revealVault
                && revealVault.Selected is { } revealed)
            {
                revealed.TogglePasswordVisibleCommand.Execute(null);
                await Task.Delay(400);
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
