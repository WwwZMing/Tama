using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace Tama.Avalonia.Views.Controls;

/// <summary>
/// 品牌标记（环 + T + 底珠）。只描边不填充，底色从笔画空隙透出来——不需要 mask。
/// 颜色由使用方给（<see cref="Stroke"/>），侧栏用强调色、锁屏 hero 用强调色。
/// </summary>
public partial class BrandMark : UserControl
{
    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<BrandMark, IBrush?>(nameof(Stroke), Brushes.MediumPurple);

    public BrandMark() => AvaloniaXamlLoader.Load(this);

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }
}
