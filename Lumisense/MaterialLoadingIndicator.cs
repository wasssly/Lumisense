using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace Lumisense;

// Индикатор загрузки Material 3 Expressive (Loading indicator, m3.material.io/components/loading-indicator): одна фигура плавно
// перетекает по кругу из семи фигур (MaterialShapes в Compose: SoftBurst, Cookie9Sided, Pentagon, Pill, Sunny, Cookie4Sided, Oval)
// и непрерывно вращается. Для ожиданий без известной длительности (до ~5 с). Активная фигура занимает 38 из 48 dp, как в спецификации;
// ContainerBrush рисует круглую подложку (вариант Contained). Кадры подписываются, только пока элемент виден.
public sealed class MaterialLoadingIndicator : FrameworkElement
{
    // Одна фигура: HoldSeconds покоя и MorphSeconds перетекания (в Material пружина, здесь сглаженная кривая); цикл 650 мс.
    // Вращение: медленное постоянное (оборот за 4,666 с) и четверть оборота на каждое перетекание.
    private const double HoldSeconds = 0.15;
    private const double MorphSeconds = 0.5;
    private const double BaseRotationSeconds = 4.666;
    private const double IndicatorRatio = 38.0 / 48.0;
    private const int SamplePoints = 120;

    // Радиус фигуры в зависимости от угла; значения относительные, размер потом нормируется по максимуму.
    private static readonly Func<double, double>[] Shapes =
    [
        theta => Star(theta, 10, 0.78, 0.8),       // SoftBurst: десять округлых лепестков
        theta => Star(theta, 9, 0.80, 1.0),        // Cookie9Sided: девять плавных «зубцов»
        theta => RoundedPolygon(theta, 5, 0.30),   // Pentagon: скруглённый пятиугольник
        theta => Stadium(theta, 1.0, 0.58),        // Pill: пилюля с прямыми сторонами
        theta => Star(theta, 8, 0.80, 1.2),        // Sunny: восьмиконечная звезда с округлыми кончиками
        theta => Star(theta, 4, 0.72, 1.0),        // Cookie4Sided: «подушка» с четырьмя углами
        theta => Ellipse(theta, 1.0, 0.72)         // Oval: овал
    ];

    public static readonly DependencyProperty ForegroundBrushProperty = DependencyProperty.Register(
        nameof(ForegroundBrush), typeof(Brush), typeof(MaterialLoadingIndicator),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    // Цвет фигуры (в XAML — акцент приложения).
    public Brush ForegroundBrush
    {
        get => (Brush)GetValue(ForegroundBrushProperty);
        set => SetValue(ForegroundBrushProperty, value);
    }

    public static readonly DependencyProperty ContainerBrushProperty = DependencyProperty.Register(
        nameof(ContainerBrush), typeof(Brush), typeof(MaterialLoadingIndicator),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    // Подложка варианта Contained; null — индикатор без подложки.
    public Brush? ContainerBrush
    {
        get => (Brush?)GetValue(ContainerBrushProperty);
        set => SetValue(ContainerBrushProperty, value);
    }

    public static readonly DependencyProperty IsAnimationEnabledProperty = DependencyProperty.Register(
        nameof(IsAnimationEnabled), typeof(bool), typeof(MaterialLoadingIndicator),
        new FrameworkPropertyMetadata(true, OnAnimationInputChanged));

    // false (например, «уменьшить движение» в системе) — статичная фигура без вращения и перетекания.
    public bool IsAnimationEnabled
    {
        get => (bool)GetValue(IsAnimationEnabledProperty);
        set => SetValue(IsAnimationEnabledProperty, value);
    }

    private readonly Stopwatch _clock = new();
    private bool _rendering;

    public MaterialLoadingIndicator()
    {
        IsHitTestVisible = false;

        // «Показывать анимацию в Windows» выключено — индикатор статичный; окна могут переопределить это настройками плеера.
        if (!SystemParameters.ClientAreaAnimation) IsAnimationEnabled = false;
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => StopRendering();
        IsVisibleChanged += (_, _) => UpdateAnimation();
    }

    private static void OnAnimationInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MaterialLoadingIndicator)d).UpdateAnimation();

    private void UpdateAnimation()
    {
        if (IsLoaded && IsVisible && IsAnimationEnabled)
        {
            if (_rendering) return;

            _clock.Start();
            CompositionTarget.Rendering += OnRendering;
            _rendering = true;
        }
        else
        {
            StopRendering();
            InvalidateVisual();
        }
    }

    private void StopRendering()
    {
        if (!_rendering) return;

        CompositionTarget.Rendering -= OnRendering;
        _clock.Stop();
        _rendering = false;
    }

    private void OnRendering(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double outer = size / 2;
        if (ContainerBrush is { } container)
            dc.DrawEllipse(container, null, center, outer, outer);

        double elapsed = IsAnimationEnabled ? _clock.Elapsed.TotalSeconds : 0.0;
        (int shape, double morph) = ShapeAtTime(elapsed);
        int step = (int)(Math.Max(elapsed, 0.0) / (HoldSeconds + MorphSeconds));
        double rotation = elapsed * 2 * Math.PI / BaseRotationSeconds + (step + morph) * Math.PI / 2;

        double[] radii = ComputeRadii(shape, morph, SamplePoints);
        double maxRadius = radii.Max();
        double scale = outer * IndicatorRatio / maxRadius;

        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            for (int i = 0; i < radii.Length; i++)
            {
                double angle = i * 2 * Math.PI / radii.Length + rotation;
                var point = new Point(
                    center.X + Math.Cos(angle) * radii[i] * scale,
                    center.Y + Math.Sin(angle) * radii[i] * scale);
                if (i == 0) ctx.BeginFigure(point, true, true);
                else ctx.LineTo(point, true, true);
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(ForegroundBrush, null, geometry);
    }

    // Какая фигура сейчас и насколько она перетекла в следующую (0..1, со сглаживанием).
    internal static (int Shape, double Morph) ShapeAtTime(double seconds)
    {
        double cycle = HoldSeconds + MorphSeconds;
        double safe = Math.Max(seconds, 0.0);
        int step = (int)(safe / cycle);
        double within = safe - step * cycle;
        double morph = within <= HoldSeconds ? 0.0 : SmoothStep((within - HoldSeconds) / MorphSeconds);
        return (step % Shapes.Length, morph);
    }

    // Радиусы по кругу: смесь текущей и следующей фигуры (morph 0 — только текущая, 1 — только следующая).
    internal static double[] ComputeRadii(int shape, double morph, int points)
    {
        Func<double, double> current = Shapes[shape % Shapes.Length];
        Func<double, double> next = Shapes[(shape + 1) % Shapes.Length];
        double blend = Math.Clamp(morph, 0.0, 1.0);

        var radii = new double[points];
        for (int i = 0; i < points; i++)
        {
            double theta = i * 2 * Math.PI / points;
            radii[i] = current(theta) * (1 - blend) + next(theta) * blend;
        }

        return radii;
    }

    internal static int ShapeCount => Shapes.Length;

    private static double SmoothStep(double x)
    {
        double t = Math.Clamp(x, 0.0, 1.0);
        return t * t * (3 - 2 * t);
    }

    // Звезда с n выступами: радиус между innerRadius (впадины) и 1 (кончики); sharpness < 1 округляет и расширяет кончики,
    // > 1 заостряет их.
    private static double Star(double theta, int points, double innerRadius, double sharpness)
    {
        double bump = (1 + Math.Cos(points * theta)) / 2;
        return innerRadius + (1 - innerRadius) * Math.Pow(bump, sharpness);
    }

    private static double Ellipse(double theta, double a, double b)
    {
        double c = b * Math.Cos(theta);
        double s = a * Math.Sin(theta);
        return a * b / Math.Sqrt(c * c + s * s);
    }

    // Пилюля (капсула): прямые стороны на высоте halfHeight и полукруглые торцы, halfWidth — половина полной ширины.
    private static double Stadium(double theta, double halfWidth, double halfHeight)
    {
        double cos = Math.Abs(Math.Cos(theta));
        double sin = Math.Abs(Math.Sin(theta));
        double straight = halfWidth - halfHeight;

        if (sin > 1e-9)
        {
            double onSide = halfHeight / sin;
            if (onSide * cos <= straight) return onSide;
        }

        // Луч выходит через полукруглый торец с центром (straight, 0).
        double along = straight * cos;
        return along + Math.Sqrt(along * along - straight * straight + halfHeight * halfHeight);
    }

    // Правильный n-угольник, смешанный с кругом: roundness 0 — острые углы, 1 — круг.
    private static double RoundedPolygon(double theta, int sides, double roundness)
    {
        double sector = 2 * Math.PI / sides;
        double local = (theta % sector) - sector / 2;
        double polygon = Math.Cos(Math.PI / sides) / Math.Cos(local);
        return polygon * (1 - roundness) + roundness;
    }
}
