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

    // Контур «Notch Nook»: форма свисает с верхнего края экрана. Верхние углы прямые, а по бокам верхней стороны вогнутые «уши»
    // шириной ear уходят наружу от прямоугольника и сливаются с краем экрана, как вырез ноутбука. Скругляются только нижние углы.
    public static Geometry CreateNotch(Rect rect, double bottomRadius, double ear)
    {
        double r = Math.Clamp(bottomRadius, 0, Math.Min(rect.Width, rect.Height) / 2);
        // Ухо не должно заходить ниже начала нижнего скругления, пока форма ещё низкая (в начале раскрытия).
        double e = Math.Clamp(ear, 0, Math.Max(0, Math.Min(rect.Width / 2, rect.Height - r)));

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(rect.Left - e, rect.Top), true, true);
            g.LineTo(new Point(rect.Right + e, rect.Top), true, false);
            // Правое ухо: четверть окружности с центром в (Right + e, Top + e), от верхней точки к левой.
            if (e > 0.01) g.ArcTo(new Point(rect.Right, rect.Top + e), new Size(e, e), 0, false, SweepDirection.Counterclockwise, true, false);
            g.LineTo(new Point(rect.Right, rect.Bottom - r), true, false);
            if (r > 0.01) g.ArcTo(new Point(rect.Right - r, rect.Bottom), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            g.LineTo(new Point(rect.Left + r, rect.Bottom), true, false);
            if (r > 0.01) g.ArcTo(new Point(rect.Left, rect.Bottom - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            g.LineTo(new Point(rect.Left, rect.Top + e), true, false);
            // Левое ухо: от правой точки окружности с центром в (Left - e, Top + e) к верхней.
            if (e > 0.01) g.ArcTo(new Point(rect.Left - e, rect.Top), new Size(e, e), 0, false, SweepDirection.Counterclockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    // fromTopCenter: контур начинается с середины верхней стороны и идёт по часовой стрелке — от него считается полоса прогресса.
    public static Geometry Create(Rect rect, double radius, bool fromTopCenter = false)
    {
        double room = Math.Min(rect.Width, rect.Height) / 2;
        double r = Math.Min(radius, room);
        if (r <= 0.01) return new RectangleGeometry(rect);

        double k = Math.Min(r * Reach, room);
        double h = k * Handle;

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(fromTopCenter ? rect.Left + rect.Width / 2 : rect.Left + k, rect.Top), true, true);
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
