using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Lumisense;

// Считает пики амплитуды (0..1) для WaveformView — всегда BucketCount значений независимо
// от длительности, чтобы полоса была одной ширины и для трёхминутного трека, и для часового.
public static class WaveformGenerator
{
    // 300 делений: детальнее видимого на типичной ширине окна, с запасом для широкого/квадратного вида,
    // но достаточно мало, чтобы расчёт и отрисовка (WaveformView.OnRender) были дёшевы.
    public const int BucketCount = 300;

    // Сэмплов на один грубый пик до сжатия к BucketCount: фиксированный размер, а не от TotalTime —
    // для mp3 (VBR) длительность лишь оценка, и её погрешность сдвинула бы пики к концу трека.
    private const int ChunkSamples = 1024;

    // Не блокирует вызывающий поток — вызывается из UI-потока (MainWindow), а сам расчёт читает
    // и декодирует файл целиком, для длинных FLAC/WAV это не мгновенно.
    public static Task<float[]?> GenerateAsync(string filePath, CancellationToken ct = default)
        => Task.Run(() => Generate(filePath, ct), ct);

    private static float[]? Generate(string filePath, CancellationToken ct)
    {
        try
        {
            // Отдельный AudioFileReader: играющий (MainWindow._audioFile) уже читает цепочка вывода, и параллельное
            // чтение из него сдвинуло бы позицию воспроизведения.
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

            // Даунсемплинг до BucketCount: берём максимум в группе, а не среднее, чтобы короткие громкие
            // всплески (удар барабана) не размазывались.
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

            // Нормализация к максимальному пику, иначе тихо смастеренные треки выглядели бы почти плоской линией.
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
            // Отмена пробрасывается как есть — Task.Run сам пометит задачу отменённой (в отличие от прочих ошибок ниже).
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
