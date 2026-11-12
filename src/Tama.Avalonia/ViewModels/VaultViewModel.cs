using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Tama.Avalonia.Services;
using Tama.Core.Contracts;

namespace Tama.Avalonia.ViewModels;

/// <summary>筛选项（类型 / 文件夹 / 属性）。计数来自 <see cref="CipherFacets"/>，口径见契约注释。</summary>
public partial class FacetChip : ObservableObject
{
    public FacetChip(string key, string label, Action onToggle)
    {
        Key = key;
        Label = label;
        _onToggle = onToggle;
        ToggleCommand = new RelayCommand(() => _onToggle());
    }

    private readonly Action _onToggle;

    public string Key { get; }
    public string Label { get; }

    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isEnabled = true;

    public string Display => Count > 0 ? $"{Label} {Count}" : Label;

    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(Display));

    public IRelayCommand ToggleCommand { get; }
}

/// <summary>列表行。</summary>
public sealed partial class CipherRow : ObservableObject
{
    public CipherRow(CipherDto dto, string? folderName)
    {
        Dto = dto;
        FolderName = folderName;
    }

    public CipherDto Dto { get; }
    public string? FolderName { get; }

    public string Id => Dto.Id;
    public string Name => string.IsNullOrWhiteSpace(Dto.Name) ? "(未命名)" : Dto.Name!;
    public string? Username => Dto.Login?.Username;
    public bool Favorite => Dto.Favorite;
    public bool HasTotp => !string.IsNullOrWhiteSpace(Dto.Login?.Totp);
    public bool IsPending => !string.Equals(Dto.SyncStatus, "synced", StringComparison.OrdinalIgnoreCase);

    /// <summary>已同步（行右侧绿色标签）。和 <see cref="IsPending"/> 互斥，XAML 里各用一个。</summary>
    public bool IsSynced => !IsPending;

    public bool HasFolder => !string.IsNullOrWhiteSpace(FolderName);

    public string TypeLabel => Dto.Type switch
    {
        1 => "登录",
        2 => "笔记",
        3 => "卡片",
        4 => "身份",
        _ => "其它",
    };

    /// <summary>
    /// 手机端：这一行是否就地展开（手风琴）。桌面不用——桌面是右侧详情面板。
    /// </summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>就地展开时挂在这里的详情 VM（收起时置空，避免整列表都持着一堆 VM）。</summary>
    [ObservableProperty] private CipherDetailViewModel? _detail;

    /// <summary>行首的类型图标（手写 path，见 Icons）。登录用地球而不是钥匙，
    /// 因为登录条目绝大多数是网站——钥匙会和侧栏的"通行密钥"撞车。</summary>
    public Geometry Icon => Dto.Type switch
    {
        2 => Icons.Note,
        3 => Icons.Card,
        4 => Icons.Identity,
        _ => Icons.Globe,
    };
}

/// <summary>
/// 保险库页：搜索 + 分面筛选 + 列表 + 详情。
///
/// 语义完全沿用契约里的定义（栏内并集、跨栏交集），**计数口径不能改**：
/// 每个选项的数字是"其它栏目都生效、但不含它自己那一栏"还能命中多少条，
/// 改成"全条件"的话选了文件夹 A 之后别的文件夹全变 0，一选就锁死。
/// </summary>
public partial class VaultViewModel : PageViewModel
{
    private readonly AppSession _session;
    private readonly ShellAccessor _shell;
    private readonly ClipboardService _clipboard;
    private readonly DialogService _dialogs;
    private readonly AutofillRequest _autofill;

    private readonly HashSet<int> _types = new();
    private readonly HashSet<string> _folders = new();
    private bool _hasTotp;
    private bool _hasPasskey;

    private CancellationTokenSource? _searchCts;

    public VaultViewModel(AppSession session, ShellAccessor shell, ClipboardService clipboard,
        DialogService dialogs, AutofillRequest autofill)
    {
        _session = session;
        _shell = shell;
        _clipboard = clipboard;
        _dialogs = dialogs;
        _autofill = autofill;
    }

    public ObservableCollection<CipherRow> Rows { get; } = new();
    public ObservableCollection<FacetChip> TypeChips { get; } = new();
    public ObservableCollection<FacetChip> FolderChips { get; } = new();

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private CipherRow? _selectedRow;
    [ObservableProperty] private CipherDetailViewModel? _selected;
    [ObservableProperty] private bool _hasFilters;
    [ObservableProperty] private bool _isTotpFilterOn;
    [ObservableProperty] private bool _isPasskeyFilterOn;

    /// <summary>文件夹分面是否非空（Avalonia 不做 int→bool 隐式转换，界面不能直接绑 Count）。</summary>
    [ObservableProperty] private bool _hasFolderChips;

    public bool HasSelection => Selected is not null;

    /// <summary>列表永远在（手机上详情是"就地展开"，不再切成第二屏）。</summary>
    public bool ShowList => true;

    /// <summary>只有宽屏才有右侧详情栏。</summary>
    public bool ShowDetail => !IsCompact;

    partial void OnSelectedChanged(CipherDetailViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(ShowList));
        OnPropertyChanged(nameof(ShowDetail));
    }

    protected override void OnCompactChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowList));
        OnPropertyChanged(nameof(ShowDetail));

        // 从手机拖宽成桌面：就地展开的那一行要收起来，详情已经在右栏里了
        if (!value && _expandedRow is not null) CollapseRow(_expandedRow);

        // 详情自己也要知道宽窄：窄屏不重复标题、不套卡片
        if (Selected is not null) Selected.IsCompact = value;
    }

    /// <summary>当前就地展开的那一行（手机手风琴：同时只开一个）。</summary>
    private CipherRow? _expandedRow;

    /// <summary>
    /// 列表选中行 → 造详情 VM 并载入。
    ///
    /// 桌面：详情进右栏。
    /// 手机：**就地展开在行下面**（原 Blazor 版的交互）——不跳页、不丢上下文，
    /// 收起时列表还在原来的滚动位置。手风琴语义：展开新的一行会收起上一行。
    /// </summary>
    partial void OnSelectedRowChanged(CipherRow? value)
    {
        if (_expandedRow is not null && !ReferenceEquals(_expandedRow, value))
            CollapseRow(_expandedRow);

        if (value is null) return;

        // 只有"换了一条"才重建详情 VM；同一 id 复用（否则每点一次都重新解密一遍）
        if (Selected is null || Selected.CipherId != value.Id)
        {
            Selected = CipherDetailViewModel.ForExisting(_session, _shell, _clipboard, _dialogs, RefreshAsync);
            Selected.IsCompact = IsCompact;
            _ = Selected.LoadAsync(value.Id);
        }

        if (!IsCompact) return;

        // ⚠️ 展开这段必须在"复用 VM"的短路之后单独写：早先写成
        //    "同一 id 直接 return"，结果手机端点了行只是高亮、详情不出来（实测）。
        value.Detail = Selected;
        value.IsExpanded = true;
        _expandedRow = value;
    }

    private void CollapseRow(CipherRow row)
    {
        row.IsExpanded = false;
        row.Detail = null;
        if (ReferenceEquals(_expandedRow, row)) _expandedRow = null;
    }

    /// <summary>收起就地展开的详情（行内的「收起」按钮 / 窄屏返回）。</summary>
    [RelayCommand]
    private void CloseDetail()
    {
        if (_expandedRow is not null) CollapseRow(_expandedRow);
        Selected = null;
        SelectedRow = null;
    }

    private IVaultApi Vault => _session.Services.GetRequiredService<IVaultApi>();
    private IFolderApi Folders => _session.Services.GetRequiredService<IFolderApi>();

    public override async Task OnActivatedAsync()
    {
        // 从 Android 自动填充跳过来的：把搜索框预填成那个域名，用户一眼就能看到对应的条目。
        // 只在搜索框还空着时填——不能覆盖用户自己敲的关键字。
        // 直接赋 SearchText 会触发下面的防抖（多查一次），所以这一次用 _suppressDebounce 跳过。
        var target = _autofill.Take();
        if (!string.IsNullOrEmpty(target) && string.IsNullOrWhiteSpace(SearchText))
        {
            _suppressDebounce = true;
            SearchText = target;
            _suppressDebounce = false;
        }

        await RefreshAsync();
    }

    /// <summary>为 true 时 <see cref="SearchText"/> 的变化不再排防抖查询（由调用方自己刷新）。</summary>
    private bool _suppressDebounce;

    partial void OnSearchTextChanged(string value)
    {
        if (_suppressDebounce) return;

        // 300ms 防抖：每敲一个字母就查一次库（198 条也要几十毫秒 + EF 往返）会卡输入
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, cts.Token);
                await Dispatcher.UIThread.InvokeAsync(async () => await RefreshAsync());
            }
            catch (OperationCanceledException) { /* 又敲了一个键 */ }
        }, cts.Token);
    }

    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        ClearError();
        try
        {
            var response = await Vault.Search(new CipherSearchRequest
            {
                Query = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
                Types = _types.Count == 0 ? null : _types.ToList(),
                FolderIds = _folders.Count == 0 ? null : _folders.ToList(),
                HasTotp = _hasTotp,
                HasPasskey = _hasPasskey,
            });

            Apply(response);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Apply(CipherSearchResponse response)
    {
        var folderNames = response.Folders.ToDictionary(f => f.Id, f => f.Name);

        Rows.Clear();
        foreach (var cipher in response.Ciphers)
        {
            folderNames.TryGetValue(cipher.FolderId ?? "", out var folderName);
            Rows.Add(new CipherRow(cipher, folderName));
        }

        RebuildFacets(response.Facets);

        IsEmpty = Rows.Count == 0;
        Summary = Rows.Count == 0
            ? ""
            : $"共 {Rows.Count} 条" + (response.Facets.Total > Rows.Count ? $"（库里 {response.Facets.Total} 条）" : "");

        HasFilters = _types.Count > 0 || _folders.Count > 0 || _hasTotp || _hasPasskey;

        // 选中项仍然存在就保持选中（避免每次刷新后详情面板被清空）
        if (Selected is { IsNew: false } detail && Rows.All(r => r.Id != detail.CipherId))
        {
            Selected = null;
            SelectedRow = null;
        }
    }

    private void RebuildFacets(CipherFacets facets)
    {
        if (TypeChips.Count == 0)
        {
            foreach (var type in new[] { 1, 2, 3, 4 })
            {
                var key = type;
                TypeChips.Add(new FacetChip($"type:{key}", TypeName(key), () => ToggleType(key)));
            }
        }

        foreach (var chip in TypeChips)
        {
            var type = int.Parse(chip.Key.AsSpan(5));
            var facet = facets.Types.FirstOrDefault(t => t.Type == type);
            chip.Count = facet?.Count ?? 0;
            chip.IsSelected = _types.Contains(type);
            // 计数为 0 且未选中才禁用：**已选中的永不禁用**，否则用户取消不掉（死局）
            chip.IsEnabled = chip.Count > 0 || chip.IsSelected;
        }

        FolderChips.Clear();
        foreach (var facet in facets.Folders)
        {
            var id = facet.Id;
            var chip = new FacetChip($"folder:{id}", facet.Name, () => ToggleFolder(id))
            {
                Count = facet.Count,
                IsSelected = _folders.Contains(id),
            };
            chip.IsEnabled = chip.Count > 0 || chip.IsSelected;
            FolderChips.Add(chip);
        }

        HasFolderChips = FolderChips.Count > 0;
    }

    private static string TypeName(int type) => type switch
    {
        1 => "登录",
        2 => "笔记",
        3 => "卡片",
        4 => "身份",
        _ => "其它",
    };

    // === 筛选 ===

    private void ToggleType(int type)
    {
        if (!_types.Remove(type)) _types.Add(type);
        _ = RefreshAsync();
    }

    private void ToggleFolder(string id)
    {
        if (!_folders.Remove(id)) _folders.Add(id);
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void ToggleTotp()
    {
        _hasTotp = !_hasTotp;
        IsTotpFilterOn = _hasTotp;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void TogglePasskey()
    {
        _hasPasskey = !_hasPasskey;
        IsPasskeyFilterOn = _hasPasskey;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void ClearFilters()
    {
        _types.Clear();
        _folders.Clear();
        _hasTotp = false;
        _hasPasskey = false;
        IsTotpFilterOn = false;
        IsPasskeyFilterOn = false;
        SearchText = "";
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void NewLogin() => StartNew(1);

    [RelayCommand]
    private void NewNote() => StartNew(2);

    private void StartNew(int type)
    {
        Selected = CipherDetailViewModel.ForNew(_session, _shell, _clipboard, _dialogs, RefreshAsync, type);
    }

    [RelayCommand]
    private async Task ManageFoldersAsync()
    {
        try
        {
            await _dialogs.ManageFoldersAsync(Folders);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }
}
