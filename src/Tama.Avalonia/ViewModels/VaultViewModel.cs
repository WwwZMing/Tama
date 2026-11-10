using System.Collections.ObjectModel;
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
public sealed class CipherRow
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

    public string TypeLabel => Dto.Type switch
    {
        1 => "登录",
        2 => "笔记",
        3 => "卡片",
        4 => "身份",
        _ => "其它",
    };

    public string Badge => !string.IsNullOrEmpty(FolderName) ? FolderName! : TypeLabel;
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

    private readonly HashSet<int> _types = new();
    private readonly HashSet<string> _folders = new();
    private bool _hasTotp;
    private bool _hasPasskey;

    private CancellationTokenSource? _searchCts;

    public VaultViewModel(AppSession session, ShellAccessor shell, ClipboardService clipboard, DialogService dialogs)
    {
        _session = session;
        _shell = shell;
        _clipboard = clipboard;
        _dialogs = dialogs;
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

    /// <summary>宽屏左栏 / 窄屏（无选中时）显示列表。</summary>
    public bool ShowList => !IsCompact || !HasSelection;

    /// <summary>宽屏右栏 / 窄屏（有选中时）显示详情。</summary>
    public bool ShowDetail => !IsCompact || HasSelection;

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
    }

    /// <summary>列表选中行 → 造详情 VM 并载入。</summary>
    partial void OnSelectedRowChanged(CipherRow? value)
    {
        if (value is null) return;
        if (Selected is not null && Selected.CipherId == value.Id) return;

        Selected = CipherDetailViewModel.ForExisting(_session, _shell, _clipboard, _dialogs, RefreshAsync);
        _ = Selected.LoadAsync(value.Id);
    }

    /// <summary>窄屏详情页的返回按钮。</summary>
    [RelayCommand]
    private void CloseDetail()
    {
        Selected = null;
        SelectedRow = null;
    }

    private IVaultApi Vault => _session.Services.GetRequiredService<IVaultApi>();
    private IFolderApi Folders => _session.Services.GetRequiredService<IFolderApi>();

    public override Task OnActivatedAsync() => RefreshAsync();

    partial void OnSearchTextChanged(string value)
    {
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
