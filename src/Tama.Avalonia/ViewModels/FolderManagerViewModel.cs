using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tama.Core.Contracts;

namespace Tama.Avalonia.ViewModels;

/// <summary>文件夹行：名称就地可改（避免"弹窗里再弹窗"——模态套模态在移动端尤其难用）。</summary>
public partial class FolderRowViewModel : ObservableObject
{
    private readonly FolderManagerViewModel _owner;

    public FolderRowViewModel(FolderDto dto, FolderManagerViewModel owner)
    {
        _owner = owner;
        Dto = dto;
        _name = dto.Name;
    }

    public FolderDto Dto { get; }
    public string Id => Dto.Id;
    public bool IsPending => !string.Equals(Dto.SyncStatus, "synced", StringComparison.OrdinalIgnoreCase);
    public string PendingText => IsPending ? "未同步" : "";

    [ObservableProperty] private string _name;

    public bool CanRename => !string.IsNullOrWhiteSpace(Name) && Name != Dto.Name;

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(CanRename));

    [RelayCommand]
    private Task SaveAsync() => _owner.RenameAsync(this);

    [RelayCommand]
    private Task DeleteAsync() => _owner.DeleteAsync(this);
}

/// <summary>文件夹管理弹层：新建 / 重命名 / 删除（离线都可用，改动会进推送队列）。</summary>
public partial class FolderManagerViewModel : ObservableObject
{
    private readonly IFolderApi _folders;

    public FolderManagerViewModel(IFolderApi folders) => _folders = folders;

    public ObservableCollection<FolderRowViewModel> Rows { get; } = new();

    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isBusy;

    public event Action? CloseRequested;

    public async Task LoadAsync()
    {
        try
        {
            var folders = await _folders.List();
            Rows.Clear();
            foreach (var folder in folders) Rows.Add(new FolderRowViewModel(folder, this));
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Load folders failed");
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var name = NewName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            Error = "文件夹名不能为空";
            return;
        }

        IsBusy = true;
        Error = null;
        try
        {
            await _folders.Create(name);
            NewName = "";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RenameAsync(FolderRowViewModel row)
    {
        IsBusy = true;
        Error = null;
        try
        {
            await _folders.Rename(row.Id, row.Name.Trim());
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeleteAsync(FolderRowViewModel row)
    {
        IsBusy = true;
        Error = null;
        try
        {
            // 文件夹里的条目不会跟着删（变成未分类），所以不需要二次确认弹窗
            await _folders.Delete(row.Id);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();
}
