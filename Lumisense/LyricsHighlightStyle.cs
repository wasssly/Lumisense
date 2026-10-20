using System;

namespace Lumisense;

// Значения AppSettings.SyncedLyricsHighlightEffect: "Glow" (по умолчанию), "GlowPill", "Pill", "Bar", "None".
// Подложка и полоска рисуются только в Now Playing; свечение — и там, и в панели текста главного окна.
public static class LyricsHighlightStyle
{
    // Сила свечения 0–100 %: радиус от 4 до 12 px и яркость от 0,3 до 0,8. Слишком широкое свечение размывало контуры букв.
    public const int DefaultGlowStrength = 60;

    public static double GlowBlurRadius(int strength) => 4 + 8 * Math.Clamp(strength, 0, 100) / 100.0;

    public static double GlowOpacity(int strength) => 0.3 + 0.5 * Math.Clamp(strength, 0, 100) / 100.0;

    public const int MinScalePercent = 2;
    public const int MaxScalePercent = 15;
    public const int MaxVisibleLines = 15;

    public const int MinBlurStrength = 1;
    public const int MaxBlurStrength = 12;
    public const int DefaultBlurStrength = 4;
    // Сколько строк от активной размываются: дальше они всё равно вне видимой области, а эффект на каждой строке дорог.
    public const int BlurReachLines = 12;

    public const int MinNowPlayingFontSize = 14;
    public const int MaxNowPlayingFontSize = 32;
    public const int DefaultNowPlayingFontSize = 18;

    // Неизвестные и прежние значения читаются как мягкое свечение, как и раньше.
    public static bool HasGlow(string? effect) => effect is not ("None" or "Pill" or "Bar");

    public static bool HasPill(string? effect) => effect is "GlowPill" or "Pill";

    public static bool HasBar(string? effect) => effect == "Bar";

    // Прозрачность падает на столько с каждой строкой удаления от активной: 0 % — 0,04, 50 % — 0,10, 100 % — 0,16.
    public static double FadeStep(int strength) => 0.04 + 0.12 * Math.Clamp(strength, 0, 100) / 100.0;

    // Размытие растёт с удалением от активной строки и на четвёртой строке достигает заданной силы (радиус в px).
    public static double BlurRadius(int strength, int distance) =>
        Math.Clamp(strength, MinBlurStrength, MaxBlurStrength) * Math.Min(Math.Max(distance, 0), 4) / 4.0;

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
