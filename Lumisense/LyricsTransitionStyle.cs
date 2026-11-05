using System.Windows.Media.Animation;

namespace Lumisense;

// Переход между строками синхронного текста (AppSettings.SyncedLyricsTransition): Smooth, AppleMusic (пружина и волна),
// Soft (долгое растворение), Quick (короткие переходы), None (без анимации).
public static class LyricsTransitionStyle
{
    public const string Smooth = "Smooth";
    public const string AppleMusic = "AppleMusic";
    public const string Soft = "Soft";
    public const string Quick = "Quick";
    public const string None = "None";

    public static readonly string[] All = [Smooth, AppleMusic, Soft, Quick, None];

    public static string Normalize(string? style) =>
        All.FirstOrDefault(known => known == style) ?? Smooth;

    // Прокрутка к активной строке; baseMs — длительность прежнего вида в данном окне (в панели и Now Playing разная).
    public static double ScrollMs(string? style, double baseMs) => Normalize(style) switch
    {
        AppleMusic => baseMs * 1.45,
        Soft => baseMs * 1.8,
        Quick => baseMs * 0.45,
        None => 0,
        _ => baseMs,
    };

    // Где по высоте стоит активная строка: 0,5 — по центру, у Apple Music — выше, так видно больше следующих строк.
    public static double AnchorFraction(string? style) => Normalize(style) == AppleMusic ? 0.36 : 0.5;

    public static IEasingFunction ScrollEase(string? style) => Normalize(style) switch
    {
        AppleMusic => new ExponentialEase { Exponent = 4.5, EasingMode = EasingMode.EaseOut },
        Soft => new SineEase { EasingMode = EasingMode.EaseInOut },
        Quick => new CubicEase { EasingMode = EasingMode.EaseOut },
        _ => new CubicEase { EasingMode = EasingMode.EaseInOut },
    };

    // Смена вида отдельной строки: цвет, прозрачность, размытие, масштаб.
    public static double LineMs(string? style) => Normalize(style) switch
    {
        AppleMusic => 520,
        Soft => 650,
        Quick => 160,
        None => 0,
        _ => 320,
    };

    public static IEasingFunction LineEase(string? style) => Normalize(style) switch
    {
        AppleMusic => new QuinticEase { EasingMode = EasingMode.EaseOut },
        Soft => new SineEase { EasingMode = EasingMode.EaseInOut },
        _ => new CubicEase { EasingMode = EasingMode.EaseOut },
    };

    // Задержка на каждую строку ниже активной (волна); у остальных видов строки меняются одновременно.
    public static double StaggerMs(string? style) => Normalize(style) == AppleMusic ? 32 : 0;

    public const int MaxStaggerLines = 6;

    public static TimeSpan LineDelay(string? style, int linesBelowActive) =>
        TimeSpan.FromMilliseconds(StaggerMs(style) * Math.Clamp(linesBelowActive, 0, MaxStaggerLines));

    public static string DisplayName(string? style) => Normalize(style) switch
    {
        AppleMusic => "Как в Apple Music",
        Soft => "Мягкое растворение",
        Quick => "Быстрый",
        None => "Без анимации",
        _ => "Плавный (по умолчанию)",
    };
}
