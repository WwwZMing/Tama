using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Tama.Avalonia.Services;
using Tama.Core.Contracts;

namespace Tama.Avalonia.ViewModels;

/// <summary>
/// 安全卫士（Watchtower）：弱密码 / 重复使用 / 撞库泄露 / 无 2FA / 不安全条目。
///
/// 报告本身带 12 小时缓存（服务端实现），所以进入页面直接请求、不做前端缓存；
/// 撞库检测要联网（HIBP k-anonymity），慢的时候界面必须看得出来"正在算"——
/// 那不是卡死，是这个页面本来就要等几秒。
/// </summary>
public partial class WatchtowerViewModel : PageViewModel
{
    private readonly AppSession _session;

    public WatchtowerViewModel(AppSession session) => _session = session;

    public ObservableCollection<SecurityIssueDto> Issues { get; } = new();

    [ObservableProperty] private double _score;
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private int _totalItems;
    [ObservableProperty] private int _weak;
    [ObservableProperty] private int _reused;
    [ObservableProperty] private int _breached;
    [ObservableProperty] private int _no2Fa;
    [ObservableProperty] private int _unsecure;

    /// <summary>有问题才画问题列表；"没有发现问题"只在**分析过且确实为空**时出现。</summary>
    public bool HasIssues => Issues.Count > 0;
    public bool ShowNoIssues => IsLoaded && Issues.Count == 0;

    /// <summary>还没出结果时不要显示 0——"0 分 · 风险较高"会被误读成"库很危险"。</summary>
    public string ScoreText => IsLoaded ? $"{Math.Round(Score)}" : "—";
    public string ScoreCaption => !IsLoaded ? "还没分析" : Score switch
    {
        >= 90 => "很安全",
        >= 70 => "还可以，有几处要修",
        >= 40 => "有明显问题",
        _ => "风险较高",
    };

    partial void OnScoreChanged(double value)
    {
        OnPropertyChanged(nameof(ScoreText));
        OnPropertyChanged(nameof(ScoreCaption));
    }

    partial void OnIsLoadedChanged(bool value)
    {
        OnPropertyChanged(nameof(ScoreText));
        OnPropertyChanged(nameof(ScoreCaption));
    }

    private IWatchtowerApi Watchtower => _session.Services.GetRequiredService<IWatchtowerApi>();

    public override Task OnActivatedAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        ClearError();
        try
        {
            var report = await Watchtower.Report();

            Score = report.Score;
            TotalItems = report.Stats.TotalItems;
            Weak = report.Stats.WeakPasswords;
            Reused = report.Stats.ReusedPasswords;
            Breached = report.Stats.BreachedPasswords;
            No2Fa = report.Stats.No2Fa;
            Unsecure = report.Stats.Unsecure;

            Issues.Clear();
            foreach (var issue in report.Issues) Issues.Add(issue);
            OnPropertyChanged(nameof(HasIssues));
            OnPropertyChanged(nameof(ShowNoIssues));

            IsLoaded = true;
        }
        catch (Exception ex)
        {
            Fail(ex, "安全报告生成失败（撞库检测需要联网）");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
