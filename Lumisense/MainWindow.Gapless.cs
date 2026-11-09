using System.IO;
using static Lumisense.BackgroundTask;

namespace Lumisense;

// Гэплесс: следующий трек собирается заранее и подставляется на конце текущего без остановки выхода.
// Если условия не сходятся (повтор, ручной выбор, смена очереди), переход идёт обычным путём через PlaybackStopped.
public partial class MainWindow
{
    private const double GaplessArmWindowSeconds = 7;

    private sealed class ArmedGaplessTrack
    {
        public required string Path { get; init; }
        public required string PreviousPath { get; init; }
        public required PreparedTrack Prepared { get; init; }
        public required ChainParts Chain { get; init; }
        public required bool FromQueue { get; init; }
        public required bool Shuffle { get; init; }
        public required RepeatMode Repeat { get; init; }
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

        if (!_settings.GaplessEnabled || _gapless is null || _audioFile is null || _gaplessArming || !_isPlaying) return;
        if (_audioFile.TotalTime - _audioFile.CurrentTime > TimeSpan.FromSeconds(GaplessArmWindowSeconds)) return;

        ArmGapless();
    }

    private bool IsGaplessArmStillValid(ArmedGaplessTrack armed)
    {
        if (!_settings.GaplessEnabled || _repeatMode == RepeatMode.One || _repeatMode != armed.Repeat
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
                || _gapless is null || _audioFile is null || !_settings.GaplessEnabled)
                return;

            var format = _audioFile.WaveFormat;
            var nextFormat = prepared.AudioFile.WaveFormat;
            if (nextFormat.SampleRate != format.SampleRate || nextFormat.Channels != format.Channels)
            {
                // Другая частота или каналы: без ресемплинга бесшовно не получится, этот переход пойдёт обычным путём.
                _gaplessRejectedPath = next;
                return;
            }

            var armed = new ArmedGaplessTrack
            {
                Path = next,
                PreviousPath = current,
                Prepared = prepared,
                Chain = BuildChain(prepared.Equalizer),
                FromQueue = fromQueue,
                Shuffle = _shuffleSession.IsEnabled,
                Repeat = _repeatMode
            };
            if (!_gapless.Arm(armed.Chain.Meter, armed)) return;

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
        SetTrackUserState(TrackUserState.Playing);
        ShowTrackChangeToast(TrackChangeOrigin.Automatic, true);
        ScrollPlaylistToCurrentTrack();
        _gaplessRejectedPath = null;

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
