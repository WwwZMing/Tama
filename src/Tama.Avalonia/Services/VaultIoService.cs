using Microsoft.Extensions.DependencyInjection;
using Tama.Core.Contracts;

namespace Tama.Avalonia.Services;

/// <summary>
/// 保险库的"进出口"：KeePass(.kdbx) 导入 与 明文 JSON 导出。
///
/// 为什么单独抽出来：设置页和保险库列表都要这两个入口，而它们的流程里全是对话框交互
/// （选文件 → 要密码 → 确认分组 → 另存），复制到两个 VM 里必然走样。
///
/// 手机上的两个真实坑（之前在这里坏了）：
///   1. **导出不能只写 Downloads**：Android 上 <c>SpecialFolder.UserProfile</c> 落在应用私有沙盒，
///      文件写成功但用户在文件管理器里根本看不到 —— 表现为"导出功能没了"。所以导出后一律弹
///      **系统"另存为"**（Android 是 SAF，桌面是保存对话框），落到用户选的位置。
///   2. **文件选择器不能只给 Patterns**：Android SAF 按 MIME 过滤，<c>*.kdbx</c> 映射不到任何 MIME，
///      选择器会显示成空的。所以 FilePickerFileType 里同时给 <c>*/*</c> 兜底。
/// </summary>
public sealed class VaultIoService
{
    private readonly AppSession _session;
    private readonly ShellAccessor _shell;
    private readonly DialogService _dialogs;

    public VaultIoService(AppSession session, ShellAccessor shell, DialogService dialogs)
    {
        _session = session;
        _shell = shell;
        _dialogs = dialogs;
    }

    private IVaultApi Vault => _session.Services.GetRequiredService<IVaultApi>();
    private IImportApi Importer => _session.Services.GetRequiredService<IImportApi>();

    /// <summary>导入 KeePass 数据库（全部条目）。返回导入条数，取消/失败返回 null。</summary>
    public async Task<int?> ImportKdbxAsync()
    {
        var file = await _dialogs.PickFileAsync("选择 KeePass 数据库", "*.kdbx", "*.kdb");
        if (file is null) return null;

        try
        {
            // 先不带密码解析：未加密的库直接就过，加密库抛异常再问密码（省一次输入）
            string? password = null;
            List<KdbxGroupDto> groups;
            try
            {
                groups = Importer.ParseGroups(file);
            }
            catch
            {
                password = await _dialogs.PromptAsync("KeePass 密码", "输入该 .kdbx 的密码");
                if (password is null) return null;
                groups = Importer.ParseGroups(file, password);
            }

            if (groups.Count == 0)
            {
                _shell.Shell.Toast("这个文件里没有可导入的条目");
                return 0;
            }

            var total = groups.Sum(g => g.EntryCount);
            var preview = string.Join("、", groups.Take(6).Select(g => $"{g.Name}({g.EntryCount})"));
            var confirmed = await _dialogs.ConfirmAsync(
                "导入 KeePass",
                $"将导入 {total} 条条目：{preview}\n导入后可以再手动整理文件夹。",
                "全部导入",
                "取消");
            if (!confirmed) return null;

            var count = await Importer.Import(file, password);
            _shell.Shell.Toast($"已导入 {count} 条");
            return count;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "KeePass import failed");
            _shell.Shell.Toast($"导入失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 导出明文 JSON。返回 (条数, 用户选的位置)；<c>Location</c> 为 null 表示用户取消了另存
    /// （此时服务层已写好的那份文件仍在数据目录里，桌面端可用）。
    /// </summary>
    public async Task<(int Count, string? Location)?> ExportJsonAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync(
            "导出明文保险库",
            "导出文件包含全部条目的**明文密码**（未加密 JSON）。请放在安全的地方，用完删掉。",
            "继续导出",
            "取消");
        if (!confirmed) return null;

        try
        {
            var result = await Vault.Export();

            // 服务层固定写一份到数据目录（桌面端直接可在 Downloads 找到）；
            // 再读出来交给"另存为"，让手机上也能落到用户能看到的位置。
            string? location = null;
            try
            {
                var bytes = await File.ReadAllBytesAsync(result.Path);
                location = await _dialogs.SaveFileAsync(Path.GetFileName(result.Path), bytes);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Save-as after export failed, keeping data-dir copy");
            }

            _shell.Shell.Toast(location is null
                ? $"已导出 {result.Count} 条（已写入数据目录，未另存）"
                : $"已导出 {result.Count} 条 → {location}");

            return (result.Count, location);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Vault export failed");
            _shell.Shell.Toast($"导出失败：{ex.Message}");
            return null;
        }
    }
}
