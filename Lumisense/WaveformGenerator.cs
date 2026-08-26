using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace AudioPlayer;

// Считает данные для WaveformView (см. AppSettings.ProgressBarStyle == "Waveform") — массив
// нормализованных (0..1) пиков амплитуды на весь трек, фиксированной длины BucketCount вне
// зависимости от длительности файла: полоса воспроизведения одной и той же ширины что для
// трёхминутного трека, что для часового.
public static class WaveformGenerator
{
    // 300 делений — заметно детальнее, чем реально видно на типичной ширине окна плеера
    // (лишние тонкие бары просто сольются визуально), но с запасом на случай широкого окна/
    // квадратного вида, и всё ещё достаточно мало, чтобы отрисовка (WaveformView.OnRender) и
    // сам расчёт были дёшевы.
    public const int BucketCount = 300;

    // Сэмплов на один "грубый" пик до финального даунсемплинга к BucketCount (см. Generate) —
    // маленький фиксированный размер, не зависящий от заранее известной длины трека: для mp3
    // (особенно VBR) AudioFileReader.TotalTime — лишь оценка, и если считать размер грубого
    // деления от неё заранее, накопленная погрешность к концу длинного трека может заметно
    // сдвинуть пики. Читаем реальный поток до конца небольшими одинаковыми кусками и только
    // потом сжимаем результат до нужного количества делений — так расчёт не зависит от того,
    // насколько точна заявленная длительность.
    private const int ChunkSamples = 1024;

    // Не блокирует вызывающий поток — вызывается из UI-потока (MainWindow), а сам расчёт читает
    // и декодирует файл целиком, для длинных FLAC/WAV это не мгновенно.
    public static Task<float[]?> GenerateAsync(string filePath, CancellationToken ct = default)
        => Task.Run(() => Generate(filePath, ct), ct);

    private static float[]? Generate(string filePath, CancellationToken ct)
    {
        try
        {
            // Отдельный, независимый от воспроизведения экземпляр AudioFileReader — тот, что
            // реально играет (MainWindow._audioFile), в это время уже потребляется цепочкой
            // эквалайзер → fade → устройство вывода, читать из него параллельно ещё раз для
            // пиков нельзя, это сдвинуло бы саму позицию воспроизведения.
            using var reader = new AudioFileReader(filePath);

            int channels = System.Math.Max(reader.WaveFormat.Channels, 1);
            var buffer = new float[ChunkSamples * channels];
            var chunkPeaks = new System.Collections.Generic.List<float>();

            int read;
            while ((read = reader.Read(buffer.AsSpan())) > 0)
            {
                ct.ThrowIfCancellationRequested();

                float peak = 0f;
                for (int i = 0; i < read; i++)
                {
                    float abs = System.Math.Abs(buffer[i]);
                    if (abs > peak) peak = abs;
                }
                chunkPeaks.Add(peak);
            }

            if (chunkPeaks.Count == 0) return null;

            // Даунсемплинг грубых пиков до ровно BucketCount элементов — группируем подряд
            // идущие грубые пики и берём максимум внутри каждой группы (а не среднее), чтобы
            // короткие громкие всплески (например, удар барабана) не "размазывались" и
            // оставались заметны на полосе, как и на настоящих waveform-полосах вроде SoundCloud.
            var result = new float[BucketCount];
            for (int b = 0; b < BucketCount; b++)
            {
                int startIdx = (int)((long)b * chunkPeaks.Count / BucketCount);
                int endIdx = (int)((long)(b + 1) * chunkPeaks.Count / BucketCount);
                if (endIdx <= startIdx) endIdx = startIdx + 1;
                endIdx = System.Math.Min(endIdx, chunkPeaks.Count);

                float maxInGroup = 0f;
                for (int i = startIdx; i < endIdx; i++)
                    if (chunkPeaks[i] > maxInGroup) maxInGroup = chunkPeaks[i];

                result[b] = maxInGroup;
            }

            // Нормализация к максимальному пику трека — иначе тихо смастеренные треки выглядели
            // бы как почти плоская линия почти по всей длине, хотя визуально должны занимать
            // всю высоту полосы целиком, как и у любого настоящего waveform-плеера.
            float overallMax = result.Max();
            if (overallMax > 0.0001f)
            {
                for (int i = 0; i < result.Length; i++)
                    result[i] = System.Math.Min(result[i] / overallMax, 1f);
            }

            return result;
        }
        catch (System.OperationCanceledException)
        {
            // Пробрасываем дальше как есть — Task.Run сам корректно пометит задачу отменённой
            // (см. GenerateAsync/MainWindow.EnsureWaveformForCurrentTrackAsync), в отличие от
            // "проглатывания" ниже для остальных ошибок.
            throw;
        }
        catch (System.Exception ex)
        {
            // Повреждённый/недоступный файл — WaveformView по-прежнему показывает заглушку,
            // но причина больше не теряется и доступна в логах для диагностики.
            Logger.Warn($"Не удалось построить waveform для {filePath}: {ex.Message}");
            return null;
        }
    }
}
