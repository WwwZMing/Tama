using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Tama.Avalonia.Services;
using Tama.Core.Contracts;
using Tama.Core.Interfaces;

namespace Tama.Avalonia.ViewModels;

/// <summary>账号列表的一行。命令挂在行自己身上——XAML 里不用写跨层 DataContext 强转。</summary>
public partial class AccountRow : ObservableObject
{
    private readonly AccountsViewModel _owner;

    public AccountRow(AccountSummaryDto dto, AccountsViewModel owner)
    {
        Dto = dto;
        _owner = owner;
    }

    public AccountSummaryDto Dto { get; }
    public string Id => Dto.Id;
    public string Email => Dto.Email;
    public string TypeText => Dto.Type == "bitwarden" ? "Bitwarden" : Dto.Type;
    public string ServerText => string.IsNullOrWhiteSpace(Dto.ServerUrl) ? "bitwarden.com（默认）" : Dto.ServerUrl!;

    [ObservableProperty] private string? _status;

    [RelayCommand]
    private Task Sync() => _owner.SyncAsync(this);

    [RelayCommand]
    private Task Delete() => _owner.DeleteAsync(this);
}

/// <summary>
/// Bitwarden 账号页：登录（含两步验证 / 新设备验证）、列出、拉取同步、删除。
///
/// 一个**必须记住**的规则（CLAUDE.md 陷阱 14）：进入验证码步骤时**绝不能清主密码**——
/// 第二步要用它重新走一次 token 请求。以前清空写在 finally 里，表现就是"跳到验证码、
/// 输了码却登录失败"。
/// </summary>
public partial class AccountsViewModel : PageViewModel
{
    private readonly AppSession _session;
    private readonly ShellAccessor _shell;

    public AccountsViewModel(AppSession session, ShellAccessor shell)
    {
        _session = session;
        _shell = shell;
    }

    public ObservableCollection<AccountRow> Accounts { get; } = new();

    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _masterPassword = "";
    [ObservableProperty] private string _verificationCode = "";
    [ObservableProperty] private bool _isTwoFactorStep;
    [ObservableProperty] private bool _isNewDeviceFlow;
    [ObservableProperty] private string? _info;
    [ObservableProperty] private bool _showLoginForm;
    [ObservableProperty] private bool _isEmpty = true;

    private int? _twoFactorProvider;

    private IAuthApi Auth => _session.Services.GetRequiredService<IAuthApi>();

    public override Task OnActivatedAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        ClearError();
        try
        {
            var accounts = await Auth.Accounts();
            Accounts.Clear();
            foreach (var account in accounts) Accounts.Add(new AccountRow(account, this));
            IsEmpty = Accounts.Count == 0;
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    [RelayCommand]
    private void ToggleLoginForm()
    {
        ShowLoginForm = !ShowLoginForm;
        Error = null;
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (IsBusy) return;
        ClearError();

        if (string.IsNullOrWhiteSpace(Email))
        {
            Error = "请输入邮箱";
            return;
        }
        if (string.IsNullOrEmpty(MasterPassword))
        {
            Error = "请输入主密码";
            return;
        }

        IsBusy = true;
        try
        {
            var data = await Auth.Login(new LoginRequest
            {
                Email = Email.Trim(),
                MasterPassword = MasterPassword,
                TwoFactorCode = IsTwoFactorStep ? VerificationCode.Trim() : null,
                TwoFactorProvider = _twoFactorProvider,
                NewDeviceVerification = IsTwoFactorStep && IsNewDeviceFlow,
            });

            // 两步验证 / 新设备验证：进验证码阶段，**主密码留着**
            if (data.TwoFactorRequired)
            {
                IsTwoFactorStep = true;
                IsNewDeviceFlow = data.RequiresNewDeviceVerification;
                _twoFactorProvider = data.TwoFactorProviders?.Keys.FirstOrDefault();
                Info = IsNewDeviceFlow
                    ? "这是新设备登录验证：验证码已发到邮箱（含垃圾箱），输入后完成登录。"
                    : "请输入两步验证的验证码（验证器 App 或邮箱）。";
                VerificationCode = "";
                return;
            }

            if (string.IsNullOrEmpty(data.AccessToken))
            {
                Error = "登录失败：响应缺少 accessToken";
                return;
            }

            await Auth.AddAccount(new AddAccountRequest
            {
                Email = Email.Trim(),
                Type = "bitwarden",
                AccessToken = data.AccessToken,
                RefreshToken = data.RefreshToken,
                EncryptionKey = data.Key,
                RawTokenKey = data.RawTokenKey,
                DerivedEncKey = data.DerivedEncKey,
                DerivedMacKey = data.DerivedMacKey,
                KdfIterations = data.KdfIterations,
            });

            // 成功才清
            MasterPassword = "";
            VerificationCode = "";
            IsTwoFactorStep = false;
            IsNewDeviceFlow = false;
            Info = null;
            ShowLoginForm = false;

            await RefreshAsync();
            _shell.Shell.Toast("账号已添加，正在同步云端库…", ToastKind.Success);

            // 登录后立刻拉一次（失败不影响账号已保存）
            var added = Accounts.LastOrDefault();
            if (added is not null) _ = SyncAsync(added);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Bitwarden login failed");
            Error = ex.Message.Length > 300 ? ex.Message[..300] + "…" : ex.Message;
            // 第一步就失败（多半是密码错）→ 清掉；第二步失败要留着重试
            if (!IsTwoFactorStep) MasterPassword = "";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CancelTwoFactor()
    {
        IsTwoFactorStep = false;
        IsNewDeviceFlow = false;
        VerificationCode = "";
        Info = null;
        MasterPassword = "";
    }

    public async Task SyncAsync(AccountRow? row)
    {
        if (row is null) return;
        ClearError();
        row.Status = "同步中…";
        try
        {
            var result = await Auth.SyncNow(row.Id);
            row.Status = result.Busy
                ? "已有同步在进行中"
                : $"新增 {result.Imported} · 更新 {result.Updated} · 删除 {result.Removed}"
                  + (result.Skipped.Count > 0 ? $" · 跳过 {result.Skipped.Count}" : "");
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Sync now failed");
            row.Status = "同步失败";
            Error = ex.Message;
        }
    }

    public async Task DeleteAsync(AccountRow? row)
    {
        if (row is null) return;
        var confirmed = await _session.Services.GetRequiredService<DialogService>()
            .ConfirmAsync("删除账号", $"删除账号「{row.Email}」？\n本地保险库条目不会被删除，但会失去与云端的关联。");
        if (!confirmed) return;

        try
        {
            await Auth.DeleteAccount(row.Id);
            await RefreshAsync();
            _shell.Shell.Toast("账号已删除", ToastKind.Success);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }
}
