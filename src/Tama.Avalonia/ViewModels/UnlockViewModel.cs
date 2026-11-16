using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Avalonia.Media.Imaging;
using System.Text;
using Tama.Avalonia.Services;
using Tama.Core.Contracts;
using Tama.Core.Interfaces;

namespace Tama.Avalonia.ViewModels;

/// <summary>
/// 门禁页：首次设置主密码 / 用主密码解锁 / 生物识别解锁。
///
/// 三条流程与 Blazor 版一一对应（差异只在"通知壳"的方式）：
///   · 首次进入 = <c>IsSetup=false</c> → 设置 + 确认两个框；
///   · 密码解锁成功后，若平台支持生物识别，**静默**把主密码用平台密钥加密存下来（下次 Hello/指纹解锁）；
///   · 生物识别解锁失败且密码解出来了但不通 → 说明保存的凭据过期（改过主密码），自动清除并提示。
/// </summary>
public partial class UnlockViewModel : ObservableObject
{
    private readonly AppSession _session;

    /// <summary>解锁成功后由壳挂上的回调（切进 shell）。</summary>
    public Func<Task>? Unlocked { get; set; }

    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _confirmPassword = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isSetup;
    [ObservableProperty] private bool _bioAvailable;
    [ObservableProperty] private bool _deviceVerificationAvailable;
    [ObservableProperty] private bool _hasRecovery;
    [ObservableProperty] private bool _hasRecoveryPassphrase;
    [ObservableProperty] private bool _hasDeviceShare;
    [ObservableProperty] private bool _hasOfflineTrustedRecovery;
    [ObservableProperty] private bool _hasTrustedDeviceLinks;
    [ObservableProperty] private bool _isRecoveryOpen;
    [ObservableProperty] private bool _isPassphraseRecoveryOpen;
    [ObservableProperty] private bool _isTrustedDeviceRecoveryOpen;
    [ObservableProperty] private bool _isTrustedDeviceImportOpen;
    [ObservableProperty] private bool _isTrustedDeviceLoginQrOpen;
    [ObservableProperty] private bool _isTrustedDeviceLoginRequestOpen;
    [ObservableProperty] private bool _isTrustedDeviceLoginApproved;
    [ObservableProperty] private bool _isShowingTrustedDeviceCode;
    [ObservableProperty] private string _passphrase = "";
    [ObservableProperty] private string _passphraseRecoveryNewPassword = "";
    [ObservableProperty] private string _passphraseRecoveryConfirmPassword = "";
    [ObservableProperty] private string _recoveryShareA = "";
    [ObservableProperty] private string _recoveryShareB = "";
    [ObservableProperty] private string _recoveryNewPassword = "";
    [ObservableProperty] private string _recoveryConfirmPassword = "";
    [ObservableProperty] private string _trustedDeviceRecoveryCode = "";
    [ObservableProperty] private string _trustedDeviceNewPassword = "";
    [ObservableProperty] private string _trustedDeviceConfirmPassword = "";
    [ObservableProperty] private string _trustedDeviceImportPayload = "";
    [ObservableProperty] private string _trustedDeviceImportPairingCode = "";
    [ObservableProperty] private string _trustedDeviceImportMutualCode = "";
    [ObservableProperty] private string _trustedDeviceCodeToShare = "";
    [ObservableProperty] private string _trustedDeviceCodeSource = "";
    [ObservableProperty] private Bitmap? _trustedDeviceLoginQr;
    [ObservableProperty] private string _trustedDeviceLoginCode = "";
    [ObservableProperty] private string _trustedDeviceLoginSourceName = "";

    /// <summary>系统验证窗弹着的时候为真：按钮要显示"正在验证…"，否则用户以为点了没反应。</summary>
    [ObservableProperty] private bool _isBioBusy;
    private string _trustedDeviceLoginLinkId = "";

    public string Title => IsSetup ? "设置主密码" : "解锁保险库";

    public string Subtitle => IsSetup
        ? "主密码用于解锁；创建后请设置恢复口令或离线应急包"
        : BioAvailable && (HasRecovery || HasRecoveryPassphrase || HasOfflineTrustedRecovery)
            ? "输入主密码，或使用 Passkey / 手机扫码解锁"
            : BioAvailable
                ? "输入主密码，或使用 Passkey 解锁"
                : HasRecovery || HasRecoveryPassphrase || HasOfflineTrustedRecovery
                    ? HasOfflineTrustedRecovery
                        ? "输入主密码，或使用已配对手机扫码解锁"
                        : "输入主密码，忘记时可用恢复入口"
                    : "输入主密码解锁";

    public string SubmitText => IsBusy
        ? (IsSetup ? "正在创建…" : "正在解锁…")
        : (IsSetup ? "创建保险库" : "解锁");

    /// <summary>
    /// 桌面宿主上这就是 Windows Hello（<c>UserConsentVerifier</c>）：笔记本指纹、人脸、PIN 都算。
    /// 平台不可用时按钮根本不显示（见 <see cref="BioAvailable"/>）。
    /// </summary>
    public string BioText => IsBioBusy ? "正在验证…" : "使用 Passkey / Windows Hello 解锁";

    public UnlockViewModel(AppSession session) => _session = session;

    partial void OnIsSetupChanged(bool value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(SubmitText));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(SubmitText));
        OnPropertyChanged(nameof(CanRecover));
        OnPropertyChanged(nameof(RecoverySubmitText));
        OnPropertyChanged(nameof(CanRecoverWithPassphrase));
        OnPropertyChanged(nameof(PassphraseRecoverySubmitText));
        OnPropertyChanged(nameof(CanRecoverWithTrustedDevice));
        OnPropertyChanged(nameof(TrustedDeviceRecoverySubmitText));
        OnPropertyChanged(nameof(CanLoginWithTrustedDeviceCode));
    }

    partial void OnIsBioBusyChanged(bool value) => OnPropertyChanged(nameof(BioText));
    partial void OnBioAvailableChanged(bool value) => OnPropertyChanged(nameof(Subtitle));
    partial void OnHasRecoveryChanged(bool value)
    {
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(ShowLegacyRecoveryOption));
    }

    partial void OnHasRecoveryPassphraseChanged(bool value)
    {
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(ShowLegacyRecoveryOption));
    }

    partial void OnHasOfflineTrustedRecoveryChanged(bool value)
    {
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(ShowLegacyRecoveryOption));
    }
    partial void OnIsRecoveryOpenChanged(bool value) => OnPropertyChanged(nameof(IsLoginVisible));
    partial void OnIsPassphraseRecoveryOpenChanged(bool value) => OnPropertyChanged(nameof(IsLoginVisible));
    partial void OnIsTrustedDeviceRecoveryOpenChanged(bool value) => OnPropertyChanged(nameof(IsLoginVisible));
    partial void OnIsTrustedDeviceImportOpenChanged(bool value) => OnPropertyChanged(nameof(IsLoginVisible));
    partial void OnIsTrustedDeviceLoginQrOpenChanged(bool value) => OnPropertyChanged(nameof(IsLoginVisible));
    partial void OnIsTrustedDeviceLoginRequestOpenChanged(bool value) => OnPropertyChanged(nameof(IsLoginVisible));
    partial void OnIsShowingTrustedDeviceCodeChanged(bool value) => OnPropertyChanged(nameof(IsLoginVisible));

    private IAuthApi Auth => _session.Services.GetRequiredService<IAuthApi>();
    private IBiometricService Bio => _session.Services.GetRequiredService<IBiometricService>();
    private IVaultAccessApi Access => _session.Services.GetRequiredService<IVaultAccessApi>();
    private DialogService Dialogs => _session.Services.GetRequiredService<DialogService>();

    public bool HasFirstRecoveryShare => !string.IsNullOrWhiteSpace(RecoveryShareA);
    public bool HasBothRecoveryShares => !string.IsNullOrWhiteSpace(RecoveryShareA)
                                         && !string.IsNullOrWhiteSpace(RecoveryShareB);
    public string RecoveryShareAStatus => HasFirstRecoveryShare ? "已选择" : "待选择";
    public string RecoveryShareBStatus => !string.IsNullOrWhiteSpace(RecoveryShareB) ? "已选择" : "待选择";
    public string RecoverySelectionStatus => HasBothRecoveryShares
        ? "2 份份额已就绪"
        : HasFirstRecoveryShare
            ? "已选择 1 份，还需要 1 份"
            : "还需要选择 2 份";
    public bool CanRecover => HasBothRecoveryShares && !IsBusy;
    public bool ShowLegacyRecoveryOption =>
        HasRecovery && !HasRecoveryPassphrase && !HasOfflineTrustedRecovery;
    public string RecoverySubmitText => IsBusy ? "正在恢复…" : "恢复并解锁";
    public bool IsLoginVisible => !IsRecoveryOpen && !IsPassphraseRecoveryOpen
                                  && !IsTrustedDeviceRecoveryOpen
                                  && !IsTrustedDeviceImportOpen
                                  && !IsTrustedDeviceLoginQrOpen
                                  && !IsTrustedDeviceLoginRequestOpen
                                  && !IsShowingTrustedDeviceCode;
    public bool CanLoginWithTrustedDeviceCode =>
        OfflineTrustedDeviceCodecLength(TrustedDeviceLoginCode) == 16 && !IsBusy;
    public bool CanRecoverWithPassphrase =>
        !string.IsNullOrWhiteSpace(Passphrase) && !IsBusy;
    public string PassphraseRecoverySubmitText => IsBusy ? "正在恢复…" : "恢复并解锁";
    public bool CanRecoverWithTrustedDevice =>
        TrustedDeviceRecoveryCode.Length >= 16 && TrustedDeviceNewPassword.Length >= 6 && !IsBusy;
    public string TrustedDeviceRecoverySubmitText => IsBusy ? "正在恢复…" : "用可信设备恢复并解锁";
    public string TrustedDeviceLinksLine => HasTrustedDeviceLinks
        ? "这台设备已经保存了另一台设备的离线恢复码"
        : "这台设备尚未保存其他设备的恢复码";
    public bool HasTrustedDeviceImportMutualCode =>
        !string.IsNullOrWhiteSpace(TrustedDeviceImportMutualCode);

    partial void OnRecoveryShareAChanged(string value)
    {
        OnPropertyChanged(nameof(HasFirstRecoveryShare));
        OnPropertyChanged(nameof(HasBothRecoveryShares));
        OnPropertyChanged(nameof(RecoveryShareAStatus));
        OnPropertyChanged(nameof(RecoverySelectionStatus));
        OnPropertyChanged(nameof(CanRecover));
    }

    partial void OnRecoveryShareBChanged(string value)
    {
        OnPropertyChanged(nameof(HasBothRecoveryShares));
        OnPropertyChanged(nameof(RecoveryShareBStatus));
        OnPropertyChanged(nameof(RecoverySelectionStatus));
        OnPropertyChanged(nameof(CanRecover));
    }

    partial void OnPassphraseChanged(string value) =>
        OnPropertyChanged(nameof(CanRecoverWithPassphrase));

    partial void OnTrustedDeviceRecoveryCodeChanged(string value) =>
        OnPropertyChanged(nameof(CanRecoverWithTrustedDevice));
    partial void OnTrustedDeviceNewPasswordChanged(string value) =>
        OnPropertyChanged(nameof(CanRecoverWithTrustedDevice));
    partial void OnHasTrustedDeviceLinksChanged(bool value) =>
        OnPropertyChanged(nameof(TrustedDeviceLinksLine));
    partial void OnTrustedDeviceImportMutualCodeChanged(string value) =>
        OnPropertyChanged(nameof(HasTrustedDeviceImportMutualCode));
    partial void OnTrustedDeviceLoginCodeChanged(string value) =>
        OnPropertyChanged(nameof(CanLoginWithTrustedDeviceCode));

    /// <summary>读门禁状态（进入页面 / 每次锁定后刷新）。</summary>
    public async Task RefreshAsync()
    {
        try
        {
            var status = await Auth.Status();
            IsSetup = !status.IsSetup;
            BioAvailable = status.BioUnlockAllowed && Bio.IsAvailable() && Bio.IsConfigured();
            var access = await Access.Status();
            HasRecovery = access.HasRecovery;
            HasRecoveryPassphrase = access.HasRecoveryPassphrase;
            HasDeviceShare = access.HasDeviceShare;
            HasOfflineTrustedRecovery = access.OfflineTrustedDeviceCount > 0;
            DeviceVerificationAvailable = access.PasskeyAvailable;
            var links = await Access.ListTrustedDeviceLinks();
            HasTrustedDeviceLinks = links.Count > 0;
        }
        catch (Exception ex)
        {
            // 读不到状态就按"已经设过密码"渲染：宁可让用户看到解锁框，
            // 也别在库已存在时错误地展示"创建新库"（那会让人以为数据没了）
            Serilog.Log.Error(ex, "Failed to read auth status");
            IsSetup = false;
        }
    }

    [RelayCommand]
    private async Task SubmitAsync()
    {
        if (IsBusy) return;
        Error = null;

        if (string.IsNullOrEmpty(Password))
        {
            Error = "请输入主密码";
            return;
        }

        if (IsSetup)
        {
            if (Password.Length < 6)
            {
                Error = "主密码至少 6 位";
                return;
            }
            if (Password != ConfirmPassword)
            {
                Error = "两次输入的主密码不一致";
                return;
            }
        }

        IsBusy = true;
        try
        {
            if (IsSetup)
            {
                await Auth.Setup(new SetupRequest { Password = Password });
            }
            else
            {
                await Auth.Unlock(new UnlockRequest { Password = Password });
            }

            Password = "";
            ConfirmPassword = "";

            if (Unlocked is not null) await Unlocked();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Unlock/setup failed");
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 供外壳在启动时自动尝试一次。自动尝试失败（Activity 尚未 Resume、用户取消、
    /// 平台暂时不可用）不应在门禁页留下红色错误；手动点击时才需要明确失败原因。
    /// </summary>
    public Task RunBiometricAsync(bool showFailure = true) => BiometricCoreAsync(showFailure);

    [RelayCommand]
    private Task BiometricAsync() => BiometricCoreAsync(showFailure: true);

    private async Task BiometricCoreAsync(bool showFailure)
    {
        if (IsBusy) return;
        Error = null;
        IsBusy = true;
        try
        {
            var status = await Access.Status();
            if (!status.PasskeyConfigured)
            {
                if (showFailure) Error = "未找到 Passkey 凭据，请先用主密码解锁一次";
                BioAvailable = false;
                return;
            }

            IsBioBusy = true;
            try
            {
                if (!await Access.UnlockWithPasskey())
                {
                    if (showFailure) Error = "Passkey 验证未通过或凭据已失效";
                    return;
                }
            }
            finally
            {
                IsBioBusy = false;
            }

            if (Unlocked is not null) await Unlocked();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Biometric unlock failed");
            if (showFailure) Error = ex.Message;
        }
        finally
        {
            IsBioBusy = false;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task BeginTrustedDeviceLoginAsync()
    {
        if (IsBusy) return;
        Error = null;
        TrustedDeviceLoginCode = "";
        TrustedDeviceLoginQr?.Dispose();
        TrustedDeviceLoginQr = null;
        IsBusy = true;
        try
        {
            var offer = await Access.CreateTrustedDeviceLoginOffer(
                new CreateTrustedDeviceLoginOfferRequest
                {
                    DeviceName = Environment.MachineName,
                });
            TrustedDeviceLoginSourceName = offer.SourceDeviceName;
            TrustedDeviceLoginQr = QrCodeImageService.Create(offer.QrPayload);
            IsTrustedDeviceLoginQrOpen = true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Create trusted-device login offer failed");
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UnlockWithTrustedDeviceLoginCodeAsync()
    {
        if (IsBusy) return;
        Error = null;
        var code = TrustedDeviceLoginCode;
        if (OfflineTrustedDeviceCodecLength(code) != 16)
        {
            Error = "请输入手机上显示的 16 位登录码";
            return;
        }

        IsBusy = true;
        try
        {
            await Access.UnlockWithTrustedDeviceCode(
                new UnlockWithTrustedDeviceCodeRequest { LoginCode = code });
            TrustedDeviceLoginCode = "";
            TrustedDeviceLoginQr?.Dispose();
            TrustedDeviceLoginQr = null;
            IsTrustedDeviceLoginQrOpen = false;
            Password = "";
            ConfirmPassword = "";
            if (Unlocked is not null) await Unlocked();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Trusted-device login failed");
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CloseTrustedDeviceLoginQr()
    {
        Error = null;
        TrustedDeviceLoginCode = "";
        TrustedDeviceLoginQr?.Dispose();
        TrustedDeviceLoginQr = null;
        IsTrustedDeviceLoginQrOpen = false;
    }

    public async Task OpenDeepLinkAsync(string qrPayload)
    {
        if (qrPayload.StartsWith("tama://login", StringComparison.OrdinalIgnoreCase))
        {
            Error = null;
            IsRecoveryOpen = false;
            IsPassphraseRecoveryOpen = false;
            IsTrustedDeviceRecoveryOpen = false;
            IsTrustedDeviceImportOpen = false;
            IsTrustedDeviceLoginQrOpen = false;
            IsShowingTrustedDeviceCode = false;
            IsTrustedDeviceLoginApproved = false;
            TrustedDeviceLoginCode = "";
            TrustedDeviceLoginSourceName = "";
            _trustedDeviceLoginLinkId = "";
            IsTrustedDeviceLoginRequestOpen = true;
            try
            {
                var prompt = await Access.ReadTrustedDeviceLoginRequest(qrPayload);
                _trustedDeviceLoginLinkId = prompt.LinkId;
                TrustedDeviceLoginSourceName = prompt.SourceDeviceName;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Read trusted-device login request failed");
                Error = ex.Message;
            }
            return;
        }

        OpenTrustedDeviceImport(qrPayload);
    }

    [RelayCommand]
    private async Task ApproveTrustedDeviceLoginAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(_trustedDeviceLoginLinkId)) return;
        Error = null;
        IsBusy = true;
        try
        {
            var recovery = await Access.RevealTrustedDeviceRecoveryCode(_trustedDeviceLoginLinkId);
            TrustedDeviceLoginSourceName = recovery.SourceDeviceName;
            TrustedDeviceLoginCode = recovery.Code;
            IsTrustedDeviceLoginApproved = true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Approve trusted-device login failed");
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CloseTrustedDeviceLoginRequest()
    {
        Error = null;
        _trustedDeviceLoginLinkId = "";
        TrustedDeviceLoginCode = "";
        TrustedDeviceLoginSourceName = "";
        IsTrustedDeviceLoginApproved = false;
        IsTrustedDeviceLoginRequestOpen = false;
    }

    [RelayCommand]
    private void OpenRecovery()
    {
        Error = null;
        IsPassphraseRecoveryOpen = false;
        RecoveryShareA = "";
        RecoveryShareB = "";
        RecoveryNewPassword = "";
        RecoveryConfirmPassword = "";
        IsRecoveryOpen = true;
    }

    [RelayCommand]
    private void CloseRecovery()
    {
        Error = null;
        RecoveryShareA = "";
        RecoveryShareB = "";
        RecoveryNewPassword = "";
        RecoveryConfirmPassword = "";
        IsRecoveryOpen = false;
    }

    [RelayCommand]
    private void OpenPassphraseRecovery()
    {
        Error = null;
        IsRecoveryOpen = false;
        Passphrase = "";
        PassphraseRecoveryNewPassword = "";
        PassphraseRecoveryConfirmPassword = "";
        IsPassphraseRecoveryOpen = true;
    }

    [RelayCommand]
    private void ClosePassphraseRecovery()
    {
        Error = null;
        Passphrase = "";
        PassphraseRecoveryNewPassword = "";
        PassphraseRecoveryConfirmPassword = "";
        IsPassphraseRecoveryOpen = false;
    }

    [RelayCommand]
    private void OpenTrustedDeviceRecovery()
    {
        Error = null;
        IsRecoveryOpen = false;
        IsPassphraseRecoveryOpen = false;
        IsTrustedDeviceImportOpen = false;
        IsTrustedDeviceLoginQrOpen = false;
        IsTrustedDeviceLoginRequestOpen = false;
        TrustedDeviceRecoveryCode = "";
        TrustedDeviceNewPassword = "";
        TrustedDeviceConfirmPassword = "";
        IsTrustedDeviceRecoveryOpen = true;
    }

    [RelayCommand]
    private void CloseTrustedDeviceRecovery()
    {
        Error = null;
        TrustedDeviceRecoveryCode = "";
        TrustedDeviceNewPassword = "";
        TrustedDeviceConfirmPassword = "";
        IsTrustedDeviceRecoveryOpen = false;
    }

    [RelayCommand]
    private async Task RecoverWithTrustedDeviceCodeAsync()
    {
        if (IsBusy) return;
        Error = null;
        if (OfflineTrustedDeviceCodecLength(TrustedDeviceRecoveryCode) != 16)
        {
            Error = "请输入手机上显示的 16 位恢复码";
            return;
        }
        if (TrustedDeviceNewPassword.Length < 6)
        {
            Error = "新主密码至少 6 位";
            return;
        }
        if (TrustedDeviceNewPassword != TrustedDeviceConfirmPassword)
        {
            Error = "两次输入的新主密码不一致";
            return;
        }

        IsBusy = true;
        try
        {
            await Access.RecoverWithTrustedDeviceCode(new RecoverWithTrustedDeviceCodeRequest
            {
                RecoveryCode = TrustedDeviceRecoveryCode,
                NewPassword = TrustedDeviceNewPassword,
            });

            Password = "";
            ConfirmPassword = "";
            TrustedDeviceRecoveryCode = "";
            TrustedDeviceNewPassword = "";
            TrustedDeviceConfirmPassword = "";
            IsTrustedDeviceRecoveryOpen = false;
            HasOfflineTrustedRecovery = false;
            _session.Services.GetRequiredService<ShellAccessor>().Shell.Toast(
                "可信设备恢复成功，本次恢复码已作废。", ToastKind.Success);
            if (Unlocked is not null) await Unlocked();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Offline trusted-device recovery failed");
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Android 系统相机扫到 tama://pair 后由深链路由器调用。</summary>
    public void OpenTrustedDeviceImport(string qrPayload)
    {
        Error = null;
        IsRecoveryOpen = false;
        IsPassphraseRecoveryOpen = false;
        IsTrustedDeviceRecoveryOpen = false;
        IsTrustedDeviceLoginQrOpen = false;
        IsTrustedDeviceLoginRequestOpen = false;
        IsShowingTrustedDeviceCode = false;
        TrustedDeviceImportPayload = qrPayload;
        TrustedDeviceImportPairingCode = "";
        TrustedDeviceImportMutualCode = "";
        IsTrustedDeviceImportOpen = true;
    }

    [RelayCommand]
    private async Task ImportTrustedDeviceAsync()
    {
        if (IsBusy) return;
        Error = null;
        if (OfflineTrustedDeviceCodecLength(TrustedDeviceImportPairingCode) != 16)
        {
            Error = "请输入 Windows 上显示的 16 位配对码";
            return;
        }

        IsBusy = true;
        try
        {
            var result = await Access.ImportTrustedDevicePairing(
                new ImportTrustedDevicePairingRequest
                {
                    QrPayload = TrustedDeviceImportPayload,
                    PairingCode = TrustedDeviceImportPairingCode,
                });
            TrustedDeviceImportMutualCode = result.MutualCode;
            TrustedDeviceImportPairingCode = "";
            HasTrustedDeviceLinks = true;
            TrustedDeviceCodeSource = result.SourceDeviceName;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Offline trusted-device import failed");
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CloseTrustedDeviceImport()
    {
        Error = null;
        TrustedDeviceImportPayload = "";
        TrustedDeviceImportPairingCode = "";
        TrustedDeviceImportMutualCode = "";
        IsTrustedDeviceImportOpen = false;
    }

    [RelayCommand]
    private async Task ShowTrustedDeviceCodeAsync()
    {
        Error = null;
        try
        {
            var links = await Access.ListTrustedDeviceLinks();
            if (links.Count == 0)
            {
                HasTrustedDeviceLinks = false;
                Error = "这台设备尚未保存其他设备的恢复码";
                return;
            }

            var link = links[0];
            var recovery = await Access.RevealTrustedDeviceRecoveryCode(link.Id);
            TrustedDeviceCodeSource = recovery.SourceDeviceName;
            TrustedDeviceCodeToShare = recovery.Code;
            IsShowingTrustedDeviceCode = true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Reveal offline trusted-device code failed");
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private void CloseTrustedDeviceCode()
    {
        Error = null;
        TrustedDeviceCodeToShare = "";
        IsShowingTrustedDeviceCode = false;
    }

    [RelayCommand]
    private async Task RecoverWithPassphraseAsync()
    {
        if (IsBusy) return;
        Error = null;
        if (string.IsNullOrWhiteSpace(Passphrase))
        {
            Error = "请输入恢复口令";
            return;
        }
        if (PassphraseRecoveryNewPassword.Length < 6)
        {
            Error = "新主密码至少 6 位";
            return;
        }
        if (PassphraseRecoveryNewPassword != PassphraseRecoveryConfirmPassword)
        {
            Error = "两次输入的新主密码不一致";
            return;
        }

        IsBusy = true;
        try
        {
            await Access.RecoverWithPassphrase(new RecoverWithPassphraseRequest
            {
                Passphrase = Passphrase,
                NewPassword = PassphraseRecoveryNewPassword,
            });

            Password = "";
            ConfirmPassword = "";
            Passphrase = "";
            PassphraseRecoveryNewPassword = "";
            PassphraseRecoveryConfirmPassword = "";
            IsPassphraseRecoveryOpen = false;
            HasRecoveryPassphrase = false;
            _session.Services.GetRequiredService<ShellAccessor>().Shell.Toast(
                "恢复口令已使用并作废。登录后请设置新的恢复口令。", ToastKind.Success);
            if (Unlocked is not null) await Unlocked();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Passphrase recovery unlock failed");
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PickRecoveryShareA()
    {
        var share = await PickShareAsync("选择第一份 Tama 应急份额");
        if (share is not null) RecoveryShareA = share;
    }

    [RelayCommand]
    private async Task PickRecoveryShareB()
    {
        var share = await PickShareAsync("选择第二份 Tama 应急份额");
        if (share is not null) RecoveryShareB = share;
    }

    [RelayCommand]
    private async Task UseDeviceShareAsync()
    {
        Error = null;
        try
        {
            var share = await Access.ExportDeviceShare();
            if (string.IsNullOrWhiteSpace(share))
            {
                Error = "当前设备没有保存受信份额";
                HasDeviceShare = false;
                return;
            }
            if (string.IsNullOrWhiteSpace(RecoveryShareA))
                RecoveryShareA = share;
            else if (string.IsNullOrWhiteSpace(RecoveryShareB))
                RecoveryShareB = share;
            else
                RecoveryShareA = share;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private async Task RecoverAsync()
    {
        if (IsBusy) return;
        Error = null;
        if (!HasBothRecoveryShares)
        {
            Error = "还需要另一份应急份额";
            return;
        }
        if (RecoveryNewPassword.Length < 6)
        {
            Error = "新主密码至少 6 位";
            return;
        }
        if (RecoveryNewPassword != RecoveryConfirmPassword)
        {
            Error = "两次输入的新主密码不一致";
            return;
        }

        IsBusy = true;
        try
        {
            await Access.Recover(new RecoverVaultRequest
            {
                FirstShare = RecoveryShareA,
                SecondShare = RecoveryShareB,
                NewPassword = RecoveryNewPassword,
            });

            Password = "";
            ConfirmPassword = "";
            RecoveryNewPassword = "";
            RecoveryConfirmPassword = "";
            RecoveryShareA = "";
            RecoveryShareB = "";
            IsRecoveryOpen = false;
            _session.Services.GetRequiredService<ShellAccessor>().Shell.Toast(
                "应急恢复成功。请立即到设置页生成新的应急包。", ToastKind.Success);
            if (Unlocked is not null) await Unlocked();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Recovery unlock failed");
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ImportDeviceShareAsync()
    {
        var share = await PickShareAsync("选择要保存到本机的恢复份额");
        if (share is null) return;
        Error = null;
        try
        {
            await Access.ImportDeviceShare(new ImportDeviceShareRequest
            {
                ShareText = share,
                DeviceName = Environment.MachineName,
            });
            HasDeviceShare = true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    private async Task<string?> PickShareAsync(string title)
    {
        var picked = await Dialogs.PickFileNamedAsync(title, "*.tama-share", "*.txt");
        return picked is null ? null : Encoding.UTF8.GetString(picked.Value.Bytes);
    }

    private static int OfflineTrustedDeviceCodecLength(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? 0
            : value.Count(char.IsLetterOrDigit);
}
