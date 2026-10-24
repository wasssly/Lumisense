using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Lumisense;

// Полоса перемотки раскрытого острова, как в архиве друга: если у трека есть синхронный текст, она делится на сегменты
// по строкам (паузы без слов дают длинные «пустые» сегменты), иначе это сплошная полоса. Под курсором полоса утолщается волной.
internal sealed class IslandSeekBar : FrameworkElement
{
    private const double MarkGap = 2, MinSegmentWidth = 6, SnapReach = 5, SampleStep = 4;
    private const double PassedOpacity = 0.85, AheadOpacity = 0.3, UnplayedOpacity = 0.2, LitAheadOpacity = 0.45;
    private const double MinThickness = 2, RestThickness = 5, BulgeThickness = 6, BulgeReach = 28;

    private static readonly DependencyProperty HoverProperty = DependencyProperty.Register(
        nameof(Hover), typeof(double), typeof(IslandSeekBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private double[] _starts = [];
    private double _duration, _ratio, _hoverX;
    private double _hoverTarget;
    private int _pointed = -1;

    public IslandSeekBar() => IsHitTestVisible = false;

    // Цвет «текущего» сегмента.
    public Color Accent { get; set; } = Colors.White;

    public bool HasMarks => _starts.Length > 0 && _duration > 1;

    private double Hover
    {
        get => (double)GetValue(HoverProperty);
        set => SetValue(HoverProperty, value);
    }

    // Доля пройденного трека 0..1.
    public double Ratio
    {
        get => _ratio;
        set
        {
            value = Math.Clamp(value, 0, 1);
            if (value == _ratio) return;

            _ratio = value;
            InvalidateVisual();
        }
    }

    // Начала строк текста в секундах; пустой список — сплошная полоса.
    public void SetLines(IReadOnlyList<double> seconds)
    {
        _starts = [.. seconds];
        InvalidateVisual();
    }

    public void SetDuration(double seconds)
    {
        if (seconds == _duration) return;

        _duration = seconds;
        InvalidateVisual();
    }

    // Положение курсора по горизонтали; null — курсор ушёл. Утолщение появляется и гаснет плавно.
    public void SetPointer(double? x)
    {
        double target = x.HasValue ? 1 : 0;
        if (x is { } value)
        {
            _hoverX = value;
            InvalidateVisual();
        }

        if (target == _hoverTarget) return;

        _hoverTarget = target;
        BeginAnimation(HoverProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(target > 0 ? 160 : 220)));
    }

    // Притягивает позицию к границе строки, если она рядом: так перематывают ровно к началу строки.
    public double Snap(double x)
    {
        if (!HasMarks) return x;

        List<double> edges = Edges(ActualWidth);
        double nearest = x;
        for (int i = 1; i < edges.Count - 1; i++)
            if (Math.Abs(edges[i] - x) <= SnapReach && Math.Abs(edges[i] - x) < Math.Abs(nearest - x)) nearest = edges[i];
        return nearest;
    }

    private List<double> Edges(double width)
    {
        var edges = new List<double> { 0 };
        if (_duration > 1)
            foreach (double start in _starts)
            {
                double x = start / _duration * width;
                if (x - edges[^1] >= MinSegmentWidth && width - x >= MinSegmentWidth) edges.Add(x);
            }

        edges.Add(width);
        return edges;
    }

    private double ThicknessAt(double center, double left, double right)
    {
        double hover = Hover;
        if (hover <= 0.001) return RestThickness;

        double distance = Math.Max(0, Math.Max(left - _hoverX, _hoverX - right));
        if (left == right) distance = Math.Abs(center - _hoverX);
        return RestThickness + BulgeThickness * hover * Math.Exp(-Math.Pow(distance / BulgeReach, 2));
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        double middle = height / 2, fill = Math.Clamp(_ratio * width, 0, width);
        if (HasMarks) RenderLines(dc, width, middle, fill);
        else RenderSolid(dc, width, middle, fill);
    }

    private void RenderLines(DrawingContext dc, double width, double middle, double fill)
    {
        List<double> edges = Edges(width);
        int last = edges.Count - 2, current = 0;
        while (current < last && edges[current + 1] <= fill) current++;

        _pointed = -1;
        for (int i = 0; i <= last; i++)
            if (Hover > 0.001 && _hoverX >= edges[i] && _hoverX < edges[i + 1] + (i == last ? 1 : 0)) _pointed = i;

        for (int i = 0; i <= last; i++)
        {
            double left = edges[i] + (i > 0 ? MarkGap / 2 : 0), right = edges[i + 1] - (i < last ? MarkGap / 2 : 0);
            if (right <= left) continue;

            double thick = Math.Max(MinThickness, ThicknessAt((left + right) / 2, left, right));
            var segment = new Rect(left, middle - thick / 2, right - left, thick);
            double radius = Math.Min(thick, segment.Width) / 2;
            if (i < current)
            {
                dc.DrawRoundedRectangle(White(PassedOpacity), null, segment, radius, radius);
                continue;
            }

            if (i > current)
            {
                double ahead = i == _pointed ? UnplayedOpacity + (LitAheadOpacity - UnplayedOpacity) * Hover : UnplayedOpacity;
                dc.DrawRoundedRectangle(White(ahead), null, segment, radius, radius);
                continue;
            }

            dc.DrawRoundedRectangle(Tinted(AheadOpacity), null, segment, radius, radius);
            if (fill <= left) continue;

            dc.PushClip(new RectangleGeometry(new Rect(0, 0, fill, ActualHeight)));
            dc.DrawRoundedRectangle(Tinted(1), null, segment, radius, radius);
            dc.Pop();
        }
    }

    // Сплошная полоса — один контур, толщина которого меняется по ширине; пройденная часть заливается белым.
    private void RenderSolid(DrawingContext dc, double width, double middle, double fill)
    {
        int count = Math.Max(1, (int)Math.Ceiling(width / SampleStep));
        var thickness = new double[count];
        var centers = new double[count];
        for (int i = 0; i < count; i++)
        {
            centers[i] = width * (i + 0.5) / count;
            thickness[i] = Math.Max(MinThickness, ThicknessAt(centers[i], centers[i], centers[i]));
        }

        double left = Math.Min(thickness[0] / 2, width / 2), right = Math.Min(thickness[^1] / 2, width / 2);
        var shape = new StreamGeometry();
        using (StreamGeometryContext outline = shape.Open())
        {
            outline.BeginFigure(new Point(left, middle - left), true, true);
            for (int i = 0; i < count; i++)
                if (centers[i] > left && centers[i] < width - right)
                    outline.LineTo(new Point(centers[i], middle - thickness[i] / 2), true, true);
            outline.LineTo(new Point(width - right, middle - right), true, true);
            outline.ArcTo(new Point(width - right, middle + right), new Size(right, right), 0, false, SweepDirection.Clockwise, true, true);
            for (int i = count - 1; i >= 0; i--)
                if (centers[i] > left && centers[i] < width - right)
                    outline.LineTo(new Point(centers[i], middle + thickness[i] / 2), true, true);
            outline.LineTo(new Point(left, middle + left), true, true);
            outline.ArcTo(new Point(left, middle - left), new Size(left, left), 0, false, SweepDirection.Clockwise, true, true);
        }

        shape.Freeze();
        dc.DrawGeometry(White(UnplayedOpacity), null, shape);
        if (fill <= 0) return;

        int at = Math.Clamp((int)(fill / width * count), 0, count - 1);
        double radius = Math.Min(thickness[at] / 2, fill / 2);
        var reach = new CombinedGeometry(GeometryCombineMode.Union,
            new RectangleGeometry(new Rect(0, 0, Math.Max(0, fill - radius), ActualHeight)),
            new RectangleGeometry(new Rect(fill - radius * 2, middle - radius, radius * 2, radius * 2), radius, radius));
        dc.PushClip(reach);
        dc.DrawGeometry(Brushes.White, null, shape);
        dc.Pop();
    }

    private readonly Dictionary<int, Brush> _whites = [], _tints = [];

    private Brush White(double opacity) => Cached(_whites, opacity, Colors.White);

    private Brush Tinted(double opacity) => Cached(_tints, opacity, Accent);

    private static Brush Cached(Dictionary<int, Brush> cache, double opacity, Color color)
    {
        int key = (int)Math.Round(Math.Clamp(opacity, 0, 1) * 100) * 16777216 + (color.R << 16 | color.G << 8 | color.B);
        if (cache.TryGetValue(key, out var known)) return known;

        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * Math.Clamp(opacity, 0, 1)), color.R, color.G, color.B));
        brush.Freeze();
        return cache[key] = brush;
    }
}
