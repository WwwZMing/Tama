using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Tama.Avalonia.Services;
using Tama.Core.Contracts;

namespace Tama.Avalonia.ViewModels;

/// <summary>下拉里的文件夹选项。<c>Id</c> 为 null = 未分类。</summary>
public sealed class FolderOption
{
    public FolderOption(string? id, string name)
    {
        Id = id;
        Name = name;
    }

    public string? Id { get; }
    public string Name { get; }
}

/// <summary>
/// 条目详情 / 编辑 / 新建。
///
/// 全量覆盖语义的坑记在这里：<c>UpdateCipherRequest</c> 是整条覆盖，而页面手上的
/// <see cref="CipherDto"/> 没有卡片/身份/自定义字段——所以 **收藏按钮单独走
/// <c>SetFavorite</c>**，不要用 Update 代替（那会把服务器上那些段落清空）。
/// 目前 Avalonia 版编辑的就是登录/笔记两类，与 Blazor 版一致。
/// </summary>
public partial class CipherDetailViewModel : ObservableObject
{
    /// <summary>
    /// 窄屏标记。由保险库页下发：手机上详情是**就地展开在行下面**，
    /// 行里已经显示过名称和状态，展开区就不该再重复一遍标题，也不该再套一层卡片。
    /// </summary>
    public bool IsCompact
    {
        get => _isCompact;
        set
        {
            if (_isCompact == value) return;
            _isCompact = value;
            OnPropertyChanged();
            NotifyReadOnlyVisible();
            OnPropertyChanged(nameof(ShowCompactEditBar));
        }
    }

    private bool _isCompact;

    /// <summary>
    /// 被托管在壳内浮层里（窄屏"新建条目"）。保存或取消时顺手把浮层关掉——
    /// 否则用户点完保存，抽屉还停在原地，看起来像没保存成功。
    /// </summary>
    public bool IsInOverlay { get; set; }

    /// <summary>浮层里的标题（桌面是右栏，不需要它）。</summary>
    public string OverlayTitle => Type switch
    {
        2 => "新建笔记",
        3 => "新建卡片",
        4 => "新建身份",
        _ => "新建登录",
    };

    /// <summary>桌面：右侧面板里的卡片式只读区。</summary>
    public bool ShowDesktopReadOnly => !IsCompact && !IsEditing;

    /// <summary>手机：就地展开的分组列表（iOS inset grouped）。</summary>
    public bool ShowCompactReadOnly => IsCompact && !IsEditing;

    partial void OnIsEditingChanged(bool value)
    {
        NotifyReadOnlyVisible();
        OnPropertyChanged(nameof(ShowCompactEditBar));
    }

    /// <summary>只在浮层托管模式下关浮层；就地展开的编辑器不能把别人的浮层关掉。</summary>
    private void CloseOverlayIfHosted()
    {
        if (IsInOverlay) _shell.Shell.CloseOverlay();
    }

    private void NotifyReadOnlyVisible()
    {
        OnPropertyChanged(nameof(ShowDesktopReadOnly));
        OnPropertyChanged(nameof(ShowCompactReadOnly));
    }

    private readonly AppSession _session;
    private readonly ShellAccessor _shell;
    private readonly ClipboardService _clipboard;
    private readonly DialogService _dialogs;
    private readonly Func<Task> _onChanged;

    private CipherDto? _original;
    private DispatcherTimer? _totpTimer;

    private CipherDetailViewModel(AppSession session, ShellAccessor shell, ClipboardService clipboard,
        DialogService dialogs, Func<Task> onChanged, int type)
    {
        _session = session;
        _shell = shell;
        _clipboard = clipboard;
        _dialogs = dialogs;
        _onChanged = onChanged;
        Type = type;
    }

    /// <summary>打开一条已有条目（只读优先，点编辑才进编辑态）。</summary>
    public static CipherDetailViewModel ForExisting(AppSession session, ShellAccessor shell,
        ClipboardService clipboard, DialogService dialogs, Func<Task> onChanged)
        => new(session, shell, clipboard, dialogs, onChanged, 1);

    /// <summary>新建（直接进编辑态）。<paramref name="type"/> 1=登录 2=笔记。</summary>
    public static CipherDetailViewModel ForNew(AppSession session, ShellAccessor shell,
        ClipboardService clipboard, DialogService dialogs, Func<Task> onChanged, int type)
    {
        var vm = new CipherDetailViewModel(session, shell, clipboard, dialogs, onChanged, type) { IsNew = true, IsEditing = true };
        return vm;
    }

    public bool IsNew { get; private set; }
    [ObservableProperty] private int _type = 1;
    public string CipherId => _original?.Id ?? "";
    public bool IsLogin => Type == 1;
    public string TypeLabel => Type switch { 2 => "安全笔记", 3 => "卡片", 4 => "身份", _ => "登录" };

    partial void OnTypeChanged(int value)
    {
        OnPropertyChanged(nameof(IsLogin));
        OnPropertyChanged(nameof(TypeLabel));
    }

    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _username = "";

    /// <summary>空字段不占一行：手机上一行就是 40 多像素。</summary>
    public bool HasUsername => !string.IsNullOrWhiteSpace(Username);

    partial void OnUsernameChanged(string value) => OnPropertyChanged(nameof(HasUsername));


    public bool HasUri => !string.IsNullOrWhiteSpace(Uri);

    partial void OnUriChanged(string value) => OnPropertyChanged(nameof(HasUri));

    /// <summary>密码可见切换（手机上默认隐藏，点眼睛才显示原文）。</summary>
    [ObservableProperty] private bool _isPasswordVisible;

    public string PasswordDisplay => IsPasswordVisible
        ? Password
        : new string('•', Math.Clamp(Password.Length, 8, 14));

    /// <summary>眼睛按钮：在点状与明文之间切换（不改数据，只改显示）。</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void TogglePasswordVisible() => IsPasswordVisible = !IsPasswordVisible;

    partial void OnIsPasswordVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(PasswordDisplay));
        OnPropertyChanged(nameof(PasswordEyeIcon));
        OnPropertyChanged(nameof(EditorPasswordChar));
    }

    /// <summary>眼睛按钮的图标跟着状态换：看得见时给"遮住"，看不见时给"睁开"。</summary>
    public Geometry PasswordEyeIcon => IsPasswordVisible ? Icons.EyeOff : Icons.Eye;

    /// <summary>
    /// 编辑态密码框的掩码字符。'\0' = 不掩码（Avalonia 用零字符表示"不做密码框"）。
    /// 与只读区共用同一个开关：用户在哪一层都该能自己决定看不看。
    /// </summary>
    public char EditorPasswordChar => IsPasswordVisible ? '\0' : '●';

    /// <summary>
    /// 窄屏编辑态：**保存/取消钉在抽屉底部**。
    /// 以前这两个按钮只在桌面顶栏（<c>IsVisible="{Binding !IsCompact}"</c>），
    /// 手机上进入编辑后根本没有保存入口——用户实测"编辑完存不了"。
    /// </summary>
    public bool ShowCompactEditBar => IsCompact && IsEditing;

    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _totpSecret = "";
    [ObservableProperty] private string _uri = "";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private bool _favorite;
    [ObservableProperty] private FolderOption? _selectedFolder;
    [ObservableProperty] private string? _totpCode;
    [ObservableProperty] private int _totpSecondsLeft;
    [ObservableProperty] private string _strengthLabel = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _syncHint;

    public ObservableCollection<FolderOption> FolderOptions { get; } = new();

    public bool HasTotp => !string.IsNullOrWhiteSpace(TotpSecret);
    public string TotpDisplay => string.IsNullOrEmpty(TotpCode) ? "—" : FormatTotp(TotpCode!);

    /// <summary>六位码按 3+3 分组，读起来不容易串行。</summary>
    private static string FormatTotp(string code) => code.Length == 6 ? $"{code[..3]} {code[3..]}" : code;

    private IVaultApi Vault => _session.Services.GetRequiredService<IVaultApi>();
    private IPasswordApi Passwords => _session.Services.GetRequiredService<IPasswordApi>();
    private IFolderApi Folders => _session.Services.GetRequiredService<IFolderApi>();
    private IClockApi Clock => _session.Services.GetRequiredService<IClockApi>();

    // === 载入 ===

    public async Task LoadAsync(string id)
    {
        IsBusy = true;
        Error = null;
        try
        {
            var dto = await Vault.Get(id);
            if (dto is null)
            {
                Error = "条目不存在（可能已被删除）";
                return;
            }

            _original = dto;
            Type = dto.Type;
            Name = dto.Name ?? "";
            Username = dto.Login?.Username ?? "";
            Password = dto.Login?.Password ?? "";
            TotpSecret = dto.Login?.Totp ?? "";
            Uri = dto.Login?.Uris?.FirstOrDefault() ?? "";
            Notes = dto.Notes ?? "";
            Favorite = dto.Favorite;
            SyncHint = String.Equals(dto.SyncStatus, "synced", StringComparison.OrdinalIgnoreCase) ? null : "尚未同步到云端";

            await LoadFolderOptionsAsync(dto.FolderId);

            IsEditing = false;
            OnPropertyChanged(nameof(TypeLabel));
            OnPropertyChanged(nameof(IsLogin));
            OnPropertyChanged(nameof(HasTotp));
            StartTotpTimer();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Load cipher failed");
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadFolderOptionsAsync(string? selectedId)
    {
        FolderOptions.Clear();
        FolderOptions.Add(new FolderOption(null, "未分类"));
        try
        {
            foreach (var folder in await Folders.List()) FolderOptions.Add(new FolderOption(folder.Id, folder.Name));
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Load folders for detail failed");
        }

        SelectedFolder = FolderOptions.FirstOrDefault(o => o.Id == selectedId) ?? FolderOptions[0];
    }

    // === TOTP ===

    private void StartTotpTimer()
    {
        StopTotpTimer();
        if (!HasTotp) return;

        RefreshTotp();
        _totpTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _totpTimer.Tick += (_, _) => RefreshTotp();
        _totpTimer.Start();
    }

    private void StopTotpTimer()
    {
        _totpTimer?.Stop();
        _totpTimer = null;
    }

    private void RefreshTotp()
    {
        if (!HasTotp) return;
        try
        {
            var totp = Passwords.Totp(TotpSecret, Clock.UnixNow);
            TotpCode = totp.Code;
            TotpSecondsLeft = totp.SecondsRemaining;
            OnPropertyChanged(nameof(TotpDisplay));
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "TOTP computation failed");
            TotpCode = null;
            OnPropertyChanged(nameof(TotpDisplay));
        }
    }

    partial void OnPasswordChanged(string value)
    {
        // 密码变了，点阵长度也要跟着变
        OnPropertyChanged(nameof(PasswordDisplay));

        if (!IsLogin || string.IsNullOrEmpty(value))
        {
            StrengthLabel = "";
            return;
        }
        try { StrengthLabel = Passwords.Strength(value).Label; }
        catch { StrengthLabel = ""; }
    }

    // === 命令 ===

    [RelayCommand]
    private async Task CopyUsernameAsync()
    {
        await _clipboard.CopyAsync(Username);
        _shell.Shell.Toast("用户名已复制（30 秒后自动清空）", ToastKind.Success);
    }

    [RelayCommand]
    private async Task CopyPasswordAsync()
    {
        await _clipboard.CopyAsync(Password);
        _shell.Shell.Toast("密码已复制（30 秒后自动清空）", ToastKind.Success);
    }

    [RelayCommand]
    private async Task CopyUriAsync()
    {
        await _clipboard.CopyAsync(Uri);
        _shell.Shell.Toast("网址已复制", ToastKind.Success);
    }

    [RelayCommand]
    private async Task CopyTotpAsync()
    {
        if (!HasTotp) return;

        // 复制验证码 = 强制校时一次：用户往往就是"网站说码错了"才来复制的，
        // 这一刻最该确认时钟（CLAUDE.md 陷阱 17）。离线时降级为直接复制当前码。
        try { await Clock.SyncAsync(force: true); }
        catch (Exception ex) { Serilog.Log.Debug(ex, "Clock sync before TOTP copy failed"); }

        RefreshTotp();
        await _clipboard.CopyAsync(TotpCode);
        _shell.Shell.Toast("验证码已复制", ToastKind.Success);
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        if (IsNew || _original is null) { Favorite = !Favorite; return; }

        try
        {
            await Vault.SetFavorite(_original.Id, !Favorite);
            Favorite = !Favorite;
            await _onChanged();
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    [RelayCommand]
    private void BeginEdit()
    {
        Error = null;
        IsEditing = true;
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (IsNew)
        {
            await _onChanged();
            CloseOverlayIfHosted();
            return;
        }

        IsEditing = false;
        if (_original is not null) await LoadAsync(_original.Id);
    }

    [RelayCommand]
    private void GeneratePassword()
    {
        try
        {
            Password = Passwords.Generate(new PasswordGenerateRequest { Length = 20 }).Password;
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            Error = "名称不能为空";
            return;
        }

        IsBusy = true;
        Error = null;
        try
        {
            if (IsNew)
            {
                var created = await Vault.Create(new CreateCipherRequest
                {
                    Type = Type,
                    Name = Name,
                    Notes = Notes,
                    FolderId = SelectedFolder?.Id,
                    Login = IsLogin
                        ? new CipherLoginRequest
                        {
                            Username = Username,
                            Password = Password,
                            Totp = TotpSecret,
                            Uris = string.IsNullOrWhiteSpace(Uri) ? null : new List<string> { Uri },
                        }
                        : null,
                });

                await _onChanged();
                _shell.Shell.Toast(created.SyncStatus == "synced" ? "已保存" : "已保存到本地，等待同步", ToastKind.Success);

                // 浮层里的新建：保存即收工（列表已由 _onChanged 刷过），不用转成"编辑已存在的这条"
                if (IsInOverlay)
                {
                    CloseOverlayIfHosted();
                    return;
                }

                // 新建成功后切成"编辑已存在的这条"
                _original = await Vault.Get(created.Id);
                IsNew = false;
                if (_original is not null) await LoadAsync(_original.Id);
            }
            else if (_original is not null)
            {
                await Vault.Update(new UpdateCipherRequest
                {
                    Id = _original.Id,
                    Type = Type,
                    Name = Name,
                    Notes = Notes,
                    Favorite = Favorite,
                    FolderId = SelectedFolder?.Id,
                    Login = IsLogin
                        ? new CipherLoginRequest
                        {
                            Username = Username,
                            Password = Password,
                            Totp = TotpSecret,
                            Uris = string.IsNullOrWhiteSpace(Uri) ? null : new List<string> { Uri },
                        }
                        : null,
                });

                await _onChanged();
                _shell.Shell.Toast("已保存", ToastKind.Success);
                await LoadAsync(_original.Id);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Save cipher failed");
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (IsNew || _original is null) return;

        var confirmed = await _dialogs.ConfirmAsync("删除条目", $"确定删除「{Name}」？这一步不能撤销。");
        if (!confirmed) return;

        try
        {
            await Vault.Delete(_original.Id);
            StopTotpTimer();
            _original = null;
            await _onChanged();
            _shell.Shell.Toast("已删除", ToastKind.Success);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }
}
