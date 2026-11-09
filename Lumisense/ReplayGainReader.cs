namespace Lumisense;

// Читает ReplayGain через ATL.Track.AdditionalFields (нестандартное поле тега, без выделенного
// свойства) — пробуем несколько вариантов написания ключа. Поддержаны Track и Album Gain, предусилитель и Opus R128.
public static class ReplayGainReader
{
    private static readonly string[] GainKeys = { "replaygain_track_gain", "REPLAYGAIN_TRACK_GAIN" };
    private static readonly string[] PeakKeys = { "replaygain_track_peak", "REPLAYGAIN_TRACK_PEAK" };
    private static readonly string[] AlbumGainKeys = { "replaygain_album_gain", "REPLAYGAIN_ALBUM_GAIN" };
    private static readonly string[] AlbumPeakKeys = { "replaygain_album_peak", "REPLAYGAIN_ALBUM_PEAK" };
    private static readonly string[] R128TrackKeys = { "R128_TRACK_GAIN", "r128_track_gain" };
    private static readonly string[] R128AlbumKeys = { "R128_ALBUM_GAIN", "r128_album_gain" };

    // Opus хранит R128-усиление в Q7.8 относительно -23 LUFS, а ReplayGain 2.0 берёт опорные -18 LUFS.
    private const double R128ToReplayGainOffsetDb = 5.0;
    public const double MaxPreampDb = 12.0;

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
            // Файл не открылся или тег повреждён: ReplayGain просто не применяется, воспроизведение не страдает.
            return 1.0;
        }
    }

    public static double GetGainLinear(string filePath, bool preferAlbum, double preampDb)
    {
        try
        {
            return GetGainLinear(new ATL.Track(filePath), preferAlbum, preampDb);
        }
        catch
        {
            return 1.0;
        }
    }

    // Режим «альбом» берёт альбомное усиление, иначе трековое; пик-лимит считается после предусилителя, чтобы не было клиппинга.
    internal static double GetGainLinear(ATL.Track track, bool preferAlbum, double preampDb)
    {
        try
        {
            double gainDb = 0;
            double peak = 0;
            bool found = false;

            if (preferAlbum && TryReadDb(track, AlbumGainKeys, out gainDb))
            {
                found = true;
                TryReadDb(track, AlbumPeakKeys, out peak);
            }
            else if (preferAlbum && TryReadDb(track, R128AlbumKeys, out double r128Album))
            {
                gainDb = r128Album / 256.0 + R128ToReplayGainOffsetDb;
                found = true;
            }
            else if (TryReadDb(track, GainKeys, out gainDb))
            {
                found = true;
                TryReadDb(track, PeakKeys, out peak);
            }
            else if (TryReadDb(track, R128TrackKeys, out double r128Track))
            {
                gainDb = r128Track / 256.0 + R128ToReplayGainOffsetDb;
                found = true;
            }

            if (!found) return 1.0;

            double linear = System.Math.Pow(10.0, (gainDb + System.Math.Clamp(preampDb, -MaxPreampDb, MaxPreampDb)) / 20.0);
            if (peak > 0)
                linear = System.Math.Min(linear, 1.0 / peak);

            return linear;
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
