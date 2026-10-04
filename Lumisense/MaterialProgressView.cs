using System.Windows;
using System.Windows.Media;

namespace Lumisense;

// Полоса воспроизведения в стиле Material 3 Expressive: волнистая проигранная часть, зазор, дорожка и точка в конце.
// Только рисует; клики и перетаскивание обрабатывает Border поверх (как у WaveformView).
public sealed class MaterialProgressView : FrameworkElement
{
    // Толщина, зазор и точка в конце — 4 dp по документации Material (ProgressIndicator).
    private const double Thickness = 4.0;
    private const double GapSize = 4.0;
    private const double StopDotSize = 4.0;

    // Длина волны, амплитуда и скорость подобраны на глаз под ширину полосы плеера.
    private const double Wavelength = 40.0;
    private const double WaveAmplitude = 3.0;
    private const double WaveSpeed = 24.0;

    // За это время амплитуда плавно доходит до нуля (пауза) или до максимума (воспроизведение).
    private const double AmplitudeEaseSeconds = 0.4;

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

    public static readonly DependencyProperty IsWavingProperty = DependencyProperty.Register(
        nameof(IsWaving), typeof(bool), typeof(MaterialProgressView),
        new FrameworkPropertyMetadata(false, OnAnimationInputChanged));

    // true — трек играет: волна есть и бежит; false — пауза или остановка: волна плавно выпрямляется.
    public bool IsWaving
    {
        get => (bool)GetValue(IsWavingProperty);
        set => SetValue(IsWavingProperty, value);
    }

    public static readonly DependencyProperty IsAnimationEnabledProperty = DependencyProperty.Register(
        nameof(IsAnimationEnabled), typeof(bool), typeof(MaterialProgressView),
        new FrameworkPropertyMetadata(true, OnAnimationInputChanged));

    // false при «Меньше анимации»: волна не движется, форма меняется сразу.
    public bool IsAnimationEnabled
    {
        get => (bool)GetValue(IsAnimationEnabledProperty);
        set => SetValue(IsAnimationEnabledProperty, value);
    }

    private double _amplitudeFactor;
    private double _phase;
    private TimeSpan _lastFrame;
    private bool _isRendering;
    private Pen? _playedPen;
    private Pen? _trackPen;

    public MaterialProgressView()
    {
        Loaded += (_, _) => UpdateAnimationState();
        Unloaded += (_, _) => StopRendering();
        IsVisibleChanged += (_, _) => UpdateAnimationState();
    }

    private static void OnAnimationInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MaterialProgressView)d).UpdateAnimationState();

    // Кадры нужны только пока окно видно и волна бежит или выпрямляется: на паузе элемент ничего не тратит.
    private void UpdateAnimationState()
    {
        double target = IsWaving ? 1.0 : 0.0;

        if (IsAnimationEnabled && IsLoaded && IsVisible && (IsWaving || _amplitudeFactor > 0.001))
        {
            StartRendering();
            return;
        }

        _amplitudeFactor = target;
        StopRendering();
        InvalidateVisual();
    }

    private void StartRendering()
    {
        if (_isRendering) return;
        _isRendering = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopRendering()
    {
        if (!_isRendering) return;
        CompositionTarget.Rendering -= OnRendering;
        _isRendering = false;
        _lastFrame = TimeSpan.Zero;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        TimeSpan now = ((RenderingEventArgs)e).RenderingTime;

        // WPF может вызвать событие несколько раз за один кадр.
        if (now == _lastFrame) return;

        double dt = _lastFrame == TimeSpan.Zero ? 0.0 : Math.Min((now - _lastFrame).TotalSeconds, 0.1);
        _lastFrame = now;

        double target = IsWaving ? 1.0 : 0.0;
        double step = dt / AmplitudeEaseSeconds;
        _amplitudeFactor = _amplitudeFactor < target
            ? Math.Min(target, _amplitudeFactor + step)
            : Math.Max(target, _amplitudeFactor - step);

        _phase = (_phase + WaveSpeed * dt) % Wavelength;
        InvalidateVisual();

        if (!IsWaving && _amplitudeFactor <= 0.001)
            UpdateAnimationState();
    }

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
            double amplitude = WaveAmplitude * _amplitudeFactor;
            if (activeEnd - left < 0.5)
                dc.DrawEllipse(PlayedBrush, null, new Point(left, cy), half, half);
            else if (amplitude < 0.05)
                dc.DrawLine(playedPen, new Point(left, cy), new Point(activeEnd, cy));
            else
                dc.DrawGeometry(null, playedPen, BuildWave(left, activeEnd, cy, amplitude, _phase));
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
        double k = 2 * Math.PI / Wavelength;
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
