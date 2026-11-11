using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Tama.Avalonia.Services;
using Tama.Core.Contracts;

namespace Tama.Avalonia.ViewModels;

/// <summary>密码生成器：长度 + 字符集 + 强度评估。</summary>
public partial class GeneratorViewModel : PageViewModel
{
    private readonly AppSession _session;
    private readonly ShellAccessor _shell;
    private readonly ClipboardService _clipboard;

    public GeneratorViewModel(AppSession session, ShellAccessor shell, ClipboardService clipboard)
    {
        _session = session;
        _shell = shell;
        _clipboard = clipboard;
    }

    /// <summary>Slider 的值是 double，直接用 double 存，避免每次绑定都做一次数值转换。</summary>
    [ObservableProperty] private double _length = 20;
    [ObservableProperty] private bool _useUpper = true;
    [ObservableProperty] private bool _useLower = true;
    [ObservableProperty] private bool _useNumbers = true;
    [ObservableProperty] private bool _useSymbols = true;
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _strengthLabel = "";
    [ObservableProperty] private int _strengthScore;

    public string LengthText => $"{(int)Length} 位";

    private IPasswordApi Passwords => _session.Services.GetRequiredService<IPasswordApi>();

    public override Task OnActivatedAsync()
    {
        Generate();
        return Task.CompletedTask;
    }

    partial void OnLengthChanged(double value)
    {
        OnPropertyChanged(nameof(LengthText));
        Generate();
    }
    partial void OnUseUpperChanged(bool value) => Generate();
    partial void OnUseLowerChanged(bool value) => Generate();
    partial void OnUseNumbersChanged(bool value) => Generate();
    partial void OnUseSymbolsChanged(bool value) => Generate();

    [RelayCommand]
    private void Generate()
    {
        ClearError();
        try
        {
            var length = (int)Length;
            var result = Passwords.Generate(new PasswordGenerateRequest
            {
                Length = length,
                Options = new Dictionary<string, bool>
                {
                    ["uppercase"] = UseUpper,
                    ["lowercase"] = UseLower,
                    ["numbers"] = UseNumbers,
                    ["symbols"] = UseSymbols,
                },
            });

            Password = result.Password;

            var strength = Passwords.Strength(result.Password);
            StrengthLabel = strength.Label;
            StrengthScore = strength.Score;
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    [RelayCommand]
    private async Task CopyAsync()
    {
        if (string.IsNullOrEmpty(Password)) return;
        await _clipboard.CopyAsync(Password);
        _shell.Shell.Toast("密码已复制（30 秒后自动清空）", ToastKind.Success);
    }
}
