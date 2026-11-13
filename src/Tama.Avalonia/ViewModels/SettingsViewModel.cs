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
            _shell.Shell.Toast("已重新校时", ToastKind.Success);
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

    /// <summary>
    /// 还原正在进行。**不复用 <see cref="PageViewModel.IsBusy"/>**：这个页面里校时、存指纹也在用它，
    /// 文案会串成"正在还原…"出现在校时的时候。
    /// </summary>
    [ObservableProperty] private bool _isRestoring;

    /// <summary>还原结果（null = 还没跑过）。跑完就进**完成态**：预览收起、结果留在原地。</summary>
    [ObservableProperty] private string? _restoreSummary;
    [ObservableProperty] private string? _restoreDetail;

    /// <summary>这次到底有没有新条目进来。0 条时不能给"成功"语气（绿底 + 对勾 = 骗人）。</summary>
    private bool _restoreAnyImported;

    public bool HasPreview => ImportPreview is not null;
    public bool HasRestoreResult => RestoreSummary is not null;
    public bool ShowRestoreSuccess => HasRestoreResult && _restoreAnyImported;
    public bool ShowRestoreNone => HasRestoreResult && !_restoreAnyImported;

    /// <summary>
    /// 预览与结果**互斥**。这一条是"完成效果"的关键：还原完如果还留着"开始还原"按钮，
    /// 用户看不出到底成功了没有（结果只是下面一行灰字），就会再点一次。
    /// </summary>
    public bool ShowPreview => HasPreview && !HasRestoreResult;

    /// <summary>
    /// 只有**认得出是自家备份**时才给复选框和"开始还原"。选错文件（Bitwarden / 1Password 的导出）
    /// 时那颗按钮只会是灰的禁用状态，留在那里等于让用户怀疑"是不是又坏了"——现在只留一句原因。
    /// </summary>
    public bool CanPreviewRestore => ImportPreview is { IsTamaVault: true } && !HasRestoreResult;

    /// <summary>选进来的不是自家备份：标题按错误色显示。</summary>
    public bool ShowInvalidPreview => ImportPreview is { IsTamaVault: false };

    public bool CanRestore => _pendingBackup is not null && ImportPreview is { IsTamaVault: true };

    public string PreviewTitle => ImportPreview switch
    {
        null => "",
        // 解析层给的原因本来就能直接给用户看（"不是 Tama 备份文件（format=…）"），
        // 再套一层"这个文件不是 Tama 备份："会变成"不是备份：不是备份文件"这种车轱辘话
        { IsTamaVault: false } p => string.IsNullOrWhiteSpace(p.Error) ? "这个文件不是 Tama 备份" : p.Error,
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
        OnPropertyChanged(nameof(ShowPreview));
        OnPropertyChanged(nameof(CanPreviewRestore));
        OnPropertyChanged(nameof(ShowInvalidPreview));
        OnPropertyChanged(nameof(CanRestore));
        OnPropertyChanged(nameof(PreviewTitle));
        OnPropertyChanged(nameof(PreviewTypes));
        OnPropertyChanged(nameof(PreviewFolders));
    }

    partial void OnRestoreSummaryChanged(string? value)
    {
        OnPropertyChanged(nameof(HasRestoreResult));
        OnPropertyChanged(nameof(ShowPreview));
        OnPropertyChanged(nameof(CanPreviewRestore));
        OnPropertyChanged(nameof(ShowRestoreSuccess));
        OnPropertyChanged(nameof(ShowRestoreNone));
    }

    [RelayCommand]
    private async Task PickBackupAsync()
    {
        ClearError();

        var picked = await _io.PickBackupAsync();
        if (picked is null) return;

        var (name, data, preview) = picked.Value;
        ApplyPickedBackup(name, data, preview);
    }

    /// <summary>
    /// "选完文件"之后的状态落地。**单独成方法**是为了让自动化（只 Debug，见 App.Automation.cs）
    /// 走同一条路径：系统文件选择器没法脚本化，但"选完之后的界面长什么样"必须能截图核对。
    /// </summary>
    internal void ApplyPickedBackup(string name, byte[] data, VaultImportPreview preview)
    {
        // ⚠ 顺序是**功能本身**，不是风格：CanRestore 的刷新挂在 ImportPreview 的变更通知上，
        //   所以 _pendingBackup 必须先落地。反过来写（先赋预览、后赋字节）的话，通知那一刻
        //   CanRestore 读到的还是 null → 按钮被绑成禁用，而且之后没有任何通知再来纠正它：
        //   界面上就是"文件信息全都对，唯独「开始还原」永远是灰的"（用户 2026-11-14 报的正是这个）。
        _pendingBackup = preview.IsTamaVault ? data : null;
        RestoreFileName = name;
        ImportPreview = preview;
        OnPropertyChanged(nameof(CanRestore));
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (_pendingBackup is null) return;

        ClearError();
        _restoreAnyImported = false;   // 先复位语气，再复位文字（顺序同 ApplyPickedBackup 的道理）
        RestoreSummary = null;         // 复位上一次的结果，免得把旧结果当成这次跑出来的
        RestoreDetail = null;
        IsRestoring = true;
        try
        {
            var outcome = await _io.RestoreAsync(_pendingBackup, RestoreFolders, SkipDuplicates);
            _restoreAnyImported = outcome.Imported > 0;   // 在 RestoreSummary 之前落地：通知里要读它
            RestoreSummary = outcome.Summary;
            RestoreDetail = outcome.Detail;
        }
        catch (Exception ex)
        {
            // 失败落在页面顶部的 Error 上（红字留在原地），不是一条几秒就消失的提示条
            Fail(ex, "还原失败");
        }
        finally
        {
            IsRestoring = false;
        }
    }

    /// <summary>收起预览 / 清掉完成态（"取消"与完成态里的"完成"共用）。</summary>
    [RelayCommand]
    private void ClearPreview()
    {
        _pendingBackup = null;
        ImportPreview = null;    // 通知里读 CanRestore 时 _pendingBackup 已经是 null，口径一致
        RestoreFileName = null;
        _restoreAnyImported = false;
        RestoreSummary = null;
        RestoreDetail = null;
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
            // 成功提示由 VaultIoService 发（成功语气 + 条数），这里不再重复写一行字
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
    private void Lock() => _shell.Shell.Lock();
}
