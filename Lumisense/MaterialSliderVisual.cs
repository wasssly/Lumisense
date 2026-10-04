using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Lumisense;

// Вид Slider в стиле Material 3: активная и неактивная части дорожки, зазор, тонкая вертикальная «ручка» и точка в конце.
// Только рисует поверх шаблона; значение, мышь и клавиатуру по-прежнему ведут Slider/Track/Thumb (Thumb остаётся невидимой зоной захвата).
// Размеры из документации Material (дорожка 16, ручка 4x44, зазор 6) уменьшены под плотность настольного окна.
public sealed class MaterialSliderVisual : FrameworkElement
{
    private const double TrackThickness = 8.0;
    private const double HandleWidth = 4.0;
    private const double HandlePressedWidth = 2.0;
    private const double HandleLength = 20.0;
    private const double HandleGap = 4.0;
    private const double InnerCornerRadius = 2.0;
    private const double StopDotSize = 3.0;
    private const double DisabledOpacity = 0.38;

    // Для слайдеров с отрицательным диапазоном (эквалайзер) активная часть идёт от нуля, а не от края.
    public static readonly DependencyProperty IsCenteredProperty = DependencyProperty.Register(
        nameof(IsCentered), typeof(bool), typeof(MaterialSliderVisual),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool IsCentered
    {
        get => (bool)GetValue(IsCenteredProperty);
        set => SetValue(IsCenteredProperty, value);
    }

    public static readonly DependencyProperty ActiveBrushProperty = DependencyProperty.Register(
        nameof(ActiveBrush), typeof(Brush), typeof(MaterialSliderVisual),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush ActiveBrush
    {
        get => (Brush)GetValue(ActiveBrushProperty);
        set => SetValue(ActiveBrushProperty, value);
    }

    public static readonly DependencyProperty InactiveBrushProperty = DependencyProperty.Register(
        nameof(InactiveBrush), typeof(Brush), typeof(MaterialSliderVisual),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush InactiveBrush
    {
        get => (Brush)GetValue(InactiveBrushProperty);
        set => SetValue(InactiveBrushProperty, value);
    }

    public static readonly DependencyProperty ActiveTickBrushProperty = DependencyProperty.Register(
        nameof(ActiveTickBrush), typeof(Brush), typeof(MaterialSliderVisual),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    // Цвет точек-ориентиров на активной части дорожки (контрастный к акценту).
    public Brush ActiveTickBrush
    {
        get => (Brush)GetValue(ActiveTickBrushProperty);
        set => SetValue(ActiveTickBrushProperty, value);
    }

    public static readonly DependencyProperty InactiveTickBrushProperty = DependencyProperty.Register(
        nameof(InactiveTickBrush), typeof(Brush), typeof(MaterialSliderVisual),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    // Цвет точек-ориентиров на неактивной части дорожки.
    public Brush InactiveTickBrush
    {
        get => (Brush)GetValue(InactiveTickBrushProperty);
        set => SetValue(InactiveTickBrushProperty, value);
    }

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(MaterialSliderVisual),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    // 0..1; используется, когда элемент стоит не в шаблоне Slider, а сам по себе (полосы прогресса: главное окно, Now Playing, мини-плеер).
    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    private Slider? _slider;

    public MaterialSliderVisual()
    {
        Unloaded += (_, _) => Detach();
    }

    // Подписка ленивая: шаблон создаёт элемент скрытым, а рисоваться он начинает только в режиме Material.
    private void Attach()
    {
        if (_slider is not null || TemplatedParent is not Slider slider) return;

        _slider = slider;
        slider.ValueChanged += Slider_Changed;
        slider.SizeChanged += Slider_SizeChanged;
        slider.IsEnabledChanged += Slider_EnabledChanged;
        slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(Thumb_DragChanged));
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(Thumb_DragChanged));
    }

    private void Detach()
    {
        if (_slider is null) return;

        _slider.ValueChanged -= Slider_Changed;
        _slider.SizeChanged -= Slider_SizeChanged;
        _slider.IsEnabledChanged -= Slider_EnabledChanged;
        _slider.RemoveHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(Thumb_DragChanged));
        _slider.RemoveHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(Thumb_DragChanged));
        _slider = null;
    }

    private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => InvalidateVisual();

    private void Slider_SizeChanged(object sender, SizeChangedEventArgs e) => InvalidateVisual();

    private void Slider_EnabledChanged(object sender, DependencyPropertyChangedEventArgs e) => InvalidateVisual();

    private void Thumb_DragChanged(object sender, RoutedEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        Attach();

        Slider? slider = _slider;
        bool vertical = false;
        double length;
        double thumbLength;
        double crossSize;
        Point origin = new(0, 0);
        bool isDragging = false;

        if (TemplatedParent is Slider)
        {
            if (slider is null ||
                slider.Template?.FindName("PART_Track", slider) is not Track track ||
                track.Thumb is not Thumb thumb ||
                slider.Maximum <= slider.Minimum)
            {
                return;
            }

            vertical = slider.Orientation == Orientation.Vertical;
            length = vertical ? track.ActualHeight : track.ActualWidth;
            thumbLength = vertical ? thumb.ActualHeight : thumb.ActualWidth;
            crossSize = vertical ? track.ActualWidth : track.ActualHeight;
            isDragging = thumb.IsDragging;
            try
            {
                origin = track.TransformToVisual(this).Transform(new Point(0, 0));
            }
            catch (InvalidOperationException)
            {
                return;
            }
        }
        else
        {
            // Самостоятельный режим: горизонтальная полоса на весь элемент, позиция берётся из Progress.
            length = ActualWidth;
            crossSize = ActualHeight;
            thumbLength = HandleWidth;
        }

        if (length <= TrackThickness || crossSize <= 0 || thumbLength <= 0) return;

        // Локальные координаты: x идёт вдоль слайдера от минимума, y — поперёк от центра дорожки.
        // Вертикальный слайдер поворачивается на 90°: минимум внизу, как у штатного Slider.
        Matrix matrix = vertical
            ? new Matrix(0, -1, 1, 0, origin.X + crossSize / 2, origin.Y + length)
            : new Matrix(1, 0, 0, 1, origin.X, origin.Y + crossSize / 2);

        bool reversed = slider?.IsDirectionReversed == true;
        double minimum = slider?.Minimum ?? 0.0;
        double maximum = slider?.Maximum ?? 1.0;
        double value = slider?.Value ?? (double.IsFinite(Progress) ? Progress : 0.0);
        double range = maximum - minimum;
        double ratio = Math.Clamp((value - minimum) / range, 0.0, 1.0);
        double centerRatio = Math.Clamp((0.0 - minimum) / range, 0.0, 1.0);
        if (reversed)
        {
            ratio = 1.0 - ratio;
            centerRatio = 1.0 - centerRatio;
        }

        double usable = Math.Max(0.0, length - thumbLength);
        double handleX = thumbLength / 2 + usable * ratio;
        double originX = IsCentered && minimum < 0 && maximum > 0
            ? thumbLength / 2 + usable * centerRatio
            : reversed ? length : 0.0;

        // Нажатая ручка сужается, как в Material.
        double handleWidth = isDragging ? HandlePressedWidth : HandleWidth;
        double gapHalf = handleWidth / 2 + HandleGap;
        double radius = TrackThickness / 2;

        // Три участка вдоль слайдера: неактивный до p1, активный между p1 и p2, неактивный после p2.
        bool handleFirst = handleX <= originX;
        double p1 = Math.Min(originX, handleX);
        double p2 = Math.Max(originX, handleX);
        bool originInside = originX > 0.0 && originX < length;

        // У ручки зазор побольше, у нуля в центрированном слайдере — небольшой, у краёв зазора нет.
        double GapAt(bool isHandleEnd) => isHandleEnd ? gapHalf : originInside ? 1.0 : 0.0;

        bool enabled = IsEnabled && (slider?.IsEnabled ?? true);
        if (!enabled) dc.PushOpacity(DisabledOpacity);
        dc.PushTransform(new MatrixTransform(matrix));

        DrawSegment(dc, InactiveBrush, 0.0, p1, 0.0, GapAt(handleFirst), radius, length);
        DrawSegment(dc, ActiveBrush, p1, p2, GapAt(handleFirst), GapAt(!handleFirst), radius, length);
        DrawSegment(dc, InactiveBrush, p2, length, GapAt(!handleFirst), 0.0, radius, length);

        double dotRadius = StopDotSize / 2;
        bool hasTicks = slider is not null &&
                        DrawTicks(dc, slider, thumbLength, usable, handleX, gapHalf, p1, p2, dotRadius);

        // Точка на конце неактивного участка, как stop indicator у Material (когда своих точек-ориентиров нет).
        if (!hasTicks)
        {
            double dotX = radius;
            if (p1 - GapAt(handleFirst) > dotX + dotRadius + radius)
                dc.DrawEllipse(ActiveBrush, null, new Point(dotX, 0), dotRadius, dotRadius);
            if (p2 + GapAt(!handleFirst) < length - dotX - dotRadius - radius)
                dc.DrawEllipse(ActiveBrush, null, new Point(length - dotX, 0), dotRadius, dotRadius);
        }

        DrawRoundedBar(dc, ActiveBrush, handleX - handleWidth / 2, handleX + handleWidth / 2, HandleLength, handleWidth / 2, handleWidth / 2);

        dc.Pop();
        if (!enabled) dc.Pop();
    }

    // Точки-ориентиры только там, где они включены явно (SliderMilestoneMarkers); у слайдеров скорости и тона их нет.
    // Возвращает true, если хоть одна точка нарисована.
    private bool DrawTicks(DrawingContext dc, Slider slider, double thumbLength, double usable, double handleX,
        double gapHalf, double p1, double p2, double dotRadius)
    {
        double range = slider.Maximum - slider.Minimum;
        double frequency = 0.0;
        if (SliderMilestoneMarkers.GetIsEnabled(slider))
        {
            frequency = SliderMilestoneMarkers.GetMarkerFrequency(slider);
            if (frequency <= 0) frequency = slider.TickFrequency;
        }

        if (frequency <= 0 || range <= 0) return false;

        bool drawn = false;
        double epsilon = frequency * 0.0001;
        for (double v = slider.Minimum; v <= slider.Maximum + epsilon; v += frequency)
        {
            double rel = (Math.Min(v, slider.Maximum) - slider.Minimum) / range;
            if (slider.IsDirectionReversed) rel = 1.0 - rel;
            double x = thumbLength / 2 + usable * rel;

            // В зазоре у ручки точка не нужна: её место занимает сама ручка.
            if (Math.Abs(x - handleX) < gapHalf + dotRadius) continue;

            Brush brush = x >= p1 && x <= p2 ? ActiveTickBrush : InactiveTickBrush;
            dc.DrawEllipse(brush, null, new Point(x, 0), dotRadius, dotRadius);
            drawn = true;
        }

        return drawn;
    }

    // Участок дорожки [from, to] со срезами у соседей; внешние края (у 0 и length) скруглены полностью, внутренние — на 2.
    private static void DrawSegment(DrawingContext dc, Brush brush, double from, double to, double trimStart, double trimEnd,
        double outerRadius, double length)
    {
        double leftRadius = from <= 0.01 ? outerRadius : InnerCornerRadius;
        double rightRadius = to >= length - 0.01 ? outerRadius : InnerCornerRadius;
        double x0 = from + trimStart;
        double x1 = to - trimEnd;
        if (x1 - x0 < 2.0) return;

        DrawRoundedBar(dc, brush, x0, x1, TrackThickness, leftRadius, rightRadius);
    }

    private static void DrawRoundedBar(DrawingContext dc, Brush brush, double x0, double x1, double height, double leftRadius, double rightRadius)
    {
        double half = height / 2;
        double maxRadius = Math.Min(half, (x1 - x0) / 2);
        double rl = Math.Min(leftRadius, maxRadius);
        double rr = Math.Min(rightRadius, maxRadius);

        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(x0 + rl, -half), true, true);
            ctx.LineTo(new Point(x1 - rr, -half), true, false);
            if (rr > 0) ctx.ArcTo(new Point(x1, -half + rr), new Size(rr, rr), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(x1, half - rr), true, false);
            if (rr > 0) ctx.ArcTo(new Point(x1 - rr, half), new Size(rr, rr), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(x0 + rl, half), true, false);
            if (rl > 0) ctx.ArcTo(new Point(x0, half - rl), new Size(rl, rl), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(x0, -half + rl), true, false);
            if (rl > 0) ctx.ArcTo(new Point(x0 + rl, -half), new Size(rl, rl), 0, false, SweepDirection.Clockwise, true, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(brush, null, geometry);
    }
}
