using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace Lumisense;

// Фон раскрытого острова: свечение цвета акцента, волна спектра и матрица точек вдоль нижнего края. Всё живёт от спектра звука.
internal sealed class IslandBackdrop : FrameworkElement
{
    [Flags]
    public enum Layers
    {
        None = 0,
        Glow = 1,
        Matrix = 2,
        Wave = 4,
    }

    private const int Bands = 16, PatchCount = 4, MatrixRows = 4, OpacitySteps = 64, WaveSamples = 48;
    private const double AttackSeconds = 0.07, ReleaseSeconds = 0.45, LongestStep = 0.05, ShortestStep = 0.016, StillBelow = 0.002;
    private const double GlowBase = 0.16, GlowGain = 0.5, GlowWidth = 0.26, GlowWidthGain = 0.1, GlowHeight = 0.3, GlowHeightGain = 0.4, DriftRange = 0.06;
    private const double ColumnPitch = 8, RowPitch = 6.5, DotRadius = 1.5, MatrixBottom = 7, SideInset = 6, EdgeClearance = 3;
    private const double UnlitOpacity = 0.08, LitOpacity = 0.85, HeadWhiteness = 0.5, LitReach = 1.1, Absent = 0.01;
    private const double WaveBase = 8, WaveReach = 0.42;

    private static readonly (double Center, double Speed, double Phase, int Palette)[] Patches =
        [(0.40, 0.55, 0.0, 1), (0.63, 0.43, 2.1, 2), (0.15, 0.71, 4.0, 0), (0.86, 0.62, 5.3, 0)];

    private static readonly (double Offset, double Alpha)[] Falloff = [(0, 1), (0.25, 0.7), (0.5, 0.3), (0.75, 0.07), (1, 0)];

    private readonly record struct Column(double X, double Share, int LowestRow);

    private readonly RadialGradientBrush[] _glow = new RadialGradientBrush[PatchCount];
    private readonly BandMeter _meter = new(Bands);
    private readonly double[] _levels = new double[Bands], _targets = new double[Bands];
    private readonly float[] _spectrum = new float[SpectrumAnalyzer.Bands];
    private readonly Dictionary<(uint Rgb, int Step), Brush> _shades = [];
    private readonly long _start = Stopwatch.GetTimestamp();
    private Color[] _palette = [Colors.White, Colors.White, Colors.White];
    private Brush _waveFill = Brushes.Transparent;
    private Pen _wavePen = new(Brushes.Transparent, 1);
    private Column[] _columns = [];
    private Geometry? _unlit;
    private Size _laidOut;
    private Layers _mode;
    private double _rounding;
    private long _last;
    private bool _active, _running;

    public IslandBackdrop()
    {
        for (int p = 0; p < PatchCount; p++)
        {
            _glow[p] = new RadialGradientBrush { Opacity = GlowBase };
            foreach (var (offset, _) in Falloff) _glow[p].GradientStops.Add(new GradientStop(Colors.White, offset));
        }

        IsHitTestVisible = false;
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
    }

    public Func<float[], bool>? Spectrum { get; set; }

    // Радиус скругления карточки: точки матрицы не заходят в углы.
    public double Rounding
    {
        get => _rounding;
        set
        {
            if (value == _rounding) return;
            _rounding = value;
            _unlit = null;
            InvalidateVisual();
        }
    }

    public Layers Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            Visibility = value == Layers.None ? Visibility.Collapsed : Visibility.Visible;
            Start();
            InvalidateVisual();
        }
    }

    // Крутится, пока раскрытый остров на экране; в остальное время цикл кадров стоит.
    public bool Active
    {
        get => _active;
        set
        {
            _active = value;
            if (value) Start();
        }
    }

    // Цвета свечения и матрицы: акцент и два соседних по оттенку.
    public void Tint(Color accent)
    {
        _palette = [accent, Rotate(accent, 28), Rotate(accent, -28)];
        for (int p = 0; p < PatchCount; p++)
        {
            Color color = _palette[Patches[p].Palette % _palette.Length];
            for (int i = 0; i < Falloff.Length; i++)
                _glow[p].GradientStops[i].Color = Color.FromArgb((byte)(255 * Falloff[i].Alpha), color.R, color.G, color.B);
        }

        var fill = new LinearGradientBrush(Color.FromArgb(90, accent.R, accent.G, accent.B), Color.FromArgb(0, accent.R, accent.G, accent.B), 90);
        fill.Freeze();
        _waveFill = fill;
        var line = new SolidColorBrush(Color.FromArgb(150, accent.R, accent.G, accent.B));
        line.Freeze();
        _wavePen = new Pen(line, 1.4) { LineJoin = PenLineJoin.Round };
        _wavePen.Freeze();
        _shades.Clear();
        InvalidateVisual();
    }

    private void Start()
    {
        if (_running || !_active || !IsLoaded || _mode == Layers.None) return;

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
        double step = Stopwatch.GetElapsedTime(_last, now).TotalSeconds;
        if (step < ShortestStep) return;

        _last = now;
        if (!_active) Stop();
        else Advance(Math.Min(step, LongestStep));
    }

    private void Advance(double step)
    {
        bool playing = Spectrum?.Invoke(_spectrum) == true;
        if (playing)
        {
            _meter.Measure(_spectrum, step);
            Array.Copy(_meter.Heights, _targets, Bands);
        }
        else Array.Clear(_targets);

        double rise = 1 - Math.Exp(-step / AttackSeconds), fall = 1 - Math.Exp(-step / ReleaseSeconds);
        bool moved = false;
        for (int i = 0; i < Bands; i++)
        {
            double next = _levels[i] + (_targets[i] - _levels[i]) * (_targets[i] > _levels[i] ? rise : fall);
            if (Math.Abs(next - _levels[i]) > StillBelow) moved = true;
            _levels[i] = next;
        }

        // Свечение медленно дрейфует и без звука; остальным слоям, пока всё стоит, перерисовка не нужна.
        if (moved || playing || _mode.HasFlag(Layers.Glow)) InvalidateVisual();
    }

    // Уровень 0..1 для доли ширины от середины к краю: середина отвечает за низкие частоты, края за высокие.
    private double At(double share)
    {
        double at = Math.Clamp(share, 0, 1) * (Bands - 1);
        int low = Math.Min((int)at, Bands - 2);
        return _levels[low] + (_levels[low + 1] - _levels[low]) * (at - low);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0 || _mode == Layers.None) return;

        if (_mode.HasFlag(Layers.Glow)) DrawGlow(dc, w, h, Stopwatch.GetElapsedTime(_start).TotalSeconds);
        if (_mode.HasFlag(Layers.Wave)) DrawWave(dc, w, h);
        if (_mode.HasFlag(Layers.Matrix)) DrawMatrix(dc, w, h);
    }

    private void DrawGlow(DrawingContext dc, double w, double h, double time)
    {
        for (int p = 0; p < PatchCount; p++)
        {
            int from = p * Bands / PatchCount, to = (p + 1) * Bands / PatchCount;
            double level = 0;
            for (int band = from; band < to; band++) level += _levels[band];
            level /= to - from;

            _glow[p].Opacity = GlowBase + GlowGain * level;
            var patch = Patches[p];
            double x = w * (patch.Center + DriftRange * Math.Sin(time * patch.Speed + patch.Phase));
            dc.DrawEllipse(_glow[p], null, new Point(x, h), w * (GlowWidth + GlowWidthGain * level), h * (GlowHeight + GlowHeightGain * level));
        }
    }

    // Гладкая волна спектра вдоль низа карточки: холм посередине (басы), спад к краям.
    private void DrawWave(DrawingContext dc, double w, double h)
    {
        var points = new Point[WaveSamples + 1];
        for (int i = 0; i <= WaveSamples; i++)
        {
            double x = w * i / WaveSamples, share = Math.Abs(x / w - 0.5) * 2;
            points[i] = new Point(x, h - WaveBase - At(share) * h * WaveReach);
        }

        var area = new StreamGeometry();
        using (StreamGeometryContext context = area.Open())
        {
            context.BeginFigure(new Point(0, h), true, true);
            context.LineTo(points[0], false, false);
            context.PolyLineTo(points[1..], false, true);
            context.LineTo(new Point(w, h), false, false);
        }

        area.Freeze();
        dc.DrawGeometry(_waveFill, null, area);

        var line = new StreamGeometry();
        using (StreamGeometryContext context = line.Open())
        {
            context.BeginFigure(points[0], false, false);
            context.PolyLineTo(points[1..], true, true);
        }

        line.Freeze();
        dc.DrawGeometry(null, _wavePen, line);
    }

    // Как в архиве: колонки точек вдоль нижнего края, середина — низкие частоты; точки, не влезающие в скруглённый контур, пропускаются.
    private void DrawMatrix(DrawingContext dc, double w, double h)
    {
        dc.DrawGeometry(Shade(Colors.White, UnlitOpacity), null, LayOut(w, h));
        foreach (Column column in _columns)
        {
            double lit = At(column.Share) * MatrixRows * LitReach;
            Color color = Along(column.Share);
            for (int row = column.LowestRow; row < MatrixRows && lit - row > Absent; row++)
            {
                var center = new Point(column.X, h - MatrixBottom - row * RowPitch);
                double on = Math.Clamp(lit - row, 0, 1), head = on * (1 - Math.Clamp(lit - row - 1, 0, 1));
                dc.DrawEllipse(Shade(color, LitOpacity * on), null, center, DotRadius, DotRadius);
                if (head > Absent) dc.DrawEllipse(Shade(Colors.White, HeadWhiteness * head), null, center, DotRadius, DotRadius);
            }
        }
    }

    private Geometry LayOut(double w, double h)
    {
        var size = new Size(w, h);
        if (_unlit != null && _laidOut == size) return _unlit;

        int count = (int)((w - 2 * SideInset) / ColumnPitch) + 1;
        if (count % 2 == 0) count--;
        double left = (w - (count - 1) * ColumnPitch) / 2, middle = (count - 1) / 2.0;
        Geometry outline = IslandOutline.Create(new Rect(0, 0, w, h), _rounding);

        var columns = new List<Column>();
        var dots = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (StreamGeometryContext context = dots.Open())
        {
            for (int i = 0; i < count; i++)
            {
                double x = left + i * ColumnPitch;
                int lowest = MatrixRows;
                for (int row = MatrixRows - 1; row >= 0; row--)
                {
                    var center = new Point(x, h - MatrixBottom - row * RowPitch);
                    var room = new EllipseGeometry(center, DotRadius + EdgeClearance, DotRadius + EdgeClearance);
                    if (outline.FillContainsWithDetail(room) != IntersectionDetail.FullyContains) break;

                    AddDot(context, center);
                    lowest = row;
                }

                if (lowest < MatrixRows) columns.Add(new Column(x, Math.Abs(i - middle) / middle, lowest));
            }
        }

        dots.Freeze();
        _columns = [.. columns];
        _laidOut = size;
        return _unlit = dots;
    }

    private static void AddDot(StreamGeometryContext context, Point center)
    {
        var size = new Size(DotRadius, DotRadius);
        Point left = center - new Vector(DotRadius, 0), right = center + new Vector(DotRadius, 0);
        context.BeginFigure(left, true, true);
        context.ArcTo(right, size, 0, false, SweepDirection.Clockwise, true, false);
        context.ArcTo(left, size, 0, false, SweepDirection.Clockwise, true, false);
    }

    private Color Along(double share)
    {
        double at = Math.Clamp(share, 0, 1) * (_palette.Length - 1);
        int low = Math.Min((int)at, _palette.Length - 2);
        double part = at - low;
        Color a = _palette[low], b = _palette[low + 1];
        return Color.FromRgb((byte)(a.R + (b.R - a.R) * part), (byte)(a.G + (b.G - a.G) * part), (byte)(a.B + (b.B - a.B) * part));
    }

    private Brush Shade(Color color, double opacity)
    {
        int step = (int)Math.Round(Math.Clamp(opacity, 0, 1) * OpacitySteps);
        var key = ((uint)(color.R << 16 | color.G << 8 | color.B), step);
        if (_shades.TryGetValue(key, out var cached)) return cached;

        var brush = new SolidColorBrush(Color.FromArgb((byte)(255 * step / OpacitySteps), color.R, color.G, color.B));
        brush.Freeze();
        return _shades[key] = brush;
    }

    // Поворот оттенка цвета на заданное число градусов.
    private static Color Rotate(Color color, double degrees)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
        double hue = delta == 0 ? 0 : max == r ? ((g - b) / delta + 6) % 6 * 60 : max == g ? ((b - r) / delta + 2) * 60 : ((r - g) / delta + 4) * 60;
        double saturation = max == 0 ? 0 : delta / max;
        hue = (hue + degrees + 360) % 360;
        double c = max * saturation, x = c * (1 - Math.Abs(hue / 60 % 2 - 1)), m = max - c;
        (double R, double G, double B) = (int)(hue / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)Math.Round((R + m) * 255), (byte)Math.Round((G + m) * 255), (byte)Math.Round((B + m) * 255));
    }
}
