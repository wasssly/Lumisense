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

// Элемент для отображения в QueueItemsList (см. RefreshQueueUi) — DisplayName только для
// показа, реальные операции (удаление и т.д.) всегда идут по FilePath.
public sealed record QueueDisplayItem(string FilePath, string DisplayName, int Position);

public partial class MainWindow : FluentWindow, IIntegrationHost, INowPlayingHost, IMiniPlayerHost, ISettingsHost
{
    private enum RepeatMode { Off, All, One }

    // Три вида плеера (контекстное меню заголовка, TitleClickArea): квадратный (без плейлиста, Width == Height), прямоугольный
    // (с плейлистом, по умолчанию) и мини-плеер (отдельное окно MiniPlayerWindow).
    private enum PlayerViewMode { Square, Rectangular, Mini }

    // Поддерживаемые расширения — используются при сканировании папок
    private static readonly string[] SupportedExtensions = { ".mp3", ".wav", ".wma", ".flac", ".m4a", ".aac", ".ogg" };

    // AudioFileReader умеет читать mp3/wav/wma и сразу даёт регулировку громкости
    private AudioFileReader? _audioFile;
    private SoundTouchSampleProvider? _tempoProvider;

    // WasapiPlayer открывает поток для одного IWaveProvider, поэтому устройство создаётся заново на каждый трек; fade-out/fade-in
    // сглаживает переход, endpoint освобождается после Stop. Только Shared: системные звуки и другие приложения не блокируются.
    private IWavePlayer? _outputDevice;
    private MMDevice? _outputEndpoint;
    private readonly AudioOutputSession _audioOutputSession = new();
    private readonly AudioOutputDeviceManager _audioOutputDeviceManager = new();
    private readonly AudioOutputRecoveryCoordinator _audioOutputRecoveryCoordinator = new();
    private readonly AudioOutputRecoveryService _audioOutputRecoveryService;
    private readonly TrackExportService _trackExportService = new();
    private bool _trackExportInProgress;
    private AudioOutputEndpointMonitor? _audioOutputEndpointMonitor;
    private readonly AudioOutputDiagnostics _outputDiagnostics = new();
    private string? _pendingSystemDefaultEndpointId;
    private const int OutputRecoveryCooldownMilliseconds = 1500;
    private const int SystemDefaultEndpointDebounceMilliseconds = 180;

    // Сидит между _audioFile и _outputDevice в цепочке ISampleProvider (см. LoadAndPlay) —
    // громкость (AudioFileReader.Volume) применяется ДО эквалайзера, он только красит частоты.
    private EqualizerSampleProvider? _equalizer;
    // Измеряет уже обработанный эквалайзером сигнал для визуальной реакции Now Playing.
    private AudioLevelSampleProvider? _audioLevelMeter;
    private FadeInOutSampleProvider? _activeFade;
    // Быстрые повторные Pause/Play не должны пересекаться с fade и состоянием WASAPI.
    private bool _playPauseTransitionInProgress;
    // Храним нечётное число кликов, сделанных во время короткого fade, чтобы не терять
    // намерение пользователя и всё равно выполнять переключения строго последовательно.
    private bool _playPausePendingToggle;

    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    // Потолок естественного прироста позиции за тик (250 мс). С запасом на скорость до 2.0x
    // и задержки планировщика; всё, что больше — перемотка, а не прозвучавший звук.
    private const double MaxNaturalTickAdvanceSeconds = 1.5;
    private readonly DispatcherTimer _playbackRatePersistenceTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _systemDefaultEndpointDebounceTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(SystemDefaultEndpointDebounceMilliseconds)
    };

    // UI-настройки меняются в памяти без Save на каждое движение слайдера; checkpoint делает их устойчивыми к закрытию
    // консоли, SettingsManager пропускает неизменившийся JSON.
    private readonly DispatcherTimer _settingsCheckpointTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    // Пауза объединяет быстрый ввод поиска в один запрос, фильтрация снимка идёт вне Dispatcher — иначе каждое нажатие
    // перестраивало бы раскладку тысяч ListViewItem.
    private readonly DispatcherTimer _playlistSearchDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private CancellationTokenSource? _playlistSearchCts;
    private int _playlistSearchGeneration;
    private readonly Stopwatch _playbackClock = new();

    // Множитель громкости из ReplayGain-тегов трека (ReplayGainReader), 1.0 при выключенной настройке/нет тегов; домножается
    // на громкость в ComputeAudioFileVolume (AudioFileReader.Volume, до эквалайзера) — отдельный ISampleProvider не нужен.
    private double _replayGainFactor = 1.0;
    private Color? _coverAccentColor;
    // Waveform-полоса (AppSettings.ProgressBarStyle): кэш пиков по пути — пересчитывать волну при повторной загрузке трека
    // незачем; ограничен WaveformCacheLimit (LRU) и живёт только в сессии.
    private readonly Dictionary<string, float[]> _waveformCache = new();
    private readonly Dictionary<string, LinkedListNode<string>> _waveformCacheNodes = new();
    private readonly LinkedList<string> _waveformCacheOrder = new();
    private const int WaveformCacheLimit = 40;

    // Главная обложка 150 DIP, мини-плеер и уведомления меньше: ограничиваем декодирование UI-копии, чтобы огромные
    // embedded 4K-обложки не занимали память и не тормозили масштабирование при анимации.
    private const int ArtworkDisplayDecodePixelWidth = 512;

    // Отменяет расчёт формы волны предыдущего трека при быстром переключении: иначе устаревший результат перезапишет волну нового.
    private CancellationTokenSource? _waveformCts;
    private readonly AudioPlaybackCoordinator _audioPlaybackCoordinator = new();
    private readonly TrackPreparationService _trackPreparationService = new();
    private CancellationTokenSource? _replayGainCts;
    // Сохраняет исходное намерение воспроизведения только на время серии отменяемых Next/Previous.
    // См. LoadAndPlay: промежуточный StopPlayback не должен превратить последний переход в Pause.
    private bool _pendingNavigationAutoPlay;
    private readonly CancellationTokenSource _lifetimeCts = new();

    // Прослушивание засчитывается, когда реально сыграна (не перемотана) половина трека (ProgressTimer_Tick); сбрасывается
    // на каждую загрузку, включая повтор того же трека (RepeatMode.One).
    private bool _halfPlayCounted;

    // Фактически прозвучавшие секунды трека: считаются по приросту позиции между тиками, скачки от перемотки не
    // учитываются, иначе перетаскивание ползунка в конец сразу засчитывало бы прослушивание.
    private double _actuallyPlayedSeconds;
    private double _lastTickPositionSeconds = -1;

    // ObservableCollection: PlaylistFoldersControl (RestoreSavedPlaylistAsync) привязан один раз и получает через
    // CollectionChanged только реально новые/удалённые папки, без пересоздания контейнеров всех папок.
    private readonly ObservableCollection<PlaylistFolder> _folders = new();

    // FileSystemWatcher шлёт несколько событий на копирование файла, поэтому объединяем их в один повторный скан после
    // короткой паузы, а не пересобираем список на каждое уведомление.
    private readonly List<FileSystemWatcher> _folderWatchers = new();
    private readonly HashSet<string> _pendingFolderRefreshPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _folderRefreshDebounceTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _isFolderRefreshInProgress;

    // Пока конструктор не перенёс SavedPlaylistFolders в _folders, сохранение пустой коллекции затёрло бы плейлист в
    // settings.json при исключении в ранней инициализации; флаг true — после восстановления или подтверждённого отсутствия групп.
    private bool _playlistRestoreCompleted;

    // Виртуальная группа "Избранное" не входит в _folders и не сохраняется: собирается из FavoritesManager перед показом
    // (RefreshPlaylistView); один экземпляр переиспользуется, чтобы не терять IsExpanded при обновлении.
    private readonly PlaylistFolder _favoritesFolder = new()
    {
        DisplayName = "Избранное",
        IsFavoritesGroup = true
    };

    // true, пока вместо плейлиста показано "Избранное" (FavoritesButton_Click/SetFavoritesViewActive): влияет на PlaylistFoldersControl
    // и на список для "Далее"/"Назад"/шафла (FlattenAll/FlattenActive).
    private bool _isFavoritesView;

    private List<string>? _allTracksCache;
    private List<string>? _activeTracksCache;
    private bool _trackCachesAreFavoritesView;
    // FlattenActive кэширует состав плейлиста, но навигация проверяет File.Exists: короткий cache объединяет проверки для
    // серийных Next/Previous; LoadAndPlay всё равно проверяет итоговый путь перед открытием.
    private List<string>? _availableTracksNavigationCache;
    private bool _availableTracksNavigationCacheIsFavoritesView;
    private DateTime _availableTracksNavigationCacheCreatedUtc;
    private const int NavigationAvailabilityCacheMilliseconds = 500;

    // Нефильтрованные снимки строк для текущего вида. В отличие от прежнего Binding-конвертера,
    // поиск строит из них отдельный ItemsSource и не запускает массовую смену Visibility.
    private List<object> _playlistDisplayItems = new();
    private List<object> _favoriteDisplayItems = new();

    private readonly Random _random = new();

    // true, если settings.json ещё ни разу не сохранялся (самый первый запуск) — от этого зависит стартовый вид плеера
    // (ResolveStartupViewMode); проверяется до загрузки настроек, порядок не важен.
    private readonly bool _isFirstLaunch = !SettingsManager.HasSavedSettingsFile;
    private readonly AppSettings _settings = SettingsManager.Load();

    // Путь к треку, который сейчас загружен/играет. Хранится как путь, а не как индекс —
    // это позволяет треку спокойно доигрывать даже если его папку потом удалили из плейлиста.
    private string? _currentTrackPath;

    // Исходные значения до UI-fallback: нужны, чтобы при нормализации имени уже загруженного
    // трека сразу заново вывести исполнителя/название, не перезапуская AudioFileReader.
    private string? _currentTrackTaggedTitle;
    private string? _currentTrackTaggedArtist;

    private bool _isUserInteractingWithProgress;
    private bool _isSyncingProgressFromPlayback;
    private bool _isPlaying;
    private TrackUserState _trackUserState = TrackUserState.NoTrack;
    private readonly PlaybackStateMachine _playbackStateMachine = new();
    private readonly ShuffleSession _shuffleSession;

    // Очередь "Играть следующим" (см. PlaybackQueue) — временная вставка перед обычным
    // продолжением плейлиста/шаффла, см. ResolveNextTrackPathRespectingQueue.
    private readonly PlaybackQueue _playbackQueue = new();

    // Popup рендерится в отдельном визуальном дереве, где RelativeSource/DataContext-биндинги ненадёжны, поэтому очередь
    // заполняется из code-behind (как PlaybackControlPopup).
    private readonly ObservableCollection<QueueDisplayItem> _queueDisplayItems = new();
    private readonly ObservableCollection<PlaylistTrackRow> _unavailableFileRows = new();

    private bool _isMiniMode;

    // Отличает обычное свёрнутое окно от главного окна, только что открытого из мини-плеера внешней активацией: следующий клик
    // по кнопке в панели задач вернёт мини-плеер, не меняя обычное поведение «Свернуть».
    private bool _returnToMiniOnNextTaskbarMinimize;

    // Устанавливается строго на время синхронной обработки системной кнопки «Свернуть»
    // в TitleBar. Нужен, чтобы эта кнопка никогда не считалась кликом по панели задач.
    private bool _isSystemTitleBarMinimize;
    private RepeatMode _repeatMode = RepeatMode.Off;

    // Текущий вид (PlayerViewMode) и вид перед переходом в мини-режим — чтобы "развернуть" возвращал именно его,
    // а не вид по умолчанию.
    private PlayerViewMode _viewMode = PlayerViewMode.Square;
    private PlayerViewMode _preMiniViewMode = PlayerViewMode.Square;

    private const double DefaultWindowWidth = 440; // как задана ширина окна в XAML
    private double _lastNonZeroVolume = 0.3;

    private readonly MainWindowIntegrationController _integrations = new();
    private readonly DiscordRichPresenceManager _discordRichPresence = new();
    private MiniPlayerWindow? _miniPlayerWindow;

    private SettingsWindow? _settingsWindow;
    private StatisticsWindow? _statisticsWindow;
    private readonly TrackChangeToastController _trackChangeToastController = new();
    // См. GameOverlayDetectionService — эвристика, независимая от ручной галочки настроек,
    // может временно поднять эффективное состояние режима совместимости.
    private DispatcherTimer? _gameOverlayDetectionTimer;
    private bool _autoDetectedGameOverlayActive;
    private CoverArtWindow? _coverArtWindow;
    private NowPlayingWindow? _nowPlayingWindow;

    private bool _isExiting;
    // Стартовые значения Slider из XAML не сохраняем, пока ApplySettingsOnStartup не восстановит settings.json; потом
    // изменения пользователя сохраняются асинхронно, чтобы движение ползунка не блокировало UI.
    private bool _isApplyingStartupSettings = true;
    private bool _isOpeningPlaybackControlPopup;
    // Единственный runtime-источник скорости. До завершения InitializeComponent Slider не
    // имеет права менять его: XAML всегда создаёт Slider с Value=1.0.
    private double _runtimePlaybackRate;
    private bool _playbackRateIsReady;
    private bool _isUpdatingPlaybackRateControl;

    // Обычная ширина ContentHost совпадает со стартовой шириной окна, чтобы в исходном размере интерфейс выглядел как раньше.
    private const double NormalContentMaxWidth = 440;
    private bool _isFullscreenLayout;

    // Фиксированная ширина рабочей области квадратного вида (Square): в отличие от полноэкранного режима, где она
    // подстраивается под монитор, окно здесь обычное, поэтому предел ширины фиксирован.
    private const double SquareContentMaxWidth = 560;

    // События для внешнего окна мини-плеера (MiniPlayerWindow), которое не является частью
    // этого окна и получает обновления только через них
    public event Action<string, string, Brush?>? TrackInfoChanged;
    public event Action<double, double>? ProgressChanged;
    public event Action<bool>? PlaybackStateChanged;

    // Единый независимый снимок для мини-плеера, Now Playing и будущих интеграций. Старые
    // узкие события ниже сохраняются как совместимый фасад для уже существующих подписчиков.
    public PlaybackStateStore PlaybackState { get; } = new();

    // Только для мини-плеера: у него своя кнопка повтора (MiniPlayerWindow.RepeatButton_Click), её вид должен
    // синхронно следовать режиму, как бы его ни переключили — кнопкой, в основном окне или хоткеем.
    public event Action<string>? RepeatModeChanged;

    // Аналог RepeatModeChanged для "Перемешать" (MiniPlayerSecondaryButton показывает либо повтор, либо перемешать):
    // состояние меняется откуда угодно — из основного окна, мини-плеера или хоткеем.
    public event Action<bool>? ShuffleStateChanged;

    // Отдельно от VolumeSlider_ValueChanged (тот срабатывает и при загрузке сохранённой громкости): для индикатора процентов
    // мини-плеера при изменении хоткеями/скроллом; аргумент — итоговая громкость 0..1, как VolumeSlider.Value.
    public event Action<double>? VolumeChanged;

    private void PublishPlaybackSnapshot()
    {
        PlaybackState.Publish(new PlaybackSnapshot(
            _currentTrackPath,
            TrackTitleText.Text,
            TrackArtistText.Text,
            _isPlaying,
            CurrentPlaybackSeconds,
            CurrentTrackDurationSeconds));
    }

    private void RaiseTrackInfoChanged(string title, string artist, Brush? artBrush)
    {
        PublishPlaybackSnapshot();
        TrackInfoChanged?.Invoke(title, artist, artBrush);
    }

    private void RaiseProgressChanged(double currentSeconds, double totalSeconds)
    {
        PublishPlaybackSnapshot();
        ProgressChanged?.Invoke(currentSeconds, totalSeconds);
    }

    private void RaisePlaybackStateChanged(bool isPlaying)
    {
        ProgressMaterial.IsWaving = isPlaying;
        PublishPlaybackSnapshot();
        PlaybackStateChanged?.Invoke(isPlaying);
    }

    public bool IsMiniMode => _isMiniMode;
    public AppSettings Settings => _settings;

    // Явная реализация IIntegrationHost — доступно только через ссылку на этот интерфейс, не через window.IsPlaying.
    bool IIntegrationHost.IsPlaying => _isPlaying;

    public string CurrentTitle => TrackTitleText.Text;
    public string CurrentArtist => TrackArtistText.Text;
    // Мини-плеер и уведомление получают ImageBrush, хотя главное окно рисует artwork отдельным Image ради качества
    // масштабирования; кисть создаётся один раз при смене трека, а не на каждый запрос.
    public Brush? CurrentArtBrush => _currentArtBrush;

    // Сырые байты обложки (JPEG/PNG из тега), а не Brush/BitmapImage: TrayIconManager сам декодирует их через BitmapFrame
    // для миниатюры в меню трея (TrayIconManager.SetNowPlaying), не завися от WPF-типа остального кода.
    public byte[]? CurrentAlbumArtBytes => AlbumArtIcon.Visibility == Visibility.Visible ? null : _currentAlbumArtBytes;
    public BitmapImage? CurrentAlbumArt => _currentAlbumArt;
    public double CurrentPlaybackSeconds => _audioFile?.CurrentTime.TotalSeconds ?? 0;
    public double CurrentTrackDurationSeconds => _audioFile?.TotalTime.TotalSeconds ?? 0;
    public bool IsPlayingNow => _isPlaying;
    public AudioLevelSampleProvider? AudioLevelMeter => _audioLevelMeter;

    // Для мини-плеера: узнать режим повтора сразу при открытии, до первого RepeatModeChanged (как трек и состояние
    // воспроизведения в конструкторе MiniPlayerWindow).
    public string CurrentRepeatModeName => _repeatMode.ToString();

    // Зеркальный аналог CurrentRepeatModeName для перемешивания — см. ShuffleStateChanged.
    public bool CurrentIsShuffleEnabled => _shuffleSession.IsEnabled;

    // Нужен мини-плееру для варианта "Избранное" второй кнопки (UpdateFavoriteSecondaryButtonVisual): какой трек проверять;
    // null, пока ничего не загружено (первый запуск без сохранённого трека).
    public string? CurrentTrackPath => _currentTrackPath;

    // Пути Windows нечувствительны к регистру: один и тот же файл из разных плейлистов мог не опознаться как текущий трек.
    private static bool PathEquals(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void SettingsCheckpointTimer_Tick(object? sender, EventArgs e)
    {
        if (_isExiting) return;
        FireAndForget(SettingsManager.SaveIfChangedAsync(_settings), "SaveSettingsCheckpointAsync");
    }

    public MainWindow()
    {
        // Field-инициализатор не может ссылаться на _random (CS0236 — нестатическое поле), поэтому здесь, а не в объявлении поля.
        _shuffleSession = new ShuffleSession(_random);

        // Должно быть до InitializeComponent(): SvgPathIcon читает IconPacks.Current при первом построении дерева; дальнейшую
        // смену пака применяет IconPackContext, здесь только начальное значение.
        IconPacks.Initialize(_settings);

        // То же самое для Icon окна (см. AppIconContext, на который биндится Icon в XAML).
        AppIcons.Initialize(_settings);

        // Установщик и обновление пересоздают ярлыки со значком Aurora — возвращаем выбранный пользователем.
        if (AppIcons.Current != AppIcons.Aurora)
            BackgroundTask.FireAndForget(ShortcutIconSync.SyncAsync(AppIcons.Current), "ShortcutIconSync");

        _audioOutputRecoveryService = new(
            _audioOutputRecoveryCoordinator, TimeSpan.FromMilliseconds(OutputRecoveryCooldownMilliseconds));
        InitializeComponent();
        AccessibilityPreferences.ApplyToWindow(this, _settings);
        ApplyLyricsTextAlignment();
        LyricsPanelSyncedList.ItemsSource = _mainWindowSyncedLyrics;
        LocalizationService.Initialize(_settings, _isFirstLaunch);
        LocalizationService.Apply(this);
        SetTrackUserState(TrackUserState.NoTrack);
        LocalizationService.LanguageChanged += LocalizationService_LanguageChanged;
        ConfigureSystemTitleBarActions();
        // В этот момент ValueChanged от XAML уже мог сработать, но был проигнорирован до
        // _playbackRateIsReady. Теперь фиксируем единственное исходное значение из JSON.
        _runtimePlaybackRate = NormalizePlaybackRate(_settings.PlaybackSpeed);
        _settings.PlaybackSpeed = _runtimePlaybackRate;
        _playbackRateIsReady = true;

        FavoritesManager.Initialize(_settings.FavoriteTracks, _settings.PinnedFavoriteTracks);
        PlayCountManager.Initialize(_settings.PlayCounts);
        TrackContextMenuActions.Instance.Initialize(_settings.DisabledTrackContextMenuActions);
        MiniPlayerContextMenuActions.Instance.Initialize(_settings.DisabledMiniPlayerContextMenuActions);
        StartGameOverlayDetectionTimer();

        if (_settings.SaveQueueBetweenRestarts)
        {
            _playbackQueue.LoadFrom(_settings.SavedQueue);
            _playbackQueue.PruneMissing();
        }
        QueueItemsList.ItemsSource = _queueDisplayItems;
        UnavailableFilesList.ItemsSource = _unavailableFileRows;
        RefreshQueueUi();
        _playbackQueue.Changed += () =>
        {
            if (_settings.SaveQueueBetweenRestarts)
                _settings.SavedQueue = _playbackQueue.Items.ToList();
            RefreshQueueUi();
        };

        _progressTimer.Tick += ProgressTimer_Tick;
        _playlistSearchDebounceTimer.Tick += PlaylistSearchDebounceTimer_Tick;
        _folderRefreshDebounceTimer.Tick += FolderRefreshDebounceTimer_Tick;
        _hotkeyTrackStepTimer.Tick += HotkeyTrackStepTimer_Tick;
        _playbackRatePersistenceTimer.Tick += PlaybackRatePersistenceTimer_Tick;
        _settingsCheckpointTimer.Tick += SettingsCheckpointTimer_Tick;
        _systemDefaultEndpointDebounceTimer.Tick += SystemDefaultEndpointDebounceTimer_Tick;

        try
        {
            _audioOutputEndpointMonitor = new AudioOutputEndpointMonitor();
            _audioOutputEndpointMonitor.EndpointChanged += AudioOutputEndpointMonitor_EndpointChanged;
        }
        catch (Exception ex)
        {
            // Мониторинг endpoint — улучшение recovery, но его отсутствие не должно мешать
            // запуску плеера или штатному fallback при ошибке вывода.
            Logger.Warn($"Не удалось запустить мониторинг WASAPI-устройств: {ex.Message}");
        }

        // Rich Presence использует те же единые события, что и мини-плеер: это исключает
        // отдельный таймер, опрос UI и расхождение со сменой состояния аудиоустройства.
        TrackInfoChanged += (_, _, _) => UpdateDiscordRichPresence(force: true);
        PlaybackStateChanged += _ => UpdateDiscordRichPresence(force: true);
        ProgressChanged += (_, _) => UpdateDiscordRichPresence(force: false);
        ApplySettingsOnStartup();
        _playbackRatePersistenceTimer.Start();
        _settingsCheckpointTimer.Start();

        // Намеренно без await: проверка файлов и загрузка последнего трека идут в фоне, окно показывается сразу
        // (см. комментарий над RestoreSavedPlaylistAsync).
        FireAndForget(RestoreSavedPlaylistAsync(), "RestoreSavedPlaylistAsync");

        StateChanged += MainWindow_StateChanged;
        SizeChanged += MainWindow_SizeChanged;

        // Акцент применяем повторно после реальной отрисовки окна (MainWindow_Loaded): при AccentColorMode == "System" на части
        // машин часть элементов не подхватывает акцент, применённый до показа в ApplySettingsOnStartup.
        Loaded += MainWindow_Loaded;

        // Подстраховка для завершения сеанса Windows: OnClosing/OnClosed могут не успеть, а позиция трека, начатого перед
        // выключением, терялась бы до следующего автосохранения (ProgressTimer_Tick).
        System.Windows.Application.Current.SessionEnding += (_, _) => PersistPlaybackAndPlaylistState();
    }

    private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
    {
        foreach (PlaylistFolder folder in _folders)
            folder.RefreshLocalizedSubtitle();
        _favoritesFolder.RefreshLocalizedSubtitle();
        UpdateTrackUserStatePresentation();
    }

    // Первый ApplyAccentColor() идёт до Show(), а WPF-UI 3.0.5 не перечитывает DynamicResource-акцент для части элементов
    // до первой отрисовки (lepoco/wpfui #965/#981): повторяем после первого Loaded и сразу отписываемся.
    private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            // Slider и Popup созданы из XAML раньше, чем применены значения из settings.json (например, 0.75): повторяем
            // после полной загрузки дерева, чтобы поздняя инициализация WPF не вернула 1.0.
            SetPlaybackRate(_settings.PlaybackSpeed, persist: false);
            ApplyAccentColor();
        }), DispatcherPriority.Loaded);
    }

    // Полноэкранный режим включается кнопкой "Развернуть" (или двойным кликом по заголовку) — везде WindowState == Maximized,
    // его и отслеживаем.
    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        var fullscreen = WindowState == WindowState.Maximized;
        if (fullscreen != _isFullscreenLayout)
        {
            _isFullscreenLayout = fullscreen;
            ApplyFullscreenLayout(fullscreen);
        }

        // Только главный вид, восстановленный из мини-плеера внешней активацией, возвращает мини-плеер следующим сворачиванием
        // кнопкой панели задач; системная «Свернуть» исключена — она всегда оставляет обычное окно свёрнутым.
        if (WindowState == WindowState.Minimized
            && _returnToMiniOnNextTaskbarMinimize
            && !_isSystemTitleBarMinimize
            && !_isMiniMode)
        {
            _returnToMiniOnNextTaskbarMinimize = false;
            SetPlayerViewMode(PlayerViewMode.Mini);
        }
    }

    // Пересчитываем ширину ContentHost при изменении размера: иначе после растягивания квадратного окна или переноса на
    // другой монитор контент остался бы узким, с пустыми полями.
    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isFullscreenLayout || _viewMode == PlayerViewMode.Square) UpdateContentMaxWidth();
        if (QueuePopup?.IsOpen == true) UpdateQueuePopupWidth();
    }

    // Единственная точка первого показа окна (из App.OnStartup вместо StartupUri): EnsureHandle() создаёт HWND без показа
    // (нужен хоткеям/Now Playing/трею); Show() вызывается только при видимом старте.
    public void StartupPresent()
    {
        new WindowInteropHelper(this).EnsureHandle();

        PlayerViewMode startupMode = ResolveStartupViewMode(out bool? legacyPlaylistVisible);
        bool startsHidden = startupMode == PlayerViewMode.Mini || _settings.StartHiddenInTray;

        // Show()+Hide() нужны ради IsLoaded (иначе иконка в трее не регистрируется); чтобы окно не мелькало, DWM не даём начать
        // композицию: backdrop None, Left/Top за экраном, Minimized, ShowActivated=false, Opacity=0. Видимому старту не нужно.
        if (!IsLoaded && startsHidden)
        {
            double originalOpacity = Opacity;
            bool originalShowActivated = ShowActivated;
            Wpf.Ui.Controls.WindowBackdropType originalBackdrop = WindowBackdropType;

            // WindowStartupLocation=Manual обязателен до Left/Top: иначе CenterScreen из XAML
            // перецентрует окно при Show(), проигнорировав координаты ниже.
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -32000;
            Top = -32000;
            Opacity = 0;
            ShowActivated = false;
            WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;
            WindowState = WindowState.Minimized;
            Show();
            Hide();

            Opacity = originalOpacity;
            // Show() выше израсходовал CenterScreen из XAML, а Left/Top остались за экраном: центрируем при первом
            // настоящем показе (CenterOnFirstShowIfNeeded), когда известен фактический размер окна.
            _centerOnNextShow = true;
            // WindowState остаётся Minimized: у скрытого окна WPF не меняет состояние HWND, и Normal
            // оставил бы его свёрнутым — Show() не вывел бы окно. Разворачивает WindowState = Normal.
            ShowActivated = originalShowActivated;
            WindowBackdropType = originalBackdrop;
        }

        RestorePlayerViewMode(startupMode, legacyPlaylistVisible);

        if (_settings.StartHiddenInTray)
        {
            // RestorePlayerViewMode() выше мог уже создать и показать MiniPlayerWindow
            // (EnterMiniMode вызывает Show() безусловно, не зная про эту настройку) — прячем и его.
            _miniPlayerWindow?.Hide();

            // Если стартовый вид — мини-плеер, EnterMiniMode уже показал значок в трее —
            // не перезатираем его повторным Show().
            if (!_isMiniMode) _integrations.Tray?.Show("Lumisense");
        }
        else if (!_isMiniMode)
        {
            Show();

            // Обычного Show() иногда недостаточно, чтобы окно оказалось поверх остальных —
            // см. ForceForeground.
            ForceForeground(this);
        }

        // Уведомление migration не блокирует построение окна и не появляется в скрытом/мини-старте.
        // Оно срабатывает ровно раз после успешной установки Velopack MSI.
        if (IsVisible)
        {
            Dispatcher.BeginInvoke(new Action(UpdateMigrationGuard.TryShowFirstRunNotice),
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        FireAndForget(CheckForUpdatesOnStartupAsync(), "CheckForUpdatesOnStartupAsync");
    }

    // Windows может не отдать фокус свежему процессу (защита от кражи фокуса, заметно при запуске с закреплённого ярлыка):
    // кратковременный Topmost поднимает окно без постоянного Topmost=true; возвращаем исходное значение Topmost, а не false.
    private static void ForceForeground(Window window)
    {
        bool wasTopmost = window.Topmost;
        window.Topmost = true;
        window.Topmost = wasTopmost;
        window.Activate();
    }

    // Прогрев layout окна заранее (был WarmUpMainWindowLayout) убран: он переносил блокирующую работу с клика на старт;
    // первый разворот из мини-режима на большой библиотеке может занять время.

    // Тихая проверка на старте: в фоне и с задержкой, чтобы не отвлекать ресурсы от загрузки плейлиста/обложки; не показывает
    // диалог для версии, отклонённой кнопкой "Позже" (SkippedUpdateVersion); ошибки молча глотаются, ручная проверка их покажет.
    private async System.Threading.Tasks.Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            await System.Threading.Tasks.Task.Delay(System.TimeSpan.FromSeconds(3), _lifetimeCts.Token);
            if (_isExiting) return;

            var result = await UpdateChecker.CheckAsync();
            if (_isExiting || result.Status != UpdateCheckStatus.UpdateAvailable) return;
            if (result.LatestVersion != null && result.LatestVersion == _settings.SkippedUpdateVersion) return;

            if (_isExiting || !IsVisible) return;
            var dialog = new UpdateAvailableWindow(result, _settings);
            // Owner можно ставить только на уже показанное окно (актуально, если старт был в
            // мини-режиме — см. StartupPresent, тогда IsVisible всё ещё false).
            if (IsVisible) dialog.Owner = this;
            dialog.ShowDialog();
        }
        catch (OperationCanceledException)
        {
            // Нормальный путь при закрытии окна.
        }
        catch
        {
            // Фоновая необязательная проверка — молча игнорируем любые сбои
        }
    }

    // Плейлист хранится по группам, а воспроизведение работает с путями файлов, поэтому "плоские" списки считаем на лету
    // из групп (их размер не влияет на производительность заметно).

    // Пока открыт виртуальный плейлист "Избранное" (SetFavoritesViewActive), "Далее"/"Назад"/шафл и автопереход листают именно его,
    // поэтому обе "плоские" версии, от которых зависит навигация, подменяются списком избранного.
    private List<string> FlattenAll()
    {
        if (_allTracksCache != null && _trackCachesAreFavoritesView == _isFavoritesView)
            return _allTracksCache;

        _trackCachesAreFavoritesView = _isFavoritesView;
        _allTracksCache = _isFavoritesView
            ? FavoritesManager.GetAll()
            : _folders.SelectMany(f => f.Tracks).ToList();
        return _allTracksCache;
    }

    private List<string> FlattenActive()
    {
        if (_activeTracksCache != null && _trackCachesAreFavoritesView == _isFavoritesView)
            return _activeTracksCache;

        _trackCachesAreFavoritesView = _isFavoritesView;
        _activeTracksCache = _isFavoritesView
            ? FavoritesManager.GetAll()
            : _folders.Where(f => f.IsEnabled).SelectMany(f => f.Tracks).ToList();
        return _activeTracksCache;
    }

    private string? GetCurrentTrackPath() => _currentTrackPath;

    // Восстанавливает плейлист и последний трек; звук стартует, только если прошлый сеанс играл и автозапуск не запрещён.
    // Без File.Exists по каждому треку (давал "чёрный экран"): устаревшие записи убирает VerifyTrackExistenceInBackgroundAsync.
    private System.Threading.Tasks.Task RestoreSavedPlaylistAsync()
    {
        if (_settings.SavedPlaylistFolders.Count == 0)
        {
            _playlistRestoreCompleted = true;
            StartFolderWatchers();
            return System.Threading.Tasks.Task.CompletedTask;
        }

        try
        {
            var foldersThatWereNonEmpty = new HashSet<PlaylistFolder>();

            foreach (var saved in _settings.SavedPlaylistFolders)
            {
                var folder = new PlaylistFolder
                {
                    SourcePath = saved.SourcePath,
                    DisplayName = saved.DisplayName,
                    IsEnabled = saved.IsEnabled,
                    IsLooseFilesBucket = saved.IsLooseFilesBucket,
                    IsExpanded = saved.IsExpanded
                };
                if (saved.Tracks.Count > 0) foldersThatWereNonEmpty.Add(folder);

                folder.Tracks.AddRange(saved.Tracks);
                _folders.Add(folder);
            }

            RefreshPlaylistView();
            RestoreShuffleSessionState();
            _playlistRestoreCompleted = true;
            StartFolderWatchers();
            QueueAllFolderRefreshes();

            // Fire-and-forget — удаление устаревших записей не должно задерживать ни показ
            // плейлиста (уже показан), ни загрузку последнего трека чуть ниже.
            FireAndForget(VerifyTrackExistenceInBackgroundAsync(foldersThatWereNonEmpty), "VerifyTrackExistenceInBackgroundAsync");

            if (_folders.Count == 0) return System.Threading.Tasks.Task.CompletedTask;
            if (string.IsNullOrEmpty(_settings.LastTrackPath)) return System.Threading.Tasks.Task.CompletedTask;

            var all = FlattenAll();
            if (!all.Contains(_settings.LastTrackPath)) return System.Threading.Tasks.Task.CompletedTask;
            if (!File.Exists(_settings.LastTrackPath)) return System.Threading.Tasks.Task.CompletedTask; // единичная дешёвая проверка ОДНОГО файла — не массовое сканирование

            bool resumePlayback = _settings.WasPlayingOnClose && !_settings.NeverAutoPlayLastTrackOnStartup;
            LoadAndPlay(_settings.LastTrackPath, autoPlay: resumePlayback,
                startPosition: TimeSpan.FromSeconds(Math.Max(_settings.LastPositionSeconds, 0)),
                albumArtDirection: AlbumArtTransitionDirection.None,
                changeOrigin: TrackChangeOrigin.SessionRestore);
        }
        catch (Exception ex)
        {
            // Сохраняем исходные данные settings.json нетронутыми: при следующем запуске
            // восстановление можно повторить, а не закрепить пустой список на диске.
            Logger.Error("Не удалось восстановить сохранённый плейлист", ex);
        }

        return System.Threading.Tasks.Task.CompletedTask;
    }

    // Единственное обращение к диску для восстановленного плейлиста — после показа списка, поэтому окно появляется быстро;
    // AsParallel() ради HDD/сетевых путей с высокой задержкой, порядок удаления не важен (AsOrdered не нужен).
    private async System.Threading.Tasks.Task VerifyTrackExistenceInBackgroundAsync(HashSet<PlaylistFolder> foldersThatWereNonEmpty)
    {
        var foldersToCheck = _folders.ToList();
        bool anyChanged = false;
        bool folderWasRemoved = false;

        foreach (var folder in foldersToCheck)
        {
            if (!_folders.Contains(folder)) continue; // папку могли успеть удалить, пока проверяли предыдущую

            var tracksSnapshot = folder.Tracks.ToList();
            if (tracksSnapshot.Count == 0) continue;

            var missing = await System.Threading.Tasks.Task.Run(() =>
                tracksSnapshot.AsParallel().Where(f => !File.Exists(f)).ToList(), _lifetimeCts.Token);

            _lifetimeCts.Token.ThrowIfCancellationRequested();
            if (missing.Count == 0 || !_folders.Contains(folder) || _isExiting) continue;

            foreach (var path in missing)
                folder.Tracks.Remove(path);
            anyChanged = true;

            // Была непустой, а теперь опустела (все файлы удалены) — убираем саму папку, а не оставляем пустой заголовок
            // (см. foldersThatWereNonEmpty в RestoreSavedPlaylistAsync).
            if (folder.Tracks.Count == 0 && foldersThatWereNonEmpty.Contains(folder))
            {
                _folders.Remove(folder);
                folderWasRemoved = true;
            }
        }

        // folder.Tracks не является источником плоского списка PlaylistFoldersControl (PlaylistTrackRow): точечные изменения
        // не отражаются в нём, нужен один пересбор в конце, и только если что-то изменилось.
        if (anyChanged) RefreshPlaylistView();
        if (folderWasRemoved) StartFolderWatchers();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _integrations.InitializeMediaHotKeys(this);
        _integrations.InitializeNowPlayingIntegration(this);
        _integrations.InitializeTrayIcon(this);

        ApplyPlaybackButtonsVisibility();
    }

    // Кнопки остаются видимыми и кликабельными, но без фона — виден только значок; ховер/нажатие у ui:Button — отдельный
    // слой поверх Background, поэтому подсветка работает и с прозрачным фоном.
    public void ApplyPlaybackButtonsVisibility()
    {
        if (_settings.HidePlaybackButtons)
        {
            ShuffleButton.Background = System.Windows.Media.Brushes.Transparent;
            RepeatButton.Background = System.Windows.Media.Brushes.Transparent;
            PrevButton.Background = System.Windows.Media.Brushes.Transparent;
            PlayPauseButton.Background = System.Windows.Media.Brushes.Transparent;
            NextButton.Background = System.Windows.Media.Brushes.Transparent;
            StopButton.Background = System.Windows.Media.Brushes.Transparent;
            MiniModeButton.Background = System.Windows.Media.Brushes.Transparent;
        }
        else
        {
            // ClearValue, а не присваивание конкретного цвета — возвращает управление фоном
            // стилю WPF-UI, не дублируя и не угадывая его цвет по умолчанию.
            ShuffleButton.ClearValue(Button.BackgroundProperty);
            RepeatButton.ClearValue(Button.BackgroundProperty);
            PrevButton.ClearValue(Button.BackgroundProperty);
            PlayPauseButton.ClearValue(Button.BackgroundProperty);
            NextButton.ClearValue(Button.BackgroundProperty);
            StopButton.ClearValue(Button.BackgroundProperty);
            MiniModeButton.ClearValue(Button.BackgroundProperty);
        }
    }

    // WinForms-меню трея живёт в отдельном UI-стеке и не подхватывает тему WPF-UI: без явного вызова оставалось бы в прежней палитре.
    public void ApplyTrayTheme(bool isLight) => _integrations.Tray?.ApplyTheme(isLight);

    // WPF-UI отдаёт клик по кнопке сворачивания в MinimizeActionOverride; не полагаемся на поведение TitleBar: «Свернуть» уводит главное
    // окно только в панель задач, а в мини-плеер — лишь отдельная кнопка/пункт вида или повторная активация ярлыка.
    private void ConfigureSystemTitleBarActions()
    {
        AppTitleBar.MinimizeActionOverride = (_, window) =>
        {
            _isSystemTitleBarMinimize = true;
            // Пользователь явно выбрал обычное сворачивание, поэтому одноразовый маршрут
            // возврата в мини-плеер больше не должен срабатывать позже.
            _returnToMiniOnNextTaskbarMinimize = false;
            try
            {
                window.SetCurrentValue(Window.WindowStateProperty, WindowState.Minimized);
            }
            finally
            {
                _isSystemTitleBarMinimize = false;
            }
        };
    }

    // Явная реализация IIntegrationHost — доступ только через ссылку на интерфейс.
    void IIntegrationHost.RestoreFromTray() => RestoreFromTray();

    private void RestoreFromTray()
    {
        // Базовый NotifyIconService после OnLeftClick сам делает WindowState=Normal и Show(): у скрытого Minimized-окна это
        // рассинхронизирует WindowState и HWND (окно только в панели задач), поэтому разворачиваем до него.
        if (!IsVisible && Dispatcher.CheckAccess())
        {
            Show();
            WindowState = WindowState.Normal;
            CenterOnFirstShowIfNeeded();
            Logger.Info($"RestoreFromTray: окно было скрыто, развёрнуто синхронно (State={WindowState}, Left={Left:0}, Top={Top:0}, Mini={_isMiniMode}).");
        }

        Dispatcher.BeginInvoke(() =>
        {
            // Если активен мини-плеер, главное окно скрыто (EnterMiniMode): обычный Show() показал бы его поверх мини-плеера, поэтому
            // разворачиваем тем же путём, что кнопка "развернуть" мини-плеера (он закрывается, основное окно поднимается).
            if (_isMiniMode)
            {
                ExitMiniMode();
                return;
            }

            Show();
            WindowState = WindowState.Normal;
            CenterOnFirstShowIfNeeded();
            SyncPlaylistToCurrentTrackAfterShow();
            ForceForeground(this);
            _integrations.Tray?.Hide();
        });
    }

    void IIntegrationHost.ExitApplicationCompletely() => ExitApplicationCompletely();

    private void ExitApplicationCompletely()
    {
        Dispatcher.BeginInvoke(() =>
        {
            _isExiting = true;
            Close();
        });
    }

    // Из App при повторном запуске ярлыка (App.OnStartup/WaitForToggleSignal): в отличие от RestoreFromTray это переключение —
    // мини-плеер → обычное окно, видимое окно → мини-плеер; скрытое в трее или свёрнутое лишь восстанавливается, без мини-режима.
    public void ToggleMiniOrMainFromExternalActivation()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_isMiniMode)
            {
                ExitMiniMode(returnToMiniOnNextTaskbarMinimize: true);
                return;
            }

            if (Visibility != Visibility.Visible || WindowState == WindowState.Minimized)
            {
                if (Visibility != Visibility.Visible)
                    Show();

                WindowState = WindowState.Normal;
                CenterOnFirstShowIfNeeded();
                SyncPlaylistToCurrentTrackAfterShow();
                ForceForeground(this);
                _integrations.Tray?.Hide();
                return;
            }

            SetPlayerViewModeByName("Mini");
        });
    }

    private void ApplySettingsOnStartup()
    {
        _isApplyingStartupSettings = true;
        try
        {
            ApplicationThemeManager.Apply(_settings.IsLightThemeResolved() ? ApplicationTheme.Light : ApplicationTheme.Dark);
            ApplyAccentColor();
            ApplyWindowBackdrop();
            ApplyProgressBarStyle();
            ApplySliderStyle();

            if (_settings.AlwaysOnTop)
                Topmost = true;

            if (_settings.RememberVolume)
                VolumeSlider.Value = Math.Clamp(_settings.SavedVolume, 0.0, 1.0);

            SetPlaybackRate(_settings.PlaybackSpeed, persist: false);
            PlaybackPitchSlider.Value = Math.Clamp(_settings.PlaybackPitchSemitones, -12.0, 12.0);
            PlaybackPitchValueText.Text = FormatPlaybackPitch(PlaybackPitchSlider.Value);
            ApplyPlaybackPitchLive(PlaybackPitchSlider.Value);

            // На старте состояние кнопки шаффла нужно применить без очистки истории: сама
            // история будет отфильтрована и восстановлена после загрузки плейлиста.
            SetShuffleEnabled(_settings.IsShuffleEnabled, resetSessionHistory: false);
            SetRepeatMode(Enum.TryParse<RepeatMode>(_settings.RepeatMode, out var savedRepeatMode)
                ? savedRepeatMode
                : RepeatMode.Off);
        }
        finally
        {
            _isApplyingStartupSettings = false;
        }
    }

    // Вид полосы воспроизведения (AppSettings.ProgressBarStyle): на старте и из окна настроек на открытом плеере; ProgressSlider
    // всегда остаётся источником позиции/перемотки, переключается только видимое (см. MainWindow.xaml, ProgressWaveform).
    public void ApplyProgressBarStyle()
    {
        bool isWaveform = _settings.ProgressBarStyle == "Waveform";
        bool isMaterial = _settings.ProgressBarStyle == "Material";

        // Вид самого ползунка полосы задаётся только этой настройкой, а не общим «Видом ползунков».
        SliderAppearance.SetOverride(ProgressSlider, _settings.ProgressBarStyle == "MaterialSlider");

        ProgressSlider.Visibility = isWaveform || isMaterial ? Visibility.Collapsed : Visibility.Visible;
        ProgressWaveform.Visibility = isWaveform ? Visibility.Visible : Visibility.Collapsed;
        ProgressMaterial.Visibility = isMaterial ? Visibility.Visible : Visibility.Collapsed;
        ProgressMaterial.IsAnimationEnabled = !AccessibilityPreferences.ShouldReduceMotion(_settings);
        ProgressMaterial.IsWaving = _isPlaying;

        _nowPlayingWindow?.ApplyProgressBarStyle();

        if (isWaveform)
            FireAndForget(EnsureWaveformForCurrentTrackAsync(), "EnsureWaveformForCurrentTrackAsync");
    }

    // Вид всех Slider (AppSettings.SliderStyle): шаблоны подписаны на SliderAppearance, поэтому открытые окна обновляются сами.
    public void ApplySliderStyle() => SliderAppearance.Instance.Apply(_settings.SliderStyle);

    // Считает (или берёт из кэша) волну загруженного трека: из LoadAndPlay при режиме "Waveform" и из ApplyProgressBarStyle
    // при переключении на него на уже играющем треке.
    private async Task EnsureWaveformForCurrentTrackAsync()
    {
        string? filePath = _currentTrackPath;
        _waveformCts?.Cancel();

        if (filePath == null)
        {
            ProgressWaveform.Peaks = null;
            return;
        }

        if (_waveformCache.TryGetValue(filePath, out var cached))
        {
            ProgressWaveform.Peaks = cached;
            TouchWaveformCache(filePath);
            return;
        }

        // Пока считаем — показываем заглушку, а не форму волны предыдущего трека.
        ProgressWaveform.Peaks = null;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _waveformCts = cts;

        try
        {
            float[]? peaks = await WaveformGenerator.GenerateAsync(filePath, cts.Token);
            cts.Token.ThrowIfCancellationRequested();

            // Защита от устаревшего результата даже при изменении pipeline в будущем.
            if (!PathEquals(_currentTrackPath, filePath) || !ReferenceEquals(_waveformCts, cts)) return;

            if (peaks != null)
            {
                _waveformCache[filePath] = peaks;
                TouchWaveformCache(filePath);
                while (_waveformCacheOrder.Count > WaveformCacheLimit)
                {
                    string evicted = _waveformCacheOrder.First!.Value;
                    _waveformCacheOrder.RemoveFirst();
                    _waveformCacheNodes.Remove(evicted);
                    _waveformCache.Remove(evicted);
                }
            }
            ProgressWaveform.Peaks = peaks;
        }
        catch (OperationCanceledException)
        {
            // Новый трек или shutdown отменил расчёт; результат больше не нужен.
        }
        catch (Exception ex)
        {
            Logger.Error($"Не удалось построить waveform для файла: {filePath}", ex);
        }
        finally
        {
            if (ReferenceEquals(_waveformCts, cts)) _waveformCts = null;
            cts.Dispose();
        }
    }

    // Вызывается и при cache hit, иначе часто переслушиваемый трек вытеснялся бы как в FIFO.
    private void TouchWaveformCache(string filePath)
    {
        if (_waveformCacheNodes.TryGetValue(filePath, out var node))
            _waveformCacheOrder.Remove(node);

        _waveformCacheNodes[filePath] = _waveformCacheOrder.AddLast(filePath);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettingsWindow();

    private void StatisticsButton_Click(object sender, RoutedEventArgs e) => ShowStatisticsWindow();

    // Окно статистики (или уже открытое, как ShowSettingsWindow) — отдельное, не страница настроек: данные строятся асинхронно
    // (чтение тегов, StatisticsWindow.LoadAsync) и логически не относятся к настройкам.
    public void ShowStatisticsWindow()
    {
        if (_statisticsWindow == null)
        {
            _statisticsWindow = new StatisticsWindow(_settings) { Owner = this };
            _statisticsWindow.Closed += (_, _) => _statisticsWindow = null;
            _statisticsWindow.Show();
        }
        else
        {
            _statisticsWindow.Activate();
        }
    }

    // Открывает окно настроек (или активирует уже открытое); отдельный публичный метод, чтобы вызывать его не только по кнопке —
    // например, при закрытии списка изменений (ShowChangelogWindow).
    public void ShowSettingsWindow(string? section = null)
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_settings, this, section);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        else
        {
            // Окно уже открыто, возможно на другой странице (например, "Мини-плеер" из меню мини-плеера): переключаем страницу и
            // на открытом окне, а не только при создании.
            if (section != null) _settingsWindow.NavigateToPage(section);
            _settingsWindow.Activate();
        }
    }

    private ChangelogWindow? _changelogWindow;

    // Список изменений и настройки не открыты одновременно: открытие списка закрывает настройки, закрытие — открывает их
    // заново (вызывается из SettingsWindow.ChangelogButton_Click).
    public void ShowChangelogWindow()
    {
        if (_changelogWindow == null)
        {
            _changelogWindow = new ChangelogWindow(_settings) { Owner = this };
            _changelogWindow.Closed += (_, _) =>
            {
                _changelogWindow = null;
                if (!_isExiting) ShowSettingsWindow("About");
            };
            _changelogWindow.Show();
        }
        else
        {
            _changelogWindow.Activate();
        }

        _settingsWindow?.Close();
    }

    public void ShowNowPlayingWindow()
    {
        if (_nowPlayingWindow is null)
        {
            _nowPlayingWindow = new NowPlayingWindow(this);
            _nowPlayingWindow.Closed += (_, _) => _nowPlayingWindow = null;
            _nowPlayingWindow.Show();
        }
        else
        {
            _nowPlayingWindow.Activate();
        }
    }

    private void ShowNowPlayingMenuItem_Click(object sender, RoutedEventArgs e) => ShowNowPlayingWindow();

    private bool _isPlaylistVisible = true;
    private double _heightBeforeHidingPlaylist;

    private const double MinHeightWithPlaylist = 680; // как задан MinHeight окна в XAML

    // Квадратный вид использует крупный стиль (ApplyContentScale): без запаса сверх MinHeightWithPlaylist всё, что не влезло
    // в 680px, отнимало бы место у плейлиста вплоть до его исчезновения; запас оставляет видимыми несколько треков.
    private const double SquareMinHeightWithPlaylist = 860;

    public bool IsTrackContextMenuActionDisabled(string actionId) =>
        TrackContextMenuActions.Instance.IsDisabled(actionId);

    // Изменение тут же поднимает Epoch у TrackContextMenuActions.Instance, поэтому даже
    // созданные строки плейлиста пересчитывают Visibility без пересборки всего списка.
    public void SetTrackContextMenuActionDisabled(string actionId, bool disabled)
    {
        TrackContextMenuActions.Instance.SetDisabled(actionId, disabled);
        _settings.DisabledTrackContextMenuActions = TrackContextMenuActions.Instance.GetDisabledActionIds();
    }

    public bool IsMiniPlayerContextMenuActionDisabled(string actionId) =>
        MiniPlayerContextMenuActions.Instance.IsDisabled(actionId);

    // Epoch поднимается сразу, уже открытое (или следующее открытое) контекстное меню
    // мини-плеера пересчитывает Visibility без пересборки MiniPlayerWindow.
    public void SetMiniPlayerContextMenuActionDisabled(string actionId, bool disabled)
    {
        MiniPlayerContextMenuActions.Instance.SetDisabled(actionId, disabled);
        _settings.DisabledMiniPlayerContextMenuActions = MiniPlayerContextMenuActions.Instance.GetDisabledActionIds();
    }

    // Ctrl+Z на уровне окна: фокус между удалением и отменой мог уйти куда угодно; в текстовом поле не перехватываем —
    // там это отмена ввода.
    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F11)
        {
            ShowNowPlayingWindow();
            e.Handled = true;
            return;
        }

        bool isCtrl = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control);
        if (!isCtrl || e.Key != System.Windows.Input.Key.Z) return;
        if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
        if (_playlistDeleteUndoStack.Count == 0) return;

        e.Handled = true;
        _playlistDeleteUndoStack.Pop().Invoke();
    }

    private void CheckUnavailableFilesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        List<string> unavailablePaths = GetUnavailablePlaylistPaths();

        if (unavailablePaths.Count == 0)
        {
            LocalizedMessageBox.Show(
                this,
                LocalizationService.Translate("Все файлы плейлиста доступны."),
                LocalizationService.Translate("Недоступные файлы"),
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        string message = string.Format(
            LocalizationService.Translate("Недоступных файлов: {0}\n\nУбрать все такие записи из плейлиста и избранного?"),
            unavailablePaths.Count);
        if (LocalizedMessageBox.Show(
                this,
                message,
                LocalizationService.Translate("Недоступные файлы"),
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No) != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        foreach (PlaylistFolder folder in _folders)
            folder.Tracks.RemoveAll(path => !File.Exists(path));

        foreach (string missingFavorite in FavoritesManager.GetAll().Where(path => !File.Exists(path)).ToList())
            FavoritesManager.SetFavorite(missingFavorite, false);

        _playbackQueue.PruneMissing();
        RefreshPlaylistView();
        if (_isFavoritesView) RefreshFavoritesTrackList();
    }

    private List<string> GetUnavailablePlaylistPaths() => _folders
        .SelectMany(folder => folder.Tracks)
        .Concat(FavoritesManager.GetAll())
        .Where(path => !File.Exists(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private void RefreshUnavailableFilesBanner()
    {
        if (UnavailableFilesBanner is null || UnavailableFilesCountText is null) return;

        var regularRows = _folders
            .SelectMany(folder => folder.Tracks.Select((path, index) => new PlaylistTrackRow
            {
                Folder = folder,
                FilePath = path,
                IndexInFolder = index + 1
            }));
        var favoriteRows = FavoritesManager.GetAll().Select((path, index) => new PlaylistTrackRow
        {
            Folder = _favoritesFolder,
            FilePath = path,
            IndexInFolder = index + 1
        });
        var rows = regularRows
            .Concat(favoriteRows)
            .Where(row => !row.IsFileAvailable)
            .GroupBy(row => row.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        _unavailableFileRows.Clear();
        foreach (PlaylistTrackRow row in rows)
            _unavailableFileRows.Add(row);

        int unavailableCount = _unavailableFileRows.Count;
        UnavailableFilesBanner.Visibility = unavailableCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        UnavailableFilesCountText.Text = string.Format(
            LocalizationService.Translate("Недоступные: {0}"),
            unavailableCount);
        if (unavailableCount == 0)
            UnavailableFilesPopup.IsOpen = false;
    }

    private void UnavailableFilesDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_unavailableFileRows.Count == 0) return;
        UnavailableFilesPopup.IsOpen = !UnavailableFilesPopup.IsOpen;
    }

    private void CloseUnavailableFilesPopupButton_Click(object sender, RoutedEventArgs e) => UnavailableFilesPopup.IsOpen = false;

    private void GoToUnavailableTrackButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PlaylistTrackRow row }) return;

        UnavailableFilesPopup.IsOpen = false;
        if (row.Folder.IsFavoritesGroup)
            SetFavoritesViewActive(true);
        else if (_isFavoritesView)
            SetFavoritesViewActive(false);

        if (!row.Folder.IsExpanded)
            row.Folder.IsExpanded = true;
        RefreshPlaylistView();
        Dispatcher.BeginInvoke(new Action(() => HighlightAndScrollToTrack(row.Folder, row.FilePath)),
            DispatcherPriority.Loaded);
    }

    // Карточка показывается после готовности метаданных и обложки по политике (каждый трек, только старт или ручной выбор);
    // возобновление той же композиции этот метод не вызывает.
    private void ShowTrackChangeToast(TrackChangeOrigin origin, bool autoPlay)
    {
        if (!_settings.ShowTrackChangeToast || !ShouldShowTrackChangeToast(origin, autoPlay)) return;

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _trackChangeToastController.Show(TrackTitleText.Text, TrackArtistText.Text, CurrentArtBrush,
            _settings.IsLightThemeResolved(), ToastMonitorResolver.Resolve(_settings, handle), _settings);
    }

    private bool ShouldShowTrackChangeToast(TrackChangeOrigin origin, bool autoPlay) =>
        _settings.TrackChangeToastPolicy switch
        {
            "PlaybackOnly" => autoPlay,
            "ManualOnly" => origin == TrackChangeOrigin.User,
            _ => true // EveryTrackChange
        };

    // Вызывается из окна настроек сразу после того, как пользователь записал новую
    // комбинацию клавиш (или очистил старую) — применяет её без перезапуска приложения
    public void ReapplyHotkeys() => _integrations.HotKeys?.ApplyCustomHotkeys(_settings);

    public void ExternalPlayPause() => PlayPauseButton_Click(this, new RoutedEventArgs());
    public void ExternalNext() => PlayNextTrack();
    public void ExternalPrev() => PrevButton_Click(this, new RoutedEventArgs());
    public void ExternalChangeVolume(double delta) => ChangeVolumeBy(delta);
    public void ExternalToggleRepeat() => RepeatButton_Click(this, new RoutedEventArgs());
    public void ExternalToggleShuffle() => ShuffleButton_Click(this, new RoutedEventArgs());
    public void ExternalToggleMute() => ToggleMute();

    // Для "второй кнопки" мини-плеера в режиме "Избранное" (MiniPlayerWindow.SecondaryButton_Click): тот же метод, что у сердечка
    // в плейлисте (ToggleFavoriteAndRefresh), но путь — текущий трек; ничего не делает, если ничего не загружено.
    public void ExternalToggleFavoriteCurrentTrack()
    {
        if (_currentTrackPath != null) ToggleFavoriteAndRefresh(_currentTrackPath);
    }

    // Перетаскивание ползунков: Slider с IsHitTestVisible="False" только рисует, а мышь обрабатывает прозрачный Border поверх него —
    // перетаскивание идёт плавно из любой точки трека без конфликтов с логикой Thumb.

    private bool _isDraggingProgressOverlay;
    private bool _isDraggingVolumeOverlay;

    // Раз в ~30 секунд игры (120 тиков по 250 мс) сохраняем трек/позицию: при аварийном завершении позиция потеряется
    // не более чем на ~30 секунд (пауза, смена трека и выход сохраняют сразу, см. PersistPlaybackAndPlaylistState).
    private const int AutoSaveEveryNTicks = 120;
    private int _ticksSinceLastAutoSave;

    // Обновляет Rich Presence единым снимком аудиосостояния; длительность и позиция берутся только из AudioFileReader,
    // поэтому Discord не зависит от текстовых полей UI.
    private void UpdateDiscordRichPresence(bool force)
    {
        var audioFile = _audioFile;
        _discordRichPresence.Update(
            _settings,
            CurrentTitle,
            CurrentArtist,
            _isPlaying,
            audioFile?.CurrentTime.TotalSeconds ?? 0,
            audioFile?.TotalTime.TotalSeconds ?? 0,
            audioFile != null && !string.IsNullOrWhiteSpace(_currentTrackPath),
            force);
    }

    // Вызывается SettingsWindow сразу после изменения включения, Application ID или параметров
    // приватности. При выключении менеджер сам очищает активность и освобождает Discord IPC.
    public void ApplyDiscordRichPresenceSettingsLive()
    {
        UpdateDiscordRichPresence(force: true);
    }

    // Живо применяет только тему/акцент/подложку после импорта .lumi или сброса; остальное (хоткеи, эквалайзер, трей,
    // мини-плеер) читается при старте подсистем, поэтому SettingsWindow предлагает перезапуск.
    public void ApplyImportedSettingsLive()
    {
        ApplicationThemeManager.Apply(_settings.IsLightThemeResolved() ? ApplicationTheme.Light : ApplicationTheme.Dark);
        ApplyAccentColor();
        ApplyWindowBackdrop();
        _miniPlayerWindow?.ApplyArtworkProgressVisibility();
        _miniPlayerWindow?.ApplyArtworkProgressThickness();
        _miniPlayerWindow?.ApplyArtworkProgressColor();
        _miniPlayerWindow?.ApplyArtworkStyle();
        ApplyDiscordRichPresenceSettingsLive();
        TrackContextMenuActions.Instance.Initialize(_settings.DisabledTrackContextMenuActions);
        MiniPlayerContextMenuActions.Instance.Initialize(_settings.DisabledMiniPlayerContextMenuActions);
        ApplyPlaybackRateLive(_settings.PlaybackSpeed);
        ApplyPlaybackPitchLive(_settings.PlaybackPitchSemitones);
        IconPacks.SetCurrent(_settings.IconPack);
        AppIcons.SetCurrent(_settings.AppIcon);
    }

}
