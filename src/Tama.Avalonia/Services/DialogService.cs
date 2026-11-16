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
        var cancelButton = new Button { Content = cancel, Classes = { "secondary" } };

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
        var cancelButton = new Button { Content = "取消", Classes = { "secondary" } };

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
        => (await PickFileNamedAsync(title, patterns))?.Bytes;

    /// <summary>
    /// 同 <see cref="PickFileAsync"/>，但连文件名一起返回——还原备份时要在界面上写出
    /// "正在还原 tama-export-20260322.json" 这种话，只拿到字节是没法说的。
    /// </summary>
    public async Task<(string Name, byte[] Bytes)?> PickFileNamedAsync(string title, params string[] patterns)
    {
        var storage = TopLevelProvider?.Invoke()?.StorageProvider;
        if (storage is null) return null;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            // MimeTypes 给 */* 是**必须**的：Android SAF 按 MIME 过滤，而 .kdbx 没有任何注册的
            // MIME 类型 —— 只给 Patterns 的话选择器会显示成空的，用户以为"选不了文件"（实测踩过）。
            FileTypeFilter = patterns.Length == 0
                ? null
                : new[] { new FilePickerFileType(title) { Patterns = patterns, MimeTypes = new[] { "*/*" } } },
        });

        if (files.Count == 0) return null;

        await using var stream = await files[0].OpenReadAsync();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return (files[0].Name, buffer.ToArray());
    }

    /// <summary>
    /// 系统"另存为"（Android 走 SAF、桌面走保存对话框），把字节写到用户选的位置。
    /// 返回用户看到的文件名/路径；取消返回 null。
    ///
    /// 导出功能在手机上唯一正确的落盘方式就是这个：直接写 Downloads 会落进应用沙盒，
    /// 文件管理器里根本看不到（表现为"导出没反应"）。
    /// </summary>
    public async Task<string?> SaveFileAsync(string suggestedName, byte[] bytes,
        string extension = "json", string fileTypeName = "JSON", string mimeType = "application/json")
    {
        var storage = TopLevelProvider?.Invoke()?.StorageProvider;
        if (storage is null) return null;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存导出文件",
            SuggestedFileName = suggestedName,
            // Avalonia Android 会无条件再拼一次 DefaultExtension；已经带扩展名时不重复。
            DefaultExtension = string.Equals(Path.GetExtension(suggestedName), "." + extension,
                StringComparison.OrdinalIgnoreCase) ? null : extension,
            ShowOverwritePrompt = true,
            FileTypeChoices = new[]
            {
                new FilePickerFileType(fileTypeName)
                {
                    Patterns = new[] { $"*.{extension}" },
                    MimeTypes = new[] { mimeType },
                },
            },
        });

        if (file is null) return null;

        await using var stream = await file.OpenWriteAsync();
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();

        return string.IsNullOrEmpty(file.Path.LocalPath) ? file.Name : file.Path.LocalPath;
    }

    /// <summary>
    /// 把任意视图放进壳内浮层（手机上的"新建条目"编辑器走这条）。
    /// 返回的 Task 在浮层被关闭时完成；关掉之后要做什么由调用方决定。
    /// </summary>
    public Task ShowOverlayAsync(Control content, bool canDismiss = true)
        => _shellAccessor.Shell.ShowOverlayAsync(Card(content), canDismiss);

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

    /// <summary>窄屏判定（由 App 接上 ShellViewModel.IsCompact）。</summary>
    public Func<bool>? IsCompactProvider { get; set; }

    /// <summary>
    /// 对话框外壳。
    ///
    /// 桌面 = 居中卡片（宽 560 上限）；手机 = **底部抽屉**：贴底、满宽、只圆上沿。
    /// 手机上居中卡片有两个毛病：一是键盘一弹就被顶到看不见，二是居中留白把正文挤成窄条。
    /// 贴底的抽屉更符合单手操作，也能跟"下滑收回"的直觉对上。
    /// </summary>
    private Control Card(Control content, double padding = 20)
    {
        var compact = IsCompactProvider?.Invoke() == true;

        return new Border
        {
            Classes = { "card" },
            Padding = new Thickness(padding),
            CornerRadius = compact ? new CornerRadius(20, 20, 0, 0) : new CornerRadius(18),
            // 浮层浮起来靠阴影，不靠描边（和全站"控件不描边"同一条规矩）
            BoxShadow = BoxShadows.Parse("0 16 44 0 #77000000"),
            MaxWidth = compact ? double.PositiveInfinity : 560,
            HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Center,
            Child = new ScrollViewer { Content = content, MaxHeight = compact ? 560 : 520 },
            // 背景交给 "card" 类的样式（{DynamicResource TamaCardBrush}）。
            // 这里**不要**再 FindResource 手动取：主题画刷放在 ThemeDictionaries 里，
            // 不带 variant 的 FindResource 会返回 UnsetValue，强转 IBrush 直接抛
            // "Unable to cast UnsetValueType to IBrush" —— 表现就是"对话框点了没反应"（实测踩过）。
        };
    }
}
