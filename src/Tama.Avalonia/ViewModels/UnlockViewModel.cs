using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>系统验证窗弹着的时候为真：按钮要显示"正在验证…"，否则用户以为点了没反应。</summary>
    [ObservableProperty] private bool _isBioBusy;

    public string Title => IsSetup ? "设置主密码" : "解锁保险库";

    public string Subtitle => IsSetup
        ? "主密码直接加密本地数据库，忘了就没了——没有独立密钥文件可以绕过"
        : "输入主密码解锁本地加密数据库";

    public string SubmitText => IsBusy
        ? (IsSetup ? "正在创建…" : "正在解锁…")
        : (IsSetup ? "创建保险库" : "解锁");

    /// <summary>
    /// 桌面宿主上这就是 Windows Hello（<c>UserConsentVerifier</c>）：笔记本指纹、人脸、PIN 都算。
    /// 平台不可用时按钮根本不显示（见 <see cref="BioAvailable"/>）。
    /// </summary>
    public string BioText => IsBioBusy ? "正在验证…" : "使用 Windows Hello / 指纹解锁";

    public UnlockViewModel(AppSession session) => _session = session;

    partial void OnIsSetupChanged(bool value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(SubmitText));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(SubmitText));

    partial void OnIsBioBusyChanged(bool value) => OnPropertyChanged(nameof(BioText));

    private IAuthApi Auth => _session.Services.GetRequiredService<IAuthApi>();
    private IBiometricService Bio => _session.Services.GetRequiredService<IBiometricService>();

    /// <summary>读门禁状态（进入页面 / 每次锁定后刷新）。</summary>
    public async Task RefreshAsync()
    {
        try
        {
            var status = await Auth.Status();
            IsSetup = !status.IsSetup;
            BioAvailable = status.BioUnlockAllowed && Bio.IsAvailable() && Bio.IsConfigured();
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

            RememberBiometricPassword(Password);
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

    /// <summary>供外壳在启动时自动弹一次（旧版 auto-bio 行为）。</summary>
    public Task RunBiometricAsync() => BiometricAsync();

    [RelayCommand]
    private async Task BiometricAsync()
    {
        if (IsBusy) return;
        Error = null;
        IsBusy = true;
        try
        {
            var encrypted = Bio.GetEncryptedPassword();
            if (string.IsNullOrEmpty(encrypted))
            {
                Error = "未找到生物识别凭据，请先用主密码解锁一次";
                BioAvailable = false;
                return;
            }

            IsBioBusy = true;
            var verified = await Bio.AuthenticateAsync("解锁 Tama", "验证以解锁本地保险库");
            IsBioBusy = false;
            if (!verified)
            {
                Error = "系统验证未通过";
                return;
            }

            var decrypted = Bio.DecryptPassword(encrypted);
            if (string.IsNullOrEmpty(decrypted))
            {
                Error = "凭据解密失败";
                return;
            }

            try
            {
                await Auth.Unlock(new UnlockRequest { Password = decrypted });
            }
            catch
            {
                // 解出来了但库打不开 = 保存的凭据与当前主密码不符（改过密码 / 当初存错）。
                // 留着只会永远失败：清掉，等用户用主密码解锁一次后自动重存。
                Bio.ClearEncryptedPassword();
                BioAvailable = false;
                Error = "已保存的凭据与主密码不符，已清除。请先用主密码解锁一次，之后即可使用生物识别。";
                return;
            }

            if (Unlocked is not null) await Unlocked();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Biometric unlock failed");
            Error = ex.Message;
        }
        finally
        {
            IsBioBusy = false;
            IsBusy = false;
        }
    }

    /// <summary>静默保存加密主密码供下次生物识别解锁（失败不打断解锁流程）。</summary>
    private void RememberBiometricPassword(string password)
    {
        try
        {
            if (!Bio.IsAvailable()) return;
            Bio.SetupKey();
            var encrypted = Bio.EncryptPassword(password);
            if (!string.IsNullOrEmpty(encrypted)) Bio.SaveEncryptedPassword(encrypted);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to remember biometric password");
        }
    }
}
