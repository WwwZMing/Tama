using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Tama.Avalonia.Services;
using Tama.Core;
using Tama.Core.Contracts;
using Tama.Core.Interfaces;

namespace Tama.Avalonia.ViewModels;

/// <summary>设置页：生物识别、数据备份、主题、系统时间（校时）、运行信息（含 GC）。</summary>
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
    private IAuthApi Auth => _session.Services.GetRequiredService<IAuthApi>();
    private IBiometricService Bio => _session.Services.GetRequiredService<IBiometricService>();

    public override async Task OnActivatedAsync()
    {
        // 侧栏底部也能切主题：每次进设置都跟当前实际值对齐，否则两个入口各说各的
        ThemeMode = _theme.Current.ToString().ToLowerInvariant();
        GcLabel = GcDiagnostics.Describe();
        await LoadClockAsync();
        await RefreshBioAsync();
    }

    // === 生物识别解锁（Windows Hello / 指纹）===
    //
    // 移植自 Blazor 版设置页那张卡片，**包括"保存前先校验主密码"这条约束**：
    // 存进去的就是加密后的主密码本身，存错一条的结果是"Hello 每次都验证成功、但永远解不开库"，
    // 而界面上只会显示"密码错误"——那种故障非常难自查。

    [ObservableProperty] private bool _bioAvailable;
    [ObservableProperty] private bool _bioConfigured;
    [ObservableProperty] private bool _bioBusy;
    [ObservableProperty] private bool _isSavingBio;
    [ObservableProperty] private string _bioPassword = "";
    [ObservableProperty] private string? _bioMessage;
    [ObservableProperty] private string? _bioError;
    [ObservableProperty] private bool _bioAutoPrompt = UiPrefs.AutoBioPrompt;

    /// <summary>状态一行说完（手机上不占地方）。</summary>
    public string BioStatusLine => (BioAvailable, BioConfigured) switch
    {
        (false, _) => "平台不可用",
        (true, false) => "平台可用 · 未保存凭据",
        (true, true) => "平台可用 · 已保存凭据",
    };

    /// <summary>说明文案随状态变：不可用时解释为什么不落明文，可用时说明它会做什么。</summary>
    public string BioHint => BioAvailable
        ? "启用后可用 Windows Hello / 指纹代替主密码解锁。解锁时输入的主密码会用系统密钥加密保存一份，供下次验证后自动解锁。"
        : "当前环境读不到系统验证能力（未配置 Windows Hello、或平台实现为降级版）。此时不会保存任何凭据，更不会落明文主密码。";

    partial void OnBioAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(BioStatusLine));
        OnPropertyChanged(nameof(BioHint));
    }

    partial void OnBioConfiguredChanged(bool value) => OnPropertyChanged(nameof(BioStatusLine));
    partial void OnBioAutoPromptChanged(bool value) => UiPrefs.AutoBioPrompt = value;

    private async Task RefreshBioAsync(bool clearMessages = true)
    {
        if (clearMessages)
        {
            BioMessage = null;
            BioError = null;
        }

        try
        {
            // IsAvailable 要过一趟 WinRT（UserConsentVerifier.CheckAvailabilityAsync），
            // 放到线程池上问，别在 UI 线程上同步等系统回答
            BioAvailable = await Task.Run(Bio.IsAvailable);
            BioConfigured = Bio.IsConfigured();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Read biometric status failed");
            BioAvailable = false;
            BioConfigured = false;
        }
    }

    [RelayCommand]
    private Task RefreshBio() => RefreshBioAsync();

    [RelayCommand]
    private void BeginSaveBio()
    {
        BioError = null;
        BioMessage = null;
        BioPassword = "";
        IsSavingBio = true;
    }

    [RelayCommand]
    private void CancelSaveBio()
    {
        IsSavingBio = false;
        BioPassword = "";
    }

    [RelayCommand]
    private async Task ConfirmSaveBioAsync()
    {
        if (IsBusy) return;
        if (string.IsNullOrEmpty(BioPassword))
        {
            BioError = "请输入当前主密码";
            return;
        }

        IsBusy = true;
        BioError = null;
        BioMessage = null;
        var password = BioPassword;
        try
        {
            // 先验证主密码再保存：校验不过就不存，避免留下一条"能验证、解不开库"的坏凭据
            try
            {
                await Auth.Unlock(new UnlockRequest { Password = password });
            }
            catch
            {
                BioError = "主密码不正确，未保存";
                return;
            }

            Bio.SetupKey();
            var encrypted = Bio.EncryptPassword(password);
            if (string.IsNullOrEmpty(encrypted))
            {
                // 密钥不可用时绝不回退明文（接口约定：EncryptPassword 失败返回 null）
                BioError = "加密失败：系统密钥不可用，未保存";
                return;
            }

            Bio.SaveEncryptedPassword(encrypted);
            IsSavingBio = false;
            BioPassword = "";
            await RefreshBioAsync(clearMessages: false);
            BioMessage = "凭据已保存，下次可用系统验证解锁";
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Save biometric credential failed");
            BioError = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task TestBioAsync()
    {
        if (BioBusy) return;
        BioBusy = true;
        BioError = null;
        BioMessage = null;
        try
        {
            if (await Bio.AuthenticateAsync("Tama", "验证系统验证是否可用（测试设置）"))
                BioMessage = "验证通过";
            else
                BioError = "验证未通过";
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Biometric test failed");
            BioError = ex.Message;
        }
        finally
        {
            BioBusy = false;
        }
    }

    [RelayCommand]
    private async Task ClearBioAsync()
    {
        try
        {
            Bio.ClearEncryptedPassword();
        }
        catch (Exception ex)
        {
            // 删文件失败不该挡住后面的状态刷新：以刷新出来的真实状态为准
            Serilog.Log.Warning(ex, "Clear biometric credential failed");
        }

        await RefreshBioAsync();
        BioMessage = "已清除凭据，之后只能用主密码解锁";
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
    //
    // 「数据备份」是**导出 + 还原一体的卡片**：它们是同一个操作的正反两面，
    // 分到两处就会出现"导出在这儿、导入找不着"（这正是用户报的问题）。

    /// <summary>待还原的备份字节（选完文件后暂存，点"开始还原"才真正落库）。</summary>
    private byte[]? _pendingBackup;

    [ObservableProperty] private VaultImportPreview? _importPreview;
    [ObservableProperty] private string? _restoreFileName;
    [ObservableProperty] private bool _restoreFolders = true;
    [ObservableProperty] private bool _skipDuplicates = true;
    [ObservableProperty] private string? _ioMessage;

    public bool HasPreview => ImportPreview is not null;
    public bool CanRestore => _pendingBackup is not null && ImportPreview is { IsTamaVault: true };

    public string PreviewTitle => ImportPreview switch
    {
        null => "",
        { IsTamaVault: false } => "这个文件不是 Tama 备份",
        var p => $"{RestoreFileName} · 格式 {p.Format} v{p.Version} · 共 {p.Total} 条" +
                 (p.ExportedAt is { } t ? $" · 导出于 {t.ToLocalTime():yyyy-MM-dd HH:mm}" : ""),
    };

    public string PreviewTypes => ImportPreview is { Types.Count: > 0 } p
        ? "包含：" + string.Join(" / ", p.Types.Select(t => $"{t.Type} {t.Count}"))
        : "";

    public string PreviewFolders => ImportPreview is { Folders.Count: > 0 } p
        ? $"文件夹：{string.Join("、", p.Folders.Take(6))}" + (p.Folders.Count > 6 ? $" 等 {p.Folders.Count} 个" : "")
        : "";

    partial void OnImportPreviewChanged(VaultImportPreview? value)
    {
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(CanRestore));
        OnPropertyChanged(nameof(PreviewTitle));
        OnPropertyChanged(nameof(PreviewTypes));
        OnPropertyChanged(nameof(PreviewFolders));
    }

    [RelayCommand]
    private async Task PickBackupAsync()
    {
        ClearError();
        IoMessage = null;

        var picked = await _io.PickBackupAsync();
        if (picked is null) return;

        var (name, data, preview) = picked.Value;
        RestoreFileName = name;
        ImportPreview = preview;
        _pendingBackup = preview.IsTamaVault ? data : null;
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (_pendingBackup is null) return;

        ClearError();
        IsBusy = true;
        IoMessage = null;
        try
        {
            IoMessage = await _io.RestoreAsync(_pendingBackup, RestoreFolders, SkipDuplicates);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ClearPreview()
    {
        _pendingBackup = null;
        ImportPreview = null;
        RestoreFileName = null;
        IoMessage = null;
    }

    [RelayCommand]
    private async Task ExportTamaAsync()
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
    private async Task ExportBitwardenAsync()
    {
        ClearError();
        IsBusy = true;
        try
        {
            await _io.ExportBitwardenAsync();
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
    private async Task ImportKdbxAsync()
    {
        ClearError();
        IsBusy = true;
        try
        {
            var count = await _io.ImportKdbxAsync();
            if (count is > 0) IoMessage = $"已从 KeePass 导入 {count} 条";
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
    private void Lock() => _shell.Shell.Lock();
}
