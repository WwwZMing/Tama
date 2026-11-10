using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Tama.Avalonia.ViewModels;
using Tama.Core.Contracts;

namespace Tama.Avalonia.Services;

/// <summary>
/// 轻量对话框：确认 / 单行输入 / 选文件 / 文件夹管理。
///
/// **所有对话框都走壳里的浮层，不用系统窗口**。理由：
///   · Android 是单视图宿主，没有可用的 owner <c>Window</c>，<c>ShowDialog</c> 根本弹不出来；
///   · 两条路径（桌面窗口 / 移动浮层）意味着两套布局、两种关闭语义，迟早走样。
/// 统一成浮层之后，桌面与安卓是同一份代码、同一套样式，也不需要把主窗口引用传进来。
/// </summary>
public sealed class DialogService
{
    private readonly ShellAccessor _shellAccessor;

    public DialogService(ShellAccessor shellAccessor) => _shellAccessor = shellAccessor;

    /// <summary>TopLevel（剪贴板与文件选择器要用）。由 App 在视图建好后接上。</summary>
    public Func<TopLevel?>? TopLevelProvider { get; set; }

    public async Task<bool> ConfirmAsync(string title, string message, string ok = "确定", string cancel = "取消")
    {
        var result = false;
        var okButton = new Button { Content = ok, Classes = { "primary" } };
        var cancelButton = new Button { Content = cancel, Classes = { "ghost" } };

        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(new TextBlock { Text = title, Classes = { "h2" } });
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 });
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Children = { cancelButton, okButton },
        });

        var card = Card(body);
        var shell = _shellAccessor.Shell;

        cancelButton.Click += (_, _) => shell.CloseOverlay();
        okButton.Click += (_, _) => { result = true; shell.CloseOverlay(); };

        await shell.ShowOverlayAsync(card);
        return result;
    }

    public async Task<string?> PromptAsync(string title, string label, string? initial = null)
    {
        string? result = null;
        var input = new TextBox { Text = initial ?? "", PlaceholderText = label, Classes = { "field" } };
        var okButton = new Button { Content = "确定", Classes = { "primary" } };
        var cancelButton = new Button { Content = "取消", Classes = { "ghost" } };

        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(new TextBlock { Text = title, Classes = { "h2" } });
        body.Children.Add(new TextBlock { Text = label, Classes = { "muted" } });
        body.Children.Add(input);
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Children = { cancelButton, okButton },
        });

        var card = Card(body);
        var shell = _shellAccessor.Shell;

        void Accept()
        {
            result = input.Text;
            shell.CloseOverlay();
        }

        cancelButton.Click += (_, _) => shell.CloseOverlay();
        okButton.Click += (_, _) => Accept();
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Accept();
        };

        await shell.ShowOverlayAsync(card);
        return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
    }

    /// <summary>选一个文件并把内容读成 byte[]（导入 .kdbx / .json 用）。取消返回 null。</summary>
    public async Task<byte[]?> PickFileAsync(string title, params string[] patterns)
    {
        var storage = TopLevelProvider?.Invoke()?.StorageProvider;
        if (storage is null) return null;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = patterns.Length == 0
                ? null
                : new[] { new FilePickerFileType(title) { Patterns = patterns } },
        });

        if (files.Count == 0) return null;

        await using var stream = await files[0].OpenReadAsync();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    /// <summary>文件夹管理浮层（新建 / 就地改名 / 删除）。关掉后调用方自行刷新列表。</summary>
    public async Task ManageFoldersAsync(IFolderApi folders)
    {
        var vm = new FolderManagerViewModel(folders);
        var view = new Views.FolderManagerView { DataContext = vm };

        var shell = _shellAccessor.Shell;
        vm.CloseRequested += shell.CloseOverlay;

        await vm.LoadAsync();
        await shell.ShowOverlayAsync(Card(view, padding: 20));
    }

    private static Control Card(Control content, double padding = 20)
        => new Border
        {
            Classes = { "card" },
            Padding = new Thickness(padding),
            MaxWidth = 560,
            Child = new ScrollViewer { Content = content, MaxHeight = 520 },
            Background = (IBrush?)Application.Current?.FindResource("TamaCardBrush") ?? Brushes.Black,
        };
}
