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
    private readonly VaultIoService _io;

    public SettingsViewModel(AppSession session, ThemeService theme, ShellAccessor shell, DialogService dialogs,
        VaultIoService io)
    {
        _session = session;
        _theme = theme;
        _shell = shell;
        _dialogs = dialogs;
        _io = io;
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
    //
    // 具体流程（选文件 → 要密码 → 确认分组 → 另存为）全在 VaultIoService 里：
    // 保险库页也有同样的入口，逻辑抄两份必然走样。这里只管忙碌态与错误落点。

    [RelayCommand]
    private async Task ImportKdbxAsync()
    {
        ClearError();
        IsBusy = true;
        try
        {
            await _io.ImportKdbxAsync();
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
        ClearError();
        IsBusy = true;
        try
        {
            await _io.ExportJsonAsync();
        }
        catch (Exception ex)
        {
            Fail(ex, "导出失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Lock() => _shell.Shell.Lock();
}
