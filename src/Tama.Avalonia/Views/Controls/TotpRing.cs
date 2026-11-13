using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Tama.Avalonia.Views.Controls;

/// <summary>
/// TOTP 剩余时间的小圆环：从 12 点方向起，顺时针画"还剩多少比例"。
///
/// 为什么自己画：列表里只需要一个 14px 的剩余时间提示，线性进度条在这个尺寸下
/// 读不出比例（而且横条会被行里的其他元素挤），圆环一眼就能看出"快过期了"。
/// </summary>
public sealed class TotpRing : Control
{
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<TotpRing, double>(nameof(Progress), 1d);

    public static readonly StyledProperty<IBrush?> RingBrushProperty =
        AvaloniaProperty.Register<TotpRing, IBrush?>(nameof(RingBrush));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<TotpRing, IBrush?>(nameof(TrackBrush));

    static TotpRing()
    {
        AffectsRender<TotpRing>(ProgressProperty, RingBrushProperty, TrackBrushProperty);
    }

    /// <summary>1 = 刚刷新（整圈），0 = 已过期（空环）。</summary>
    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public IBrush? RingBrush
    {
        get => GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 1) return;

        var thickness = Math.Max(1.5, size / 8);
        var radius = (size - thickness) / 2;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);

        if (TrackBrush is { } track)
            context.DrawEllipse(null, new Pen(track, thickness), center, radius, radius);

        var progress = Math.Clamp(Progress, 0, 1);
        if (progress <= 0 || RingBrush is not { } ring) return;

        var start = -Math.PI / 2;                       // 12 点
        var end = start + progress * Math.PI * 2;       // 顺时针

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(PointOn(center, radius, start), false);
            ctx.ArcTo(PointOn(center, radius, end), new Size(radius, radius), 0,
                progress > 0.5, SweepDirection.Clockwise);
        }

        context.DrawGeometry(null, new Pen(ring, thickness), geometry);
    }

    private static Point PointOn(Point center, double radius, double angle)
        => new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
}
