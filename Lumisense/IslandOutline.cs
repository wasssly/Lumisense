using System.Windows;
using System.Windows.Media;

namespace Lumisense;

// Контур со «сглаженными» углами: кривая начинается раньше, чем у обычного скругления, поэтому переход к прямой плавнее.
internal static class IslandOutline
{
    // Насколько кривая заходит на стороны по сравнению с радиусом; 1 даёт обычную дугу.
    private const double Reach = 1.45;

    // Доля от длины захода, на которую контрольная точка отстоит от угла.
    private const double Handle = 0.42;

    public static Geometry Create(Rect rect, double radius)
    {
        double room = Math.Min(rect.Width, rect.Height) / 2;
        double r = Math.Min(radius, room);
        if (r <= 0.01) return new RectangleGeometry(rect);

        double k = Math.Min(r * Reach, room);
        double h = k * Handle;

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(rect.Left + k, rect.Top), true, true);
            g.LineTo(new Point(rect.Right - k, rect.Top), true, false);
            g.BezierTo(new Point(rect.Right - h, rect.Top), new Point(rect.Right, rect.Top + h), new Point(rect.Right, rect.Top + k), true, false);
            g.LineTo(new Point(rect.Right, rect.Bottom - k), true, false);
            g.BezierTo(new Point(rect.Right, rect.Bottom - h), new Point(rect.Right - h, rect.Bottom), new Point(rect.Right - k, rect.Bottom), true, false);
            g.LineTo(new Point(rect.Left + k, rect.Bottom), true, false);
            g.BezierTo(new Point(rect.Left + h, rect.Bottom), new Point(rect.Left, rect.Bottom - h), new Point(rect.Left, rect.Bottom - k), true, false);
            g.LineTo(new Point(rect.Left, rect.Top + k), true, false);
            g.BezierTo(new Point(rect.Left, rect.Top + h), new Point(rect.Left + h, rect.Top), new Point(rect.Left + k, rect.Top), true, false);
        }
        geometry.Freeze();
        return geometry;
    }
}
