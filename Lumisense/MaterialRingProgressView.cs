using System.Windows;
using System.Windows.Media;

namespace Lumisense;

// Прогресс вокруг обложки мини-плеера в стиле Material: волнистая проигранная часть, зазор и спокойная дорожка по контуру.
// Контур тот же, что у обычного кольца (старт в середине верхней стороны, по часовой), в системе координат холста 50x50.
public sealed class MaterialRingProgressView : MaterialWaveElement
{
    private const double ArtworkSize = 42.0;
    private const double CanvasSize = 50.0;
    private const double CornerRadius = 8.0;

    // Зазор между волной и дорожкой, амплитуда и целевая длина волны под размер кольца.
    private const double GapSize = 3.0;
    private const double WaveAmplitude = 1.2;
    private const double TargetWavelength = 14.0;
    private const double SampleStep = 1.0;

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(MaterialRingProgressView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(MaterialRingProgressView),
        new FrameworkPropertyMetadata(2.5, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public static readonly DependencyProperty IsCircleProperty = DependencyProperty.Register(
        nameof(IsCircle), typeof(bool), typeof(MaterialRingProgressView),
        new FrameworkPropertyMetadata(false, OnShapeChanged));

    // true — круглая обложка (винил), false — скруглённый квадрат.
    public bool IsCircle
    {
        get => (bool)GetValue(IsCircleProperty);
        set => SetValue(IsCircleProperty, value);
    }

    public static readonly DependencyProperty PlayedBrushProperty = DependencyProperty.Register(
        nameof(PlayedBrush), typeof(Brush), typeof(MaterialRingProgressView),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush PlayedBrush
    {
        get => (Brush)GetValue(PlayedBrushProperty);
        set => SetValue(PlayedBrushProperty, value);
    }

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(MaterialRingProgressView),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    private PathGeometry? _outline;
    private double _perimeter;
    private double _wavelength = TargetWavelength;

    protected override double Wavelength => _wavelength;

    private static void OnShapeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (MaterialRingProgressView)d;
        view._outline = null;
        view.InvalidateVisual();
    }

    // Контур строится один раз на форму; длина волны подгоняется так, чтобы по периметру укладывалось целое число волн
    // (иначе у полного кольца на стыке был бы скачок).
    private void EnsureOutline()
    {
        if (_outline is not null) return;

        const double offset = (CanvasSize - ArtworkSize) / 2.0;
        const double center = offset + ArtworkSize / 2.0;
        const double left = offset;
        const double top = offset;
        const double right = offset + ArtworkSize;
        const double bottom = offset + ArtworkSize;

        var figure = new PathFigure { StartPoint = new Point(center, top), IsClosed = true, IsFilled = false };
        if (IsCircle)
        {
            double r = ArtworkSize / 2.0;
            figure.Segments.Add(new ArcSegment(new Point(center, bottom), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
            figure.Segments.Add(new ArcSegment(new Point(center, top), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
            _perimeter = 2.0 * Math.PI * r;
        }
        else
        {
            const double c = CornerRadius;
            figure.Segments.Add(new LineSegment(new Point(right - c, top), true));
            figure.Segments.Add(new ArcSegment(new Point(right, top + c), new Size(c, c), 0, false, SweepDirection.Clockwise, true));
            figure.Segments.Add(new LineSegment(new Point(right, bottom - c), true));
            figure.Segments.Add(new ArcSegment(new Point(right - c, bottom), new Size(c, c), 0, false, SweepDirection.Clockwise, true));
            figure.Segments.Add(new LineSegment(new Point(left + c, bottom), true));
            figure.Segments.Add(new ArcSegment(new Point(left, bottom - c), new Size(c, c), 0, false, SweepDirection.Clockwise, true));
            figure.Segments.Add(new LineSegment(new Point(left, top + c), true));
            figure.Segments.Add(new ArcSegment(new Point(left + c, top), new Size(c, c), 0, false, SweepDirection.Clockwise, true));
            figure.Segments.Add(new LineSegment(new Point(center, top), true));
            _perimeter = 4.0 * (ArtworkSize - 2.0 * c) + 2.0 * Math.PI * c;
        }

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        _outline = geometry;
        _wavelength = _perimeter / Math.Max(1.0, Math.Round(_perimeter / TargetWavelength));
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        EnsureOutline();
        if (_outline is null || _perimeter <= 0) return;

        double progress = double.IsFinite(Progress) ? Math.Clamp(Progress, 0.0, 1.0) : 0.0;
        double thickness = Math.Clamp(Thickness, 1.0, 6.0);
        double activeLength = _perimeter * progress;

        var trackPen = MakePen(TrackBrush, thickness);
        var playedPen = MakePen(PlayedBrush, thickness);

        // Зазор с обеих сторон активной части; у полного кольца зазора нет.
        bool full = progress >= 0.9999;
        double gap = full ? 0.0 : GapSize + thickness;
        double amplitude = WaveAmplitude * AmplitudeFactor;

        if (progress > 0.0001)
        {
            double activeEnd = full ? _perimeter : Math.Max(0.0, activeLength - gap / 2);
            if (activeEnd > 0.5)
                dc.DrawGeometry(null, playedPen, BuildPolyline(0.0, activeEnd, amplitude, wavy: true));
            else
                DrawDot(dc, 0.0, thickness);
        }

        if (!full)
        {
            double trackStart = progress > 0.0001 ? activeLength + gap / 2 : 0.0;
            double trackEnd = progress > 0.0001 ? _perimeter - gap / 2 : _perimeter;
            if (trackEnd - trackStart > 0.5)
                dc.DrawGeometry(null, trackPen, BuildPolyline(trackStart, trackEnd, 0.0, wavy: false));
        }
    }

    private void DrawDot(DrawingContext dc, double s, double thickness)
    {
        _outline!.GetPointAtFractionLength(s / _perimeter, out Point p, out _);
        dc.DrawEllipse(PlayedBrush, null, p, thickness / 2, thickness / 2);
    }

    // Точки вдоль контура; у волны каждая точка смещается по нормали на amplitude * sin, привязанному к длине дуги.
    private StreamGeometry BuildPolyline(double from, double to, double amplitude, bool wavy)
    {
        double k = 2 * Math.PI / _wavelength;
        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            bool first = true;
            for (double s = from; ; s += SampleStep)
            {
                bool last = s >= to;
                double at = last ? to : s;
                _outline!.GetPointAtFractionLength(at / _perimeter, out Point p, out Point tangent);
                if (wavy && amplitude > 0.01)
                {
                    double offset = amplitude * Math.Sin(k * (at - Phase));
                    p = new Point(p.X - tangent.Y * offset, p.Y + tangent.X * offset);
                }

                if (first)
                {
                    ctx.BeginFigure(p, false, false);
                    first = false;
                }
                else
                {
                    ctx.LineTo(p, true, true);
                }

                if (last) break;
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private static Pen MakePen(Brush brush, double thickness) => new(brush, thickness)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round
    };
}
