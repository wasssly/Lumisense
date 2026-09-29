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

// Переход между треками (Prev/Next, зажатые хоткеи), сессия шаффла и режим повтора. Вынесено из
// MainWindow.xaml.cs только ради навигации, логика и порядок вызовов не менялись.
public partial class MainWindow
{
    void IIntegrationHost.PrevButton_Click(object sender, RoutedEventArgs e) => PrevButton_Click(sender, e);

    private void PrevButton_Click(object sender, RoutedEventArgs e)
    {
        if (ComputePreviousTrackPath(GetCurrentTrackPath()) is { } prevPath)
            LoadAndPlay(prevPath, autoPlay: _isPlaying, albumArtDirection: AlbumArtTransitionDirection.Previous,
                preserveShuffleSession: true, preservePendingPlaybackState: true);
    }

    private void NextButton_Click(object sender, RoutedEventArgs e) => PlayNextTrack();

    void IIntegrationHost.PlayNextTrack(TrackChangeOrigin changeOrigin) => PlayNextTrack(changeOrigin);

    private void PlayNextTrack(TrackChangeOrigin changeOrigin = TrackChangeOrigin.User)
    {
        if (ResolveNextTrackPathRespectingQueue(GetCurrentTrackPath()) is { } nextPath)
            LoadAndPlay(nextPath, autoPlay: _isPlaying, changeOrigin: changeOrigin, preserveShuffleSession: true,
                preservePendingPlaybackState: true);
    }

    // Отдаёт и убирает первый элемент очереди "Играть следующим", иначе обычная логика (ComputeNextTrackPath); использовать
    // только если путь точно пойдёт в LoadAndPlay (см. PlaybackQueue.PopNext и CommitPendingHotkeyTrackStep).
    private string? ResolveNextTrackPathRespectingQueue(string? currentPath)
    {
        // Файл могли удалить или переместить после постановки в очередь: пропускаем такие записи, чтобы переключение не превращалось
        // в ошибку открытия трека.
        _playbackQueue.PruneMissing();
        return _playbackQueue.PopNext() ?? ComputeNextTrackPath(currentPath);
    }

    // Чистое вычисление пути следующего/предыдущего трека без загрузки и декодирования: позволяет "прокрутить" несколько шагов
    // (HandleHotkeyNext/Previous); двигает индекс по плейлисту или истории шафла (GetShuffleHistoryTrack), это дёшево.
    private List<string> GetAvailableActiveTracks()
    {
        DateTime now = DateTime.UtcNow;
        if (_availableTracksNavigationCache is not null
            && _availableTracksNavigationCacheIsFavoritesView == _isFavoritesView
            && (now - _availableTracksNavigationCacheCreatedUtc).TotalMilliseconds <= NavigationAvailabilityCacheMilliseconds)
        {
            return _availableTracksNavigationCache;
        }

        // Единственная массовая File.Exists-проверка в коротком окне серии; устаревший путь не откроется вслепую —
        // LoadAndPlay повторяет проверку перед созданием AudioFileReader.
        _availableTracksNavigationCache = FlattenActive().Where(File.Exists).ToList();
        _availableTracksNavigationCacheIsFavoritesView = _isFavoritesView;
        _availableTracksNavigationCacheCreatedUtc = now;
        return _availableTracksNavigationCache;
    }

    private string? ComputeNextTrackPath(string? currentPath, List<string>? activeTracks = null)
    {
        // Файлы могли исчезнуть после построения плейлиста: обычная навигация проверяет доступность сразу, при удержании hotkey
        // берётся короткоживущий snapshot, а LoadAndPlay всё равно перепроверит конечный путь.
        var active = activeTracks ?? GetAvailableActiveTracks();
        if (active.Count == 0) return null;

        if (_shuffleSession.IsEnabled)
        {
            // После шага назад по истории шафла "вперёд" сначала возвращает туда, откуда ушли, и только по исчерпании истории
            // генерирует новый случайный трек и дописывает его в конец.
            return GetShuffleHistoryTrack(+1, active, currentPath)
                   ?? AppendNewShuffleTrack(active, currentPath);
        }

        int posInActive = currentPath != null ? active.IndexOf(currentPath) : -1;
        int nextPos = posInActive < 0 ? 0 : (posInActive + 1) % active.Count;
        return active[nextPos];
    }

    private string? ComputePreviousTrackPath(string? currentPath, List<string>? activeTracks = null)
    {
        // См. ComputeNextTrackPath: во время одного удержания hotkey повторно используем
        // уже проверенный snapshot, а обычная навигация всегда строит актуальный список.
        var active = activeTracks ?? GetAvailableActiveTracks();
        if (active.Count == 0) return null;

        if (_shuffleSession.IsEnabled)
        {
            // Идём на шаг назад по истории шафла; если назад некуда (самый первый "назад"), подбираем случайный трек и дописываем
            // его в начало истории, чтобы дальнейшие "вперёд"/"назад" оставались последовательными.
            return GetShuffleHistoryTrack(-1, active, currentPath)
                   ?? PrependNewShuffleTrack(active, currentPath);
        }

        int posInActive = currentPath != null ? active.IndexOf(currentPath) : -1;
        int prevPos = posInActive <= 0 ? active.Count - 1 : posInActive - 1;
        return active[prevPos];
    }

    // Быстрое переключение зажатой хоткей-клавишей: первый переход сразу, при удержании через 280 мс — собственный повтор каждые 140 мс
    // (~7 треков/сек), независимый от автоповтора Windows; LoadAndPlay отменяет прошлую подготовку, а частота жёстко ограничена.
    private const int HotkeyTrackInitialHoldDelayMs = 280;
    private const int HotkeyTrackRepeatIntervalMs = 140;
    // Короткий опрос нужен только до начала повтора: он быстро замечает отпускание клавиши,
    // поэтому два отдельных быстрых нажатия не смешиваются с её удержанием.
    private const int HotkeyTrackReleasePollIntervalMs = 30;
    private readonly DispatcherTimer _hotkeyTrackStepTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(HotkeyTrackReleasePollIntervalMs)
    };
    private int _pendingHotkeyNetSteps;
    private int _heldHotkeyVirtualKey;
    private int _heldHotkeyDirection;
    private bool _hotkeyTrackRepeatStarted;
    private DateTime _hotkeyTrackHoldStartedUtc;
    private string? _hotkeyTrackNavigationCursor;
    // Список доступных путей за одно удержание почти не меняется: снимок исключает синхронный File.Exists по плейлисту каждые 140 ms,
    // а LoadAndPlay всё равно перепроверит конечный путь.
    private List<string>? _hotkeyAvailableTracksSnapshot;

    void IIntegrationHost.HandleHotkeyNext(int virtualKey) => HandleHotkeyNext(virtualKey);

    private void HandleHotkeyNext(int virtualKey) => HandleHotkeyTrackStep(+1, virtualKey);

    void IIntegrationHost.HandleHotkeyPrevious(int virtualKey) => HandleHotkeyPrevious(virtualKey);

    private void HandleHotkeyPrevious(int virtualKey) => HandleHotkeyTrackStep(-1, virtualKey);

    private void HandleHotkeyTrackStep(int stepDirection, int virtualKey)
    {
        // Повторы WM_HOTKEY при удержании игнорируем: собственный таймер сам опрашивает физическое отпускание и выдаёт шаги
        // с фиксированной безопасной частотой, независимо от настроек Windows.
        bool sameHeldKey = _hotkeyTrackStepTimer.IsEnabled
            && _heldHotkeyVirtualKey == virtualKey && _heldHotkeyDirection == stepDirection;
        if (sameHeldKey)
            return;

        _hotkeyAvailableTracksSnapshot = GetAvailableActiveTracks();
        _pendingHotkeyNetSteps += stepDirection;
        CommitPendingHotkeyTrackStep();

        _heldHotkeyVirtualKey = virtualKey;
        _heldHotkeyDirection = stepDirection;
        _hotkeyTrackRepeatStarted = false;
        _hotkeyTrackHoldStartedUtc = DateTime.UtcNow;
        _hotkeyTrackStepTimer.Stop();

        // Короткий polling начинается сразу: он отделяет быстрое одиночное нажатие от
        // удержания. Само повторение начнётся только через HotkeyTrackInitialHoldDelayMs.
        if (virtualKey != 0 && GlobalMediaHotKeys.IsVirtualKeyDown(virtualKey))
        {
            _hotkeyTrackStepTimer.Interval = TimeSpan.FromMilliseconds(HotkeyTrackReleasePollIntervalMs);
            _hotkeyTrackStepTimer.Start();
        }
    }

    private void HotkeyTrackStepTimer_Tick(object? sender, EventArgs e)
    {
        if (_heldHotkeyVirtualKey == 0 || !GlobalMediaHotKeys.IsVirtualKeyDown(_heldHotkeyVirtualKey))
        {
            StopHotkeyTrackRepeat();
            return;
        }

        if (!_hotkeyTrackRepeatStarted)
        {
            double heldMilliseconds = (DateTime.UtcNow - _hotkeyTrackHoldStartedUtc).TotalMilliseconds;
            if (heldMilliseconds < HotkeyTrackInitialHoldDelayMs)
            {
                _hotkeyTrackStepTimer.Interval = TimeSpan.FromMilliseconds(HotkeyTrackReleasePollIntervalMs);
                _hotkeyTrackStepTimer.Start();
                return;
            }

            _hotkeyTrackRepeatStarted = true;
        }

        _pendingHotkeyNetSteps += _heldHotkeyDirection;
        CommitPendingHotkeyTrackStep();
        _hotkeyTrackStepTimer.Interval = TimeSpan.FromMilliseconds(HotkeyTrackRepeatIntervalMs);
        _hotkeyTrackStepTimer.Start();
    }

    private void StopHotkeyTrackRepeat()
    {
        _hotkeyTrackStepTimer.Stop();
        _pendingHotkeyNetSteps = 0;
        _heldHotkeyVirtualKey = 0;
        _heldHotkeyDirection = 0;
        _hotkeyTrackRepeatStarted = false;
        _hotkeyTrackHoldStartedUtc = DateTime.MinValue;
        _hotkeyTrackNavigationCursor = null;
        _hotkeyAvailableTracksSnapshot = null;
    }

    private void CommitPendingHotkeyTrackStep()
    {
        int steps = _pendingHotkeyNetSteps;
        _pendingHotkeyNetSteps = 0;
        if (steps == 0) return;

        // NavigationCursor хранит уже запрошенный путь, пока асинхронная загрузка не сделала его CurrentTrackPath: так удержание
        // идёт по последовательности, а не запрашивает один и тот же файл.
        var direction = steps > 0 ? AlbumArtTransitionDirection.Next : AlbumArtTransitionDirection.Previous;
        string? path = _hotkeyTrackNavigationCursor ?? GetCurrentTrackPath();
        List<string>? activeTracksSnapshot = _hotkeyAvailableTracksSnapshot;
        string? targetPath = null;

        for (int i = 0; i < Math.Abs(steps); i++)
        {
            string? next = steps > 0
                ? ComputeNextTrackPath(path, activeTracksSnapshot)
                : ComputePreviousTrackPath(path, activeTracksSnapshot);
            if (next == null) break;
            targetPath = next;
            path = next;
        }

        if (targetPath == null) return;
        _hotkeyTrackNavigationCursor = targetPath;
        LoadAndPlay(targetPath, autoPlay: _isPlaying, albumArtDirection: direction, preserveShuffleSession: true,
            preservePendingPlaybackState: true);
    }

    private string? GetShuffleHistoryTrack(int shift, List<string> activeTracks, string? currentPath) =>
        _shuffleSession.GetHistoryTrack(shift, activeTracks, currentPath);

    private string AppendNewShuffleTrack(List<string> activeTracks, string? currentPath) =>
        _shuffleSession.AppendNew(activeTracks, currentPath);

    private string PrependNewShuffleTrack(List<string> activeTracks, string? currentPath) =>
        _shuffleSession.PrependNew(activeTracks, currentPath);

    void IIntegrationHost.ShuffleButton_Click(object sender, RoutedEventArgs e) => ShuffleButton_Click(sender, e);

    private void ShuffleButton_Click(object sender, RoutedEventArgs e) => SetShuffleEnabled(!_shuffleSession.IsEnabled);

    // Вынесено из ShuffleButton_Click, чтобы применять то же (состояние и иконка) при восстановлении на старте без эмуляции клика.
    private void SetShuffleEnabled(bool enabled, bool resetSessionHistory = true)
    {
        _shuffleSession.SetEnabled(enabled);
        SetAccentButtonActive(ShuffleButton, enabled);
        IconResources.SetOnAccent(ShuffleIcon, enabled);
        ShuffleStateChanged?.Invoke(enabled);

        // Смена пользователем режима шаффла начинает новый заезд. Исключение — старт
        // приложения: там восстановим ранее сохранённую историю после загрузки плейлиста.
        if (resetSessionHistory)
            ResetShuffleState();
    }

    // Вызывается из настроек при переключении "Шаффл без повторов": колода старого/нового алгоритма после смены режима
    // бессмысленна, поэтому начинаем заново.
    public void ResetShuffleState() => _shuffleSession.Reset();

    // Вызывается после восстановления SavedPlaylistFolders: повреждённые, удалённые или выключенные пути не должны
    // делать «Назад» непредсказуемым, поэтому берём актуальные активные треки и ограничиваем сохранённый индекс.
    private void PersistShuffleSessionState() => _shuffleSession.PersistTo(_settings);

    private void RestoreShuffleSessionState()
    {
        if (!_settings.IsShuffleEnabled)
        {
            _shuffleSession.Reset();
            return;
        }

        var activePaths = new HashSet<string>(FlattenActive(), StringComparer.OrdinalIgnoreCase);
        _shuffleSession.RestoreFrom(_settings, activePaths, _settings.LastTrackPath);
    }

    void IIntegrationHost.RepeatButton_Click(object sender, RoutedEventArgs e) => RepeatButton_Click(sender, e);

    private void RepeatButton_Click(object sender, RoutedEventArgs e)
    {
        // Циклически переключаем: выключено -> повтор плейлиста -> повтор одного трека -> выключено
        var nextMode = _repeatMode switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off
        };

        SetRepeatMode(nextMode);
    }

    // Вынесено из RepeatButton_Click по той же причине, что и SetShuffleEnabled выше —
    // переиспользуется при восстановлении сохранённого состояния при запуске приложения.
    private void SetRepeatMode(RepeatMode mode)
    {
        _repeatMode = mode;

        switch (_repeatMode)
        {
            case RepeatMode.Off:
                RepeatButton.Icon = IconResources.Make("IconRepeatAll");
                SetAccentButtonActive(RepeatButton, false);
                RepeatButton.ToolTip = LocalizationService.Translate("Повтор: выключен");
                break;
            case RepeatMode.All:
                RepeatButton.Icon = IconResources.MakeOnAccent("IconRepeatAll");
                SetAccentButtonActive(RepeatButton, true);
                RepeatButton.ToolTip = LocalizationService.Translate("Повтор: весь плейлист");
                break;
            case RepeatMode.One:
                RepeatButton.Icon = IconResources.MakeOnAccent("IconRepeatOne");
                SetAccentButtonActive(RepeatButton, true);
                RepeatButton.ToolTip = LocalizationService.Translate("Повтор: один трек");
                break;
        }

        RepeatModeChanged?.Invoke(_repeatMode.ToString());
    }
}
