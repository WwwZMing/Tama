using Avalonia.Media;

namespace Tama.Avalonia;

/// <summary>
/// 图标：手写 <see cref="StreamGeometry"/>，不引图标字体也不引 icon pack。
///
/// 为什么不引字体（比如 Segoe Fluent Icons）：Android 上没有这套字体，缺字形就是豆腐块；
/// 为什么不用图标库：整个应用只需要十个出头的字形，引一整个包不如写十行 path。
/// 统一用线框（Stroke 绘制，见 Styles/Tama.axaml 的 Path.icon），16~18px 下比实心块清爽。
/// </summary>
public static class Icons
{
    // === 侧栏导航 ===

    /// <summary>保险库：带把手的保险箱。</summary>
    public static readonly Geometry Vault = Geometry.Parse(
        "M6 4 H18 A2 2 0 0 1 20 6 V18 A2 2 0 0 1 18 20 H6 A2 2 0 0 1 4 18 V6 A2 2 0 0 1 6 4 Z " +
        "M12 8.5 A3.5 3.5 0 1 0 12 15.5 A3.5 3.5 0 1 0 12 8.5 Z M12 12 V15.5");

    /// <summary>通行密钥：钥匙。</summary>
    public static readonly Geometry Key = Geometry.Parse(
        "M12.5 12 H20 M17 12 V15 M19.5 12 V14.5 " +
        "M9 8.5 A3.5 3.5 0 1 0 9 15.5 A3.5 3.5 0 1 0 9 8.5 Z");

    /// <summary>安全卫士：盾牌。</summary>
    public static readonly Geometry Shield = Geometry.Parse(
        "M12 3.5 L19 6.5 V12.5 C19 16.8 16 19.6 12 20.8 C8 19.6 5 16.8 5 12.5 V6.5 Z");

    /// <summary>生成器：四角星。</summary>
    public static readonly Geometry Sparkle = Geometry.Parse(
        "M12 4 L13.7 9.3 L19 11 L13.7 12.7 L12 18 L10.3 12.7 L5 11 L10.3 9.3 Z");

    /// <summary>账号：人像。</summary>
    public static readonly Geometry User = Geometry.Parse(
        "M12 8 A3.5 3.5 0 1 0 12 15 A3.5 3.5 0 1 0 12 8 Z " +
        "M5 20 C6.2 16.6 8.8 15 12 15 C15.2 15 17.8 16.6 19 20");

    /// <summary>设置：齿轮。</summary>
    public static readonly Geometry Gear = Geometry.Parse(
        "M12 9 A3 3 0 1 0 12 15 A3 3 0 1 0 12 9 Z " +
        "M12 3 V5.5 M12 18.5 V21 M3 12 H5.5 M18.5 12 H21 " +
        "M5.6 5.6 L7.4 7.4 M16.6 16.6 L18.4 18.4 " +
        "M18.4 5.6 L16.6 7.4 M7.4 16.6 L5.6 18.4");

    // === 列表行（按条目类型）===

    /// <summary>登录：地球（网址）。</summary>
    public static readonly Geometry Globe = Geometry.Parse(
        "M12 4 A8 8 0 1 0 12 20 A8 8 0 1 0 12 4 Z M4 12 H20 " +
        "M12 4 C15 7 15 17 12 20 M12 4 C9 7 9 17 12 20");

    /// <summary>笔记：文档。</summary>
    public static readonly Geometry Note = Geometry.Parse(
        "M7 4 H15 L19 8 V20 H7 Z M15 4 V8 H19 M10 12 H16 M10 16 H14");

    /// <summary>卡片：银行卡。</summary>
    public static readonly Geometry Card = Geometry.Parse(
        "M3 7 H21 V17 H3 Z M3 10.5 H21 M6 14.5 H10");

    /// <summary>身份：证件。</summary>
    public static readonly Geometry Identity = Geometry.Parse(
        "M5 5 H19 V19 H5 Z M9 10 A2 2 0 1 0 9 14 A2 2 0 1 0 9 10 Z M7 17 C7.6 15.4 9 14.8 10.4 14.8 " +
        "M12.5 10 H17 M12.5 13 H17");

    /// <summary>列表行右侧的指示箭头。</summary>
    public static readonly Geometry Chevron = Geometry.Parse("M9 6 L15 12 L9 18");

    /// <summary>侧栏底部的深浅色切换（月牙）。</summary>
    public static readonly Geometry Moon = Geometry.Parse(
        "M19 14.5 A7.5 7.5 0 1 1 9.5 5 A6 6 0 0 0 19 14.5 Z");

    /// <summary>锁定。</summary>
    public static readonly Geometry Lock = Geometry.Parse(
        "M7.5 10 V8 A4.5 4.5 0 0 1 16.5 8 V10 M5 10 H19 V20 H5 Z");
}
