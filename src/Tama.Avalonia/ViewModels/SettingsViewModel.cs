using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Tama.Avalonia.Services;
using Tama.Core;
using Tama.Core.Contracts;

namespace Tama.Avalonia.ViewModels;

/// <summary>设置页：主题、系统时间（校时）、导入 / 导出、运行信息（含 GC）。</summary>
public partial class SettingsViewModel : PageViewModel
{
    private readonly AppSession _session;
    private readonly ThemeService _theme;
    private readonly ShellAccessor _shell;
    private readonly DialogService _dialogs;

    public SettingsViewModel(AppSession session, ThemeService theme, ShellAccessor shell, DialogService dialogs)
    {
        _session = session;
        _theme = theme;
        _shell = shell;
        _dialogs = dialogs;
        _themeMode = theme.Current.ToString().ToLowerInvariant();
    }

    [ObservableProperty] private string _themeMode;
    [ObservableProperty] private string _clockStatus = "尚未校时";
    [ObservableProperty] private string? _clockSource;
    [ObservableProperty] private string _gcLabel = GcDiagnostics.Describe();
    [ObservableProperty] private string _version = typeof(SettingsViewModel).Assembly.GetName().Version?.ToString() ?? "1.0.0";

    public string DataDir => AppPaths.DataDir;
    public bool IsSystemTheme => ThemeMode == "system";
    public bool IsDarkTheme => ThemeMode == "dark";
    public bool IsLightTheme => ThemeMode == "light";

    private IClockApi Clock => _session.Services.GetRequiredService<IClockApi>();

    public override async Task OnActivatedAsync()
    {
        GcLabel = GcDiagnostics.Describe();
        await LoadClockAsync();
    }

    partial void OnThemeModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsSystemTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(IsLightTheme));
    }

    [RelayCommand]
    private void UseSystemTheme() => ApplyTheme(ThemeService.Mode.System);

    [RelayCommand]
    private void UseDarkTheme() => ApplyTheme(ThemeService.Mode.Dark);

    [RelayCommand]
    private void UseLightTheme() => ApplyTheme(ThemeService.Mode.Light);

    private void ApplyTheme(ThemeService.Mode mode)
    {
        _theme.Apply(mode);
        ThemeMode = mode.ToString().ToLowerInvariant();
    }

    // === 校时 ===

    private async Task LoadClockAsync()
    {
        try
        {
            var snapshot = Clock.LastSnapshot;
            if (snapshot is null)
            {
                ClockStatus = "尚未校时（点「重新检测」）";
                ClockSource = null;
                return;
            }
            Render(snapshot);
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Read clock snapshot failed");
        }
    }

    [RelayCommand]
    private async Task RecheckClockAsync()
    {
        ClearError();
        IsBusy = true;
        try
        {
            var snapshot = await Clock.SyncAsync(force: true);
            Render(snapshot);
            _shell.Shell.Toast("已重新校时");
        }
        catch (Exception ex)
        {
            Fail(ex, "校时失败：连不上任何时间源");
            await LoadClockAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Render(ClockSyncSnapshot snapshot)
    {
        var skewSeconds = (int)Math.Round(snapshot.Skew.TotalSeconds);
        ClockStatus = Math.Abs(skewSeconds) < 1
            ? "系统时间与网络时间一致"
            : $"本机比网络时间{(skewSeconds < 0 ? "慢" : "快")} {Math.Abs(skewSeconds)} 秒" +
              (snapshot.IsDrifted ? "（超过 30 秒，验证码已按网络时间校正）" : "");
        ClockSource = $"网络时间 {snapshot.NetworkUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 来源 {snapshot.Sources} 个 · 检测于 {snapshot.CheckedAt.ToLocalTime():HH:mm:ss}";
    }

    // === 导入 / 导出 ===

    [RelayCommand]
    private async Task ImportKdbxAsync()
    {
        var file = await _dialogs.PickFileAsync("选择 KeePass 数据库（.kdbx）", "*.kdbx", "*.kdb");
        if (file is null) return;

        ClearError();
        IsBusy = true;
        try
        {
            var importer = _session.Services.GetRequiredService<IImportApi>();

            // 先试着不带密码解析：加密库会抛异常，那时再问密码（省掉未加密库的输入步骤）
            string? password = null;
            try
            {
                _ = importer.ParseGroups(file);
            }
            catch
            {
                password = await _dialogs.PromptAsync("KeePass 密码", "输入该 .kdbx 的密码");
                if (password is null) return;
                _ = importer.ParseGroups(file, password);
            }

            var groups = importer.ParseGroups(file, password);
            var summary = string.Join("、", groups.Take(6).Select(g => $"{g.Name}({g.EntryCount})"));
            var confirmed = await _dialogs.ConfirmAsync("导入 KeePass",
                $"将导入 {groups.Sum(g => g.EntryCount)} 条条目：{summary}\n导入后可以再手动整理文件夹。",
                $"全部导入", "取消");
            if (!confirmed) return;

            var count = await importer.Import(file, password);
            _shell.Shell.Toast($"已导入 {count} 条");
        }
        catch (Exception ex)
        {
            Fail(ex, "导入失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync("导出明文保险库",
            "导出文件包含**全部条目的明文密码**（未加密 JSON）。请放在安全的地方，用完删掉。",
            "继续导出", "取消");
        if (!confirmed) return;

        try
        {
            var result = await _session.Services.GetRequiredService<IVaultApi>().Export();
            _shell.Shell.Toast($"已导出 {result.Count} 条 → {result.Path}");
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    [RelayCommand]
    private void Lock() => _shell.Shell.Lock();
}
