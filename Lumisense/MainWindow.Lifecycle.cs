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

namespace Lumisense;

// Жизненный цикл окна: закрытие (OnClosing/OnClosed), сохранение состояния воспроизведения и плейлиста, сброс данных.
// Вынесено из MainWindow.xaml.cs только ради навигации, логика и порядок вызовов не менялись.
public partial class MainWindow
{
    protected override void OnClosing(CancelEventArgs e)
    {
        // Сворачиваем в трей вместо закрытия, если это настроено и закрытие не через "Выход" из трея
        if (!_isExiting && _settings.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            Hide();
            _integrations.Tray?.Show("Lumisense");

            // MinimizeToTrayOnClose включён по умолчанию, и закрытие крестиком идёт сюда, а не в OnClosed: без явного сохранения
            // позиция трека могла не обновляться месяцами (PersistPlaybackAndPlaylistState).
            PersistPlaybackAndPlaylistState();
            return;
        }

        base.OnClosing(e);
    }

    public void ResetAllUserData()
    {
        FlushPlaybackClock();
        StopPlayback();

        StopFolderWatchers();
        _folders.Clear();
        _favoritesFolder.Tracks.Clear();
        FavoritesManager.Reset();
        PlayCountManager.Reset();

        LumiProfileIO.ResetToDefaults(_settings);
        _settings.SavedPlaylistFolders = new List<SavedPlaylistFolder>();
        _settings.SavedPlaylist = null;
        _settings.FavoriteTracks = new List<string>();
        _settings.PinnedFavoriteTracks = new List<string>();
        _settings.PlayCounts = new Dictionary<string, int>();
        _settings.LastTrackPath = null;
        _settings.LastPositionSeconds = 0;
        _settings.WasPlayingOnClose = false;
        _settings.ShuffleHistory = new List<string>();
        _settings.ShuffleHistoryIndex = -1;
        _settings.ShuffleBag = new List<string>();
        _settings.EqualizerPresets.Clear();

        _currentTrackPath = null;
        ResetShuffleState();
        _replayGainFactor = 1.0;
        SetTrackInfoText("Файл не выбран", "—");
        SetTrackUserState(TrackUserState.NoTrack);
        TotalTimeText.Text = "00:00";
        ResetAlbumArtPlaceholder(AlbumArtTransitionDirection.None);
        RefreshPlaylistView();
        if (_isFavoritesView) RefreshFavoritesTrackList();
        StartFolderWatchers();
        SettingsManager.Save(_settings);
    }

    // Возвращает последний полный снимок перед явным сбросом: восстанавливаем не только AppSettings, но и runtime-коллекции,
    // очищенные сбросом; сохранённый трек грузится на паузе — возврат настроек не должен внезапно включать музыку.
    public bool TryRestoreLastSettingsReset()
    {
        if (!SettingsResetRecoveryService.TryRestoreLatest(_settings)) return false;

        FlushPlaybackClock();
        StopPlayback();
        StopFolderWatchers();
        _folders.Clear();
        _favoritesFolder.Tracks.Clear();
        FavoritesManager.Initialize(_settings.FavoriteTracks, _settings.PinnedFavoriteTracks);
        FavoritesChangeNotifier.Instance.Bump();
        PlayCountManager.Initialize(_settings.PlayCounts);

        _currentTrackPath = null;
        ResetShuffleState();
        _replayGainFactor = 1.0;
        SetTrackInfoText("Файл не выбран", "—");
        SetTrackUserState(TrackUserState.NoTrack);
        TotalTimeText.Text = "00:00";
        ResetAlbumArtPlaceholder(AlbumArtTransitionDirection.None);

        SetShuffleEnabled(_settings.IsShuffleEnabled, resetSessionHistory: false);
        RepeatMode restoredRepeatMode = Enum.TryParse(_settings.RepeatMode, ignoreCase: true, out RepeatMode parsedRepeatMode)
            ? parsedRepeatMode
            : RepeatMode.Off;
        SetRepeatMode(restoredRepeatMode);

        // RestoreSavedPlaylistAsync выполняет построение списка синхронно и только проверку
        // файлов продолжает в фоне. Временно выключаем resume, чтобы возврат был предсказуемым.
        bool wasPlayingOnClose = _settings.WasPlayingOnClose;
        _settings.WasPlayingOnClose = false;
        _playlistRestoreCompleted = false;
        RestoreSavedPlaylistAsync();
        _settings.WasPlayingOnClose = wasPlayingOnClose;

        if (_isFavoritesView) RefreshFavoritesTrackList();
        ApplyImportedSettingsLive();
        ApplyAccessibilityPreferences();
        SettingsManager.Save(_settings);
        return true;
    }

    // Включение сразу снимает снимок текущей очереди (иначе settings.json хранил бы старое значение до первого её изменения),
    // выключение чистит сохранённую копию, чтобы она не всплыла при повторном включении.
    public void SetSaveQueueBetweenRestarts(bool enabled)
    {
        _settings.SaveQueueBetweenRestarts = enabled;
        _settings.SavedQueue = enabled ? _playbackQueue.Items.ToList() : new List<string>();
    }

    // Раньше вызывалась только из OnClosed, а при MinimizeToTrayOnClose (по умолчанию) окно прячется в трей и до "Выход"
    // могло не доходить месяцами; теперь вызывается ещё при сворачивании в трей, на паузе и периодически.
    private void PersistPlaybackAndPlaylistState(bool asyncSave = false)
    {
        // До завершения RestoreSavedPlaylistAsync часть runtime-полей (трек, режим окна) ещё содержит XAML-значения:
        // их нельзя записывать поверх settings.json после неудачного или прерванного старта.
        if (!_playlistRestoreCompleted)
        {
            Logger.Warn("Пропущено раннее сохранение: восстановление состояния плеера ещё не завершено");
            return;
        }

        // Сохраняем speed из независимого runtime-state, а не из Popup или временного Slider.
        _settings.PlaybackSpeed = _runtimePlaybackRate;

        if (_settings.RememberVolume)
            _settings.SavedVolume = VolumeSlider.Value;

        // Не затираем сохранённый плейлист пустой коллекцией, пока конструктор ещё не
        // завершил его восстановление. Это особенно важно, если старт прерван исключением.
        if (_playlistRestoreCompleted)
        {
            _settings.SavedPlaylistFolders = _folders.Select(f => new SavedPlaylistFolder
            {
                DisplayName = f.PersistedDisplayName,
                SourcePath = f.SourcePath,
                IsEnabled = f.IsEnabled,
                IsExpanded = f.IsExpanded,
                Tracks = f.Tracks.ToList(),
                IsLooseFilesBucket = f.IsLooseFilesBucket
            }).ToList();
        }

        _settings.LastTrackPath = GetCurrentTrackPath();
        _settings.LastPositionSeconds = _audioFile?.CurrentTime.TotalSeconds ?? _settings.LastPositionSeconds;
        _settings.WasPlayingOnClose = _isPlaying;
        _settings.WasMiniPlayerOnClose = _isMiniMode;
        _settings.IsPlaylistVisible = _isPlaylistVisible;
        _settings.PlayerViewMode = _viewMode.ToString();
        _settings.IsShuffleEnabled = _shuffleSession.IsEnabled;
        _settings.RepeatMode = _repeatMode.ToString();
        PersistShuffleSessionState();
        _settings.FavoriteTracks = FavoritesManager.GetOrder();
        _settings.PinnedFavoriteTracks = FavoritesManager.GetPinnedPaths();
        _settings.PlayCounts = PlayCountManager.GetAll();

        if (asyncSave)
            FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
        else
            SettingsManager.Save(_settings);
    }

    protected override void OnClosed(EventArgs e)
    {
        // OnClosed — окно закрывается насовсем (в отличие от OnClosing): выставляем флаг, чтобы Closed-обработчик ShowChangelogWindow
        // не открыл настройки посреди выключения; таймер останавливаем до финального Save, чтобы не начал запись параллельно.
        _playbackRatePersistenceTimer.Stop();
        _settingsCheckpointTimer.Stop();
        _systemDefaultEndpointDebounceTimer.Stop();
        _playlistSearchDebounceTimer.Stop();
        StopGameOverlayDetectionTimer();
        StopHotkeyTrackRepeat();
        _playlistSearchCts?.Cancel();
        _settings.PlaybackSpeed = _runtimePlaybackRate;
        _isExiting = true;
        StopFolderWatchers();
        LocalizationService.LanguageChanged -= LocalizationService_LanguageChanged;
        _lifetimeCts.Cancel();
        _audioPlaybackCoordinator.CancelCurrentLoad();
        _replayGainCts?.Cancel();
        _waveformCts?.Cancel();
        CancelMainWindowLyricsLoad();

        // Сохраняем состояние ДО остановки — StopPlayback ниже обнуляет _audioFile, а
        // PersistPlaybackAndPlaylistState читает текущую позицию именно из него.
        FlushPlaybackClock();
        PersistPlaybackAndPlaylistState();
        StopPlayback(disposeOnly: true);
        _audioPlaybackCoordinator.Dispose();
        // StopPlayback уже освобождает WasapiPlayer и endpoint между треками. Повторный вызов
        // остаётся безопасной подстраховкой для частично инициализированного output при ошибке.
        DisposeOutputDeviceSafely();
        if (_audioOutputEndpointMonitor is not null)
        {
            _audioOutputEndpointMonitor.EndpointChanged -= AudioOutputEndpointMonitor_EndpointChanged;
            _audioOutputEndpointMonitor.Dispose();
            _audioOutputEndpointMonitor = null;
        }
        _discordRichPresence.Dispose();
        _integrations.HotKeys?.Dispose();
        _integrations.NowPlaying?.Dispose();
        _integrations.Tray?.Dispose();
        _miniPlayerWindow?.Close();
        _settingsWindow?.Close();
        _statisticsWindow?.Close();
        _trackChangeToastController.Dispose();
        _changelogWindow?.Close();
        _coverArtWindow?.Close();
        _nowPlayingWindow?.Close();
        // Track-load, ReplayGain и waveform tasks владеют своими CTS и освобождают их сами после отмены: здесь Dispose нельзя,
        // пока task ещё может обращаться к TokenSource.
        _lifetimeCts.Dispose();

        base.OnClosed(e);
    }
}
