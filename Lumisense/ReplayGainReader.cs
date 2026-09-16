namespace Lumisense;

// Читает ReplayGain через ATL.Track.AdditionalFields (нестандартное поле тега, без выделенного
// свойства) — пробуем несколько вариантов написания ключа. Только Track Gain, без Album Gain.
public static class ReplayGainReader
{
    private static readonly string[] GainKeys = { "replaygain_track_gain", "REPLAYGAIN_TRACK_GAIN" };
    private static readonly string[] PeakKeys = { "replaygain_track_peak", "REPLAYGAIN_TRACK_PEAK" };

    // Переводит ReplayGain в линейный множитель для AudioFileReader.Volume. Тега нет или файл
    // не открылся — 1.0, тихая деградация, а не ошибка воспроизведения.
    public static double GetTrackGainLinear(string filePath)
    {
        try
        {
            var track = new ATL.Track(filePath);
            return GetTrackGainLinear(track);
        }
        catch
        {
            return 1.0;
        }
    }

    internal static double GetTrackGainLinear(ATL.Track track)
    {
        try
        {
            if (!TryReadDb(track, GainKeys, out double gainDb)) return 1.0;

            double linear = System.Math.Pow(10.0, gainDb / 20.0);

            // Пиковый лимитер — часть спецификации ReplayGain: без него завышенный gain
            // мог бы клиппинговать сильнее, чем вообще без него. Peak уже линеен (не в дБ).
            if (TryReadDb(track, PeakKeys, out double peak) && peak > 0)
                linear = System.Math.Min(linear, 1.0 / peak);

            return linear;
        }
        catch
        {
            // Нет тега, повреждён, формат не поддерживает ReplayGain — считаем, что тега
            // просто нет, а не роняем воспроизведение.
            return 1.0;
        }
    }

    // Значения ReplayGain в тегах обычно хранятся как текст вида "-3.51 dB" — суффикс
    // отбрасываем перед разбором, если он есть.
    private static bool TryReadDb(ATL.Track track, string[] keys, out double value)
    {
        foreach (string key in keys)
        {
            if (track.AdditionalFields.TryGetValue(key, out string? raw) && !string.IsNullOrWhiteSpace(raw))
            {
                string trimmed = raw.Replace("dB", "", StringComparison.OrdinalIgnoreCase).Trim();
                if (double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out value))
                    return true;
            }
        }

        value = 0;
        return false;
    }
}
