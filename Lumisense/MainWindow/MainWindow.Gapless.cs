using System.IO;
using static Lumisense.BackgroundTask;

namespace Lumisense;

// Гэплесс: следующий трек собирается заранее и подставляется на конце текущего без остановки выхода.
// Если условия не сходятся (повтор, ручной выбор, смена очереди), переход идёт обычным путём через PlaybackStopped.
public partial class MainWindow
{
    private const double GaplessArmWindowSeconds = 7;

    // Заготовка нужна и для гэплесса, и для кроссфейда.
    private bool GaplessOrCrossfadeWanted => _settings.GaplessEnabled || _settings.CrossfadeSeconds > 0;

    // Остаток трека в кадрах выходного формата (после ресемплинга, если он есть) с учётом скорости.
    private Func<long> RemainingFramesOf(NAudio.Wave.AudioFileReader reader, int outputSampleRate) =>
        () => (long)((reader.TotalTime - reader.CurrentTime).TotalSeconds / Math.Max(0.25, _runtimePlaybackRate) * outputSampleRate);

    // Приводит следующий трек к формату выхода (моно↔стерео, частота); null, если число каналов не поддержано.
    private static NAudio.Wave.ISampleProvider? ConvertToOutputFormat(NAudio.Wave.ISampleProvider source, NAudio.Wave.WaveFormat target)
    {
        var current = source;
        if (current.WaveFormat.Channels != target.Channels)
        {
            if (current.WaveFormat.Channels == 1 && target.Channels == 2)
                current = new NAudio.Wave.SampleProviders.MonoToStereoSampleProvider(current);
            else if (current.WaveFormat.Channels == 2 && target.Channels == 1)
                current = new NAudio.Wave.SampleProviders.StereoToMonoSampleProvider(current);
            else
                return null;
        }

        if (current.WaveFormat.SampleRate != target.SampleRate)
            current = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(current, target.SampleRate);

        return current;
    }

    // Длина перекрытия в кадрах для пары треков: не длиннее половины любого из них и 0 для треков одного альбома.
    private int ComputeCrossfadeFrames(PreparedTrack next)
    {
        int seconds = _settings.CrossfadeSeconds;
        if (seconds <= 0 || _audioFile is null) return 0;
        if (_settings.CrossfadeSkipSameAlbum && !string.IsNullOrWhiteSpace(_currentTrackAlbum)
            && string.Equals(_currentTrackAlbum, next.Album, StringComparison.OrdinalIgnoreCase))
            return 0;

        double limit = Math.Min(_audioFile.TotalTime.TotalSeconds, next.AudioFile.TotalTime.TotalSeconds) / 2;
        return (int)(Math.Min(seconds, limit) * (_gapless?.WaveFormat.SampleRate ?? _audioFile.WaveFormat.SampleRate));
    }

    private NAudio.Wave.AudioFileReader? _gaplessPreviousFile;

    private void DisposeGaplessPreviousFile()
    {
        var file = _gaplessPreviousFile;
        _gaplessPreviousFile = null;
        try
        {
            file?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось освободить AudioFileReader предыдущего трека после перекрытия", ex);
        }
    }

    // Приходит с audio thread, когда перекрытие закончилось и прежний трек больше не читается.
    private void OnGaplessPreviousFinished()
    {
        if (_isExiting || Dispatcher.HasShutdownStarted) return;

        try
        {
            Dispatcher.BeginInvoke(DisposeGaplessPreviousFile);
        }
        catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            // Dispatcher закрывается одновременно с audio callback.
        }
    }

    private sealed class ArmedGaplessTrack
    {
        public required string Path { get; init; }
        public required string PreviousPath { get; init; }
        public required PreparedTrack Prepared { get; init; }
        public required ChainParts Chain { get; init; }
        public required bool FromQueue { get; init; }
        public required bool Shuffle { get; init; }
        public required RepeatMode Repeat { get; init; }
        public required int CrossfadeFrames { get; init; }
    }

    private ArmedGaplessTrack? _gaplessArmed;
    private CancellationTokenSource? _gaplessCts;
    private bool _gaplessArming;
    private string? _gaplessRejectedPath;

    // Вызывается с тиком прогресса, пока трек играет: проверяет готовую заготовку и запускает новую, когда трек близок к концу.
    private void UpdateGaplessState()
    {
        if (_gaplessArmed is { } armed)
        {
            if (!IsGaplessArmStillValid(armed)) DisarmGapless();
            return;
        }

        if (!GaplessOrCrossfadeWanted || _gapless is null || _audioFile is null || _gaplessArming || !_isPlaying) return;
        if (_audioFile.TotalTime - _audioFile.CurrentTime > TimeSpan.FromSeconds(Math.Max(GaplessArmWindowSeconds, _settings.CrossfadeSeconds + 5))) return;

        ArmGapless();
    }

    // Читает теги и обложки соседей заранее: ручное переключение тогда не ждёт диск. При шафле историю не трогаем.
    private void SchedulePrefetchNeighbors()
    {
        if (_isExiting || Dispatcher.HasShutdownStarted) return;

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            try
            {
                string? current = _currentTrackPath;
                if (string.IsNullOrEmpty(current)) return;

                var paths = new List<string?> { _playbackQueue.PeekNext() };
                if (!_shuffleSession.IsEnabled)
                {
                    paths.Add(ComputeNextTrackPath(current));
                    paths.Add(ComputePreviousTrackPath(current));
                }

                foreach (var path in paths.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!PathEquals(path, current) && File.Exists(path)) TrackTagCache.Prefetch(path!);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Не удалось запланировать предзагрузку соседних треков: {ex.Message}");
            }
        });
    }

    private bool IsGaplessArmStillValid(ArmedGaplessTrack armed)
    {
        if (!GaplessOrCrossfadeWanted || _repeatMode == RepeatMode.One || _repeatMode != armed.Repeat
            || _shuffleSession.IsEnabled != armed.Shuffle || !PathEquals(_currentTrackPath, armed.PreviousPath))
            return false;

        _playbackQueue.PruneMissing();
        if (armed.FromQueue) return PathEquals(_playbackQueue.PeekNext(), armed.Path);
        if (_playbackQueue.Count > 0) return false;
        if (!armed.Shuffle && !PathEquals(ComputeNextTrackPath(_currentTrackPath), armed.Path)) return false;
        return File.Exists(armed.Path);
    }

    private bool IsLastTrackOfRound(string current)
    {
        var active = FlattenActive();
        int position = active.IndexOf(current);
        return _shuffleSession.IsEnabled ? _shuffleSession.HasPlayedAll(active) : position < 0 || position == active.Count - 1;
    }

    private void ArmGapless()
    {
        string? current = _currentTrackPath;
        if (string.IsNullOrEmpty(current) || _repeatMode == RepeatMode.One) return;

        _playbackQueue.PruneMissing();
        bool fromQueue = _playbackQueue.Count > 0;
        string? next;
        if (fromQueue)
        {
            next = _playbackQueue.PeekNext();
        }
        else
        {
            if (_repeatMode == RepeatMode.Off && IsLastTrackOfRound(current)) return;
            next = ComputeNextTrackPath(current);
        }

        if (next is null || PathEquals(next, current) || PathEquals(next, _gaplessRejectedPath) || !File.Exists(next)) return;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _gaplessCts = cts;
        _gaplessArming = true;
        int generation = _audioPlaybackCoordinator.CurrentGeneration;
        FireAndForget(ArmGaplessAsync(next, current, fromQueue, generation, cts), nameof(ArmGaplessAsync));
    }

    private async Task ArmGaplessAsync(string next, string current, bool fromQueue, int generation, CancellationTokenSource cts)
    {
        PreparedTrack? prepared = null;
        try
        {
            prepared = await StartTrackPreparation(next, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (_isExiting || generation != _audioPlaybackCoordinator.CurrentGeneration || !PathEquals(current, _currentTrackPath)
                || _gapless is null || _audioFile is null || !GaplessOrCrossfadeWanted)
                return;

            var outputFormat = _gapless.WaveFormat;
            var chain = BuildChain(prepared.Equalizer);
            var tail = ConvertToOutputFormat(chain.Meter, outputFormat);
            if (tail is null)
            {
                // Число каналов, которое не умеем привести к выходу (не моно и не стерео): переход пойдёт обычным путём.
                _gaplessRejectedPath = next;
                return;
            }

            int crossfadeFrames = ComputeCrossfadeFrames(prepared);
            var armed = new ArmedGaplessTrack
            {
                CrossfadeFrames = crossfadeFrames,
                Path = next,
                PreviousPath = current,
                Prepared = prepared,
                Chain = chain,
                FromQueue = fromQueue,
                Shuffle = _shuffleSession.IsEnabled,
                Repeat = _repeatMode
            };
            if (!_gapless.Arm(tail, armed, crossfadeFrames, RemainingFramesOf(prepared.AudioFile, outputFormat.SampleRate))) return;

            prepared = null;
            _gaplessArmed = armed;
            if (_tempoProvider is not null) _tempoProvider.TailFadeEnabled = false;
        }
        catch (OperationCanceledException)
        {
            // Заготовку отменили: ручной выбор, остановка или закрытие.
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось подготовить следующий трек для перехода без паузы: {ex.Message}");
            _gaplessRejectedPath = next;
        }
        finally
        {
            prepared?.Dispose();
            if (ReferenceEquals(_gaplessCts, cts)) _gaplessCts = null;
            cts.Dispose();
            _gaplessArming = false;
        }
    }

    // Если audio thread уже переключился, заготовка принадлежит OnGaplessSwitched и здесь не освобождается.
    private void DisarmGapless()
    {
        var cts = _gaplessCts;
        _gaplessCts = null;
        cts?.Cancel();

        if (_gapless?.Disarm() is ArmedGaplessTrack pending)
            pending.Prepared.Dispose();

        _gaplessArmed = null;
        if (_tempoProvider is not null) _tempoProvider.TailFadeEnabled = true;
    }

    // Приходит с audio thread в момент подстановки следующего трека.
    private void OnGaplessSwitched(object token)
    {
        if (token is not ArmedGaplessTrack armed || _isExiting || Dispatcher.HasShutdownStarted) return;

        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    AdoptGaplessTrack(armed);
                }
                catch (Exception ex)
                {
                    Logger.Error("Ошибка перехода на следующий трек без паузы", ex);
                }
            });
        }
        catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            // Dispatcher закрывается одновременно с audio callback.
        }
    }

    // Выход уже играет следующий трек: обновляем всё остальное (поля цепочки, подписи, обложку, очередь, текст) так же, как после обычной загрузки.
    private void AdoptGaplessTrack(ArmedGaplessTrack armed)
    {
        if (_isExiting) return;

        if (ReferenceEquals(_gaplessArmed, armed)) _gaplessArmed = null;
        if (_gapless is null || _outputDevice is null)
        {
            // Воспроизведение уже остановлено (StopPlayback): выход закрыт, подготовленный трек больше никому не нужен.
            armed.Prepared.Dispose();
            return;
        }

        var previousAudioFile = _audioFile;
        AttachPreparedTrack(armed.Prepared);
        AssignChain(armed.Chain);

        if (armed.FromQueue && PathEquals(_playbackQueue.PeekNext(), armed.Path))
            _playbackQueue.PopNext();

        ApplyLoadedTrack(armed.Prepared, armed.Path, AlbumArtTransitionDirection.Next, TrackChangeOrigin.Automatic, preserveShuffleSession: true);
        ApplyInitialPosition(null, seek: false);

        // Заготовка собрана раньше: подтягиваем настройки, которые могли измениться за это время.
        ApplyEqualizerGainsFromSettings();
        ApplyVolumeToChain(VolumeSlider.Value);
        ApplyChannelMode();
        ApplyStereoImage();
        ApplyLoudnessCompensation();
        ApplyLimiter();

        if (_isLyricsPanelActive)
            FireAndForget(LoadMainWindowLyricsAsync(armed.Path), "LoadMainWindowLyricsAsync");
        if (_shuffleSession.IsEnabled && _playlistRestoreCompleted)
            PersistPlaybackAndPlaylistState(asyncSave: true);

        StartLoudnessScanIfNeeded(armed.Path);
        SchedulePrefetchNeighbors();
        SetTrackUserState(TrackUserState.Playing);
        ShowTrackChangeToast(TrackChangeOrigin.Automatic, true);
        ScrollPlaylistToCurrentTrack();
        _gaplessRejectedPath = null;

        if (armed.CrossfadeFrames > 0 && previousAudioFile is not null)
        {
            // Прежний трек ещё звучит в перекрытии: освободим его, когда провайдер сообщит об окончании.
            DisposeGaplessPreviousFile();
            _gaplessPreviousFile = previousAudioFile;
            return;
        }

        try
        {
            previousAudioFile?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось освободить AudioFileReader предыдущего трека после перехода без паузы", ex);
        }
    }
}
