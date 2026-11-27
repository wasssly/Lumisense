using System.Globalization;
using System.IO;

namespace Lumisense;

// Тексты для подписи состояния трека в режимах, отличных от «Состояние» (AppSettings.TrackStateBadgeMode).
// Единицы (kbps, kHz, MB) одинаковы во всех языках, поэтому в LocalizationService не попадают.
internal static class TrackBadgeFormatter
{
    public static string? Extension(string? path)
    {
        string extension = Path.GetExtension(path ?? string.Empty).TrimStart('.');
        return extension.Length == 0 ? null : extension.ToUpperInvariant();
    }

    public static string? Bitrate(int bitrateKbps) =>
        bitrateKbps > 0 ? string.Create(CultureInfo.InvariantCulture, $"{bitrateKbps} kbps") : null;

    public static string? SampleRate(int sampleRateHz) =>
        sampleRateHz > 0 ? string.Create(CultureInfo.InvariantCulture, $"{sampleRateHz / 1000.0:0.#} kHz") : null;

    public static string? Quality(int bitrateKbps, int sampleRateHz) => Join(Bitrate(bitrateKbps), SampleRate(sampleRateHz));

    // Склеивает непустые части через « · »; null — если подходящих частей нет.
    public static string? Join(params string?[] parts)
    {
        string[] present = parts.Where(part => !string.IsNullOrEmpty(part)).Select(part => part!).ToArray();
        return present.Length == 0 ? null : string.Join(" · ", present);
    }

    public static string? FileSize(long bytes)
    {
        if (bytes <= 0) return null;

        double megabytes = bytes / (1024.0 * 1024.0);
        return megabytes >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{megabytes:0.#} MB")
            : string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0} KB");
    }
}
