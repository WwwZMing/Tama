using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Tama.Avalonia.Services;
using Tama.Core.Contracts;

namespace Tama.Avalonia.ViewModels;

/// <summary>通行密钥列表的一行（命令挂在行上，XAML 不用跨层强转 DataContext）。</summary>
public partial class PasskeyRow : ObservableObject
{
    private readonly PasskeysViewModel _owner;

    public PasskeyRow(PasskeyEntryDto dto, PasskeysViewModel owner)
    {
        Dto = dto;
        _owner = owner;
    }

    public PasskeyEntryDto Dto { get; }
    public string CredentialId => Dto.CredentialId;
    public string RpId => Dto.RpId;
    public string? UserName => Dto.UserName;

    /// <summary>Bitwarden 同步下来的、只有公钥元数据、还不能用来登录。</summary>
    public bool IsCloudOnly => string.Equals(Dto.Source, "bitwarden", StringComparison.OrdinalIgnoreCase);

    public bool IsLocal => !IsCloudOnly;
    public string SourceText => IsCloudOnly ? "未启用（云端）" : "本机可用";
    public string Detail => string.IsNullOrWhiteSpace(UserName) ? $"计数器 {Dto.Counter}" : $"{UserName} · 计数器 {Dto.Counter}";

    [RelayCommand]
    private Task Adopt() => _owner.AdoptAsync(this);

    [RelayCommand]
    private Task Delete() => _owner.DeleteAsync(this);
}

/// <summary>
/// 通行密钥页。
///
/// 两条来源必须看得见区别：<c>Source == "bitwarden"</c> 的行 = **只有公钥元数据、还不能用**
/// （标签写"未启用（云端）"，给的是「启用」按钮而不是删除）；本机可用的那些才能删。
/// 创建入口不在这个页面——通行密钥由**网站**发起（navigator.credentials.create → 浏览器扩展）。
/// </summary>
public partial class PasskeysViewModel : PageViewModel
{
    private readonly AppSession _session;
    private readonly ShellAccessor _shell;
    private readonly DialogService _dialogs;

    public PasskeysViewModel(AppSession session, ShellAccessor shell, DialogService dialogs)
    {
        _session = session;
        _shell = shell;
        _dialogs = dialogs;
    }

    public ObservableCollection<PasskeyRow> Entries { get; } = new();

    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private bool _isEmpty;

    private IPasskeyApi Passkeys => _session.Services.GetRequiredService<IPasskeyApi>();

    public override Task OnActivatedAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        ClearError();
        try
        {
            IsAvailable = Passkeys.Available();
            var entries = await Passkeys.List();
            Entries.Clear();
            foreach (var entry in entries) Entries.Add(new PasskeyRow(entry, this));
            IsEmpty = Entries.Count == 0;
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    [RelayCommand]
    private async Task ImportJsonAsync()
    {
        var file = await _dialogs.PickFileAsync("选择 Bitwarden 未加密导出（.json）", "*.json");
        if (file is null) return;

        try
        {
            var result = await Passkeys.ImportFromBitwardenJson(file);
            Report(result);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    [RelayCommand]
    private async Task AdoptAllAsync()
    {
        try
        {
            var result = await Passkeys.AdoptSyncedPasskeys();
            Report(result);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    public async Task AdoptAsync(PasskeyRow? row)
    {
        if (row is null) return;
        try
        {
            var result = await Passkeys.AdoptSyncedPasskeys(row.CredentialId);
            Report(result);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    public async Task DeleteAsync(PasskeyRow? row)
    {
        if (row is null) return;
        var confirmed = await _dialogs.ConfirmAsync("删除通行密钥",
            $"删除「{row.RpId}」这一枚本机凭据？云端那一份不受影响。");
        if (!confirmed) return;

        try
        {
            await Passkeys.Delete(row.CredentialId);
            await RefreshAsync();
            _shell.Shell.Toast("已删除", ToastKind.Success);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void Report(PasskeyImportResult result)
    {
        var text = $"新增 {result.Imported} · 已存在 {result.Skipped} · 失败 {result.Failed}";
        if (result.Warnings.Count > 0) Error = string.Join("；", result.Warnings);
        _shell.Shell.Toast(text);
    }
}
