namespace Lumisense;

// Читает ReplayGain из тегов файла — REPLAYGAIN_TRACK_GAIN/PEAK, TXXX-фреймы у ID3v2, те же
// поля у Vorbis Comments/APE и т.д. TagLibSharp сам разбирается, откуда именно их читать, в
// зависимости от формата файла — этому классу достаточно попросить Tag.ReplayGainTrackGain.
// См. AppSettings.ReplayGainEnabled, MainWindow._replayGainFactor/ComputeAudioFileVolume.
//
// Только Track Gain, без Album Gain — сама библиотека плейлиста в этом плеере не группирует
// треки по альбомам настолько строго, чтобы "выравнивание громкости внутри альбома" имело
// однозначный смысл (папка плейлиста — не то же самое, что альбом), а Track Gain одинаково
// применим в любом контексте воспроизведения.
public static class ReplayGainReader
{
    // Читает ReplayGain трека и сразу переводит его в линейный множитель громкости — то, на
    // что домножается AudioFileReader.Volume (см. MainWindow.ComputeAudioFileVolume). Тега нет
    // (double.NaN — так TagLibSharp помечает отсутствующее значение) или файл не открылся —
    // 1.0, то есть без изменений: тихая деградация, а не ошибка воспроизведения.
    public static double GetTrackGainLinear(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            return GetTrackGainLinear(file.Tag);
        }
        catch
        {
            return 1.0;
        }
    }

    internal static double GetTrackGainLinear(TagLib.Tag tag)
    {
        try
        {
            double gainDb = tag.ReplayGainTrackGain;
            if (double.IsNaN(gainDb)) return 1.0;

            double linear = System.Math.Pow(10.0, gainDb / 20.0);

            // Пиковый лимитер — стандартная часть самой спецификации ReplayGain, а не что-то
            // добавленное поверх: без него трек с завышенным gain (не так уж редко для
            // самостоятельно посчитанных тегов) мог бы клиппинговать на самых громких местах
            // сильнее, чем вообще без ReplayGain. Peak — уже линейное значение (доля от полной
            // шкалы, обычно ~0.1–1.1, не дБ), поэтому единственная защита здесь — не позволить
            // linear * peak превысить 1.0.
            double peak = tag.ReplayGainTrackPeak;
            if (!double.IsNaN(peak) && peak > 0)
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
}
