using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace Tama.Avalonia.Converters;

/// <summary>
/// 把"顶部那层浮层的高度"换成列表的顶部内边距。
///
/// 为什么需要它：Apple 的工具栏是**浮在内容之上**的（内容从它底下滚过去），
/// 而不是把内容往下推的一条实心栏。要做到这点，列表必须铺满整个区域，
/// 同时让第一行从浮层下沿开始——也就是把浮层高度变成列表的 top padding。
/// 高度是运行期测出来的（筛选 chip 会换行），所以只能绑定 + 转换，不能写死。
/// </summary>
public sealed class TopInsetConverter : IValueConverter
{
    /// <summary>浮层与第一行之间留一点呼吸，否则行会贴着浮层下沿。</summary>
    public double Extra { get; set; } = 6;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double height && height > 0
            ? new Thickness(0, height + Extra, 0, 0)
            : default(Thickness);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 手机端列表的内边距：顶部让开浮动工具栏（同 <see cref="TopInsetConverter"/>），
/// **底部让开浮动 Tab 条**。为什么底部也要：Tab 条现在是**浮在列表上**的玻璃层
/// （不模糊就没东西可采），列表必须铺满全高，末行才不会永远压在条底下出不来。
/// </summary>
public sealed class MobileListInsetConverter : IMultiValueConverter
{
    /// <summary>底部 Tab 条的占位：64 高 + 上下 margin/padding ≈ 90。</summary>
    public double BottomInset { get; set; } = 92;

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var top = values.Count > 0 && values[0] is double h && h > 0 ? h + 6 : 0;
        var compact = values.Count > 1 && values[1] is true;
        return new Thickness(0, top, 0, compact ? BottomInset : 0);
    }
}
