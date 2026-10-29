using System.Globalization;
using System.Windows.Media;

namespace Lumisense;

// Цвет фона уведомления о смене трека (AppSettings.TrackChangeToastColorMode/Hex/Strength): цвет подмешивается к фону по теме,
// а светлота получившегося фона решает, каким будет текст, чтобы он читался на любой обложке.
internal static class ToastColorCalculator
{
    // strength 0 — чистый фон по теме, 1 — целиком выбранный цвет; без цвета (tint == null) фон остаётся по теме.
    public static Color Blend(Color baseColor, Color? tint, double strength)
    {
        if (tint is not { } color) return baseColor;

        double amount = double.IsFinite(strength) ? Math.Clamp(strength, 0.0, 1.0) : 0.0;
        return Color.FromRgb(
            Mix(baseColor.R, color.R, amount),
            Mix(baseColor.G, color.G, amount),
            Mix(baseColor.B, color.B, amount));
    }

    // Тёмный текст нужен, когда он контрастнее белого (WCAG): порог по яркости оставлял белый текст на средних тонах с контрастом ~2,8:1.
    public static bool IsLight(Color color)
    {
        double luminance = 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        double whiteContrast = 1.05 / (luminance + 0.05);
        double darkContrast = (luminance + 0.05) / (DarkTextLuminance + 0.05);
        return darkContrast >= whiteContrast;
    }

    // Относительная яркость цвета текста #1A1A1A, который окно уведомления берёт на светлом фоне.
    private const double DarkTextLuminance = 0.0103;

    private static double Linear(byte channel)
    {
        double value = channel / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    public static Color? TryParse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;

        string value = hex.Trim().TrimStart('#');
        if (value.Length != 6 || !int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            return null;

        return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    private static byte Mix(byte from, byte to, double amount) => (byte)Math.Round(from + (to - from) * amount);
}
