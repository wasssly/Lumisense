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

    // Воспринимаемая яркость (Rec. 601): светлому фону нужен тёмный текст, тёмному — светлый.
    public static bool IsLight(Color color) =>
        (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0 > 0.6;

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
