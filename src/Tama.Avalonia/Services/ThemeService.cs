using Avalonia;
using Avalonia.Styling;

namespace Tama.Avalonia.Services;

/// <summary>
/// 主题：三档（跟随系统 / 强制暗 / 强制亮），落 <see cref="Tama.Core.AppPaths.ThemeModePath"/>。
///
/// 与 Blazor 版本的区别：那边要靠 JS 在电路起来之前上色（否则闪白），
/// Avalonia 里 XAML 一加载就是最终颜色，没有首帧问题——文件读取只要在 <c>App.Initialize</c> 里完成即可。
/// </summary>
public sealed class ThemeService
{
    public enum Mode { System, Dark, Light }

    public Mode Current { get; private set; } = Mode.System;

    /// <summary>当前**实际**生效的配色（跟随系统时取系统值）。</summary>
    public bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    /// <summary>启动时调用一次：读回上次选择并应用。</summary>
    public void Initialize()
    {
        try
        {
            if (File.Exists(Core.AppPaths.ThemeModePath))
            {
                Current = File.ReadAllText(Core.AppPaths.ThemeModePath).Trim().ToLowerInvariant() switch
                {
                    "dark" => Mode.Dark,
                    "light" => Mode.Light,
                    _ => Mode.System,
                };
            }
        }
        catch
        {
            // 主题文件坏了不影响启动，退回跟随系统
            Current = Mode.System;
        }

        Apply(Current, persist: false);
    }

    public void Apply(Mode mode, bool persist = true)
    {
        Current = mode;
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = mode switch
            {
                Mode.Dark => ThemeVariant.Dark,
                Mode.Light => ThemeVariant.Light,
                _ => ThemeVariant.Default,
            };
        }

        if (!persist) return;
        try { File.WriteAllText(Core.AppPaths.ThemeModePath, mode.ToString().ToLowerInvariant()); }
        catch { /* 主题落盘失败不值得打断用户 */ }
    }
}
