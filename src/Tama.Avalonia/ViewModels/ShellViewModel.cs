using Avalonia.Controls;
using Avalonia.Media;
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
    [ObservableProperty] private ToastKind _toastKind;
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

    /// <summary>侧栏的分组视图（底部 tab 不用它，tab 只认顺序）。</summary>
    public IReadOnlyList<NavGroup> NavGroups { get; }

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

    /// <summary>侧栏底部那颗按钮的文案：显示"点一下会切到什么"，而不是当前状态。</summary>
    public string ThemeToggleLabel => _theme.IsDark ? "浅色模式" : "深色模式";

    /// <summary>深/浅直接互切（跟随系统仍然在设置页里选，这里不做三态循环）。</summary>
    [RelayCommand]
    private void ToggleTheme()
    {
        _theme.Apply(_theme.IsDark ? ThemeService.Mode.Light : ThemeService.Mode.Dark);
        OnPropertyChanged(nameof(ThemeToggleLabel));
        OnPropertyChanged(nameof(IsDarkTheme));
    }

    public ShellViewModel(AppSession session, ThemeService theme, ShellAccessor shellAccessor)
    {
        _session = session;
        _theme = theme;
        shellAccessor.Value = this;

        NavItems = new[]
        {
            new NavItem(AppPage.Vault, "保险库", "保险库", Icons.Vault, page => _ = NavigateAsync(page)),
            new NavItem(AppPage.Generator, "生成器", "生成", Icons.Sparkle, page => _ = NavigateAsync(page)),
            new NavItem(AppPage.Watchtower, "安全卫士", "安全", Icons.Shield, page => _ = NavigateAsync(page)),
            new NavItem(AppPage.Accounts, "账号", "账号", Icons.User, page => _ = NavigateAsync(page)),
            new NavItem(AppPage.Passkeys, "通行密钥", "密钥", Icons.Key, page => _ = NavigateAsync(page)),
            new NavItem(AppPage.Settings, "设置", "设置", Icons.Gear, page => _ = NavigateAsync(page)),
        };

        // 侧栏按语义分三组（密码库 / 安全 / 账号）：六项平铺会把"我在干什么"这层信息丢掉。
        // 顺序与**底部 tab**（NavItems 的原始顺序）刻意不同：tab 要按使用频率排，
        // 侧栏按语义归类——两处各按各的逻辑，不强行统一。
        NavGroups = new[]
        {
            new NavGroup("密码库", new[] { NavItems[0], NavItems[4] }),
            new NavGroup("安全", new[] { NavItems[2], NavItems[1] }),
            new NavGroup("账号", new[] { NavItems[3], NavItems[5] }),
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
            else await TryAutoBiometricAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Shell initialization failed");
        }
    }

    private bool _bioAutoPromptTried;

    /// <summary>
    /// 启动时自动弹一次 Windows Hello / 指纹验证（旧版解锁页的行为）：既然已经保存过凭据，
    /// 就不该让人每次开应用都先敲一遍主密码。
    ///
    /// 三个"不弹"的条件都是有来历的：
    ///   · 已经试过一次 → 取消/失败后绝不再骚扰，手动按钮兜底（旧版 <c>_bioAutoTried</c>）；
    ///   · <see cref="UnlockViewModel.BioAvailable"/> 为假 → 平台不支持、没存凭据、或处在
    ///     72h 主密码重验证窗口内（这时按策略就不该允许 Hello 解锁）；
    ///   · 截图/自动化模式 → 验证窗会挡住渲染，而且自动化跑在隔离库上，弹了也没意义。
    /// </summary>
    public async Task TryAutoBiometricAsync()
    {
        if (_bioAutoPromptTried || IsUnlocked || !Unlock.BioAvailable) return;
        if (!UiPrefs.AutoBioPrompt) return;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TAMA_UI_SNAPSHOT"))) return;

        _bioAutoPromptTried = true;
        try
        {
            await Unlock.RunBiometricAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Auto biometric unlock failed");
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

    // === 左右滑动切页（窄屏） ===

    /// <summary>
    /// 窄屏左右滑动切页。<paramref name="dir"/> = +1 下一项 / −1 上一项，按 <see cref="NavItems"/> 的顺序。
    ///
    /// 语义照搬 Blazor 版的 <c>shell.js</c> + <c>MainLayout.OnSwipeAsync</c>：
    ///   · 只在底栏页面之间来回，到两端不动作（不循环）；
    ///   · 当前页不在导航里就什么都不做（绝不猜用户的意图）。
    /// 额外一条手机上更符合直觉的：**保险库详情页右滑 = 返回列表**（不切到上一个 tab）。
    /// </summary>
    public void SwipeNavigate(int dir)
    {
        if (!IsUnlocked || !IsCompact || dir == 0) return;

        // 详情页右滑 → 退回列表（Android 上"右滑返回"是肌肉记忆，切页会让人懵）
        if (dir < 0 && CurrentPage is VaultViewModel { Selected: not null } vault)
        {
            vault.Selected = null;
            vault.SelectedRow = null;
            return;
        }

        var index = -1;
        for (var i = 0; i < NavItems.Count; i++)
        {
            if (NavItems[i].Page != CurrentPageKey) continue;
            index = i;
            break;
        }
        if (index < 0) return;

        var next = index + dir;
        if (next < 0 || next >= NavItems.Count) return;   // 两端到头，不动

        _ = NavigateAsync(NavItems[next].Page);
    }

    // === 浮层（对话框） ===

    /// <summary>点遮罩 / 按 Esc 是否允许关掉当前浮层（表单类浮层会设成 false）。</summary>
    public bool OverlayCanDismiss { get; private set; } = true;

    public async Task ShowOverlayAsync(Control content, bool canDismiss = true)
    {
        OverlayContent = content;
        OverlayCanDismiss = canDismiss;
        _overlayTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _overlayTcs.Task;
    }

    /// <summary>点遮罩 / Esc：允许关才关（关 = 走与"取消"相同的语义）。</summary>
    public void DismissOverlay()
    {
        if (OverlayContent is null || !OverlayCanDismiss) return;
        CloseOverlay();
    }

    public void CloseOverlay()
    {
        OverlayContent = null;
        var tcs = _overlayTcs;
        _overlayTcs = null;
        tcs?.TrySetResult();
    }

    // === 提示 ===

    void IShell.Toast(string message, ToastKind kind) => Toast(message, kind);

    /// <summary>
    /// 提示条的语气与图标。颜色在样式里（Border.toast / .ok / .err），
    /// 这里只把"是哪一类"和对应图标暴露给视图。
    /// </summary>
    public bool IsToastSuccess => ToastKind == ToastKind.Success;
    public bool IsToastError => ToastKind == ToastKind.Error;
    public Geometry ToastIcon => ToastKind switch
    {
        ToastKind.Success => Icons.Check,
        ToastKind.Error => Icons.Alert,
        _ => Icons.Info,
    };

    partial void OnToastKindChanged(ToastKind value)
    {
        OnPropertyChanged(nameof(IsToastSuccess));
        OnPropertyChanged(nameof(IsToastError));
        OnPropertyChanged(nameof(ToastIcon));
    }

    private void Toast(string message, ToastKind kind = ToastKind.Info)
    {
        ToastKind = kind;
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

        // 先清空再选：手机端"就地展开"挂在选中变化上，直接赋同一行不会触发（截不到展开态）
        vault.SelectedRow = null;
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

    public NavItem(AppPage page, string label, string shortLabel, Geometry icon, Action<AppPage> navigate)
    {
        Page = page;
        Label = label;
        ShortLabel = shortLabel;
        Icon = icon;
        _navigate = navigate;
        GoCommand = new RelayCommand(() => _navigate(Page));
    }

    public AppPage Page { get; }
    public string Label { get; }
    public string ShortLabel { get; }
    public Geometry Icon { get; }
    public IRelayCommand GoCommand { get; }

    [ObservableProperty] private bool _isActive;
}

/// <summary>侧栏的一个分组（标题 + 若干导航项）。</summary>
public sealed class NavGroup
{
    public NavGroup(string title, IReadOnlyList<NavItem> items)
    {
        Title = title;
        Items = items;
    }

    public string Title { get; }
    public IReadOnlyList<NavItem> Items { get; }
}
