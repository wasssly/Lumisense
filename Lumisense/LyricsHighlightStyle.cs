using System;

namespace Lumisense;

// Значения AppSettings.SyncedLyricsHighlightEffect: "Glow" (по умолчанию), "GlowPill", "Pill", "Bar", "None".
// Подложка и полоска рисуются только в Now Playing; свечение — и там, и в панели текста главного окна.
public static class LyricsHighlightStyle
{
    // Малый радиус и умеренная яркость: широкое свечение размывало контуры букв.
    public const double GlowBlurRadius = 7;
    public const double GlowOpacity = 0.5;

    public const int MinScalePercent = 2;
    public const int MaxScalePercent = 15;
    public const int MaxVisibleLines = 15;

    // Неизвестные и прежние значения читаются как мягкое свечение, как и раньше.
    public static bool HasGlow(string? effect) => effect is not ("None" or "Pill" or "Bar");

    public static bool HasPill(string? effect) => effect is "GlowPill" or "Pill";

    public static bool HasBar(string? effect) => effect == "Bar";

    // Прозрачность падает на столько с каждой строкой удаления от активной: 0 % — 0,04, 50 % — 0,10, 100 % — 0,16.
    public static double FadeStep(int strength) => 0.04 + 0.12 * Math.Clamp(strength, 0, 100) / 100.0;

    public static double ScaleFactor(int percent) => 1 + Math.Clamp(percent, MinScalePercent, MaxScalePercent) / 100.0;

    // Увеличенный текст не должен упираться в край списка: запас по бокам растёт вместе с масштабом (при 5 % — 46 px).
    public static double ScaleSidePadding(int percent) => 14 + Math.Ceiling(Math.Clamp(percent, MinScalePercent, MaxScalePercent) * 6.4);

    // 0 — показывать все строки; иначе нечётное число 3–15: активная строка и поровну строк с каждой стороны.
    public static int NormalizeVisibleLines(int lines)
    {
        if (lines < 3) return 0;
        if (lines % 2 == 0) lines++;
        return Math.Min(lines, MaxVisibleLines);
    }

    public static int VisibleRadius(int normalizedLines) => normalizedLines <= 0 ? int.MaxValue : (normalizedLines - 1) / 2;
}
