using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Tama.Avalonia.Services;
using Tama.Core.Contracts;
using Tama.Core.Interfaces;

namespace Tama.Avalonia.ViewModels;

/// <summary>
/// 应用壳：门禁状态机 + 页面导航 + 全局提示 + 时钟偏差横幅。
///
/// 与 Blazor 版的 <c>MainLayout</c> 对应，但简单得多——那边还要处理 "SSR 预渲染 / 电路首帧 /
/// Mud 抽屉 margin"，Avalonia 里只有一个窗口和一棵控件树，导航就是换一个 DataContext。
/// </summary>
public partial class ShellViewModel : ObservableObject, IShell
{
    private static readonly TimeSpan ClockPollInterval = TimeSpan.FromMinutes(5);

    private readonly AppSession _session;
    private readonly ThemeService _theme;
    private readonly Dictionary<AppPage, PageViewModel> _pages = new();

    private IVaultSyncNotifier? _notifier;
    private IClockApi? _clock;
    private DispatcherTimer? _clockTimer;
    private CancellationTokenSource? _toastCts;

    public UnlockViewModel Unlock { get; }

    [ObservableProperty] private bool _isUnlocked;
    [ObservableProperty] private PageViewModel? _currentPage;
    [ObservableProperty] private AppPage _currentPageKey = AppPage.Vault;
    [ObservableProperty] private string? _toastMessage;
    [ObservableProperty] private string? _clockWarning;

    /// <summary>当前浮层内容（对话框）。null = 没有浮层。</summary>
    [ObservableProperty] private Control? _overlayContent;

    private TaskCompletionSource? _overlayTcs;

    /// <summary>
    /// 窄屏（手机 / 窄窗口）：外层换成"顶栏 + 底部 Tab"，保险库切成"列表 ↔ 详情"两屏。
    ///
    /// 桌面由窗口宽度驱动（App.axaml.cs 里 &lt; 780 就置 true），Android 恒 true。
    /// </summary>
    [ObservableProperty] private bool _isCompact;

    /// <summary>
    /// 导航项：侧栏与底部 Tab **共用这一份数据**，只是控件模板不同。
    /// 原先侧栏是 6 个硬编码 Button，加移动端就得抄第二份——抄完必然走样。
    /// </summary>
    public IReadOnlyList<NavItem> NavItems { get; }

    /// <summary>桌面壳（左侧栏）：已解锁且不是窄屏。</summary>
    public bool ShowDesktopShell => IsUnlocked && !IsCompact;

    /// <summary>移动壳（顶栏 + 底部 Tab）。</summary>
    public bool ShowMobileShell => IsUnlocked && IsCompact;

    /// <summary>窄屏顶栏显示的当前页名（直接取导航项的完整标签，避免再抄一份标题表）。</summary>
    public string CurrentTitle => NavItems.FirstOrDefault(n => n.Page == CurrentPageKey)?.Label ?? "Tama";

    partial void OnCurrentPageKeyChanged(AppPage value)
    {
        SyncNavActive();
        OnPropertyChanged(nameof(CurrentTitle));
    }

    /// <summary>
    /// 把导航项的高亮跟当前页对齐。
    ///
    /// 不能只靠 <c>OnCurrentPageKeyChanged</c>：初始页是 Vault（默认值），
    /// 首次导航到 Vault 时 key **没有变化** → 变化回调不触发 → 6 个 tab 全是灰的
    /// （实测：保险库页截图里底部第一个 tab 没高亮，而设置页正常）。
    /// </summary>
    private void SyncNavActive()
    {
        foreach (var item in NavItems) item.IsActive = item.Page == CurrentPageKey;
    }

    partial void OnIsCompactChanged(bool value)
    {
        // 统一下发给所有已创建的页面（含以后新导航到的：见 NavigateAsync）
        foreach (var page in _pages.Values) page.IsCompact = value;

        OnPropertyChanged(nameof(ShowDesktopShell));
        OnPropertyChanged(nameof(ShowMobileShell));
    }

    partial void OnIsUnlockedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowDesktopShell));
        OnPropertyChanged(nameof(ShowMobileShell));
    }

    /// <summary>设置页会显示它：用户至少要能自己确认"跑的是哪个 GC"。</summary>
    public string GcLabel => GcDiagnostics.Describe();

    public bool IsDarkTheme => _theme.IsDark;

    public ShellViewModel(AppSession session, ThemeService theme, ShellAccessor shellAccessor)
    {
        _session = session;
        _theme = theme;
        shellAccessor.Value = this;

        NavItems = new[]
        {
            new NavItem(AppPage.Vault, "保险库", "保险库", page => _ = NavigateAsync(page)),
            new NavItem(AppPage.Generator, "生成器", "生成", page => _ = NavigateAsync(page)),
            new NavItem(AppPage.Watchtower, "安全卫士", "安全", page => _ = NavigateAsync(page)),
            new NavItem(AppPage.Accounts, "账号", "账号", page => _ = NavigateAsync(page)),
            new NavItem(AppPage.Passkeys, "通行密钥", "密钥", page => _ = NavigateAsync(page)),
            new NavItem(AppPage.Settings, "设置", "设置", page => _ = NavigateAsync(page)),
        };

        Unlock = new UnlockViewModel(session) { Unlocked = OnUnlockedAsync };
    }

    // === 启动 ===

    public async Task InitializeAsync()
    {
        await Unlock.RefreshAsync();
        try
        {
            var status = await _session.Services.GetRequiredService<IAuthApi>().Status();
            if (status.IsUnlocked) await EnterShellAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Shell initialization failed");
        }
    }

    private async Task OnUnlockedAsync()
    {
        await EnterShellAsync();
        await RefreshClockAsync(force: true);
    }

    /// <summary>
    /// **只给截图/自动化用**：直接用主密码解锁（没设过密码就先 Setup）。
    ///
    /// 调用点唯一，并且额外要求 <c>TAMA_DATA_DIR</c> 已重定向——所以这条路径不可能
    /// 碰到用户的真实数据目录。密码来自环境变量，不写盘、不记日志。
    /// </summary>
    internal async Task<bool> TryUnlockForAutomationAsync(string password)
    {
        if (!Tama.Core.AppPaths.IsDataDirOverridden)
        {
            Serilog.Log.Error("Automation unlock refused: TAMA_DATA_DIR is not set");
            return false;
        }

        try
        {
            var auth = _session.Services.GetRequiredService<IAuthApi>();
            var status = await auth.Status();
            if (!status.IsSetup) await auth.Setup(new SetupRequest { Password = password });
            else await auth.Unlock(new UnlockRequest { Password = password });

            await EnterShellAsync();
            return true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Automation unlock failed");
            return false;
        }
    }

    private async Task EnterShellAsync()
    {
        IsUnlocked = true;

        DetachSubscriptions();
        _pages.Clear();

        _notifier = _session.Services.GetRequiredService<IVaultSyncNotifier>();
        _notifier.Completed += OnVaultSyncCompleted;

        _clock = _session.Services.GetRequiredService<IClockApi>();
        _clock.Synced += OnClockSynced;

        await NavigateAsync(AppPage.Vault);
        StartClockTimer();
        OnPropertyChanged(nameof(IsDarkTheme));
    }

    // === 导航 ===

    void IShell.Navigate(AppPage page) => _ = NavigateAsync(page);

    /// <summary>XAML 里的导航按钮统一走这一条（参数是枚举名，避免为六个页面写六个命令）。</summary>
    [RelayCommand]
    private Task GoAsync(string? page)
        => Enum.TryParse<AppPage>(page, out var target) ? NavigateAsync(target) : Task.CompletedTask;

    private async Task NavigateAsync(AppPage page)
    {
        if (!IsUnlocked && page != AppPage.Settings) return;

        try
        {
            if (!_pages.TryGetValue(page, out var vm))
            {
                vm = page switch
                {
                    AppPage.Vault => _session.Services.GetRequiredService<VaultViewModel>(),
                    AppPage.Generator => _session.Services.GetRequiredService<GeneratorViewModel>(),
                    AppPage.Watchtower => _session.Services.GetRequiredService<WatchtowerViewModel>(),
                    AppPage.Accounts => _session.Services.GetRequiredService<AccountsViewModel>(),
                    AppPage.Passkeys => _session.Services.GetRequiredService<PasskeysViewModel>(),
                    _ => _session.Services.GetRequiredService<SettingsViewModel>(),
                };
                _pages[page] = vm;
            }

            CurrentPage = vm;
            CurrentPageKey = page;
            SyncNavActive();   // 首次导航到默认页时 key 不变，补一次，否则 tab 不高亮
            vm.IsCompact = IsCompact;
            await vm.OnActivatedAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Navigation to {Page} failed", page);
            Toast($"打开页面失败：{ex.Message}");
        }
    }

    // === 锁定 ===

    [RelayCommand]
    private void Lock()
    {
        DetachSubscriptions();
        StopClockTimer();
        CloseOverlay();

        CurrentPage = null;
        _pages.Clear();
        ClockWarning = null;

        // 重建会话作用域 + 清掉内存里的数据库密钥
        _session.Reset();

        IsUnlocked = false;
        _ = Unlock.RefreshAsync();
        Toast("已锁定");
    }

    void IShell.Lock() => Lock();

    // === 浮层（对话框） ===

    public async Task ShowOverlayAsync(Control content)
    {
        OverlayContent = content;
        _overlayTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _overlayTcs.Task;
    }

    public void CloseOverlay()
    {
        OverlayContent = null;
        var tcs = _overlayTcs;
        _overlayTcs = null;
        tcs?.TrySetResult();
    }

    // === 提示 ===

    void IShell.Toast(string message) => Toast(message);

    private void Toast(string message)
    {
        ToastMessage = message;

        _toastCts?.Cancel();
        var cts = new CancellationTokenSource();
        _toastCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(4), cts.Token);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (ReferenceEquals(_toastCts, cts)) ToastMessage = null;
                });
            }
            catch (OperationCanceledException) { /* 被新的提示顶掉 */ }
        }, cts.Token);
    }

    // === 同步完成 ===

    private void OnVaultSyncCompleted(VaultSyncResult result)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (result.Busy) Toast("已有同步在进行中");
            else if (result.Total > 0) Toast($"已从云端同步：新增 {result.Imported} · 更新 {result.Updated} · 删除 {result.Removed}");

            if (CurrentPage is VaultViewModel vault) _ = vault.RefreshAsync();
        });
    }

    // === 时钟偏差 ===

    private void StartClockTimer()
    {
        StopClockTimer();
        _clockTimer = new DispatcherTimer { Interval = ClockPollInterval };
        _clockTimer.Tick += (_, _) => _ = RefreshClockAsync(force: false);
        _clockTimer.Start();
    }

    private void StopClockTimer()
    {
        _clockTimer?.Stop();
        _clockTimer = null;
    }

    private void OnClockSynced(ClockSyncSnapshot snapshot)
        => Dispatcher.UIThread.Post(() => UpdateClockWarning(snapshot));

    [RelayCommand]
    private async Task RecheckClockAsync()
    {
        var ok = await RefreshClockAsync(force: true);
        Toast(ok ? "已重新校时" : "校时失败：连不上任何时间源");
    }

    /// <summary>返回 false 表示这次校时失败（离线）。**失败不弹告警**——离线不是错误。</summary>
    private async Task<bool> RefreshClockAsync(bool force)
    {
        if (_clock is null) return false;
        try
        {
            var snapshot = await _clock.SyncAsync(force);
            UpdateClockWarning(snapshot);
            return true;
        }
        catch (Exception ex)
        {
            // 全部时间源都失败：沿用上次快照（可能为 null），界面上看起来就是"没有横幅"
            Serilog.Log.Debug(ex, "Clock sync failed");
            UpdateClockWarning(_clock.LastSnapshot);
            return false;
        }
    }

    private void UpdateClockWarning(ClockSyncSnapshot? snapshot)
    {
        if (snapshot is null || !snapshot.IsDrifted)
        {
            ClockWarning = null;
            return;
        }

        var minutes = (int)Math.Round(Math.Abs(snapshot.Skew.TotalMinutes));
        var direction = snapshot.Skew < TimeSpan.Zero ? "慢" : "快";
        var amount = minutes >= 1 ? $"{minutes} 分钟" : $"{Math.Abs((int)snapshot.Skew.TotalSeconds)} 秒";
        ClockWarning = $"系统时钟比网络时间{direction} {amount}，验证码已按网络时间校正";
    }

#if DEBUG
    /// <summary>仅截图自动化用：在保险库页选中某条（找不到就选第一条）。</summary>
    internal void OpenCipherForAutomation(string name)
    {
        if (!_pages.TryGetValue(AppPage.Vault, out var page) || page is not VaultViewModel vault) return;

        var row = vault.Rows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
                  ?? vault.Rows.FirstOrDefault();
        vault.SelectedRow = row;

        // TAMA_UI_SNAPSHOT_EDIT=1：顺带进编辑态（编辑表单的排版要单独核对）
        if (Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT_EDIT") == "1" && vault.Selected is { } detail)
            detail.BeginEditCommand.Execute(null);
    }
#endif

    // === 订阅清理 ===
    private void DetachSubscriptions()
    {
        if (_notifier is not null) _notifier.Completed -= OnVaultSyncCompleted;
        _notifier = null;

        if (_clock is not null) _clock.Synced -= OnClockSynced;
        _clock = null;
    }
}

/// <summary>
/// 一个导航项。<see cref="Label"/> 给桌面侧栏，<see cref="ShortLabel"/> 给手机底部 Tab
/// （"通行密钥"在 412dp 宽的 6 等分格子里放不下，缩成"密钥"）。
/// 命令挂在项自己身上，两套模板都不需要跨层强转 DataContext。
/// </summary>
public sealed partial class NavItem : ObservableObject
{
    private readonly Action<AppPage> _navigate;

    public NavItem(AppPage page, string label, string shortLabel, Action<AppPage> navigate)
    {
        Page = page;
        Label = label;
        ShortLabel = shortLabel;
        _navigate = navigate;
        GoCommand = new RelayCommand(() => _navigate(Page));
    }

    public AppPage Page { get; }
    public string Label { get; }
    public string ShortLabel { get; }
    public IRelayCommand GoCommand { get; }

    [ObservableProperty] private bool _isActive;
}
