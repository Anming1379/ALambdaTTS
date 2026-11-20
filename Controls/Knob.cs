using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ALambdaTTS.Services;

namespace ALambdaTTS.Controls;

/// <summary>
/// 数字音频工作站样式的旋钮：
/// 鼠标上划顺时针拧（值变大），下划逆时针；滚轮按步进微调，双击回默认值。
/// 弧线用 135 度到 405 度的 270 度量程，和常见音频软件一致。
/// </summary>
public sealed class Knob : Control
{
    private const double StartAngle = 135;
    private const double SweepAngle = 270;

    private bool _dragging;
    private double _dragStartValue;
    private double _dragStartY;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(Knob),
        new FrameworkPropertyMetadata(
            0d,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnValueChanged,
            CoerceValue));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum),
        typeof(double),
        typeof(Knob),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum),
        typeof(double),
        typeof(Knob),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step),
        typeof(double),
        typeof(Knob),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// 可选的双极中心点：设了以后该值落在 12 点方向（例如语速 0%），
    /// 两侧各自线性铺开。音量这类单极量不设即可。
    /// </summary>
    public static readonly DependencyProperty CenterValueProperty = DependencyProperty.Register(
        nameof(CenterValue),
        typeof(double?),
        typeof(Knob),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ResetValueProperty = DependencyProperty.Register(
        nameof(ResetValue),
        typeof(double),
        typeof(Knob),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public Knob()
    {
        Width = 30;
        Height = 30;
        Focusable = false;
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;

        // 色板是 DynamicResource，切主题后要主动重画
        Loaded += (_, _) => ThemeManager.Changed += OnThemeChanged;
        Unloaded += (_, _) => ThemeManager.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => InvalidateVisual();

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Step
    {
        get => (double)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public double ResetValue
    {
        get => (double)GetValue(ResetValueProperty);
        set => SetValue(ResetValueProperty, value);
    }

    public double? CenterValue
    {
        get => (double?)GetValue(CenterValueProperty);
        set => SetValue(CenterValueProperty, value);
    }

    private static object CoerceValue(DependencyObject d, object baseValue)
    {
        var knob = (Knob)d;
        var value = (double)baseValue;
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return knob.Minimum;
        }

        return Math.Clamp(value, knob.Minimum, knob.Maximum);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((Knob)d).InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 4)
        {
            return;
        }

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var thickness = Math.Max(2.5, size * 0.1);
        var radius = size / 2 - thickness / 2 - 1;

        // 透明填充保证整个圆都可命中
        drawingContext.DrawEllipse(Brushes.Transparent, null, center, radius, radius);

        var track = ResolveBrush("BorderStrong", Color.FromRgb(0xCC, 0xCC, 0xCC));
        var accent = ResolveBrush("Accent", Color.FromRgb(0x20, 0x80, 0x8D));
        var body = ResolveBrush("SurfaceAlt", Color.FromRgb(0xF0, 0xF0, 0xF0));

        // 旋钮本体：内圈实心，让弧线和指针看起来像实体旋钮
        var bodyRadius = Math.Max(1, radius - thickness * 0.85);
        drawingContext.DrawEllipse(body, null, center, bodyRadius, bodyRadius);

        var trackPen = new Pen(track, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };

        drawingContext.DrawGeometry(null, trackPen, BuildArc(center, radius, StartAngle, SweepAngle));

        var fraction = FractionOf(Value);
        if (fraction > 0.001)
        {
            var accentPen = new Pen(accent, thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            drawingContext.DrawGeometry(null, accentPen, BuildArc(center, radius, StartAngle, SweepAngle * fraction));
        }

        // 指针：从圆心指向内圈边缘
        var angle = (StartAngle + SweepAngle * fraction) * Math.PI / 180;
        var direction = new Vector(Math.Cos(angle), Math.Sin(angle));
        var inner = center + direction * (Math.Min(bodyRadius, radius) * 0.18);
        var outer = center + direction * (radius - thickness * 1.05);
        var pointerPen = new Pen(accent, Math.Max(2, thickness * 0.62))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        drawingContext.DrawLine(pointerPen, inner, outer);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (e.ClickCount == 2)
        {
            SetCurrentValue(ValueProperty, Snap(ResetValue));
            e.Handled = true;
            return;
        }

        _dragging = true;
        _dragStartValue = Value;
        _dragStartY = e.GetPosition(this).Y;
        Cursor = Cursors.SizeNS;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!_dragging)
        {
            return;
        }

        // 向上拖 160 px 走完整个量程
        var delta = _dragStartY - e.GetPosition(this).Y;
        var range = Maximum - Minimum;
        var raw = _dragStartValue + delta * range / 160.0;
        SetCurrentValue(ValueProperty, Snap(raw));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Cursor = Cursors.Hand;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        var steps = e.Delta / 120.0;
        SetCurrentValue(ValueProperty, Snap(Value + steps * Step));
        e.Handled = true;
    }

    /// <summary>值在 0~1 弧线上的位置；有中心点时两侧分别线性铺开。</summary>
    private double FractionOf(double value)
    {
        if (CenterValue is { } center && center > Minimum && center < Maximum)
        {
            return value >= center
                ? 0.5 + 0.5 * Math.Clamp((value - center) / (Maximum - center), 0, 1)
                : 0.5 * Math.Clamp((value - Minimum) / (center - Minimum), 0, 1);
        }

        var range = Maximum - Minimum;
        return range <= 0 ? 0 : Math.Clamp((value - Minimum) / range, 0, 1);
    }

    private double Snap(double raw)
    {
        var step = Step <= 0 ? 1 : Step;
        var snapped = Math.Round(raw / step, MidpointRounding.AwayFromZero) * step;
        return Math.Clamp(snapped, Minimum, Maximum);
    }

    private static SolidColorBrush ResolveBrush(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is SolidColorBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(fallback);
    }

    private static Geometry BuildArc(Point center, double radius, double startDegrees, double sweepDegrees)
    {
        if (Math.Abs(sweepDegrees) < 0.1)
        {
            return Geometry.Empty;
        }

        var start = PointOnCircle(center, radius, startDegrees);
        var end = PointOnCircle(center, radius, startDegrees + sweepDegrees);

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, isFilled: false, isClosed: false);
            context.ArcTo(
                end,
                new Size(radius, radius),
                rotationAngle: 0,
                isLargeArc: Math.Abs(sweepDegrees) > 180,
                sweepDirection: sweepDegrees > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise,
                isStroked: true,
                isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }
}
