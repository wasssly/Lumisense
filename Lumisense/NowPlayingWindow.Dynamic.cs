using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Lumisense;

// Покадровые фоны «Orbs» и «Waves»: считаются в CompositionTarget.Rendering только при воспроизведении и без «Меньше анимации».
// Шары — овалы с радиальным градиентом вместо BlurEffect (он считается на CPU и на весь экран слишком тяжёл).
public partial class NowPlayingWindow
{
    private const int WaveCount = 3;
    private const int WavesBitmapWidth = 256;
    private const int WavesBlurRadius = 7;
    private const double WaveStep = 2;
    private static readonly Duration PaletteFadeDuration = new(TimeSpan.FromMilliseconds(700));

    // Параметры шаров: размер-добавка (px), период обхода орбиты (с), амплитуда орбиты (% ширины/высоты), центр (% окна),
    // сдвиг по времени (с) и обратное направление. Набор и значения воспроизводят расстановку из исходного CSS-образца.
    private static readonly OrbSpec[] OrbSpecs =
    {
        new(12, 20, 12, 14, 18, 22, -3, false),
        new(18, 26, 14, 10, 78, 68, -9, true),
        new(10, 18, 10, 13, 50, 14, -6, false),
        new(16, 30, 15, 12, 28, 82, -12, true),
        new(14, 22, 11, 15, 70, 32, -4, false),
        new(11, 19, 12, 10, 12, 60, -7, true),
        new(17, 24, 11, 14, 88, 18, -11, false),
        new(13, 28, 14, 12, 42, 46, -2, true),
        new(19, 32, 13, 11, 64, 88, -14, false),
        new(15, 25, 10, 16, 32, 8, -5, true),
        new(14, 21, 14, 10, 84, 54, -10, false),
        new(16, 27, 12, 13, 8, 36, -3.5, true),
        new(10, 23, 13, 12, 56, 74, -15, false),
        new(18, 29, 14, 11, 22, 50, -8, true),
        new(20, 34, 12, 14, 74, 8, -18, false),
        new(12, 19.5, 11, 13, 46, 92, -6, true),
        new(11, 17, 10, 15, 92, 42, -1, false),
        new(17, 26.5, 14, 12, 36, 26, -13, true),
        new(13, 22.5, 12, 14, 60, 60, -7, false),
        new(19, 31, 13, 11, 4, 78, -16, true),
    };

    // Углы орбиты по кругу: (-,-) → (+,-) → (+,+) → (-,+) → (-,-).
    private static readonly (double X, double Y)[] OrbCorners = { (-1, -1), (1, -1), (1, 1), (-1, 1), (-1, -1) };

    private sealed record OrbSpec(double Extra, double Period, double AmplitudeX, double AmplitudeY, double CenterX,
        double CenterY, double Delay, bool Reverse);

    private sealed class Orb
    {
        public required OrbSpec Spec { get; init; }
        public required Ellipse Shape { get; init; }
        public required ScaleTransform Scale { get; init; }
        public required TranslateTransform Translate { get; init; }
        public required GradientStop Center { get; init; }
        public required GradientStop Middle { get; init; }
        public required GradientStop Edge { get; init; }
    }

    private Color[] _dynamicPalette = CreateFallbackPalette();
    private readonly List<Orb> _orbs = new();
    private readonly SolidColorBrush[] _waveBrushes = new SolidColorBrush[WaveCount];
    private readonly DrawingVisual _wavesVisual = new();
    private RenderTargetBitmap? _wavesRender;
    private WriteableBitmap? _wavesOutput;
    private byte[] _wavesPixels = Array.Empty<byte>();
    private byte[] _wavesScratch = Array.Empty<byte>();
    private int _wavesBitmapHeight;
    private bool _dynamicRendering;
    private TimeSpan _dynamicLastTime = TimeSpan.MinValue;
    private double _dynamicTime;
    private double _dynamicLevel;

    // Новая палитра обложки: цвета шаров и волн плавно перетекают, а не меняются рывком.
    private void ApplyDynamicPalette(Color[] palette, bool animate)
    {
        if (palette.Length < 5) return;
        _dynamicPalette = palette;
        animate = animate && (IsOrbsBackground || IsWavesBackground)
            && !AccessibilityPreferences.ShouldReduceMotion(_owner.Settings);

        EnsureWaveBrushes();
        Color[] waveColors = { palette[1], palette[3], palette[4] };
        for (int index = 0; index < WaveCount; index++)
            AnimateColor(_waveBrushes[index], SolidColorBrush.ColorProperty, _waveBrushes[index].Color, waveColors[index], animate);

        for (int index = 0; index < _orbs.Count; index++)
        {
            // Цвета шаров циклически берутся из палитры; каждый второй чуть светлее, чтобы фон не был плоским.
            Color baseColor = palette[index % palette.Length];
            Color color = index % 2 == 0 ? baseColor : Lighten(baseColor, 0.18);
            Orb orb = _orbs[index];
            AnimateColor(orb.Center, GradientStop.ColorProperty, orb.Center.Color, WithAlpha(color, 0xE6), animate);
            AnimateColor(orb.Middle, GradientStop.ColorProperty, orb.Middle.Color, WithAlpha(color, 0x8C), animate);
            // Край шара — тот же цвет с нулевой непрозрачностью: градиент к «прозрачному чёрному» дал бы тёмный ободок.
            AnimateColor(orb.Edge, GradientStop.ColorProperty, orb.Edge.Color, WithAlpha(color, 0x00), animate);
        }

        if (!_dynamicRendering) RenderDynamicFrame();
    }

    private static void AnimateColor(Animatable target, DependencyProperty property, Color previous, Color to, bool animate)
    {
        if (animate)
        {
            target.SetValue(property, to);
            target.BeginAnimation(property, new ColorAnimation(previous, to, PaletteFadeDuration));
            return;
        }

        target.BeginAnimation(property, null);
        target.SetValue(property, to);
    }

    private static Color Lighten(Color color, double amount) => Color.FromRgb(
        (byte)(color.R + (255 - color.R) * amount), (byte)(color.G + (255 - color.G) * amount), (byte)(color.B + (255 - color.B) * amount));

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private void EnsureWaveBrushes()
    {
        if (_waveBrushes[0] is not null) return;
        for (int index = 0; index < WaveCount; index++)
            _waveBrushes[index] = new SolidColorBrush(Colors.Transparent);
    }

    private void EnsureOrbs()
    {
        if (_orbs.Count > 0) return;

        foreach (OrbSpec spec in OrbSpecs)
        {
            var center = new GradientStop(Colors.Transparent, 0);
            var middle = new GradientStop(Colors.Transparent, 0.55);
            var edge = new GradientStop(Colors.Transparent, 1);
            var brush = new RadialGradientBrush();
            brush.GradientStops.Add(center);
            brush.GradientStops.Add(middle);
            brush.GradientStops.Add(edge);

            var scale = new ScaleTransform(1, 1);
            var translate = new TranslateTransform();
            var transform = new TransformGroup();
            transform.Children.Add(scale);
            transform.Children.Add(translate);

            var shape = new Ellipse
            {
                Fill = brush,
                IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = transform,
            };
            OrbsBackdrop.Children.Add(shape);
            _orbs.Add(new Orb { Spec = spec, Shape = shape, Scale = scale, Translate = translate, Center = center, Middle = middle, Edge = edge });
        }
    }

    // Размеры шаров и битмапа волн подгоняются под окно; вызывается при создании окна и при смене размера.
    private void UpdateBackdropGeometry()
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        // Размер шара из исходного образца — 300 px на экране высотой 1080; для других размеров масштабируем по высоте.
        // Радиус градиента с запасом (×1.45) заменяет размытие на 48 px у исходных шаров.
        double unit = height / 1080.0;
        foreach (Orb orb in _orbs)
        {
            double size = (300 + orb.Spec.Extra) * unit * 1.45;
            orb.Shape.Width = size;
            orb.Shape.Height = size;
            Canvas.SetLeft(orb.Shape, orb.Spec.CenterX / 100 * width - size / 2);
            Canvas.SetTop(orb.Shape, orb.Spec.CenterY / 100 * height - size / 2);
        }

        int bitmapHeight = Math.Max(48, (int)Math.Round(WavesBitmapWidth * height / width));
        if (_wavesRender is null || bitmapHeight != _wavesBitmapHeight)
        {
            _wavesBitmapHeight = bitmapHeight;
            _wavesRender = new RenderTargetBitmap(WavesBitmapWidth, bitmapHeight, 96, 96, PixelFormats.Pbgra32);
            _wavesOutput = new WriteableBitmap(WavesBitmapWidth, bitmapHeight, 96, 96, PixelFormats.Pbgra32, null);
            _wavesPixels = new byte[WavesBitmapWidth * bitmapHeight * 4];
            _wavesScratch = new byte[_wavesPixels.Length];
            WavesBackdrop.Source = _wavesOutput;
        }

        if (!_dynamicRendering) RenderDynamicFrame();
    }

    // Запускает или останавливает покадровый расчёт. Вызывается из UpdateAmbientAnimation при смене состояния воспроизведения.
    private void UpdateDynamicDriver(bool isPlaying)
    {
        bool animated = IsOrbsBackground || IsWavesBackground;
        bool run = animated && isPlaying && IsLoaded && !AccessibilityPreferences.ShouldReduceMotion(_owner.Settings);
        if (run && !_dynamicRendering)
        {
            _dynamicLastTime = TimeSpan.MinValue;
            CompositionTarget.Rendering += DynamicBackdrop_Rendering;
            _dynamicRendering = true;
        }
        else if (!run && _dynamicRendering)
        {
            StopDynamicDriver();
        }

        // На паузе и при «Меньше анимации» остаётся один статичный кадр.
        if (animated && !_dynamicRendering) RenderDynamicFrame();
    }

    private void StopDynamicDriver()
    {
        if (!_dynamicRendering) return;
        CompositionTarget.Rendering -= DynamicBackdrop_Rendering;
        _dynamicRendering = false;
    }

    private void DynamicBackdrop_Rendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs args) return;

        double delta = _dynamicLastTime == TimeSpan.MinValue ? 0 : (args.RenderingTime - _dynamicLastTime).TotalSeconds;
        _dynamicLastTime = args.RenderingTime;
        delta = Math.Clamp(delta, 0, 0.1);

        _dynamicTime += delta;
        double level = Math.Sqrt(Math.Clamp(_owner.AudioLevelMeter?.NormalizedLevel ?? 0d, 0d, 1d));
        // Громкость сглаживаем: сырое значение дёргается от кадра к кадру, и волны мигали бы.
        _dynamicLevel += (level - _dynamicLevel) * Math.Min(1, delta * 7);
        RenderDynamicFrame();
    }

    private void RenderDynamicFrame()
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        if (IsOrbsBackground) RenderOrbs(width, height);
        else if (IsWavesBackground) RenderWaves();
    }

    // Движение шара: обход углов прямоугольника (±амплитуда) за период с плавным разгоном и торможением на каждой стороне;
    // «дыхание» — синусоида прозрачности и масштаба с периодом 0.63 от орбитального.
    private void RenderOrbs(double width, double height)
    {
        foreach (Orb orb in _orbs)
        {
            OrbSpec spec = orb.Spec;
            double phase = Frac((_dynamicTime - spec.Delay) / spec.Period);
            if (spec.Reverse) phase = 1 - phase;

            double segmentPosition = phase * 4;
            int segment = Math.Min(3, (int)segmentPosition);
            double eased = SmoothStep(segmentPosition - segment);

            double cornerX = OrbCorners[segment].X + (OrbCorners[segment + 1].X - OrbCorners[segment].X) * eased;
            double cornerY = OrbCorners[segment].Y + (OrbCorners[segment + 1].Y - OrbCorners[segment].Y) * eased;
            orb.Translate.X = cornerX * spec.AmplitudeX / 100 * width;
            orb.Translate.Y = cornerY * spec.AmplitudeY / 100 * height;

            double breathePeriod = spec.Period * 0.63;
            double breathe = 0.5 - 0.5 * Math.Cos(Math.PI * (_dynamicTime - (spec.Delay * 0.5 - 1)) / breathePeriod);
            orb.Shape.Opacity = 0.6 + 0.35 * breathe;
            double scale = 0.94 + 0.11 * breathe;
            orb.Scale.ScaleX = scale;
            orb.Scale.ScaleY = scale;
        }
    }

    private static double Frac(double value) => value - Math.Floor(value);

    private static double SmoothStep(double t) => t * t * (3 - 2 * t);

    private void RenderWaves()
    {
        if (_wavesRender is null || _wavesOutput is null) return;
        EnsureWaveBrushes();

        double width = WavesBitmapWidth, height = _wavesBitmapHeight;
        using (DrawingContext context = _wavesVisual.RenderOpen())
        {
            for (int index = 0; index < WaveCount; index++)
            {
                double direction = index % 2 == 0 ? 1 : -1;
                double baseY = height * (0.56 + 0.13 * index);
                double amplitude = height * (0.035 + 0.11 * _dynamicLevel) * (1 - 0.16 * index);
                double frequency = Math.PI * 2 / (width * (0.55 + 0.22 * index));
                double phase = _dynamicTime * (0.55 + 0.3 * index) * direction;

                var points = new List<Point>((int)(width / WaveStep) + 3);
                for (double x = 0; x <= width + WaveStep; x += WaveStep)
                {
                    double y = baseY
                        + amplitude * Math.Sin(x * frequency + phase)
                        + amplitude * 0.45 * Math.Sin(x * frequency * 2.1 - phase * 1.3);
                    points.Add(new Point(x, y));
                }

                var geometry = new StreamGeometry();
                using (StreamGeometryContext figure = geometry.Open())
                {
                    figure.BeginFigure(new Point(0, height + 1), isFilled: true, isClosed: true);
                    figure.PolyLineTo(points, isStroked: false, isSmoothJoin: true);
                    figure.LineTo(new Point(width + WaveStep, height + 1), isStroked: false, isSmoothJoin: false);
                }

                context.PushOpacity(0.55 - index * 0.09);
                context.DrawGeometry(_waveBrushes[index], null, geometry);
                context.Pop();
            }
        }

        _wavesRender.Clear();
        _wavesRender.Render(_wavesVisual);

        int stride = WavesBitmapWidth * 4;
        _wavesRender.CopyPixels(_wavesPixels, stride, 0);

        // Двойной box-blur по горизонтали и вертикали поверх предумноженных каналов: даёт гладкое размытие без «ступенек»,
        // которые были заметны при простом растягивании маленького битмапа.
        for (int pass = 0; pass < 2; pass++)
        {
            BoxBlur(_wavesPixels, _wavesScratch, WavesBitmapWidth, _wavesBitmapHeight, WavesBlurRadius, horizontal: true);
            BoxBlur(_wavesScratch, _wavesPixels, WavesBitmapWidth, _wavesBitmapHeight, WavesBlurRadius, horizontal: false);
        }

        _wavesOutput.WritePixels(new Int32Rect(0, 0, WavesBitmapWidth, _wavesBitmapHeight), _wavesPixels, stride, 0);
    }

    // Размытие скользящим окном за O(n): одна сумма на канал, обновляемая добавлением и вычитанием крайних пикселей.
    // Края — «ближайший пиксель», поэтому волны у границ окна не бледнеют.
    private static void BoxBlur(byte[] source, byte[] target, int width, int height, int radius, bool horizontal)
    {
        int lines = horizontal ? height : width;
        int length = horizontal ? width : height;
        int lineStep = horizontal ? width * 4 : 4;
        int pixelStep = horizontal ? 4 : width * 4;
        int window = radius * 2 + 1;

        for (int line = 0; line < lines; line++)
        {
            int lineStart = line * lineStep;
            for (int channel = 0; channel < 4; channel++)
            {
                int sum = 0;
                for (int offset = -radius; offset <= radius; offset++)
                    sum += source[lineStart + Math.Clamp(offset, 0, length - 1) * pixelStep + channel];

                for (int position = 0; position < length; position++)
                {
                    target[lineStart + position * pixelStep + channel] = (byte)(sum / window);
                    int leaving = Math.Clamp(position - radius, 0, length - 1);
                    int entering = Math.Clamp(position + radius + 1, 0, length - 1);
                    sum += source[lineStart + entering * pixelStep + channel] - source[lineStart + leaving * pixelStep + channel];
                }
            }
        }
    }
}
