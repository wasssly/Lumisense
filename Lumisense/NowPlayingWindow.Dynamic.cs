using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Lumisense;

// Покадровые фоны Now Playing: «Orbs» (мягкие цветные шары на орбитах) и «Waves» (размытые волны). Кадры считаются в
// CompositionTarget.Rendering только во время воспроизведения и без «Меньше анимации»; на паузе остаётся статичный кадр.
//
// Шары — овалы с радиальным градиентом (мягкий край вместо BlurEffect, который в WPF считается на CPU и на весь экран был
// бы слишком тяжёлым); каждый движется по прямоугольной орбите со сглаженными поворотами и «дышит». Волны рисуются в
// небольшой битмап прямо в массив пикселей (без RenderTargetBitmap и чтения кадра с GPU), размываются в коде (box-blur)
// и растягиваются на окно с линейным сглаживанием. Волны перерисовываются 30 раз в секунду, а не с частотой монитора:
// на глаз разницы нет, а нагрузка на поток интерфейса вдвое меньше.
public partial class NowPlayingWindow
{
    private const int WaveCount = 3;
    // Ширина битмапа волн и радиус размытия вместе задают мягкость фона: радиус 2 при ширине 320 — это ≈0.6% ширины окна
    // на каждую сторону (раньше 2 при 480, ≈0.4%). Меньший битмап заодно вдвое с лишним дешевле в расчёте.
    private const int WavesBitmapWidth = 320;
    private const int WavesBlurRadius = 2;
    private const double WavesCrestThickness = 1.1;
    private const double WavesFrameInterval = 1.0 / 30;
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
    private readonly SolidColorBrush[] _waveCrestBrushes = new SolidColorBrush[WaveCount];
    private WriteableBitmap? _wavesOutput;
    private byte[] _wavesPixels = Array.Empty<byte>();
    private byte[] _wavesScratch = Array.Empty<byte>();
    private int _wavesBitmapHeight;
    private bool _dynamicRendering;
    private TimeSpan _dynamicLastTime = TimeSpan.MinValue;
    private double _dynamicTime;
    private double _dynamicLevel;
    private double _wavesSinceRender;

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
        {
            AnimateColor(_waveBrushes[index], SolidColorBrush.ColorProperty, _waveBrushes[index].Color, waveColors[index], animate);
            AnimateColor(_waveCrestBrushes[index], SolidColorBrush.ColorProperty, _waveCrestBrushes[index].Color, Lighten(waveColors[index], 0.55), animate);
        }

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
        {
            _waveBrushes[index] = new SolidColorBrush(Colors.Transparent);
            // Цвет гребня тянется за цветом волны (осветлённым), чтобы он перетекал вместе с палитрой.
            _waveCrestBrushes[index] = new SolidColorBrush(Colors.White);
        }
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
        if (_wavesOutput is null || bitmapHeight != _wavesBitmapHeight)
        {
            _wavesBitmapHeight = bitmapHeight;
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
            _wavesSinceRender = WavesFrameInterval;
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

        // Свёрнутое или скрытое окно ничего не показывает: кадры не считаем, а время не накапливаем, чтобы после
        // возврата анимация продолжилась без скачка.
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            _dynamicLastTime = TimeSpan.MinValue;
            return;
        }

        double delta = _dynamicLastTime == TimeSpan.MinValue ? 0 : (args.RenderingTime - _dynamicLastTime).TotalSeconds;
        _dynamicLastTime = args.RenderingTime;
        delta = Math.Clamp(delta, 0, 0.1);

        _dynamicTime += delta;
        double level = Math.Sqrt(Math.Clamp(_owner.AudioLevelMeter?.NormalizedLevel ?? 0d, 0d, 1d));
        // Громкость сглаживаем: сырое значение дёргается от кадра к кадру, и волны мигали бы.
        _dynamicLevel += (level - _dynamicLevel) * Math.Min(1, delta * 7);

        if (IsWavesBackground)
        {
            // Порог 90% интервала: при 60 Гц кадры приходят каждые ≈16.7 мс, и без запаса часть кадров давала бы 3 пропуска вместо 2.
            _wavesSinceRender += delta;
            if (_wavesSinceRender < WavesFrameInterval * 0.9) return;
            _wavesSinceRender = 0;
        }

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

    // Волны рисуются прямо в массив пикселей Pbgra32: для каждого столбца считается высота гребня, ниже неё заливка с
    // дробным покрытием верхней строки (мягкий край без сглаживания WPF), а по самому гребню — светлая линия.
    // Так не нужны ни RenderTargetBitmap, ни чтение кадра с GPU, ни временные геометрии на каждом кадре.
    private void RenderWaves()
    {
        if (_wavesOutput is null) return;
        EnsureWaveBrushes();

        int width = WavesBitmapWidth, height = _wavesBitmapHeight;
        Array.Clear(_wavesPixels);

        for (int index = 0; index < WaveCount; index++)
        {
            double direction = index % 2 == 0 ? 1 : -1;
            double baseY = height * (0.56 + 0.13 * index);
            double amplitude = height * (0.045 + 0.15 * _dynamicLevel) * (1 - 0.14 * index);
            double frequency = Math.PI * 2 / (width * (0.55 + 0.22 * index));
            double phase = _dynamicTime * (0.55 + 0.3 * index) * direction;

            Color fillColor = _waveBrushes[index].Color;
            Color crestColor = _waveCrestBrushes[index].Color;
            double fillOpacity = 0.78 - index * 0.12;
            double crestOpacity = 0.55 - index * 0.12;

            for (int column = 0; column < width; column++)
            {
                double x = column + 0.5;
                double y = baseY
                    + amplitude * Math.Sin(x * frequency + phase)
                    + amplitude * 0.45 * Math.Sin(x * frequency * 2.1 - phase * 1.3);

                double top = Math.Floor(y);
                int firstRow = (int)top;
                for (int row = Math.Max(firstRow, 0); row < height; row++)
                {
                    double coverage = row == firstRow ? 1 - (y - top) : 1;
                    BlendPixel(_wavesPixels, (row * width + column) * 4, fillColor, (int)(fillOpacity * coverage * 255 + 0.5));
                }

                double crestTop = y - WavesCrestThickness / 2, crestBottom = y + WavesCrestThickness / 2;
                int crestFirst = Math.Max(0, (int)Math.Floor(crestTop));
                int crestLast = Math.Min(height - 1, (int)Math.Floor(crestBottom));
                for (int row = crestFirst; row <= crestLast; row++)
                {
                    double coverage = Math.Min(row + 1, crestBottom) - Math.Max(row, crestTop);
                    if (coverage > 0)
                        BlendPixel(_wavesPixels, (row * width + column) * 4, crestColor, (int)(crestOpacity * coverage * 255 + 0.5));
                }
            }
        }

        int stride = width * 4;

        // Box-blur по горизонтали и вертикали поверх предумноженных каналов: гладкое размытие без «ступенек»,
        // которые были бы заметны при простом растягивании маленького битмапа.
        BoxBlur(_wavesPixels, _wavesScratch, width, height, WavesBlurRadius, horizontal: true);
        BoxBlur(_wavesScratch, _wavesPixels, width, height, WavesBlurRadius, horizontal: false);

        _wavesOutput.WritePixels(new Int32Rect(0, 0, width, height), _wavesPixels, stride, 0);
    }

    // «Источник поверх» для предумноженного Pbgra32: цвет домножается на альфу, подложка гасится на (1 − альфа).
    private static void BlendPixel(byte[] pixels, int offset, Color color, int alpha)
    {
        if (alpha <= 0) return;
        if (alpha > 255) alpha = 255;
        int inverse = 255 - alpha;
        pixels[offset] = (byte)((color.B * alpha + pixels[offset] * inverse) / 255);
        pixels[offset + 1] = (byte)((color.G * alpha + pixels[offset + 1] * inverse) / 255);
        pixels[offset + 2] = (byte)((color.R * alpha + pixels[offset + 2] * inverse) / 255);
        pixels[offset + 3] = (byte)((255 * alpha + pixels[offset + 3] * inverse) / 255);
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
