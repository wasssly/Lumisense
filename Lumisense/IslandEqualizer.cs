using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace Lumisense;

// Эквалайзер острова: один элемент, рисует сам. Низкие частоты в середине, высокие по краям; полоски растут от центра
// (или зажигают точки снизу вверх) по спектру играющего звука. Без спектра качается по громкости.
internal sealed class IslandEqualizer : FrameworkElement
{
    private const int OpacitySteps = 64;
    private const double BarWidth = 3, DotPitch = 4.2;
    private const double AttackSeconds = 0.03, ReleaseSeconds = 0.17, LongestStep = 0.05;
    private const double StillBelow = 0.002;
    private const double QuietOpacity = 0.55, LoudnessGain = 0.45;
    private const double UnlitOpacity = 0.16, LitOpacity = 0.72, HeadWhiteness = 0.5;

    private static readonly double[] WobbleFast = [7.1, 9.3, 6.2, 10.4, 8.0, 5.6, 9.9, 7.7];
    private static readonly double[] WobbleSlow = [2.3, 3.1, 1.7, 2.9, 3.7, 2.1, 1.3, 3.3];

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Color), typeof(IslandEqualizer),
        new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => Array.Clear(((IslandEqualizer)d)._shades)));

    private double[] _levels = [], _targets = [];
    private int[] _bandOfBar = [];
    private BandMeter _meter = new(2);
    private int _bars;
    private readonly float[] _spectrum = new float[SpectrumAnalyzer.Bands];
    private readonly Brush?[] _shades = new Brush?[OpacitySteps + 1];
    private readonly Brush?[] _whites = new Brush?[OpacitySteps + 1];
    private readonly long _start = Stopwatch.GetTimestamp();
    private long _last;
    private bool _active, _running, _dots;

    public IslandEqualizer()
    {
        Bars = 5;
        Loaded += (_, _) =>
        {
            if (_active) Start();
        };
        Unloaded += (_, _) => Stop();
    }

    // Число полосок (5 в свёрнутом острове, 8 в раскрытом, как в архиве друга).
    public int Bars
    {
        get => _bars;
        set
        {
            value = Math.Clamp(value, 2, WobbleFast.Length);
            if (value == _bars) return;

            _bars = value;
            _levels = new double[value];
            _targets = new double[value];
            _meter = new BandMeter(value);
            // Полоска низких частот в середине, следующие по очереди справа и слева от неё.
            _bandOfBar = new int[value];
            int center = (value - 1) / 2;
            for (int band = 0; band < value; band++)
                _bandOfBar[center + (band % 2 == 1 ? (band + 1) / 2 : -band / 2)] = band;
            InvalidateVisual();
        }
    }

    public Color Fill
    {
        get => (Color)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    // Спектр звука (40 полос); false — звука нет или спектр недоступен.
    public Func<float[], bool>? Spectrum { get; set; }

    // Запасной вариант без спектра: громкость 0..1.
    public Func<double>? Level { get; set; }

    public bool Dots
    {
        get => _dots;
        set
        {
            if (_dots == value) return;
            _dots = value;
            InvalidateVisual();
        }
    }

    // Выключенные полоски плавно опускаются и замирают, цикл кадров при этом сам останавливается.
    public bool Active
    {
        get => _active;
        set
        {
            _active = value;
            Start();
        }
    }

    public void Reset()
    {
        Array.Clear(_levels);
        Array.Clear(_targets);
        InvalidateVisual();
    }

    private void Start()
    {
        if (_running || !IsLoaded) return;

        _running = true;
        _last = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnRendering;
    }

    private void Stop()
    {
        if (!_running) return;

        _running = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        double step = Math.Min(Stopwatch.GetElapsedTime(_last, now).TotalSeconds, LongestStep);
        if (step <= 0) return;

        _last = now;
        if (!Advance(step, Stopwatch.GetElapsedTime(_start, now).TotalSeconds)) Stop();
    }

    private bool Advance(double step, double time)
    {
        if (!_active) Array.Clear(_targets);
        else if (Spectrum?.Invoke(_spectrum) == true) FollowSpectrum(step);
        else FollowLoudness(Level?.Invoke() ?? 0, time);

        double rise = 1 - Math.Exp(-step / AttackSeconds), fall = 1 - Math.Exp(-step / ReleaseSeconds);
        bool moved = false;
        for (int i = 0; i < _bars; i++)
        {
            double next = _levels[i] + (_targets[i] - _levels[i]) * (_targets[i] > _levels[i] ? rise : fall);
            if (Math.Abs(next - _levels[i]) > StillBelow) moved = true;
            _levels[i] = next;
        }

        if (moved) InvalidateVisual();
        return _active || moved;
    }

    private void FollowSpectrum(double step)
    {
        _meter.Measure(_spectrum, step);
        for (int bar = 0; bar < _bars; bar++) _targets[bar] = _meter.Heights[_bandOfBar[bar]];
    }

    // Громкость одна на все полоски, поэтому каждая качается в своём ритме, а средние выше крайних.
    private void FollowLoudness(double loudness, double time)
    {
        double level = Math.Pow(Math.Clamp(loudness, 0, 1), 0.6);
        double middle = (_bars - 1) / 2.0;
        for (int i = 0; i < _bars; i++)
        {
            double noise = 0.5 + 0.5 * Math.Sin(time * WobbleFast[i] + i * 1.9) * Math.Cos(time * WobbleSlow[i] + i * 0.7);
            double envelope = 1 - 0.3 * Math.Abs(i - middle) / middle;
            _targets[i] = level * envelope * (0.3 + 0.7 * noise);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        double gap = (width - _bars * BarWidth) / (_bars - 1), x = 0;
        for (int i = 0; i < _bars; i++)
        {
            if (_dots) DrawDots(dc, x, height, _levels[i]);
            else DrawBar(dc, x, height, _levels[i]);
            x += BarWidth + gap;
        }
    }

    private void DrawBar(DrawingContext dc, double x, double height, double level)
    {
        double barHeight = BarWidth + level * (height - BarWidth);
        dc.DrawRoundedRectangle(Shade(QuietOpacity + LoudnessGain * level), null,
            new Rect(x, (height - barHeight) / 2, BarWidth, barHeight), BarWidth / 2, BarWidth / 2);
    }

    // Столбик из точек, как в архиве: зажигается снизу вверх, верхняя точка чуть белее; неполная верхняя точка растёт плавно.
    private void DrawDots(DrawingContext dc, double x, double height, double level)
    {
        double fit = height / DotPitch, whole = Math.Floor(fit), part = Math.Clamp((fit - whole - 0.3) / 0.4, 0, 1);
        double rows = Math.Max(whole + part * part * (3 - 2 * part), 1);
        double gap = (height - rows * BarWidth) / Math.Max(rows - 1, 1), y = height, lit = 1 + level * (rows - 1);
        for (int r = 0; r < rows; r++)
        {
            double row = Math.Min(rows - r, 1), size = BarWidth * row;
            double on = Math.Clamp(lit - r, 0, 1), head = on * (1 - Math.Clamp(lit - r - 1, 0, 1));
            var center = new Point(x + BarWidth / 2, y - size / 2);
            dc.DrawEllipse(Shade((UnlitOpacity + (LitOpacity - UnlitOpacity) * on + (1 - LitOpacity) * head) * row), null, center, size / 2, size / 2);
            if (head > 0.01) dc.DrawEllipse(Shade(HeadWhiteness * head * row, Colors.White), null, center, size / 2, size / 2);
            y -= row * (BarWidth + gap);
        }
    }

    private Brush Shade(double opacity, Color? color = null)
    {
        int step = (int)Math.Round(Math.Clamp(opacity, 0, 1) * OpacitySteps);
        if (color is not null)
        {
            if (_whites[step] is { } known) return known;

            var white = new SolidColorBrush(Color.FromArgb((byte)(255 * step / OpacitySteps), 255, 255, 255));
            white.Freeze();
            return _whites[step] = white;
        }

        if (_shades[step] is { } cached) return cached;

        Color fill = Fill;
        var brush = new SolidColorBrush(Color.FromArgb((byte)(fill.A * step / OpacitySteps), fill.R, fill.G, fill.B));
        brush.Freeze();
        return _shades[step] = brush;
    }
}
