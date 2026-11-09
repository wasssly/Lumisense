using NAudio.Wave;
using static Lumisense.BackgroundTask;

namespace Lumisense;

// Фоновое измерение громкости треков без ReplayGain-тегов; результат кэшируется и применяется как ReplayGain.
public partial class MainWindow
{
    private CancellationTokenSource? _loudnessScanCts;

    private void StartLoudnessScanIfNeeded(string path)
    {
        Interlocked.Exchange(ref _loudnessScanCts, null)?.Cancel();
        if (!_settings.ReplayGainEnabled || !_settings.LoudnessScanEnabled) return;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _loudnessScanCts = cts;
        FireAndForget(ScanLoudnessAsync(path, cts), nameof(ScanLoudnessAsync));
    }

    private async Task ScanLoudnessAsync(string path, CancellationTokenSource cts)
    {
        try
        {
            bool needed = await Task.Run(() => !LoudnessCache.TryGetLufs(path, out _) && !ReplayGainReader.HasGainTag(path), cts.Token);
            if (!needed) return;

            double lufs = await Task.Run(() =>
            {
                // Фоновая работа не должна отнимать процессор у воспроизведения.
                var thread = Thread.CurrentThread;
                var previousPriority = thread.Priority;
                thread.Priority = ThreadPriority.BelowNormal;
                try
                {
                    using var reader = new AudioFileReader(path);
                    return LoudnessAnalyzer.MeasureIntegratedLufs(reader, cts.Token);
                }
                finally
                {
                    thread.Priority = previousPriority;
                }
            }, cts.Token);

            if (double.IsInfinity(lufs) || double.IsNaN(lufs)) return;

            LoudnessCache.Store(path, lufs);
            if (!_isExiting && PathEquals(path, _currentTrackPath))
                RefreshReplayGain();
        }
        catch (OperationCanceledException)
        {
            // Трек сменился или плеер закрывается.
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось измерить громкость файла {path}: {ex.Message}");
        }
        finally
        {
            Interlocked.CompareExchange(ref _loudnessScanCts, null, cts);
            cts.Dispose();
        }
    }
}
