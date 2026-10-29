using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace Lumisense;

// Индикатор загрузки Material 3 Expressive (Loading indicator, m3.material.io/components/loading-indicator): одна фигура плавно
// перетекает по кругу из семи фигур MaterialShapes (SoftBurst, Cookie9Sided, Pentagon, Pill, Sunny, Cookie4Sided, Oval)
// и непрерывно вращается. Для ожиданий без известной длительности (до ~5 с). Все фигуры вписаны в круг 38 из 48 dp, как в спецификации;
// ContainerBrush рисует круглую подложку (вариант Contained). Кадры подписываются, только пока элемент виден.
public sealed class MaterialLoadingIndicator : FrameworkElement
{
    // Как в Compose: перетекание в следующую фигуру запускается каждые 650 мс пружиной (жёсткость 200, затухание 0,6) и
    // заканчивается на первом пике; дальше фигура держится до следующего цикла. Вращение: оборот за 4,666 с и четверть оборота на фигуру.
    private const double CycleSeconds = 0.65;
    private const double SpringStiffness = 200;
    private const double SpringDamping = 0.6;
    private const double BaseRotationSeconds = 4.666;
    private const double IndicatorRatio = 38.0 / 48.0;
    internal const int SamplePoints = 240;

    // Радиусы фигур по кругу углов; нормированы так, что большая сторона габарита равна 1.
    private static readonly double[][] Profiles = MaterialShapeGeometry.IndeterminateProfiles(SamplePoints);

    // Общий масштаб для всех фигур: самая дальняя точка любой фигуры от центра не выходит за круг индикатора при вращении.
    private static readonly double MaxRadius = Profiles.Max(profile => profile.Max());

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
        int step = (int)(Math.Max(elapsed, 0.0) / CycleSeconds);
        double rotation = elapsed * 2 * Math.PI / BaseRotationSeconds + (step + 1 + morph) * Math.PI / 2;
        double cosRotation = Math.Cos(rotation);
        double sinRotation = Math.Sin(rotation);

        double[] radii = ComputeRadii(shape, morph);
        double scale = outer * IndicatorRatio / MaxRadius;

        var points = new Point[radii.Length];
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        for (int i = 0; i < radii.Length; i++)
        {
            double angle = i * 2 * Math.PI / radii.Length;
            points[i] = new Point(Math.Cos(angle) * radii[i], Math.Sin(angle) * radii[i]);
            minX = Math.Min(minX, points[i].X);
            maxX = Math.Max(maxX, points[i].X);
            minY = Math.Min(minY, points[i].Y);
            maxY = Math.Max(maxY, points[i].Y);
        }

        // Как в Compose, габарит фигуры ставится в центр контейнера и вращение идёт вокруг него.
        double shiftX = (minX + maxX) / 2;
        double shiftY = (minY + maxY) / 2;

        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            for (int i = 0; i < points.Length; i++)
            {
                double x = (points[i].X - shiftX) * scale;
                double y = (points[i].Y - shiftY) * scale;
                var point = new Point(
                    center.X + x * cosRotation - y * sinRotation,
                    center.Y + x * sinRotation + y * cosRotation);
                if (i == 0) ctx.BeginFigure(point, true, true);
                else ctx.LineTo(point, true, true);
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(ForegroundBrush, null, geometry);
    }

    // Какая фигура сейчас и насколько она перетекла в следующую (0..1).
    internal static (int Shape, double Morph) ShapeAtTime(double seconds)
    {
        double safe = Math.Max(seconds, 0.0);
        int step = (int)(safe / CycleSeconds);
        return (step % Profiles.Length, SpringProgress(safe - step * CycleSeconds));
    }

    // Ход пружины от 0 до 1 за цикл; перетекание не выходит за 1 и после первого пика пружины остаётся на 1.
    internal static double SpringProgress(double seconds)
    {
        double omega = Math.Sqrt(SpringStiffness);
        double dampedOmega = omega * Math.Sqrt(1 - SpringDamping * SpringDamping);
        if (seconds >= Math.PI / dampedOmega) return 1.0;

        double t = Math.Max(seconds, 0.0);
        double decay = Math.Exp(-SpringDamping * omega * t);
        double value = 1 - decay * (Math.Cos(dampedOmega * t) + SpringDamping * omega / dampedOmega * Math.Sin(dampedOmega * t));
        return Math.Clamp(value, 0.0, 1.0);
    }

    // Радиусы по кругу: смесь текущей и следующей фигуры (morph 0 — только текущая, 1 — только следующая).
    internal static double[] ComputeRadii(int shape, double morph)
    {
        double[] current = Profiles[shape % Profiles.Length];
        double[] next = Profiles[(shape + 1) % Profiles.Length];
        double blend = Math.Clamp(morph, 0.0, 1.0);

        var radii = new double[SamplePoints];
        for (int i = 0; i < radii.Length; i++)
            radii[i] = current[i] * (1 - blend) + next[i] * blend;

        return radii;
    }

    internal static int ShapeCount => Profiles.Length;

    internal static double MaxShapeRadius => MaxRadius;
}
