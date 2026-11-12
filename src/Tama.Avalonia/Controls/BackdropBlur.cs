using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.VisualTree;
using SkiaSharp;

namespace Tama.Avalonia.Controls;

/// <summary>
/// 真正的"背后内容模糊"（backdrop blur / 毛玻璃）。
///
/// 原理：Avalonia 的文字/图形最终画进一个 Skia surface。在自绘操作里通过
/// <see cref="ISkiaSharpApiLeaseFeature"/> 拿到当前 surface，<c>Snapshot()</c> 就能读到
/// **画到此刻为止已经画好的内容**（也就是"我背后"）。再给这张快照套一个高斯模糊、
/// 裁成自己的圆角矩形画上去，就得到了真正的背景模糊。
///
/// 这不是模拟：
///   · 不是拿半透明色糊一层（那样底下的字会透出来）；
///   · 也不是先渲染到 RenderTargetBitmap 再降采样（那样滚动时会"冻结"）。
///
/// 代价与限制（写清楚，免得以后踩）：
///   1. 只对 **Skia 后端**有效。Avalonia 正与 Google 合作接 Impeller，将来换后端时
///      这里会拿到 null lease —— 已经做了降级（画一层纯色，不崩）。
///   2. 每帧 Snapshot 是一次 GPU 拷贝。静止时用 <see cref="IsDynamic"/> = false 缓存，
///      只在需要时 <see cref="InvalidateBackdrop"/>。
///   3. 模糊半径按设备像素算，所以要乘 canvas 的缩放（高 DPI 下不乘会糊得不一样）。
/// </summary>
public sealed class BackdropBlur : Control
{
    public static readonly StyledProperty<double> RadiusProperty =
        AvaloniaProperty.Register<BackdropBlur, double>(nameof(Radius), 18d,
            coerce: (_, v) => Math.Max(0, v));

    public static readonly StyledProperty<IBrush?> TintProperty =
        AvaloniaProperty.Register<BackdropBlur, IBrush?>(nameof(Tint));

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<BackdropBlur, CornerRadius>(nameof(CornerRadius));

    /// <summary>
    /// true = 每帧重新取背景（跟手，但每帧一次 GPU 拷贝）；
    /// false = 只在第一次和 <see cref="InvalidateBackdrop"/> 时取（内容滚动时用这个，滚动结束再刷新）。
    /// </summary>
    public static readonly StyledProperty<bool> IsDynamicProperty =
        AvaloniaProperty.Register<BackdropBlur, bool>(nameof(IsDynamic), true);

    /// <summary>取不到 Skia 时的兜底色（跨后端降级用，不能是 null，否则那块会透明）。</summary>
    public static readonly StyledProperty<IBrush?> FallbackProperty =
        AvaloniaProperty.Register<BackdropBlur, IBrush?>(nameof(Fallback));

    static BackdropBlur()
    {
        AffectsRender<BackdropBlur>(RadiusProperty, TintProperty, CornerRadiusProperty);
    }

    public double Radius
    {
        get => GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    public IBrush? Tint
    {
        get => GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public bool IsDynamic
    {
        get => GetValue(IsDynamicProperty);
        set => SetValue(IsDynamicProperty, value);
    }

    public IBrush? Fallback
    {
        get => GetValue(FallbackProperty);
        set => SetValue(FallbackProperty, value);
    }

    /// <summary>丢掉缓存的背影，下一帧重新取（内容变了 / 滚动结束）。</summary>
    public void InvalidateBackdrop()
    {
        _cachedBackdrop?.Dispose();
        _cachedBackdrop = null;
        InvalidateVisual();
    }

    private SKImage? _cachedBackdrop;
    private bool _backendUnavailable;

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _cachedBackdrop?.Dispose();
        _cachedBackdrop = null;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        if (_backendUnavailable)
        {
            DrawFallback(context, bounds);
            return;
        }

        context.Custom(new BlurOp(this, bounds, GetTintColor(),
            IsDynamic ? null : _cachedBackdrop));
    }

    private Color? GetTintColor() => Tint switch
    {
        ISolidColorBrush solid => solid.Color,
        _ => null,
    };

    private void DrawFallback(DrawingContext context, Rect bounds)
    {
        var brush = Fallback ?? Tint;
        if (brush is not null) context.FillRectangle(brush, bounds, (float)CornerRadius.TopLeft);
    }

    /// <summary>一次自绘：取背影 → 模糊 → 裁圆角 → 叠色。</summary>
    private sealed class BlurOp : ICustomDrawOperation
    {
        private readonly BackdropBlur _owner;
        private readonly Rect _bounds;
        private readonly Color? _tint;
        private readonly SKImage? _reusable;

        public BlurOp(BackdropBlur owner, Rect bounds, Color? tint, SKImage? reusable)
        {
            _owner = owner;
            _bounds = bounds;
            _tint = tint;
            _reusable = reusable;
        }

        public Rect Bounds => _bounds;

        public bool HitTest(Point p) => false;   // 只是装饰，不参与命中测试

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Dispose() => _reusable?.Dispose();

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature is null)
            {
                // 非 Skia 后端（未来的 Impeller / 软件渲染）：降级成纯色，不能崩
                _owner._backendUnavailable = true;
                return;
            }

            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;
            var surface = lease.SkSurface;
            if (surface is null)
            {
                _owner._backendUnavailable = true;
                return;
            }

            // 背景快照：动态就每帧取，静态就复用缓存的（缓存归属控件）
            var backdrop = _reusable ?? surface.Snapshot();
            if (_reusable is null)
            {
                _owner._cachedBackdrop?.Dispose();
                _owner._cachedBackdrop = backdrop;
            }

            var matrix = canvas.TotalMatrix;
            if (!matrix.TryInvert(out var inverse)) return;

            // 控件在屏幕上的位置 → 设备像素矩形（快照就是设备像素尺寸）
            var deviceRect = matrix.MapRect(new SKRect(
                (float)_bounds.Left, (float)_bounds.Top,
                (float)_bounds.Right, (float)_bounds.Bottom));

            var scale = (float)Math.Sqrt(Math.Abs(matrix.ScaleX * matrix.ScaleY));
            if (scale <= 0) scale = 1;

            canvas.Save();
            canvas.ResetMatrix();

            var radius = (float)Math.Max(0, _owner.CornerRadius.TopLeft) * scale;
            using var clip = new SKRoundRect(deviceRect, radius, radius);
            canvas.ClipRoundRect(clip, SKClipOperation.Intersect, antialias: true);

            var sigma = (float)_owner.Radius * scale;
            if (sigma > 0.01f)
            {
                using var blur = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp);
                using var paint = new SKPaint { ImageFilter = blur, IsAntialias = true };
                canvas.DrawImage(backdrop, 0, 0, paint);
            }
            else
            {
                canvas.DrawImage(backdrop, 0, 0);
            }

            if (_tint is { } tint)
            {
                using var tintPaint = new SKPaint { Color = new SKColor(tint.R, tint.G, tint.B, tint.A) };
                canvas.DrawRect(deviceRect, tintPaint);
            }

            canvas.Restore();
            _ = inverse;
        }
    }
}
