namespace Lumisense;

// Пресеты размера мини-плеера (AppSettings.MiniPlayerSizePreset). Раскладка окна рассчитана на базовые 200x88 (версия 1.22.0);
// остальные размеры получаются масштабом всей раскладки, чтобы ничего не пересчитывать по месту.
internal static class MiniPlayerSizePreset
{
    public const string Compact = "Compact";
    public const string Current = "Current";
    public const string Classic = "Classic";

    public const double BaseWidth = 200;

    public static bool IsKnown(string? preset) => preset is Compact or Current or Classic;

    // Classic даёт 220x97 при размере 220x96 в версии 1.21.0, Compact — 180x79.
    public static double ScaleOf(string? preset) => preset switch
    {
        Compact => 0.9,
        Classic => 1.1,
        _ => 1.0,
    };
}
