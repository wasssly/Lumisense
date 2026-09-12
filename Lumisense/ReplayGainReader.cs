namespace Lumisense;

// Читает ReplayGain из тегов файла — REPLAYGAIN_TRACK_GAIN/PEAK, TXXX-фреймы у ID3v2, те же
// поля у Vorbis Comments/APE и т.д. ATL.NET читает их как обычные нестандартные поля тега
// (Track.AdditionalFields), без выделенного свойства — поэтому здесь пробуем несколько
// вариантов написания ключа, они отличаются по регистру и формату в зависимости от типа тега.
// См. AppSettings.ReplayGainEnabled, MainWindow._replayGainFactor/ComputeAudioFileVolume.
//
// Только Track Gain, без Album Gain — сама библиотека плейлиста в этом плеере не группирует
// треки по альбомам настолько строго, чтобы "выравнивание громкости внутри альбома" имело
// однозначный смысл (папка плейлиста — не то же самое, что альбом), а Track Gain одинаково
// применим в любом контексте воспроизведения.
public static class ReplayGainReader
{
    private static readonly string[] GainKeys = { "replaygain_track_gain", "REPLAYGAIN_TRACK_GAIN" };
    private static readonly string[] PeakKeys = { "replaygain_track_peak", "REPLAYGAIN_TRACK_PEAK" };

    // Читает ReplayGain трека и сразу переводит его в линейный множитель громкости — то, на
    // что домножается AudioFileReader.Volume (см. MainWindow.ComputeAudioFileVolume). Тега нет
    // или файл не открылся — 1.0, то есть без изменений: тихая деградация, а не ошибка
    // воспроизведения.
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

            // Пиковый лимитер — стандартная часть самой спецификации ReplayGain, а не что-то
            // добавленное поверх: без него трек с завышенным gain (не так уж редко для
            // самостоятельно посчитанных тегов) мог бы клиппинговать на самых громких местах
            // сильнее, чем вообще без ReplayGain. Peak — уже линейное значение (доля от полной
            // шкалы, обычно ~0.1–1.1, не дБ), поэтому единственная защита здесь — не позволить
            // linear * peak превысить 1.0.
            if (TryReadDb(track, PeakKeys, out double peak) && peak > 0)
                linear = System.Math.Min(linear, 1.0 / peak);

            return linear;
        }
        catch
        {
            // Файл без тегов ReplayGain вообще, повреждённые/нечитаемые теги, формат, которому
            // ReplayGain не свойственен, и т.п. — тихо считаем, что тега просто нет, а не роняем
            // воспроизведение из-за необязательной, чисто косметической по смыслу функции.
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
