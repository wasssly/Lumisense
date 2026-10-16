using System.Windows;
using System.Windows.Media;

namespace Lumisense;

// Полоса воспроизведения в стиле Material 3 Expressive: волнистая проигранная часть, зазор, дорожка и точка в конце.
// Только рисует; клики и перетаскивание обрабатывает Border поверх (как у WaveformView).
// Размер и форма настраиваются множителями (ProgressBarAppearance); значения по умолчанию воспроизводят прежний вид.
public sealed class MaterialProgressView : MaterialWaveElement
{
    // Толщина, зазор и точка в конце — 4 dp по документации Material (ProgressIndicator).
    private const double BaseThickness = 4.0;
    private const double GapSize = 4.0;
    private const double StopDotSize = 4.0;

    // Длина волны, амплитуда и скорость подобраны на глаз под ширину полосы плеера (скорость 32 px/с).
    private const double BaseWaveAmplitude = 3.0;
    private const double BaseWavelength = 40.0;
    private const double BaseWaveSpeed = 32.0;

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

    public static readonly DependencyProperty ThicknessScaleProperty = DependencyProperty.Register(
        nameof(ThicknessScale), typeof(double), typeof(MaterialProgressView),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    // Множитель толщины линии (ProgressBarAppearance.TrackThickness).
    public double ThicknessScale
    {
        get => (double)GetValue(ThicknessScaleProperty);
        set => SetValue(ThicknessScaleProperty, value);
    }

    public static readonly DependencyProperty IsFlatProperty = DependencyProperty.Register(
        nameof(IsFlat), typeof(bool), typeof(MaterialProgressView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    // true — прямые концы линии вместо скруглённых (ProgressBarAppearance.EndShape).
    public bool IsFlat
    {
        get => (bool)GetValue(IsFlatProperty);
        set => SetValue(IsFlatProperty, value);
    }

    public static readonly DependencyProperty AmplitudeScaleProperty = DependencyProperty.Register(
        nameof(AmplitudeScale), typeof(double), typeof(MaterialProgressView),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double AmplitudeScale
    {
        get => (double)GetValue(AmplitudeScaleProperty);
        set => SetValue(AmplitudeScaleProperty, value);
    }

    public static readonly DependencyProperty WavelengthScaleProperty = DependencyProperty.Register(
        nameof(WavelengthScale), typeof(double), typeof(MaterialProgressView),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double WavelengthScale
    {
        get => (double)GetValue(WavelengthScaleProperty);
        set => SetValue(WavelengthScaleProperty, value);
    }

    public static readonly DependencyProperty SpeedScaleProperty = DependencyProperty.Register(
        nameof(SpeedScale), typeof(double), typeof(MaterialProgressView),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double SpeedScale
    {
        get => (double)GetValue(SpeedScaleProperty);
        set => SetValue(SpeedScaleProperty, value);
    }

    private Pen? _playedPen;
    private Pen? _trackPen;

    private double Thickness => BaseThickness * Finite(ThicknessScale, 1.0);

    private double CurrentWavelength => BaseWavelength * Math.Max(0.1, Finite(WavelengthScale, 1.0));

    protected override double Wavelength => CurrentWavelength;

    protected override double WaveSpeed => BaseWaveSpeed * Math.Max(0.0, Finite(SpeedScale, 1.0));

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double thickness = Thickness;
        double half = thickness / 2;

        // Прямые концы не выступают за край, поэтому отступ у краёв и от зазора нужен только скруглённым.
        double inset = IsFlat ? 0.0 : half;
        double width = ActualWidth;
        double height = ActualHeight;
        double left = inset;
        double right = width - inset;
        if (width <= 0 || height <= 0 || right <= left) return;

        double cy = height / 2;
        double progress = double.IsFinite(Progress) ? Math.Clamp(Progress, 0.0, 1.0) : 0.0;
        double progressX = left + (right - left) * progress;

        // Штрих с круглыми концами выступает на половину толщины, поэтому зазор считаем до центров концов.
        double gapToEnd = GapSize / 2 + inset;
        double activeEnd = progress >= 1.0 ? right : progressX - gapToEnd;
        double trackStart = progressX + gapToEnd;

        Pen playedPen = EnsurePen(ref _playedPen, PlayedBrush, thickness, IsFlat);
        Pen trackPen = EnsurePen(ref _trackPen, TrackBrush, thickness, IsFlat);

        if (progress > 0)
        {
            double amplitude = BaseWaveAmplitude * Finite(AmplitudeScale, 1.0) * AmplitudeFactor;
            if (activeEnd - left < 0.5)
            {
                if (IsFlat)
                    dc.DrawRectangle(PlayedBrush, null, new Rect(left, cy - half, Math.Max(activeEnd - left, 0.5), thickness));
                else
                    dc.DrawEllipse(PlayedBrush, null, new Point(left, cy), half, half);
            }
            else if (amplitude < 0.05)
                dc.DrawLine(playedPen, new Point(left, cy), new Point(activeEnd, cy));
            else
                dc.DrawGeometry(null, playedPen, BuildWave(left, activeEnd, cy, amplitude, Phase, CurrentWavelength));
        }

        if (trackStart <= right)
        {
            dc.DrawLine(trackPen, new Point(trackStart, cy), new Point(right, cy));
            dc.DrawEllipse(PlayedBrush, null, new Point(right, cy), StopDotSize / 2, StopDotSize / 2);
        }
    }

    // Волна привязана к абсолютному x, а сдвигается только phase: при росте прогресса рисунок не дёргается.
    private static StreamGeometry BuildWave(double x0, double x1, double cy, double amplitude, double phase, double wavelength)
    {
        double k = 2 * Math.PI / wavelength;
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

    private static Pen EnsurePen(ref Pen? pen, Brush brush, double thickness, bool flat)
    {
        PenLineCap cap = flat ? PenLineCap.Flat : PenLineCap.Round;
        if (pen is null || !ReferenceEquals(pen.Brush, brush) || pen.Thickness != thickness || pen.StartLineCap != cap)
        {
            pen = new Pen(brush, thickness)
            {
                StartLineCap = cap,
                EndLineCap = cap,
                LineJoin = PenLineJoin.Round
            };
        }

        return pen;
    }

    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;
}
