using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Lumisense;

// Заглушка на время поиска текста: три серые строки, по которым пробегает блик.
internal sealed class IslandShimmer : FrameworkElement
{
    private const double RowHeight = 10, RowGap = 12, SheenWidth = 170;
    private static readonly TimeSpan SweepTime = TimeSpan.FromSeconds(1.5);
    private static readonly (double Width, double Alpha)[] Rows = [(0.58, 0.55), (0.84, 1), (0.42, 0.55)];

    private readonly SolidColorBrush _base = new(Color.FromArgb(26, 255, 255, 255));
    private readonly LinearGradientBrush _sheen;
    private readonly TranslateTransform _move = new() { X = -SheenWidth * 2 };

    public IslandShimmer()
    {
        _base.Freeze();
        _sheen = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(SheenWidth, 0),
            Transform = _move,
        };
        _sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0));
        _sheen.GradientStops.Add(new GradientStop(Color.FromArgb(70, 255, 255, 255), 0.5));
        _sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1));
        IsHitTestVisible = false;
        IsVisibleChanged += (_, _) => Restart();
        SizeChanged += (_, _) => Restart();
    }

    private void Restart()
    {
        _move.BeginAnimation(TranslateTransform.XProperty, null);
        if (!IsVisible || ActualWidth <= 0) return;

        _move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-SheenWidth, ActualWidth, SweepTime)
        {
            RepeatBehavior = RepeatBehavior.Forever,
        });
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double total = Rows.Length * RowHeight + (Rows.Length - 1) * RowGap, y = (h - total) / 2;
        foreach (var (width, _) in Rows)
        {
            var rect = new Rect((w - w * width) / 2, y, w * width, RowHeight);
            dc.DrawRoundedRectangle(_base, null, rect, RowHeight / 2, RowHeight / 2);
            dc.DrawRoundedRectangle(_sheen, null, rect, RowHeight / 2, RowHeight / 2);
            y += RowHeight + RowGap;
        }
    }
}
