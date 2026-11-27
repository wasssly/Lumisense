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

    private static readonly double[] CosTable = Enumerable.Range(0, SamplePoints).Select(i => Math.Cos(i * 2 * Math.PI / SamplePoints)).ToArray();
    private static readonly double[] SinTable = Enumerable.Range(0, SamplePoints).Select(i => Math.Sin(i * 2 * Math.PI / SamplePoints)).ToArray();

    // Шаги, кратные 240; выбирается наибольший, при котором точек не меньше 2,5 на пиксель диаметра (но не меньше 60).
    private static readonly int[] Strides = { 6, 5, 4, 3, 2, 1 };

    private static int StrideFor(double size)
    {
        double wanted = Math.Clamp(size * 2.5, 60, SamplePoints);
        foreach (int stride in Strides)
        {
            if (SamplePoints / stride >= wanted) return stride;
        }

        return 1;
    }

    private readonly Stopwatch _clock = new();
    private double[] _xs = Array.Empty<double>();
    private double[] _ys = Array.Empty<double>();
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

        double[] current = Profiles[shape % Profiles.Length];
        double[] next = Profiles[(shape + 1) % Profiles.Length];
        double blend = Math.Clamp(morph, 0.0, 1.0);
        double scale = outer * IndicatorRatio / MaxRadius;

        // Маленькому индикатору хватает части из 240 отсчётов: шаг подбирается так, чтобы сторона многоугольника была около пикселя.
        int stride = StrideFor(size);
        int count = SamplePoints / stride;
        if (_xs.Length != count)
        {
            _xs = new double[count];
            _ys = new double[count];
        }

        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        for (int k = 0; k < count; k++)
        {
            int i = k * stride;
            double radius = current[i] * (1 - blend) + next[i] * blend;
            double x = CosTable[i] * radius;
            double y = SinTable[i] * radius;
            _xs[k] = x;
            _ys[k] = y;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        // Как в Compose, габарит фигуры ставится в центр контейнера и вращение идёт вокруг него.
        double shiftX = (minX + maxX) / 2;
        double shiftY = (minY + maxY) / 2;

        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            for (int k = 0; k < count; k++)
            {
                double x = (_xs[k] - shiftX) * scale;
                double y = (_ys[k] - shiftY) * scale;
                var point = new Point(
                    center.X + x * cosRotation - y * sinRotation,
                    center.Y + x * sinRotation + y * cosRotation);
                if (k == 0) ctx.BeginFigure(point, true, true);
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
