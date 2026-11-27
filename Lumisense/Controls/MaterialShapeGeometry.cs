namespace Lumisense;

// Фигуры индикатора загрузки Material 3 Expressive: вершины и скругления взяты из MaterialShapes.kt (androidx, Apache-2.0),
// скругление углов считается как в RoundedPolygon (дуга окружности, сглаживание в этих фигурах не используется).
// Каждая фигура сводится к радиусу от центра по углу, нормированному так, что большая сторона габарита равна 1.
internal static class MaterialShapeGeometry
{
    private readonly record struct Corner(double X, double Y, double Rounding);

    // Порядок как в LoadingIndicatorDefaults.IndeterminateIndicatorPolygons.
    internal static double[][] IndeterminateProfiles(int samples) =>
    [
        Profile(SoftBurst(), 0.5, 0.5, samples),
        Profile(Rotated(Star(9, 0.8, 0.5), -90), 0, 0, samples),
        Profile(Pentagon(), 0.5, 0.5, samples),
        Profile(Pill(), 0.5, 0.5, samples),
        Profile(Star(8, 0.8, 0.15), 0, 0, samples),
        Profile(Cookie4(), 0.5, 0.5, samples),
        Profile(Oval(), 0, 0, samples)
    ];

    private static List<Corner> SoftBurst() =>
        Repeat([new Corner(0.193, 0.277, 0.053), new Corner(0.176, 0.055, 0.053)], 10, 0.5, 0.5);

    private static List<Corner> Pentagon() =>
        Mirrored(
            [new Corner(0.500, -0.009, 0.172), new Corner(1.030, 0.365, 0.164), new Corner(0.828, 0.970, 0.169)],
            1, 0.5, 0.5);

    private static List<Corner> Pill() =>
        Mirrored(
            [new Corner(0.961, 0.039, 0.426), new Corner(1.001, 0.428, 0), new Corner(1.000, 0.609, 1.0)],
            2, 0.5, 0.5);

    private static List<Corner> Cookie4() =>
        Repeat([new Corner(1.237, 1.236, 0.258), new Corner(0.500, 0.918, 0.233)], 4, 0.5, 0.5);

    // Круг радиуса 1, сжатый по Y до 0,64 и повёрнутый на -45°; круг достаточно плотно задан обычными вершинами.
    private static List<Corner> Oval()
    {
        const int count = 720;
        var result = new List<Corner>(count);
        for (int i = 0; i < count; i++)
        {
            double angle = i * 2 * Math.PI / count;
            result.Add(new Corner(Math.Cos(angle), 0.64 * Math.Sin(angle), 0));
        }

        return Rotated(result, -45);
    }

    // Звезда с центром в нуле: внешние вершины на радиусе 1, внутренние на innerRadius, у всех одно скругление.
    private static List<Corner> Star(int points, double innerRadius, double rounding)
    {
        var result = new List<Corner>(points * 2);
        for (int i = 0; i < points; i++)
        {
            double outer = Math.PI / points * 2 * i;
            double inner = Math.PI / points * (2 * i + 1);
            result.Add(new Corner(Math.Cos(outer), Math.Sin(outer), rounding));
            result.Add(new Corner(Math.Cos(inner) * innerRadius, Math.Sin(inner) * innerRadius, rounding));
        }

        return result;
    }

    private static List<Corner> Rotated(List<Corner> corners, double degrees)
    {
        double angle = degrees * Math.PI / 180;
        double cos = Math.Cos(angle);
        double sin = Math.Sin(angle);
        return corners.Select(c => c with { X = c.X * cos - c.Y * sin, Y = c.X * sin + c.Y * cos }).ToList();
    }

    // Набор вершин повторяется reps раз с поворотом вокруг центра.
    private static List<Corner> Repeat(Corner[] corners, int reps, double cx, double cy)
    {
        var result = new List<Corner>(corners.Length * reps);
        for (int i = 0; i < corners.Length * reps; i++)
        {
            Corner corner = corners[i % corners.Length];
            double angle = i / corners.Length * 2 * Math.PI / reps;
            double dx = corner.X - cx;
            double dy = corner.Y - cy;
            result.Add(new Corner(
                dx * Math.Cos(angle) - dy * Math.Sin(angle) + cx,
                dx * Math.Sin(angle) + dy * Math.Cos(angle) + cy,
                corner.Rounding));
        }

        return result;
    }

    // Вершины повторяются reps раз, каждый второй набор отражается (customPolygon с mirroring = true).
    private static List<Corner> Mirrored(Corner[] corners, int reps, double cx, double cy)
    {
        double[] angles = corners.Select(c => Math.Atan2(c.Y - cy, c.X - cx) * 180 / Math.PI).ToArray();
        double[] distances = corners.Select(c => Math.Sqrt((c.X - cx) * (c.X - cx) + (c.Y - cy) * (c.Y - cy))).ToArray();
        int actualReps = reps * 2;
        double section = 360.0 / actualReps;

        var result = new List<Corner>();
        for (int rep = 0; rep < actualReps; rep++)
        {
            bool even = rep % 2 == 0;
            for (int index = 0; index < corners.Length; index++)
            {
                int i = even ? index : corners.Length - 1 - index;
                if (i <= 0 && !even) continue;

                double degrees = section * rep + (even ? angles[i] : section - angles[i] + 2 * angles[0]);
                double radians = degrees * Math.PI / 180;
                result.Add(new Corner(
                    Math.Cos(radians) * distances[i] + cx,
                    Math.Sin(radians) * distances[i] + cy,
                    corners[i].Rounding));
            }
        }

        return result;
    }

    // Радиус контура от центра (cx, cy) на равномерной сетке углов; нормировка по большей стороне габарита.
    private static double[] Profile(List<Corner> corners, double cx, double cy, int samples)
    {
        List<(double X, double Y)> outline = RoundedOutline(corners);

        double minX = outline.Min(p => p.X), maxX = outline.Max(p => p.X);
        double minY = outline.Min(p => p.Y), maxY = outline.Max(p => p.Y);
        double side = Math.Max(maxX - minX, maxY - minY);

        var radii = new double[samples];
        for (int i = 0; i < samples; i++)
        {
            double angle = i * 2 * Math.PI / samples;
            radii[i] = RayDistance(outline, cx, cy, Math.Cos(angle), Math.Sin(angle)) / side;
        }

        return radii;
    }

    // Контур многоугольника, где каждая вершина заменена дугой: как RoundedCorner в androidx, где срез угла
    // ограничен тем, что соседние срезы вместе не длиннее общей стороны.
    private static List<(double X, double Y)> RoundedOutline(List<Corner> corners)
    {
        const double epsilon = 1e-4;
        int n = corners.Count;
        var expectedCut = new double[n];
        var d1 = new (double X, double Y)[n];
        var d2 = new (double X, double Y)[n];

        for (int i = 0; i < n; i++)
        {
            Corner prev = corners[(i + n - 1) % n], current = corners[i], next = corners[(i + 1) % n];
            double v1x = prev.X - current.X, v1y = prev.Y - current.Y;
            double v2x = next.X - current.X, v2y = next.Y - current.Y;
            double l1 = Math.Sqrt(v1x * v1x + v1y * v1y), l2 = Math.Sqrt(v2x * v2x + v2y * v2y);
            if (l1 <= 0 || l2 <= 0) continue;

            d1[i] = (v1x / l1, v1y / l1);
            d2[i] = (v2x / l2, v2y / l2);
            double cos = d1[i].X * d2[i].X + d1[i].Y * d2[i].Y;
            double sin = Math.Sqrt(Math.Max(0, 1 - cos * cos));
            expectedCut[i] = sin > 1e-3 ? current.Rounding * (cos + 1) / sin : 0;
        }

        var sideRatio = new double[n];
        for (int i = 0; i < n; i++)
        {
            Corner a = corners[i], b = corners[(i + 1) % n];
            double side = Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
            double wanted = expectedCut[i] + expectedCut[(i + 1) % n];
            sideRatio[i] = wanted > side ? side / wanted : 1;
        }

        var outline = new List<(double X, double Y)>();
        for (int i = 0; i < n; i++)
        {
            Corner c = corners[i];
            double allowed = Math.Min(expectedCut[i] * sideRatio[(i + n - 1) % n], expectedCut[i] * sideRatio[i]);
            if (expectedCut[i] < epsilon || allowed < epsilon || c.Rounding < epsilon)
            {
                outline.Add((c.X, c.Y));
                continue;
            }

            double cut = Math.Min(allowed, expectedCut[i]);
            double radius = c.Rounding * cut / expectedCut[i];
            double bisectorX = (d1[i].X + d2[i].X) / 2, bisectorY = (d1[i].Y + d2[i].Y) / 2;
            double bisectorLength = Math.Sqrt(bisectorX * bisectorX + bisectorY * bisectorY);
            double centerDistance = Math.Sqrt(radius * radius + cut * cut);
            double centerX = c.X + bisectorX / bisectorLength * centerDistance;
            double centerY = c.Y + bisectorY / bisectorLength * centerDistance;

            double start = Math.Atan2(c.Y + d1[i].Y * cut - centerY, c.X + d1[i].X * cut - centerX);
            double end = Math.Atan2(c.Y + d2[i].Y * cut - centerY, c.X + d2[i].X * cut - centerX);
            double sweep = end - start;
            while (sweep > Math.PI) sweep -= 2 * Math.PI;
            while (sweep < -Math.PI) sweep += 2 * Math.PI;

            int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 60)));
            for (int s = 0; s <= steps; s++)
            {
                double angle = start + sweep * s / steps;
                outline.Add((centerX + Math.Cos(angle) * radius, centerY + Math.Sin(angle) * radius));
            }
        }

        return outline;
    }

    // Самое дальнее пересечение луча из центра с контуром (фигуры звёздчатые относительно центра, пересечение одно).
    private static double RayDistance(List<(double X, double Y)> outline, double cx, double cy, double ux, double uy)
    {
        double best = 0;
        for (int i = 0; i < outline.Count; i++)
        {
            (double ax, double ay) = outline[i];
            (double bx, double by) = outline[(i + 1) % outline.Count];
            double ex = bx - ax, ey = by - ay;
            double wx = ax - cx, wy = ay - cy;

            double denominator = ux * ey - uy * ex;
            if (Math.Abs(denominator) < 1e-12) continue;

            double t = (wx * ey - wy * ex) / denominator;
            double s = (wx * uy - wy * ux) / denominator;
            if (t > best && s >= -1e-9 && s <= 1 + 1e-9) best = t;
        }

        return best;
    }
}
