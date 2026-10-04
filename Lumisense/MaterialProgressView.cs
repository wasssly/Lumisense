using System.Windows;
using System.Windows.Media;

namespace Lumisense;

// Полоса воспроизведения в стиле Material 3 Expressive: волнистая проигранная часть, зазор, дорожка и точка в конце.
// Только рисует; клики и перетаскивание обрабатывает Border поверх (как у WaveformView).
public sealed class MaterialProgressView : MaterialWaveElement
{
    // Толщина, зазор и точка в конце — 4 dp по документации Material (ProgressIndicator).
    private const double Thickness = 4.0;
    private const double GapSize = 4.0;
    private const double StopDotSize = 4.0;

    // Длина волны, амплитуда и скорость подобраны на глаз под ширину полосы плеера (скорость задаёт WaveFrequency в базовом классе).
    private const double WaveAmplitude = 3.0;
    private const double WavelengthValue = 40.0;

    // За это время амплитуда плавно доходит до нуля (пауза) или до максимума (воспроизведение).

    // Шаг точек ломаной, которой рисуется синусоида.
    private const double WaveStep = 2.0;

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(MaterialProgressView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    // 0..1 — доля проигранного трека (то же отношение, что у WaveformView.Progress).
    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public static readonly DependencyProperty PlayedBrushProperty = DependencyProperty.Register(
        nameof(PlayedBrush), typeof(Brush), typeof(MaterialProgressView),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    // Цвет волны и точки; задаётся из кода (MainWindow.RefreshAccentDependentIcons) из-за ручного акцента.
    public Brush PlayedBrush
    {
        get => (Brush)GetValue(PlayedBrushProperty);
        set => SetValue(PlayedBrushProperty, value);
    }

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(MaterialProgressView),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    // Цвет ещё не проигранной дорожки (ресурс темы, задаётся в XAML).
    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    private Pen? _playedPen;
    private Pen? _trackPen;

    protected override double Wavelength => WavelengthValue;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double width = ActualWidth;
        double height = ActualHeight;
        double half = Thickness / 2;
        double left = half;
        double right = width - half;
        if (width <= 0 || height <= 0 || right <= left) return;

        double cy = height / 2;
        double progress = double.IsFinite(Progress) ? Math.Clamp(Progress, 0.0, 1.0) : 0.0;
        double progressX = left + (right - left) * progress;

        // Штрих с круглыми концами выступает на половину толщины, поэтому зазор считаем до центров концов.
        double gapToEnd = GapSize / 2 + half;
        double activeEnd = progress >= 1.0 ? right : progressX - gapToEnd;
        double trackStart = progressX + gapToEnd;

        Pen playedPen = EnsurePen(ref _playedPen, PlayedBrush);
        Pen trackPen = EnsurePen(ref _trackPen, TrackBrush);

        if (progress > 0)
        {
            double amplitude = WaveAmplitude * AmplitudeFactor;
            if (activeEnd - left < 0.5)
                dc.DrawEllipse(PlayedBrush, null, new Point(left, cy), half, half);
            else if (amplitude < 0.05)
                dc.DrawLine(playedPen, new Point(left, cy), new Point(activeEnd, cy));
            else
                dc.DrawGeometry(null, playedPen, BuildWave(left, activeEnd, cy, amplitude, Phase));
        }

        if (trackStart <= right)
        {
            dc.DrawLine(trackPen, new Point(trackStart, cy), new Point(right, cy));
            dc.DrawEllipse(PlayedBrush, null, new Point(right, cy), StopDotSize / 2, StopDotSize / 2);
        }
    }

    // Волна привязана к абсолютному x, а сдвигается только phase: при росте прогресса рисунок не дёргается.
    private static StreamGeometry BuildWave(double x0, double x1, double cy, double amplitude, double phase)
    {
        double k = 2 * Math.PI / WavelengthValue;
        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(x0, cy + amplitude * Math.Sin(k * (x0 - phase))), false, false);
            for (double x = x0 + WaveStep; x < x1; x += WaveStep)
                ctx.LineTo(new Point(x, cy + amplitude * Math.Sin(k * (x - phase))), true, true);
            ctx.LineTo(new Point(x1, cy + amplitude * Math.Sin(k * (x1 - phase))), true, true);
        }

        geometry.Freeze();
        return geometry;
    }

    private static Pen EnsurePen(ref Pen? pen, Brush brush)
    {
        if (pen is null || !ReferenceEquals(pen.Brush, brush))
        {
            pen = new Pen(brush, Thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
        }

        return pen;
    }
}
