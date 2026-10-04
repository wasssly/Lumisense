using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls.Primitives;
using WpfSlider = System.Windows.Controls.Slider;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using static Lumisense.BackgroundTask;

namespace Lumisense;

// Воспроизведение: загрузка и запуск трека (LoadAndPlay), окончание трека, пауза/возобновление, стоп, fade при смене.
// Вынесено из MainWindow.xaml.cs только ради навигации, логика и порядок вызовов не менялись.
public partial class MainWindow
{
    // Запуск через FireAndForget, а не async void: ошибки загрузки должны попадать в лог.
    private void LoadAndPlay(string filePath, bool autoPlay = true, TimeSpan? startPosition = null,
        AlbumArtTransitionDirection albumArtDirection = AlbumArtTransitionDirection.Next,
        TrackChangeOrigin changeOrigin = TrackChangeOrigin.User, bool preserveShuffleSession = false,
        bool preservePendingPlaybackState = false)
        => FireAndForget(
            LoadAndPlayAsync(filePath, autoPlay, startPosition, albumArtDirection, changeOrigin,
                preserveShuffleSession, preservePendingPlaybackState),
            nameof(LoadAndPlayAsync));

    private async Task LoadAndPlayAsync(string filePath, bool autoPlay, TimeSpan? startPosition,
        AlbumArtTransitionDirection albumArtDirection, TrackChangeOrigin changeOrigin,
        bool preserveShuffleSession, bool preservePendingPlaybackState)
    {
        if (!File.Exists(filePath))
        {
            HandleMissingTrackFile();
            return;
        }

        autoPlay = ResolveAutoPlay(autoPlay, preservePendingPlaybackState);

        var previousGain = Interlocked.Exchange(ref _replayGainCts, null);
        previousGain?.Cancel();

        AudioPlaybackCoordinator.LoadOperation operation;
        try
        {
            operation = await _audioPlaybackCoordinator.BeginTrackLoadAsync(_lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        int generation = operation.Generation;
        PreparedTrack? prepared = null;
        Task<PreparedTrack>? preparationTask = null;
        var performance = new TrackLoadPerformanceMeasurement(_settings.TrackLoadTraceEnabled);

        try
        {
            // _isPlaying ещё true: предыдущий трек слышен, пока идёт fade-out. См. комментарий у TrackUserState.
            SetTrackUserState(TrackUserState.Loading);

            preparationTask = StartTrackPreparation(filePath, operation.CancellationToken);

            await FadeOutBeforeTrackChangeAsync(operation.CancellationToken);
            performance.MarkStage("fade-out");
            if (!operation.IsCurrent || _isExiting)
                return;

            try
            {
                prepared = await preparationTask;
            }
            finally
            {
                // После await результат либо передан prepared, либо fault/cancellation уже
                // наблюдаются текущей цепочкой. Фоновая очистка нужна только при раннем exit.
                preparationTask = null;
            }
            performance.MarkStage("wait-prepared-audio-and-metadata");

            operation.CancellationToken.ThrowIfCancellationRequested();
            if (!operation.IsCurrent || _isExiting)
                return;

            PreparedTrack loaded = AttachPreparedTrack(prepared);
            prepared = null;

            ApplyLoadedTrack(loaded, filePath, albumArtDirection, changeOrigin, preserveShuffleSession);
            performance.MarkStage("apply-track-ui");

            ApplyInitialPosition(startPosition);

            // Сохраняем shuffle-сессию сразу после применения трека: периодического checkpoint мало — при быстром переключении
            // и выходе история/колода остались бы устаревшими.
            if (_shuffleSession.IsEnabled && _playlistRestoreCompleted)
                PersistPlaybackAndPlaylistState(asyncSave: true);

            // Скрытая панель текста ничего не делает в фоне; если она открыта, новая композиция отменяет прошлый запрос и
            // грузит свой LRC/TXT/кэш или точное онлайн-совпадение.
            if (_isLyricsPanelActive)
                FireAndForget(LoadMainWindowLyricsAsync(filePath), "LoadMainWindowLyricsAsync");

            StartOutputChain(generation, performance);
            ApplyPlaybackStartState(autoPlay);

            // Причина и факт запуска передаются политике уведомлений явно: так автопереход,
            // выбор трека на паузе и восстановление сессии не маскируются друг под друга.
            ShowTrackChangeToast(changeOrigin, autoPlay);
            ScrollPlaylistToCurrentTrack();
            performance.MarkStage("ready");
            performance.Complete(succeeded: true);
        }
        catch (OperationCanceledException)
        {
            // A newer track request or application shutdown superseded this load.
        }
        catch (Exception ex)
        {
            performance.MarkStage("failed");
            performance.Complete(succeeded: false);
            HandleTrackLoadFailure(filePath, ex);
        }
        finally
        {
            prepared?.Dispose();
            if (preparationTask is not null)
            {
                // При отмене во время fade задача могла создать AudioFileReader, но ещё не вернуться: дожидаемся её и освобождаем result,
                // чтобы быстрые Next/Previous не держали handle устаревшего запроса.
                FireAndForget(DisposeUnusedPreparedTrackAsync(preparationTask), "DisposeUnusedPreparedTrackAsync");
            }
            if (operation.IsCurrent)
                _pendingNavigationAutoPlay = false;
            operation.Dispose();
        }
    }

    private Task<PreparedTrack> StartTrackPreparation(string filePath, CancellationToken cancellationToken)
    {
        // Готовим граф следующего трека, пока текущий поток доигрывает fade/drain; новый WasapiPlayer не создаём —
        // endpoint останавливается только после нулевого хвоста в его буфере.
        double volumeSliderValue = VolumeSlider.Value;
        bool replayGainEnabled = _settings.ReplayGainEnabled;
        bool equalizerEnabled = _settings.EqualizerEnabled;
        double[] equalizerGains = (double[])_settings.EqualizerBandGainsDb.Clone();
        double playbackSpeed = _runtimePlaybackRate;
        double playbackPitch = Math.Clamp(_settings.PlaybackPitchSemitones, -12.0, 12.0);
        bool traceTrackPreparation = _settings.TrackLoadTraceEnabled;
        return _trackPreparationService.PrepareAsync(
            filePath,
            new TrackPreparationOptions(
                volumeSliderValue,
                _settings.UseLogarithmicVolume,
                replayGainEnabled,
                equalizerEnabled,
                equalizerGains,
                playbackSpeed,
                playbackPitch,
                traceTrackPreparation),
            cancellationToken);
    }

    private PreparedTrack AttachPreparedTrack(PreparedTrack prepared)
    {
        _audioFile = prepared.AudioFile;
        _tempoProvider = prepared.TempoProvider;
        ApplyPlaybackRateToCurrentStream();
        _equalizer = prepared.Equalizer;
        _replayGainFactor = prepared.ReplayGainFactor;
        return prepared;
    }

    private void StartOutputChain(int generation, TrackLoadPerformanceMeasurement performance)
    {
        _audioLevelMeter = new AudioLevelSampleProvider(_equalizer!);
        var fadeIn = new FadeInOutSampleProvider(_audioLevelMeter, initiallySilent: true);
        fadeIn.BeginFadeIn(70);
        _activeFade = fadeIn;
        InitializeOutputDevice(fadeIn);
        _settingsWindow?.RefreshOutputDeviceRuntimeStatus();
        performance.MarkStage("initialize-output");
        _outputDevice!.PlaybackStopped += OutputDevice_PlaybackStopped;
        ReapplySavedPlaybackRateAfterTrackReady(generation);
    }

    private void HandleMissingTrackFile()
    {
        LocalizedMessageBox.Show(
            this,
            LocalizationService.Translate("Не удалось открыть трек: файл недоступен."),
            LocalizationService.Translate("Недоступные файлы"),
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Warning);
        RefreshPlaylistView();
        if (_isFavoritesView) RefreshFavoritesTrackList();
        _pendingNavigationAutoPlay = false;
    }

    private bool ResolveAutoPlay(bool autoPlay, bool preservePendingPlaybackState)
    {
        // FadeOutBeforeTrackChangeAsync останавливает промежуточный output и сбрасывает _isPlaying: при быстром Next/Previous
        // следующая заявка читала false и грузила последний трек на паузе, поэтому сохраняем исходное намерение пользователя.
        if (preservePendingPlaybackState)
        {
            if (_pendingNavigationAutoPlay)
                autoPlay = true;
            else
                _pendingNavigationAutoPlay = autoPlay;
        }
        else
        {
            _pendingNavigationAutoPlay = false;
        }

        return autoPlay;
    }

    private void ApplyLoadedTrack(PreparedTrack loaded, string filePath, AlbumArtTransitionDirection albumArtDirection,
        TrackChangeOrigin changeOrigin, bool preserveShuffleSession)
    {
        var audioFile = _audioFile!;
        _currentTrackTaggedTitle = loaded.Title;
        _currentTrackTaggedArtist = loaded.Artist;
        var metadata = FileNameNormalizer.ResolveArtistAndTitle(
            filePath, _currentTrackTaggedArtist, _currentTrackTaggedTitle, "—");
        SetTrackInfoText(metadata.Title, metadata.Artist);
        TotalTimeText.Text = audioFile.TotalTime.ToString(@"mm\:ss");
        ProgressSlider.Maximum = Math.Max(audioFile.TotalTime.TotalSeconds, 0.01);
        _currentTrackPath = filePath;
        // Ручной выбор строки — новая отправная точка обычного шаффла, иначе «Следующий» продолжил бы старую историю и вернул
        // пройденную последовательность; кнопки, hotkey и автопереход явно передают preserveShuffleSession=true.
        if (!preserveShuffleSession && changeOrigin == TrackChangeOrigin.User)
            _shuffleSession.StartStandardSession(filePath, _settings.UseImprovedShuffle);

        _halfPlayCounted = false;
        _actuallyPlayedSeconds = 0;
        _lastTickPositionSeconds = -1;
        ApplyPreparedAlbumArt(loaded, albumArtDirection);
    }

    private void ApplyInitialPosition(TimeSpan? startPosition)
    {
        var audioFile = _audioFile!;
        if (_settings.ProgressBarStyle == "Waveform")
            FireAndForget(EnsureWaveformForCurrentTrackAsync(), "EnsureWaveformForCurrentTrackAsync");

        var position = startPosition.HasValue && startPosition.Value < audioFile.TotalTime
            ? startPosition.Value
            : TimeSpan.Zero;
        audioFile.CurrentTime = position;
        ProgressSlider.Value = position.TotalSeconds;
        CurrentTimeText.Text = position.ToString(@"mm\:ss");
        ProgressWaveform.Progress = audioFile.TotalTime.TotalSeconds > 0
            ? position.TotalSeconds / audioFile.TotalTime.TotalSeconds
            : 0;
        _integrations.NowPlaying?.UpdateTrackInfo(TrackTitleText.Text, TrackArtistText.Text);
        RaiseTrackInfoChanged(TrackTitleText.Text, TrackArtistText.Text, CurrentArtBrush);
        RaiseProgressChanged(position.TotalSeconds, audioFile.TotalTime.TotalSeconds);
    }

    private void ApplyPlaybackStartState(bool autoPlay)
    {
        if (autoPlay)
        {
            _outputDevice!.Play();
            _isPlaying = true;
            PlayPauseButton.Icon = IconResources.MakeOnAccent("IconPause", 15);
            _progressTimer.Start();
            _playbackClock.Start();
            _integrations.NowPlaying?.SetPlaybackStatus(Windows.Media.MediaPlaybackStatus.Playing);
            RaisePlaybackStateChanged(true);
        }
        else
        {
            _isPlaying = false;
            PlayPauseButton.Icon = IconResources.MakeOnAccent("IconPlay", 15);
            _integrations.NowPlaying?.SetPlaybackStatus(Windows.Media.MediaPlaybackStatus.Paused);
            RaisePlaybackStateChanged(false);
        }

        SetTrackUserState(autoPlay ? TrackUserState.Playing : TrackUserState.Paused);
    }

    private void HandleTrackLoadFailure(string filePath, Exception ex)
    {
        StopPlayback();
        DisposeOutputDeviceSafely();
        _currentTrackPath = null;
        _replayGainFactor = 1.0;
        SetTrackInfoText("Файл не выбран", "—");
        TotalTimeText.Text = "00:00";
        ResetAlbumArtPlaceholder(AlbumArtTransitionDirection.None);
        SetTrackUserState(TrackUserState.Error);
        if (!_isExiting)
            PlaybackErrorExperience.Show(this, filePath, ex);
    }

    private static async Task DisposeUnusedPreparedTrackAsync(Task<PreparedTrack> task)
    {
        try
        {
            (await task.ConfigureAwait(false)).Dispose();
        }
        catch (OperationCanceledException)
        {
            // Обычный исход при следующем запросе Next/Previous.
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось завершить отменённую подготовку трека: {ex.Message}");
        }
    }

    // Подсветка — обычное выделение строки (ListViewItem.IsSelected): при смене трека выставляем SelectedItem, разворачиваем
    // группу и прокручиваем список к строке.

    private void ScrollPlaylistToCurrentTrack()
    {
        var path = _currentTrackPath;
        var folder = string.IsNullOrEmpty(path)
            ? null
            : _isFavoritesView
                ? (_favoritesFolder.Tracks.Contains(path) ? _favoritesFolder : null)
                : _folders.FirstOrDefault(f => f.Tracks.Contains(path));

        if (folder != null && !folder.IsExpanded)
        {
            folder.IsExpanded = true;

            // Строка появится в плоском списке (PlaylistTrackRow) только после пересборки (RefreshPlaylistView), поэтому разворачивание
            // требует явного пересбора.
            RefreshPlaylistView();
        }

        // Пересборка ItemsSource выше применяется к раскладке не сразу — ждём завершения
        // текущего цикла раскладки/рендера, прежде чем искать строку трека в списке.
        Dispatcher.BeginInvoke(new Action(() => HighlightAndScrollToTrack(folder, path)),
            DispatcherPriority.Loaded);
    }

    private void HighlightAndScrollToTrack(PlaylistFolder? folder, string? trackPath)
    {
        var listView = _isFavoritesView ? FavoritesTrackListView : PlaylistFoldersControl;

        if (folder == null || trackPath == null)
        {
            listView.SelectedIndex = -1;
            return;
        }

        PlaylistTrackRow? row = null;
        foreach (var item in listView.Items)
        {
            if (item is PlaylistTrackRow candidate && ReferenceEquals(candidate.Folder, folder) && PathEquals(candidate.FilePath, trackPath))
            {
                row = candidate;
                break;
            }
        }

        if (row == null)
        {
            listView.SelectedIndex = -1;
            return;
        }

        // ScrollIntoView и прокручивает, и заставляет WPF реализовать контейнер виртуализированного списка (иначе ContainerFromItem
        // вне видимой области вернул бы null).
        listView.ScrollIntoView(row);
        listView.SelectedItem = row;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;

            var descendant = FindVisualChild<T>(child);
            if (descendant != null) return descendant;
        }
        return null;
    }

    private void SetTrackInfoText(string title, string artist)
    {
        TrackTitleText.Text = title;
        TrackArtistText.Text = artist;
    }

    private void SetTrackUserState(TrackUserState state)
    {
        if (!_playbackStateMachine.TryTransitionTo(state))
        {
            Logger.Warn($"Недопустимый переход состояния воспроизведения: {_playbackStateMachine.Current} → {state}");
            return;
        }

        _trackUserState = _playbackStateMachine.Current;
        UpdateTrackUserStatePresentation();
    }

    private void UpdateTrackUserStatePresentation()
    {
        (string textKey, string hintKey) = _trackUserState switch
        {
            TrackUserState.Loading => (LocalizationKey.TrackStateLoading, LocalizationKey.TrackStateLoadingHint),
            TrackUserState.Playing => (LocalizationKey.TrackStatePlaying, LocalizationKey.TrackStatePlayingHint),
            TrackUserState.Paused => (LocalizationKey.TrackStatePaused, LocalizationKey.TrackStatePausedHint),
            TrackUserState.Stopped => (LocalizationKey.TrackStateStopped, LocalizationKey.TrackStateStoppedHint),
            TrackUserState.Error => (LocalizationKey.TrackStateError, LocalizationKey.TrackStateErrorHint),
            _ => (LocalizationKey.TrackStateNoTrack, LocalizationKey.TrackStateNoTrackHint)
        };

        TrackStateText.Text = LocalizationService.Get(textKey);
        TrackStateText.ToolTip = LocalizationService.Get(hintKey);
        TrackStateBadge.Opacity = _trackUserState == TrackUserState.Error ? 1.0 : 0.82;
    }

    private void OutputDevice_PlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // Сохраняем generation и путь именно остановившегося reader: callback приходит с audio thread, а Dispatcher может
        // выполнить его уже после быстрой загрузки следующего трека.
        int generation = _audioPlaybackCoordinator.CurrentGeneration;
        string? stoppedPath = _currentTrackPath;
        Exception? playbackError = e.Exception;
        if (_isExiting || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;

        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (_isExiting || generation != _audioPlaybackCoordinator.CurrentGeneration ||
                        !string.Equals(stoppedPath, _currentTrackPath, StringComparison.Ordinal))
                        return;

                    Exception? failure = playbackError;
                    if (failure is not null && IsExclusiveEndOfStreamBufferError(failure))
                    {
                        // NAudio 3.x в эксклюзивном режиме на конце потока отдаёт Release(0, Silent), и драйвер отвечает AUDCLNT_E_BUFFER_SIZE_ERROR.
                        // Трек дочитан — это обычное окончание, а не сбой: восстановление перезапускало бы трек и подменяло устройство.
                        Logger.Info($"Эксклюзивный режим: драйвер отклонил release пустого буфера в конце трека ({failure.Message}); считаем трек завершённым.");
                        failure = null;
                    }

                    if (failure is not null)
                    {
                        RecoverOutputDeviceAfterFailure(failure, resumePlayback: _isPlaying);
                        return;
                    }

                    if (_audioFile != null && _audioFile.TotalTime - _audioFile.CurrentTime <= TimeSpan.FromMilliseconds(750))
                        HandleTrackFinishedNaturally();
                }
                catch (Exception ex)
                {
                    Logger.Error("Ошибка обработки завершения воспроизведения в Dispatcher callback", ex);
                }
            });
        }
        catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            // Dispatcher закрывается одновременно с audio callback.
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось поставить PlaybackStopped callback в Dispatcher", ex);
        }
    }

    // AUDCLNT_E_BUFFER_SIZE_ERROR, пришедший, когда reader уже дочитан до конца (тот же порог, что у естественного окончания ниже).
    private bool IsExclusiveEndOfStreamBufferError(Exception error) =>
        error.HResult == unchecked((int)0x88890016) &&
        _audioFile != null && _audioFile.TotalTime - _audioFile.CurrentTime <= TimeSpan.FromMilliseconds(750);

    private void HandleTrackFinishedNaturally()
    {
        string? currentPath = GetCurrentTrackPath();
        if (currentPath == null) return;

        // Очередь важнее RepeatMode.One: иначе поставленный в очередь трек не сыграл бы при повторе одного трека
        // (та ветка ниже просто перезапускает текущий, минуя PlayNextTrack/очередь).
        if (_playbackQueue.Count > 0)
        {
            PlayNextTrack(TrackChangeOrigin.Automatic);
            return;
        }

        switch (_repeatMode)
        {
            case RepeatMode.One:
                // Повторяем тот же самый трек с начала
                LoadAndPlay(currentPath, changeOrigin: TrackChangeOrigin.Automatic);
                break;

            case RepeatMode.All:
                PlayNextTrack(TrackChangeOrigin.Automatic);
                break;

            case RepeatMode.Off:
            default:
                var active = FlattenActive();
                int posInActive = active.IndexOf(currentPath);
                // Без повтора и без шафла останавливаемся на последнем треке активных групп,
                // а не зацикливаем плейлист заново
                bool isLastTrack = !_shuffleSession.IsEnabled && (posInActive < 0 || posInActive == active.Count - 1);
                if (isLastTrack)
                    StopPlayback();
                else
                    PlayNextTrack(TrackChangeOrigin.Automatic);
                break;
        }
    }

    void IIntegrationHost.PlayPauseButton_Click(object sender, RoutedEventArgs e) => PlayPauseButton_Click(sender, e);

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        => FireAndForget(TogglePlaybackAsync(), nameof(TogglePlaybackAsync));

    private async Task TogglePlaybackAsync()
    {
        // Признак готового источника — _audioFile, а не _outputDevice: WasapiPlayer создаётся для текущего источника и
        // освобождается при StopPlayback, так что наличие output ничего не значит.
        if (_audioFile == null)
        {
            var active = FlattenActive();
            if (active.Count > 0)
            {
                LoadAndPlay(active[0]);
            }
            return;
        }

        // Pause/Play у WASAPI нельзя запускать параллельно: быстрый второй клик мог попасть в смену состояния endpoint и оборвать
        // ненулевой sample, поэтому запоминаем лишь чётность дополнительных переключений и выполняем её после fade.
        if (_playPauseTransitionInProgress)
        {
            _playPausePendingToggle = !_playPausePendingToggle;
            return;
        }
        _playPauseTransitionInProgress = true;
        AudioPlaybackCoordinator.ExclusiveLease? audioLease = null;

        try
        {
            audioLease = await _audioPlaybackCoordinator.EnterExclusiveAsync(_lifetimeCts.Token);
            if (_isPlaying)
                await PausePlaybackAsync();
            else
                ResumePlayback();
        }
        catch (OperationCanceledException)
        {
            // A track load or application shutdown owns the audio graph at the moment.
        }
        finally
        {
            audioLease?.Dispose();
            _playPauseTransitionInProgress = false;
            if (_playPausePendingToggle)
            {
                _playPausePendingToggle = false;
                await Dispatcher.InvokeAsync(
                    () => FireAndForget(TogglePlaybackAsync(), nameof(TogglePlaybackAsync)));
            }
        }
    }

    private async Task PausePlaybackAsync()
    {
        IWavePlayer? output = _outputDevice;
        FadeInOutSampleProvider? fade = _activeFade;
        if (fade is not null)
        {
            fade.BeginFadeOut(PlayPauseFadeMilliseconds);
            // Даём audio-thread записать нулевой хвост до Pause. Ожидание короче
            // обычной человеческой реакции и не меняет позицию трека.
            await Task.Delay(PlayPauseFadeMilliseconds + PlayPauseFadeSafetyMilliseconds);
        }

        if (!ReferenceEquals(output, _outputDevice) || !_isPlaying) return;

        try
        {
            output?.Pause();
        }
        catch (Exception ex)
        {
            // Устройство могло исчезнуть во время работы (отключили наушники, упал драйвер): не выдаём «На паузе» за факт,
            // оставляем состояние воспроизведения и показываем ошибку с подсказкой в индикаторе трека.
            RecoverOutputDeviceAfterFailure(ex, resumePlayback: true);
            return;
        }

        _isPlaying = false;
        PlayPauseButton.Icon = IconResources.MakeOnAccent("IconPlay", 15);
        // UI-таймер оставляем активным во время паузы: он продолжает подтверждать
        // текущую позицию и не оставляет устаревшее время до следующего Play.
        FlushPlaybackClock();
        _integrations.NowPlaying?.SetPlaybackStatus(Windows.Media.MediaPlaybackStatus.Paused);
        RaisePlaybackStateChanged(false);
        SetTrackUserState(TrackUserState.Paused);

        // На паузе часто и надолго оставляют трек, не закрывая плеер вовсе — сохраняем
        // позицию сразу же, а не ждём следующего реального закрытия (см. PersistPlaybackAndPlaylistState).
        PersistPlaybackAndPlaylistState(asyncSave: true);
    }

    private void ResumePlayback()
    {
        IWavePlayer? output = _outputDevice;
        _activeFade?.BeginFadeIn(PlayPauseFadeMilliseconds);

        try
        {
            output?.Play();
        }
        catch (Exception ex)
        {
            // См. комментарий у Pause() выше — та же защита от падения из-за проблем с
            // самим устройством вывода, а не с плеером как таковым.
            RecoverOutputDeviceAfterFailure(ex, resumePlayback: true);
            return;
        }

        _isPlaying = true;
        PlayPauseButton.Icon = IconResources.MakeOnAccent("IconPause", 15);
        _progressTimer.Start();
        _playbackClock.Start();
        _integrations.NowPlaying?.SetPlaybackStatus(Windows.Media.MediaPlaybackStatus.Playing);
        RaisePlaybackStateChanged(true);
        SetTrackUserState(TrackUserState.Playing);
    }

    void IIntegrationHost.StopButton_Click(object sender, RoutedEventArgs e) => StopButton_Click(sender, e);

    private void StopButton_Click(object sender, RoutedEventArgs e) => StopPlayback();

    private const int TrackChangeFadeOutMilliseconds = 24;
    private const int TrackChangeDrainSafetyMilliseconds = 8;
    private const int PlayPauseFadeMilliseconds = 18;
    private const int PlayPauseFadeSafetyMilliseconds = 10;

    private async Task FadeOutBeforeTrackChangeAsync(CancellationToken token)
    {
        FadeInOutSampleProvider? activeFade = _activeFade;
        IWavePlayer? activeOutput = _outputDevice;
        if (activeFade is not null && _isPlaying)
        {
            // BeginFadeOut применяется на следующем Read audio thread: фиксированная пауза 30 ms могла вызвать Stop до записи нулевого
            // хвоста в WASAPI buffer (щелчок на части endpoint). Ждём сигнал тишины и даём буферу доиграть silent tail.
            var fadeOutCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler fadeOutHandler = (_, _) => fadeOutCompleted.TrySetResult(true);
            activeFade.FadeOutComplete += fadeOutHandler;
            try
            {
                activeFade.BeginFadeOut(TrackChangeFadeOutMilliseconds);
                // FadeOutComplete приходит с audio thread; если output уже paused/stopped, 350-ms timeout замедлял Next/Previous,
                // поэтому ждём расчётный путь: buffer, 24-ms fade и запас — endpoint уже играет тишину, и Stop не обрывает sample.
                int fallbackDelayMilliseconds = GetTrackChangeFadeFallbackDelayMilliseconds(activeOutput);
                Task completed = await Task.WhenAny(
                    fadeOutCompleted.Task,
                    Task.Delay(fallbackDelayMilliseconds, token));
                token.ThrowIfCancellationRequested();

                if (completed == fadeOutCompleted.Task)
                {
                    int drainDelayMilliseconds = GetTrackChangeDrainDelayMilliseconds(activeOutput);
                    if (drainDelayMilliseconds > 0)
                        await Task.Delay(drainDelayMilliseconds, token);
                }
                // else: FadeOutComplete не пришёл — ожидаемо при заполненном вперёд WASAPI-буфере, drain выше это уже покрывает.
            }
            finally
            {
                activeFade.FadeOutComplete -= fadeOutHandler;
            }
        }

        StopPlayback(disposeOnly: true);
    }

    private static int GetTrackChangeFadeFallbackDelayMilliseconds(IWavePlayer? outputDevice)
    {
        int drainDelayMilliseconds = GetTrackChangeDrainDelayMilliseconds(outputDevice);
        if (drainDelayMilliseconds == 0)
            drainDelayMilliseconds = WasapiRequestedLatencyMilliseconds + TrackChangeDrainSafetyMilliseconds;

        return Math.Clamp(
            drainDelayMilliseconds + TrackChangeFadeOutMilliseconds,
            TrackChangeFadeOutMilliseconds + TrackChangeDrainSafetyMilliseconds,
            WasapiRequestedLatencyMilliseconds * 2 + TrackChangeFadeOutMilliseconds + TrackChangeDrainSafetyMilliseconds);
    }

    private static int GetTrackChangeDrainDelayMilliseconds(IWavePlayer? outputDevice)
    {
        try
        {
            if (outputDevice is WasapiPlayer wasapiPlayer)
            {
                double milliseconds = wasapiPlayer.CurrentLatency.TotalMilliseconds + TrackChangeDrainSafetyMilliseconds;
                return Math.Clamp((int)Math.Ceiling(milliseconds), TrackChangeDrainSafetyMilliseconds,
                    WasapiRequestedLatencyMilliseconds * 3);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось получить текущую задержку WASAPI перед сменой трека: {ex.Message}");
        }

        return 0;
    }

    private void StopPlayback(bool disposeOnly = false)
    {
        // Явный Stop отменяет незавершённую цепочку Next/Previous. Внутренний Stop между
        // треками передаёт disposeOnly: true и не трогает новый запрос загрузки.
        if (!disposeOnly)
        {
            _audioPlaybackCoordinator.CancelCurrentLoad();
            _pendingNavigationAutoPlay = false;
        }

        StopProgressTimerAndAnimation();

        // Stop()/Dispose() поднимают PlaybackStopped и при естественном завершении, и при ручной остановке: без предварительной отписки
        // автопереключение из OutputDevice_PlaybackStopped вызывалось бы повторно для старого _audioFile, и автопереход работал через раз.
        if (_outputDevice != null)
            _outputDevice.PlaybackStopped -= OutputDevice_PlaybackStopped;

        // WasapiPlayer нельзя безопасно переинициализировать после Init для следующего источника.
        // После Stop сразу освобождаем output и endpoint перед следующим источником.
        try
        {
            _outputDevice?.Stop();
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось корректно остановить устройство вывода", ex);
        }
        finally
        {
            DisposeOutputDeviceSafely();
        }

        try
        {
            _audioFile?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось освободить AudioFileReader", ex);
        }
        finally
        {
            _tempoProvider = null;
            _audioFile = null;
        }
        _equalizer = null;
        _audioLevelMeter = null;
        _activeFade = null;
        _isPlaying = false;

        if (!disposeOnly)
        {
            ProgressSlider.Value = 0;
            CurrentTimeText.Text = "00:00";
            ProgressWaveform.Peaks = null;
            PlayPauseButton.Icon = IconResources.MakeOnAccent("IconPlay", 15);
            _integrations.NowPlaying?.SetPlaybackStatus(Windows.Media.MediaPlaybackStatus.Stopped);
            RaisePlaybackStateChanged(false);
            _discordRichPresence.ClearAndDispose();
            SetTrackUserState(TrackUserState.Stopped);
        }
    }
}
