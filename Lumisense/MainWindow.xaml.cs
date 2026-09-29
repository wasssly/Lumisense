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

// Элемент для отображения в QueueItemsList (см. RefreshQueueUi) — DisplayName только для
// показа, реальные операции (удаление и т.д.) всегда идут по FilePath.
public sealed record QueueDisplayItem(string FilePath, string DisplayName, int Position);

public partial class MainWindow : FluentWindow, IIntegrationHost
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
    private string? _activeOutputFormat;
    private long _lastOutputInitializationMilliseconds;
    private int _outputRecoveryCount;
    private string? _lastOutputRecoveryReason;
    private int _meaningfulOutputDeviceEventCount;
    private AudioOutputEndpointChangeKind? _lastOutputDeviceEventKind;
    private string? _lastOutputDeviceEventEndpointId;
    private string? _pendingSystemDefaultEndpointId;
    private const int OutputRecoveryCooldownMilliseconds = 1500;
    private const int SystemDefaultEndpointDebounceMilliseconds = 180;

    // Отдельно от settings.json храним то, что реально открыл WASAPI: так Settings объяснит fallback после отключения
    // USB/Bluetooth-устройства, и пользователь не гадает, куда идёт звук.
    private string _activeOutputDeviceKey = AudioOutputDeviceService.SystemDefaultDeviceName;
    private string? _outputDeviceFallbackFrom;
    // Реально применённый режим WASAPI ("Shared"/"Exclusive") — может отличаться от
    // _settings.WasapiMode сразу после автоматического отката в EnsureOutputDevice.
    private string _activeWasapiMode = "Shared";

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

    // Оборачивает fire-and-forget async-вызовы логированием исключения сразу: TaskScheduler.UnobservedTaskException
    // (App.xaml.cs) сработает лишь после сборки мусора, а иногда и вовсе не успеет до закрытия процесса.
    private static async void FireAndForget(Task task, string operationName)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            Logger.Error($"Ошибка в фоновой операции \"{operationName}\"", ex);
        }
    }

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

        _audioOutputRecoveryService = new(
            _audioOutputRecoveryCoordinator, TimeSpan.FromMilliseconds(OutputRecoveryCooldownMilliseconds));
        InitializeComponent();
        AccessibilityPreferences.ApplyToWindow(this, _settings);
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
                ForceForeground(this);
                _integrations.Tray?.Hide();
                return;
            }

            SetPlayerViewModeByName("Mini");
        });
    }

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

    private void ApplySettingsOnStartup()
    {
        _isApplyingStartupSettings = true;
        try
        {
            ApplicationThemeManager.Apply(_settings.IsLightThemeResolved() ? ApplicationTheme.Light : ApplicationTheme.Dark);
            ApplyAccentColor();
            ApplyWindowBackdrop();
            ApplyProgressBarStyle();

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

        ProgressSlider.Visibility = isWaveform ? Visibility.Collapsed : Visibility.Visible;
        ProgressWaveform.Visibility = isWaveform ? Visibility.Visible : Visibility.Collapsed;

        if (isWaveform)
            FireAndForget(EnsureWaveformForCurrentTrackAsync(), "EnsureWaveformForCurrentTrackAsync");
    }

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

    // Шеврон "Плейлист" скрывает/показывает панель независимо от PlayerViewMode: квадратный вид — увеличенное окно с крупным
    // стилем, а не просто "плейлист скрыт", так что это две независимые настройки.
    private void TogglePlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        SetPlaylistVisibility(!_isPlaylistVisible);
    }

    // Показывает/скрывает плейлист и подгоняет высоту окна; вынесено из TogglePlaylistButton_Click, чтобы применять
    // то же при восстановлении состояния на старте.
    private void SetPlaylistVisibility(bool visible)
    {
        _isPlaylistVisible = visible;

        if (_isPlaylistVisible)
        {
            PlaylistBorder.Visibility = Visibility.Visible;
            BodyGrid.RowDefinitions[6].Height = new GridLength(1, GridUnitType.Star);
            MinHeight = MinHeightWithPlaylist;
            Height = _heightBeforeHidingPlaylist > 0 ? _heightBeforeHidingPlaylist : MinHeightWithPlaylist;
        }
        else
        {
            _heightBeforeHidingPlaylist = Height;
            PlaylistBorder.Visibility = Visibility.Collapsed;
            BodyGrid.RowDefinitions[6].Height = new GridLength(0);

            // Захардкоженная высота оказывалась меньше нужной для контента (обложка, прогресс, кнопки, громкость) и обрезала
            // нижний край, поэтому WPF сам измеряет место, нужное оставшимся строкам грида.
            MinHeight = 0;
            SizeToContent = SizeToContent.Height;
            UpdateLayout();
            double collapsedHeight = ActualHeight;
            SizeToContent = SizeToContent.Manual;

            MinHeight = collapsedHeight;
            Height = collapsedHeight;
        }

        UpdatePlaylistSurface();
        TogglePlaylistButton.Icon = IconResources.Make(_isPlaylistVisible ? "IconChevronDown" : "IconChevronRight");
        TogglePlaylistButton.ToolTip = LocalizationService.Translate(_isPlaylistVisible ? "Скрыть плейлист" : "Показать плейлист");
    }

    // Панель показывает одно из трёх представлений (плейлист, избранное, текст песни): выбор содержимого отделён от
    // видимости панели — шеврон сворачивает весь блок, а кнопка текста заменяет только его содержимое.
    private void UpdatePlaylistSurface()
    {
        bool panelVisible = _isPlaylistVisible;
        bool showLyrics = panelVisible && _isLyricsPanelActive;
        bool showFavorites = panelVisible && !_isLyricsPanelActive && _isFavoritesView;
        bool showPlaylist = panelVisible && !_isLyricsPanelActive && !_isFavoritesView;

        PlaylistBorder.Visibility = panelVisible ? Visibility.Visible : Visibility.Collapsed;
        LyricsPanel.Visibility = showLyrics ? Visibility.Visible : Visibility.Collapsed;
        PlaylistSearchBox.Visibility = showPlaylist || showFavorites ? Visibility.Visible : Visibility.Collapsed;
        PlaylistFoldersControl.Visibility = showPlaylist ? Visibility.Visible : Visibility.Collapsed;
        FavoritesTrackListView.Visibility = showFavorites ? Visibility.Visible : Visibility.Collapsed;
        PlaylistScrollTrack.Visibility = showLyrics ? Visibility.Collapsed : Visibility.Visible;

        PlaylistHeaderText.Text = LocalizationService.Translate(showLyrics
            ? "Текст песни"
            : _isFavoritesView ? "Избранное" : "Плейлист");

        FavoritesButton.Visibility = showLyrics ? Visibility.Collapsed : Visibility.Visible;
        AddButton.Visibility = showPlaylist ? Visibility.Visible : Visibility.Collapsed;
        ClearPlaylistButton.Visibility = showPlaylist ? Visibility.Visible : Visibility.Collapsed;
        SetAccentButtonActive(FavoritesButton, _isFavoritesView && !showLyrics);
        LyricsPanelButton.Opacity = showLyrics ? 1.0 : 0.86;
    }

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

    // Ручное обновление — запасной вариант для сетевых папок и ФС без событий FileSystemWatcher; как и автообновление,
    // использует AddFolderPathAsync и добавляет только отсутствующие файлы.
    private async void RescanFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PlaylistFolder folder }) return;
        if (folder.SourcePath == null) return;

        int before = folder.Tracks.Count;
        bool foundAnything = await AddFolderPathAsync(folder.SourcePath);
        if (_isExiting) return;
        int addedCount = folder.Tracks.Count - before;

        if (!foundAnything || addedCount <= 0)
        {
            LocalizedMessageBox.Show(this, "Новых треков в этой папке не найдено.",
                "Ничего не найдено", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
    }

    private void RemoveFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PlaylistFolder folder }) return;

        // Просто убираем группу; играющий из неё трек не трогаем — он уже загружен и от списка не зависит, а на следующем
        // "Далее/Назад" плеер перейдёт к первому доступному активному треку.
        _folders.Remove(folder);
        RefreshPlaylistView();
        StartFolderWatchers();
    }

    // В отличие от удаления одной группы, очистка не оставляет, на что переключиться: если что-то играло, останавливаем
    // и возвращаем плеер в пустое состояние ("Файл не выбран"), а не даём треку доигрывать.
    private void ClearPlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        if (_folders.Count == 0) return;

        var confirm = LocalizedMessageBox.Show(
            this,
            "Очистить весь плейлист?\n\nВсе папки и файлы будут убраны из списка (сами файлы на диске не затрагиваются).",
            "Очистка плейлиста",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);

        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        StopPlayback();
        _currentTrackPath = null;
        _folders.Clear();
        RefreshPlaylistView();
        StartFolderWatchers();

        TrackTitleText.Text = LocalizationService.Translate("Файл не выбран");
        TrackArtistText.Text = "—";
        SetTrackUserState(TrackUserState.NoTrack);
        TotalTimeText.Text = "00:00";
        ResetAlbumArtPlaceholder();

        RaiseTrackInfoChanged(TrackTitleText.Text, TrackArtistText.Text, CurrentArtBrush);
    }

    // Полный пересбор при каждом изменении _folders дёшев благодаря виртуализации (ItemsSource — плоский список
    // PlaylistFolder/PlaylistTrackRow, PlaylistDisplaySelectors.cs); свёрнутые папки не кладут строки в список.
    private void RefreshPlaylistView()
    {
        _allTracksCache = null;
        _activeTracksCache = null;
        _availableTracksNavigationCache = null;
        _availableTracksNavigationCacheCreatedUtc = DateTime.MinValue;

        var items = new List<object>();

        foreach (var folder in _folders)
        {
            items.Add(folder);
            if (!folder.IsExpanded) continue;

            int index = 1;
            foreach (var path in folder.Tracks)
            {
                items.Add(new PlaylistTrackRow { Folder = folder, FilePath = path, IndexInFolder = index });
                index++;
            }
        }

        _playlistDisplayItems = items;
        RefreshUnavailableFilesBanner();
        QueuePlaylistSearch();
    }

    // Поиск применяется только после короткой паузы во вводе. Очистка поля, напротив,
    // возвращает полный снимок сразу — пользователь не видит устаревших результатов.
    private void PlaylistSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        QueuePlaylistSearch();

    private void QueuePlaylistSearch()
    {
        if (_isExiting || PlaylistSearchBox is null) return;

        _playlistSearchDebounceTimer.Stop();
        if (string.IsNullOrWhiteSpace(PlaylistSearchBox.Text))
        {
            Interlocked.Increment(ref _playlistSearchGeneration);
            _playlistSearchCts?.Cancel();
            ApplyPlaylistSearchResult(_isFavoritesView ? _favoriteDisplayItems : _playlistDisplayItems, _isFavoritesView);
            return;
        }

        _playlistSearchDebounceTimer.Start();
    }

    private void PlaylistSearchDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _playlistSearchDebounceTimer.Stop();
        FireAndForget(ApplyPlaylistSearchAsync(), "PlaylistSearchAsync");
    }

    private async Task ApplyPlaylistSearchAsync()
    {
        if (_isExiting) return;

        string query = PlaylistSearchBox.Text.Trim();
        bool favoritesView = _isFavoritesView;
        List<object> snapshot = (favoritesView ? _favoriteDisplayItems : _playlistDisplayItems).ToList();
        int generation = Interlocked.Increment(ref _playlistSearchGeneration);

        _playlistSearchCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _playlistSearchCts = cts;
        try
        {
            List<object> filtered = await Task.Run(
                () => FilterPlaylistDisplayItems(snapshot, query, cts.Token), cts.Token);

            if (_isExiting || cts.IsCancellationRequested || generation != Volatile.Read(ref _playlistSearchGeneration) ||
                favoritesView != _isFavoritesView)
                return;

            ApplyPlaylistSearchResult(filtered, favoritesView);
        }
        catch (OperationCanceledException)
        {
            // Новый символ в поле поиска отменяет устаревший запрос — это нормальный путь.
        }
        finally
        {
            if (ReferenceEquals(_playlistSearchCts, cts))
                _playlistSearchCts = null;
            cts.Dispose();
        }
    }

    private void ApplyPlaylistSearchResult(IEnumerable<object> items, bool favoritesView)
    {
        if (favoritesView)
            FavoritesTrackListView.ItemsSource = items;
        else
            PlaylistFoldersControl.ItemsSource = items;
    }

    // Плейлист — смешанный список заголовков и строк: при поиске заголовок остаётся только у папки с совпадениями, "Избранное"
    // проходит тем же методом. Работает над неизменяемым снимком в фоне — без обращений к WPF и диску.
    private static List<object> FilterPlaylistDisplayItems(
        IReadOnlyList<object> source, string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) return source.ToList();

        var filtered = new List<object>();
        PlaylistFolder? pendingFolder = null;
        bool pendingFolderAdded = false;

        foreach (object item in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item is PlaylistFolder folder)
            {
                pendingFolder = folder;
                pendingFolderAdded = false;
                continue;
            }

            if (item is not PlaylistTrackRow row || !MatchesPlaylistSearch(row.FilePath, query))
                continue;

            if (pendingFolder is not null && !pendingFolderAdded)
            {
                filtered.Add(pendingFolder);
                pendingFolderAdded = true;
            }
            filtered.Add(row);
        }

        return filtered;
    }

    private static bool MatchesPlaylistSearch(string? filePath, string query)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        return Path.GetFileNameWithoutExtension(filePath)
            .Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void FavoritesButton_Click(object sender, RoutedEventArgs e) => SetFavoritesViewActive(!_isFavoritesView);

    // Оба списка лежат друг на друге, переключается только Visibility (PlaylistFoldersControl не пересоздаётся); "Добавить"/
    // "Очистить" в избранном скрыты — в виртуальную группу нельзя добавлять файлы, очищать нечего.
    private void SetFavoritesViewActive(bool active)
    {
        _isFavoritesView = active;
        if (active)
        {
            _isLyricsPanelActive = false;
            CancelMainWindowLyricsLoad();
        }

        FavoritesButtonIcon.Icon = active ? "IconHeartFilled" : "IconHeart";
        IconResources.SetOnAccent(FavoritesButtonIcon, active);
        UpdatePlaylistSurface();

        if (active)
            RefreshFavoritesTrackList();
        else
            QueuePlaylistSearch();
    }

    // Пересобирает только содержимое виртуального "Избранного", не трогая PlaylistFoldersControl: стоимость зависит от числа
    // избранных, а не от размера библиотеки, поэтому вызывать можно часто.
    private void RefreshFavoritesTrackList()
    {
        var favorites = FavoritesManager.GetAll();

        _favoritesFolder.Tracks.Clear();
        _favoritesFolder.Tracks.AddRange(favorites);

        // Тот же PlaylistTrackRow и общий TrackItemTemplate (MainWindow.xaml); Folder — _favoritesFolder, без группировки и заголовков.
        var items = new List<object>(favorites.Count);
        for (int i = 0; i < favorites.Count; i++)
            items.Add(new PlaylistTrackRow { Folder = _favoritesFolder, FilePath = favorites[i], IndexInFolder = i + 1 });

        _favoriteDisplayItems = items;
        if (_isFavoritesView)
            QueuePlaylistSearch();
    }

    // Сердечко на строке (TrackFavoriteButton) и одноимённый пункт меню приходят сюда: путь берётся из унаследованного
    // DataContext строки (PlaylistTrackRow, см. TrackItemTemplate в MainWindow.xaml).
    private void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PlaylistTrackRow row }) return;
        ToggleFavoriteAndRefresh(row.FilePath);
    }

    private void FavoriteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: PlaylistTrackRow row }) return;
        ToggleFavoriteAndRefresh(row.FilePath);
    }

    // Минимальное обновление UI: сердечки в строках перерисовывает FavoritesChangeNotifier (DataTrigger в TrackItemTemplate),
    // вручную пересобирается лишь открытое виртуальное "Избранное", чтобы трек сразу исчезал при снятии сердечка.
    private void ToggleFavoriteAndRefresh(string filePath)
    {
        FavoritesManager.Toggle(filePath);

        if (_isFavoritesView)
            RefreshFavoritesTrackList();
    }

    // Закрепление наверху "Избранного" (FavoritesManager.TogglePin): кнопка и пункт меню видны только в нём
    // (Folder.IsFavoritesGroup) — порядок показа важен лишь на этой странице.
    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PlaylistTrackRow row }) return;
        TogglePinAndRefresh(row.FilePath);
    }

    private void PinMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: PlaylistTrackRow row }) return;
        TogglePinAndRefresh(row.FilePath);
    }

    // Как в ToggleFavoriteAndRefresh: иконку закрепления обновляет FavoritesChangeNotifier (IsPinnedMultiConverter/TrackPinIcon),
    // а порядок строк "Избранного" от закрепления меняется, поэтому список пересобираем явно.
    private void TogglePinAndRefresh(string filePath)
    {
        FavoritesManager.TogglePin(filePath);

        if (_isFavoritesView)
            RefreshFavoritesTrackList();
    }

    private void ToggleFolderExpand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PlaylistFolder folder }) return;
        folder.IsExpanded = !folder.IsExpanded;

        // Треки папки — отдельные элементы плоского списка (PlaylistTrackRow), а не содержимое вложенного контрола (раньше
        // сворачивание шло через Visibility по IsExpanded), поэтому их нужно добавлять/убирать из ItemsSource — явный пересбор.
        RefreshPlaylistView();
    }

    // PlaylistFoldersControl/FavoritesTrackListView — отдельные ListView со своим скроллом (VerticalScrollBarVisibility="Hidden");
    // общий скролл — один кастомный PlaylistScrollTrack/Thumb на ScrollViewer видимого списка (в XAML без имени, ищем по дереву).
    private System.Windows.Controls.ScrollViewer? _playlistFoldersScrollViewer;
    private System.Windows.Controls.ScrollViewer? _favoritesScrollViewer;

    private System.Windows.Controls.ScrollViewer? GetActivePlaylistScrollViewer()
        => _isFavoritesView
            ? _favoritesScrollViewer ??= FindVisualChild<System.Windows.Controls.ScrollViewer>(FavoritesTrackListView)
            : _playlistFoldersScrollViewer ??= FindVisualChild<System.Windows.Controls.ScrollViewer>(PlaylistFoldersControl);

    // PreviewMouseWheel идёт раньше bubbling MouseWheel встроенного скролла: e.Handled не даёт более резкому (~3 строки за деление)
    // скроллу сработать вдобавок к нашему.
    private void PlaylistTrackList_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        var scrollViewer = GetActivePlaylistScrollViewer();
        if (scrollViewer == null) return;

        e.Handled = true;

        // e.Delta приходит ~120 за деление колеса — переводим в пиксели сами, чтобы прокрутка
        // была плавной, а не резкими скачками по ~120px.
        const double pixelsPerNotch = 48.0;
        double offsetDelta = e.Delta / 120.0 * pixelsPerNotch;
        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - offsetDelta);
    }

    // Свой скроллбар плейлиста без ScrollBar/Track: PlaylistScrollTrack и PlaylistScrollThumb из XAML; ScrollChanged/SizeChanged
    // пересчитывают ползунок, клик по дорожке прыгает к точке, перетаскивание ползунка — ручной MouseCapture.
    private bool _isDraggingPlaylistThumb;
    private double _playlistThumbDragStartMouseY;
    private double _playlistThumbDragStartOffset;

    private void PlaylistScrollViewer_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        UpdatePlaylistScrollThumb();
    }

    // ScrollViewer обрезает содержимое прямоугольно, и при прокрутке карточки у края спорят со скруглённой рамкой PlaylistBorder:
    // свой Clip с радиусом 8 (как у карточек, а не 10 у рамки — иначе неконцентрично); общий обработчик клипует sender.
    private void PlaylistScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;

        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
        {
            element.Clip = null;
            return;
        }

        element.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 8, 8);
    }

    private void PlaylistScrollTrack_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePlaylistScrollThumb();
    }

    private void UpdatePlaylistScrollThumb()
    {
        var scrollViewer = GetActivePlaylistScrollViewer();
        if (scrollViewer == null)
        {
            PlaylistScrollThumb.Visibility = Visibility.Collapsed;
            return;
        }

        double trackHeight = PlaylistScrollTrack.ActualHeight;
        double extent = scrollViewer.ExtentHeight;
        double viewport = scrollViewer.ViewportHeight;
        double offset = scrollViewer.VerticalOffset;

        // Весь плейлист помещается на экран — прятать ползунок, скроллить нечего
        if (trackHeight <= 0 || extent <= viewport || extent <= 0)
        {
            PlaylistScrollThumb.Visibility = Visibility.Collapsed;
            return;
        }

        PlaylistScrollThumb.Visibility = Visibility.Visible;

        double rawThumbHeight = trackHeight * (viewport / extent);
        double thumbHeight = Math.Min(Math.Max(rawThumbHeight, 24), trackHeight);
        double maxThumbTop = Math.Max(0, trackHeight - thumbHeight);
        double maxOffset = Math.Max(0, extent - viewport);
        double thumbTop = maxOffset <= 0 ? 0 : Math.Clamp(offset / maxOffset * maxThumbTop, 0, maxThumbTop);

        PlaylistScrollThumb.Height = thumbHeight;
        PlaylistScrollThumb.Margin = new Thickness(0, thumbTop, 0, 0);
    }

    // Клик по дорожке (не по самому ползунку) — мгновенный прыжок к месту клика,
    // ползунок центрируется под курсором, как в обычных современных скроллбарах.
    private void PlaylistScrollTrack_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsDescendantOf(source, PlaylistScrollThumb)) return;
        if (PlaylistScrollThumb.Visibility != Visibility.Visible) return;

        var scrollViewer = GetActivePlaylistScrollViewer();
        if (scrollViewer == null) return;

        double trackHeight = PlaylistScrollTrack.ActualHeight;
        double extent = scrollViewer.ExtentHeight;
        double viewport = scrollViewer.ViewportHeight;
        double thumbHeight = PlaylistScrollThumb.ActualHeight;
        double maxThumbTop = Math.Max(0, trackHeight - thumbHeight);
        double maxOffset = Math.Max(0, extent - viewport);
        if (maxThumbTop <= 0 || maxOffset <= 0) return;

        double clickY = e.GetPosition(PlaylistScrollTrack).Y;
        double targetThumbTop = Math.Clamp(clickY - thumbHeight / 2, 0, maxThumbTop);
        double newOffset = targetThumbTop / maxThumbTop * maxOffset;

        scrollViewer.ScrollToVerticalOffset(newOffset);
    }

    private void PlaylistScrollThumb_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var scrollViewer = GetActivePlaylistScrollViewer();
        if (scrollViewer == null) return;

        _isDraggingPlaylistThumb = true;
        _playlistThumbDragStartMouseY = e.GetPosition(PlaylistScrollTrack).Y;
        _playlistThumbDragStartOffset = scrollViewer.VerticalOffset;
        PlaylistScrollThumb.CaptureMouse();
        e.Handled = true;
    }

    private void PlaylistScrollThumb_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDraggingPlaylistThumb) return;

        var scrollViewer = GetActivePlaylistScrollViewer();
        if (scrollViewer == null) return;

        double trackHeight = PlaylistScrollTrack.ActualHeight;
        double extent = scrollViewer.ExtentHeight;
        double viewport = scrollViewer.ViewportHeight;
        double thumbHeight = PlaylistScrollThumb.ActualHeight;
        double maxThumbTop = Math.Max(0, trackHeight - thumbHeight);
        double maxOffset = Math.Max(0, extent - viewport);
        if (maxThumbTop <= 0 || maxOffset <= 0) return;

        double currentY = e.GetPosition(PlaylistScrollTrack).Y;
        double deltaOffset = (currentY - _playlistThumbDragStartMouseY) / maxThumbTop * maxOffset;

        scrollViewer.ScrollToVerticalOffset(Math.Clamp(_playlistThumbDragStartOffset + deltaOffset, 0, maxOffset));
    }

    private void PlaylistScrollThumb_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isDraggingPlaylistThumb = false;
        PlaylistScrollThumb.ReleaseMouseCapture();
    }

    private static bool IsDescendantOf(DependencyObject element, DependencyObject ancestor)
    {
        var current = element;
        while (current != null)
        {
            if (ReferenceEquals(current, ancestor)) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void PlaylistTrackList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListView listView) return;
        if (listView.SelectedItem is not PlaylistTrackRow row) return;

        LoadAndPlay(row.FilePath);
    }

    // Delete удаляет выбранные (SelectionMode="Extended"), Ctrl+Z откатывает: стек только для удаления треков, не общий undo —
    // каждый Delete кладёт замыкание, полностью восстанавливающее удалённое; Ctrl+Z выполняет верхнее.
    private readonly Stack<Action> _playlistDeleteUndoStack = new();

    private void PlaylistTrackList_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Delete) return;
        if (sender is not System.Windows.Controls.ListView listView) return;

        var rows = listView.SelectedItems.OfType<PlaylistTrackRow>().ToList();
        if (rows.Count == 0) return;

        e.Handled = true;
        DeleteTracksFromPlaylist(rows);
    }

    // Убирает строки пачкой (как "Убрать из плейлиста", RemoveTrackMenuItem_Click) и кладёт в _playlistDeleteUndoStack
    // одно действие, откатывающее именно эту пачку.
    private void DeleteTracksFromPlaylist(IReadOnlyList<PlaylistTrackRow> rows)
    {
        var undoActions = new List<Action>();
        bool touchedFavorites = false;

        foreach (var group in rows.GroupBy(row => row.Folder))
        {
            var folder = group.Key;

            // В виртуальной группе "Избранное" своего списка треков нет — "удалить" значит
            // "снять сердечко", а отменить — поставить обратно.
            if (folder.IsFavoritesGroup)
            {
                foreach (var row in group)
                {
                    string path = row.FilePath;
                    FavoritesManager.SetFavorite(path, false);
                    undoActions.Add(() => FavoritesManager.SetFavorite(path, true));
                }
                touchedFavorites = true;
                continue;
            }

            // Индексы считаем ДО удаления — после RemoveAt они сдвигаются, поэтому удаляем по
            // убыванию индекса, а восстанавливаем при отмене по возрастанию.
            var indexed = group
                .Select(row => (Index: folder.Tracks.IndexOf(row.FilePath), row.FilePath))
                .Where(entry => entry.Index >= 0)
                .OrderByDescending(entry => entry.Index)
                .ToList();

            foreach (var (index, _) in indexed)
                folder.Tracks.RemoveAt(index);

            foreach (var (index, path) in indexed.OrderBy(entry => entry.Index))
            {
                var capturedFolder = folder;
                var capturedIndex = index;
                var capturedPath = path;
                undoActions.Add(() =>
                {
                    // Min — на случай, если список этой папки успел ещё уменьшиться между
                    // удалением и отменой.
                    int insertAt = Math.Min(capturedIndex, capturedFolder.Tracks.Count);
                    capturedFolder.Tracks.Insert(insertAt, capturedPath);
                });
            }
        }

        if (undoActions.Count == 0) return;

        RefreshPlaylistView();
        if (touchedFavorites && _isFavoritesView) RefreshFavoritesTrackList();

        _playlistDeleteUndoStack.Push(() =>
        {
            foreach (var undo in undoActions)
                undo();

            RefreshPlaylistView();
            if (touchedFavorites && _isFavoritesView) RefreshFavoritesTrackList();
        });
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
            LocalizedMessageBox.Show(
                this,
                LocalizationService.Translate("Не удалось открыть трек: файл недоступен."),
                LocalizationService.Translate("Недоступные файлы"),
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            RefreshPlaylistView();
            if (_isFavoritesView) RefreshFavoritesTrackList();
            _pendingNavigationAutoPlay = false;
            return;
        }

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

            // Готовим граф следующего трека, пока текущий поток доигрывает fade/drain; новый WasapiPlayer не создаём —
            // endpoint останавливается только после нулевого хвоста в его буфере.
            double volumeSliderValue = VolumeSlider.Value;
            bool replayGainEnabled = _settings.ReplayGainEnabled;
            bool equalizerEnabled = _settings.EqualizerEnabled;
            double[] equalizerGains = (double[])_settings.EqualizerBandGainsDb.Clone();
            double playbackSpeed = _runtimePlaybackRate;
            double playbackPitch = Math.Clamp(_settings.PlaybackPitchSemitones, -12.0, 12.0);
            bool traceTrackPreparation = _settings.TrackLoadTraceEnabled;
            preparationTask = _trackPreparationService.PrepareAsync(
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
                operation.CancellationToken);

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

            _audioFile = prepared.AudioFile;
            _tempoProvider = prepared.TempoProvider;
            ApplyPlaybackRateToCurrentStream();
            _equalizer = prepared.Equalizer;
            _replayGainFactor = prepared.ReplayGainFactor;
            PreparedTrack loaded = prepared;
            prepared = null;

            _currentTrackTaggedTitle = loaded.Title;
            _currentTrackTaggedArtist = loaded.Artist;
            var metadata = FileNameNormalizer.ResolveArtistAndTitle(
                filePath, _currentTrackTaggedArtist, _currentTrackTaggedTitle, "—");
            SetTrackInfoText(metadata.Title, metadata.Artist);
            TotalTimeText.Text = _audioFile.TotalTime.ToString(@"mm\:ss");
            ProgressSlider.Maximum = Math.Max(_audioFile.TotalTime.TotalSeconds, 0.01);
            _currentTrackPath = filePath;
            // Ручной выбор строки — новая отправная точка обычного шаффла, иначе «Следующий» продолжил бы старую историю и вернул
            // пройденную последовательность; кнопки, hotkey и автопереход явно передают preserveShuffleSession=true.
            if (!preserveShuffleSession && changeOrigin == TrackChangeOrigin.User)
                _shuffleSession.StartStandardSession(filePath, _settings.UseImprovedShuffle);

            _halfPlayCounted = false;
            _actuallyPlayedSeconds = 0;
            _lastTickPositionSeconds = -1;
            ApplyPreparedAlbumArt(loaded, albumArtDirection);
            performance.MarkStage("apply-track-ui");

            if (_settings.ProgressBarStyle == "Waveform")
                FireAndForget(EnsureWaveformForCurrentTrackAsync(), "EnsureWaveformForCurrentTrackAsync");

            var position = startPosition.HasValue && startPosition.Value < _audioFile.TotalTime
                ? startPosition.Value
                : TimeSpan.Zero;
            _audioFile.CurrentTime = position;
            ProgressSlider.Value = position.TotalSeconds;
            CurrentTimeText.Text = position.ToString(@"mm\:ss");
            ProgressWaveform.Progress = _audioFile.TotalTime.TotalSeconds > 0
                ? position.TotalSeconds / _audioFile.TotalTime.TotalSeconds
                : 0;
            _integrations.NowPlaying?.UpdateTrackInfo(TrackTitleText.Text, TrackArtistText.Text);
            RaiseTrackInfoChanged(TrackTitleText.Text, TrackArtistText.Text, CurrentArtBrush);
            RaiseProgressChanged(position.TotalSeconds, _audioFile.TotalTime.TotalSeconds);

            // Сохраняем shuffle-сессию сразу после применения трека: периодического checkpoint мало — при быстром переключении
            // и выходе история/колода остались бы устаревшими.
            if (_shuffleSession.IsEnabled && _playlistRestoreCompleted)
                PersistPlaybackAndPlaylistState(asyncSave: true);

            // Скрытая панель текста ничего не делает в фоне; если она открыта, новая композиция отменяет прошлый запрос и
            // грузит свой LRC/TXT/кэш или точное онлайн-совпадение.
            if (_isLyricsPanelActive)
                FireAndForget(LoadMainWindowLyricsAsync(filePath), "LoadMainWindowLyricsAsync");

            _audioLevelMeter = new AudioLevelSampleProvider(_equalizer!);
            var fadeIn = new FadeInOutSampleProvider(_audioLevelMeter, initiallySilent: true);
            fadeIn.BeginFadeIn(70);
            _activeFade = fadeIn;
            InitializeOutputDevice(fadeIn);
            _settingsWindow?.RefreshOutputDeviceRuntimeStatus();
            performance.MarkStage("initialize-output");
            _outputDevice!.PlaybackStopped += OutputDevice_PlaybackStopped;
            ReapplySavedPlaybackRateAfterTrackReady(generation);
            if (autoPlay)
            {
                _outputDevice.Play();
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

                    if (playbackError is not null)
                    {
                        RecoverOutputDeviceAfterFailure(playbackError, resumePlayback: _isPlaying);
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
    // 30 мс вызывали щелчки на стыке треков (drain не успевал дождаться реального опустошения
    // WASAPI-буфера) — 60 мс безопасный минимум для плеера с realtime DSP (SoundTouch, эквалайзер).
    private const int WasapiRequestedLatencyMilliseconds = 60;

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

    private void InitializeOutputDevice(ISampleProvider sampleProvider)
    {
        try
        {
            CreateAndInitializeWasapiPlayer(sampleProvider);
        }
        catch (Exception ex) when (!string.IsNullOrWhiteSpace(_settings.OutputDeviceName))
        {
            // Endpoint мог исчезнуть между перечислением и Init (USB/Bluetooth). Однократно
            // переходим на системный render endpoint Windows, сохраняя причину fallback в UI.
            Logger.Error("Не удалось инициализировать выбранное устройство вывода; используется системное устройство", ex);
            string failedDeviceKey = _settings.OutputDeviceName;
            DisposeOutputDeviceSafely();
            _activeOutputDeviceKey = AudioOutputDeviceService.SystemDefaultDeviceName;
            _outputDeviceFallbackFrom = AudioOutputDeviceManager.GetFallbackSourceKey(failedDeviceKey, usedFallback: true);
            _settings.OutputDeviceName = AudioOutputDeviceService.SystemDefaultDeviceName;
            _ = SettingsManager.SaveAsync(_settings);
            _settingsWindow?.RefreshOutputDeviceSelection();
            CreateAndInitializeWasapiPlayer(sampleProvider);
        }
    }

    private void CreateAndInitializeWasapiPlayer(ISampleProvider sampleProvider)
    {
        EnsureOutputDevice();
        var initializationTimer = Stopwatch.StartNew();
        _audioOutputSession.Initialize(sampleProvider);
        initializationTimer.Stop();
        _lastOutputInitializationMilliseconds = initializationTimer.ElapsedMilliseconds;

        if (_outputDevice is WasapiPlayer wasapiPlayer)
        {
            WaveFormat format = wasapiPlayer.OutputWaveFormat;
            _activeOutputFormat = $"{format.SampleRate / 1000.0:0.#} kHz · {format.Channels} ch · {format.BitsPerSample}-bit";
        }
    }

    private void EnsureOutputDevice()
    {
        if (_outputDevice is not null) return;

        string requestedDeviceKey = _settings.OutputDeviceName;
        AudioOutputDeviceService.ResolvedEndpoint resolved = _audioOutputDeviceManager.Resolve(requestedDeviceKey);
        _activeOutputFormat = null;
        _lastOutputInitializationMilliseconds = 0;
        _activeOutputDeviceKey = resolved.ActiveDeviceKey;

        if (resolved.UsedFallback)
        {
            _outputDeviceFallbackFrom = requestedDeviceKey;
            Logger.Warn($"Выбранное WASAPI-устройство недоступно: {requestedDeviceKey}. Используется системное устройство Windows.");
            _settings.OutputDeviceName = AudioOutputDeviceService.SystemDefaultDeviceName;
            _ = SettingsManager.SaveAsync(_settings);
            _settingsWindow?.RefreshOutputDeviceSelection();
        }
        else
        {
            _outputDeviceFallbackFrom = null;
            // Старые WaveOut-ключи мигрируют к устойчивому endpoint-ID без сброса выбора.
            if (AudioOutputDeviceManager.ShouldPersistActiveKey(
                    requestedDeviceKey, resolved.ActiveDeviceKey, resolved.UsedFallback))
            {
                _settings.OutputDeviceName = resolved.ActiveDeviceKey;
                _ = SettingsManager.SaveAsync(_settings);
                _settingsWindow?.RefreshOutputDeviceSelection();
            }
        }

        try
        {
            _outputEndpoint = resolved.Device;
            bool wantsExclusiveMode = string.Equals(_settings.WasapiMode, "Exclusive", StringComparison.OrdinalIgnoreCase);
            try
            {
                _outputDevice = BuildWasapiPlayer(_outputEndpoint, wantsExclusiveMode);
                _activeWasapiMode = wantsExclusiveMode ? "Exclusive" : "Shared";
            }
            catch (Exception ex) when (wantsExclusiveMode)
            {
                // Не все устройства/форматы поддерживают монопольный режим — откатываемся на
                // общий, чтобы плеер не остался нерабочим.
                Logger.Warn($"Не удалось открыть устройство в монопольном режиме WASAPI, переключение на общий: {ex.Message}");
                _settings.WasapiMode = "Shared";
                _ = SettingsManager.SaveAsync(_settings);
                _settingsWindow?.RefreshWasapiModeSelection();
                _outputDevice = BuildWasapiPlayer(_outputEndpoint, useExclusiveMode: false);
                _activeWasapiMode = "Shared";
            }
            _audioOutputSession.Attach(_outputDevice!, _outputEndpoint!);
        }
        catch
        {
            resolved.Device.Dispose();
            _outputEndpoint = null;
            throw;
        }
    }

    private WasapiPlayer BuildWasapiPlayer(MMDevice device, bool useExclusiveMode)
    {
        var builder = new WasapiPlayerBuilder()
            .WithDevice(device)
            .WithEventSync()
            .WithLatency(WasapiRequestedLatencyMilliseconds)
            .WithCategory(AudioStreamCategory.Media)
            // "Pro Audio" — дефолт NAudio для этого параметра (мы сузили до "Audio" без причины);
            // эта MMCSS-категория защищает поток от щелчков при всплеске нагрузки (запуск игры).
            .WithMmcssThreadPriority("Pro Audio");
        builder = useExclusiveMode ? builder.WithExclusiveMode() : builder.WithSharedMode();
        return builder.Build();
    }

    // Вызывается из SettingsWindow сразу после выбора устройства. Снимок разделяет
    // запрошенную latency профиля и фактическую latency, которую WasapiPlayer получил после Init.
    internal AudioOutputRuntimeStatus GetOutputDeviceRuntimeStatus()
    {
        WasapiPlayer? player = _outputDevice as WasapiPlayer;
        string activeKey = _outputDevice is null ? _settings.OutputDeviceName : _activeOutputDeviceKey;
        string? activeEndpointId = player?.DeviceId ?? TryGetActiveOutputEndpointId();
        return new AudioOutputRuntimeStatus(
            AudioOutputDeviceService.GetDisplayName(activeKey),
            string.IsNullOrWhiteSpace(_outputDeviceFallbackFrom) ? null : AudioOutputDeviceService.GetDisplayName(_outputDeviceFallbackFrom),
            _outputDevice is not null,
            $"WASAPI {_activeWasapiMode} · WasapiPlayer",
            WasapiRequestedLatencyMilliseconds,
            player?.LatencyMilliseconds,
            _activeOutputFormat,
            activeEndpointId,
            GetOutputPlaybackState(_outputDevice),
            AudioOutputRecoveryPolicy.FollowsSystemDefault(_settings.OutputDeviceName),
            _lastOutputInitializationMilliseconds,
            _outputRecoveryCount,
            _lastOutputRecoveryReason,
            _meaningfulOutputDeviceEventCount,
            _lastOutputDeviceEventKind,
            _lastOutputDeviceEventEndpointId);
    }

    // Отчёт формируется только по явному действию пользователя для clipboard. Он не включает
    // путь, название, исполнителя или другие данные текущего трека.
    internal string BuildAudioDiagnosticsReport() =>
        AudioDiagnosticsReportFormatter.Format(UpdateChecker.GetCurrentVersion(), GetOutputDeviceRuntimeStatus());

    private string? TryGetActiveOutputEndpointId()
    {
        try
        {
            return _outputEndpoint?.ID;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось прочитать активный WASAPI endpoint: {ex.Message}");
            return null;
        }
    }

    private static string GetOutputPlaybackState(IWavePlayer? outputDevice) => outputDevice?.PlaybackState switch
    {
        NAudio.Wave.PlaybackState.Playing => "Воспроизводится",
        NAudio.Wave.PlaybackState.Paused => "На паузе",
        NAudio.Wave.PlaybackState.Stopped => "Остановлен",
        _ => "Не инициализирован"
    };

    public void ApplyOutputDeviceSelection()
    {
        if (_isExiting) return;

        _outputDeviceFallbackFrom = null;
        string? currentPath = _currentTrackPath;
        TimeSpan position = _audioFile?.CurrentTime ?? TimeSpan.Zero;
        bool wasPlaying = _isPlaying;
        StopPlayback(disposeOnly: true);
        DisposeOutputDeviceSafely();

        if (!string.IsNullOrWhiteSpace(currentPath) && File.Exists(currentPath))
        {
            LoadAndPlay(currentPath, autoPlay: wasPlaying, startPosition: position,
                changeOrigin: TrackChangeOrigin.Automatic);
        }
    }

    private void AudioOutputEndpointMonitor_EndpointChanged(object? sender, AudioOutputEndpointChangedEventArgs e)
    {
        if (_isExiting || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        _ = Dispatcher.BeginInvoke(new Action(() => HandleAudioOutputEndpointChanged(e)), DispatcherPriority.Background);
    }

    private void HandleAudioOutputEndpointChanged(AudioOutputEndpointChangedEventArgs e)
    {
        if (_isExiting)
            return;

        // Список и карточка Settings должны сразу отражать подключение/отключение, но callback переносим с Core Audio worker
        // thread на Dispatcher; PropertyValueChanged приходит часто (например, громкость) и не требует пересборки ComboBox или recovery.
        if (e.Kind != AudioOutputEndpointChangeKind.DevicePropertiesChanged && _settingsWindow?.IsLoaded == true)
            _settingsWindow.RefreshOutputDeviceSelection();

        if (_outputDevice is null || _audioOutputRecoveryCoordinator.IsInProgress ||
            e.Kind == AudioOutputEndpointChangeKind.DevicePropertiesChanged)
            return;

        RecordMeaningfulOutputDeviceEvent(e);
        if (e.Kind == AudioOutputEndpointChangeKind.DefaultDeviceChanged)
        {
            QueueSystemDefaultEndpointRecovery(e.EndpointId);
            return;
        }

        string? activeEndpointId = TryGetActiveOutputEndpointId();
        bool activeEndpointBecameUnavailable =
            string.Equals(activeEndpointId, e.EndpointId, StringComparison.Ordinal) &&
            (e.Kind == AudioOutputEndpointChangeKind.DeviceRemoved ||
             e.Kind == AudioOutputEndpointChangeKind.DeviceStateChanged && e.State is not DeviceState.Active);
        if (!activeEndpointBecameUnavailable)
            return;

        const string reason = "активное устройство вывода отключено или стало недоступно";
        Logger.Warn($"{reason}; выполняется восстановление WASAPI.");
        RecoverOutputDeviceAfterFailure(new InvalidOperationException(reason), _isPlaying, expectedDeviceEvent: true);
    }

    private void RecordMeaningfulOutputDeviceEvent(AudioOutputEndpointChangedEventArgs e)
    {
        _meaningfulOutputDeviceEventCount++;
        _lastOutputDeviceEventKind = e.Kind;
        _lastOutputDeviceEventEndpointId = e.EndpointId;
    }

    private void QueueSystemDefaultEndpointRecovery(string? changedDefaultEndpointId)
    {
        if (!AudioOutputRecoveryPolicy.FollowsSystemDefault(_settings.OutputDeviceName))
        {
            Logger.Info("Смена системного устройства Windows не влияет на явно выбранный WASAPI endpoint.");
            return;
        }

        _pendingSystemDefaultEndpointId = changedDefaultEndpointId;
        _systemDefaultEndpointDebounceTimer.Stop();
        _systemDefaultEndpointDebounceTimer.Interval = TimeSpan.FromMilliseconds(SystemDefaultEndpointDebounceMilliseconds);
        _systemDefaultEndpointDebounceTimer.Start();
        Logger.Info("Windows изменил системное устройство вывода; endpoint будет подтверждён перед восстановлением WASAPI.");
    }

    private void SystemDefaultEndpointDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _systemDefaultEndpointDebounceTimer.Stop();
        if (_isExiting || _outputDevice is null || _audioOutputRecoveryCoordinator.IsInProgress)
            return;

        DateTime now = DateTime.UtcNow;
        DateTime lastRecoveryStartedUtc = _audioOutputRecoveryCoordinator.LastStartedUtc;
        double elapsedSinceRecovery = (now - lastRecoveryStartedUtc).TotalMilliseconds;
        if (lastRecoveryStartedUtc != DateTime.MinValue && elapsedSinceRecovery < OutputRecoveryCooldownMilliseconds)
        {
            // Endpoint notifications часто приходят пачкой. Не теряем последнее default-событие,
            // если recovery ещё stabilizes, а ждём окончание уже действующего cooldown.
            _systemDefaultEndpointDebounceTimer.Interval = TimeSpan.FromMilliseconds(
                Math.Max(1, OutputRecoveryCooldownMilliseconds - elapsedSinceRecovery));
            _systemDefaultEndpointDebounceTimer.Start();
            return;
        }

        string? changedDefaultEndpointId = _pendingSystemDefaultEndpointId;
        _pendingSystemDefaultEndpointId = null;
        string? activeEndpointId = TryGetActiveOutputEndpointId();
        if (!AudioOutputRecoveryPolicy.ShouldRecoverAfterDefaultDeviceChanged(
                _settings.OutputDeviceName, activeEndpointId, changedDefaultEndpointId))
        {
            Logger.Info("Смена default WASAPI endpoint уже отражена активным устройством; recovery не требуется.");
            return;
        }

        const string reason = "Windows изменил системное устройство вывода";
        Logger.Info($"{reason}; выполняется controlled recovery WASAPI.");
        RecoverOutputDeviceAfterFailure(new InvalidOperationException(reason), _isPlaying, expectedDeviceEvent: true);
    }

    // Устройство могло исчезнуть при Play/Pause, прислать PlaybackStopped с ошибкой или сообщить через Core Audio endpoint event:
    // один controlled retry через Windows audio mapper лучше повторных ошибок (после отключения USB/Bluetooth это уже новое устройство).
    private void RecoverOutputDeviceAfterFailure(Exception error, bool resumePlayback, bool expectedDeviceEvent = false)
    {
        if (expectedDeviceEvent)
            Logger.Warn($"Восстановление WASAPI после события endpoint: {error.Message}");
        else
            Logger.Error("Ошибка устройства вывода; выполняется восстановление через системное устройство", error);
        if (_isExiting) return;

        DateTime now = DateTime.UtcNow;
        OutputRecoveryReason reason = expectedDeviceEvent
            ? OutputRecoveryReason.EndpointUnavailable
            : OutputRecoveryReason.PlaybackFailure;
        var request = new OutputRecoveryRequest(
            reason, error.Message, resumePlayback, expectedDeviceEvent);
        OutputRecoveryExecutionResult execution = _audioOutputRecoveryService.Execute(
            request,
            now,
            CapturePlaybackRecoverySnapshot,
            ExecutePlaybackRecovery,
            out OutputRecoveryDecision decision);

        if (!execution.Started)
            return;

        SetTrackUserState(TrackUserState.Loading);
        _outputRecoveryCount = execution.RecoveryCount;
        _lastOutputRecoveryReason = expectedDeviceEvent
            ? error.Message
            : "Ошибка WASAPI при инициализации или воспроизведении";
        if (!execution.Completed)
        {
            StopPlayback();
            DisposeOutputDeviceSafely();
            SetTrackUserState(TrackUserState.Error);
        }
    }

    private PlaybackRecoverySnapshot? CapturePlaybackRecoverySnapshot()
    {
        string? currentPath = _currentTrackPath;
        if (string.IsNullOrWhiteSpace(currentPath) || !File.Exists(currentPath))
            return null;

        return new PlaybackRecoverySnapshot(
            currentPath,
            _audioFile?.CurrentTime ?? TimeSpan.Zero,
            _isPlaying,
            _settings.OutputDeviceName,
            TryGetActiveOutputEndpointId());
    }

    private void ExecutePlaybackRecovery(PlaybackRecoverySnapshot snapshot)
    {
        StopPlayback(disposeOnly: true);
        string failedDeviceKey = snapshot.SavedDeviceKey ?? string.Empty;
        DisposeOutputDeviceSafely();
        _activeOutputDeviceKey = AudioOutputDeviceService.SystemDefaultDeviceName;
        _outputDeviceFallbackFrom = AudioOutputDeviceManager.GetFallbackSourceKey(
            failedDeviceKey, usedFallback: true);
        _settings.OutputDeviceName = AudioOutputDeviceService.SystemDefaultDeviceName;
        _ = SettingsManager.SaveAsync(_settings);
        _settingsWindow?.RefreshOutputDeviceSelection();
        LoadAndPlay(snapshot.TrackPath, autoPlay: snapshot.WasPlaying,
            startPosition: snapshot.Position, changeOrigin: TrackChangeOrigin.Automatic);
    }

    private void DisposeOutputDeviceSafely()
    {
        try
        {
            _audioOutputSession.Release();
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось освободить WASAPI-устройство вывода", ex);
        }
        finally
        {
            _outputDevice = null;
            _outputEndpoint = null;
        }
    }

    // Координация с внешней записью в файл (теги/обложка, см. TrackTagsWindow.SaveButton_Click), пока файл открыт NAudio-потоком:
    // null, если играет другой трек; иначе освобождает хендл и возвращает точку для ResumeAfterExternalWrite после записи.
    public (TimeSpan Position, bool WasPlaying)? ReleaseFileForExternalWrite(string filePath)
    {
        if (!PathEquals(filePath, _currentTrackPath) || _audioFile == null) return null;

        var snapshot = (_audioFile.CurrentTime, _isPlaying);
        StopPlayback(disposeOnly: true);
        return snapshot;
    }

    public void ResumeAfterExternalWrite(string filePath, (TimeSpan Position, bool WasPlaying) snapshot)
    {
        LoadAndPlay(filePath, autoPlay: snapshot.WasPlaying, startPosition: snapshot.Position,
            changeOrigin: TrackChangeOrigin.ExternalEdit);
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

    // Раз в ~10 секунд игры (40 тиков по 250 мс) сохраняем трек/позицию: при аварийном завершении позиция потеряется
    // не более чем на секунды (см. PersistPlaybackAndPlaylistState).
    private const int AutoSaveEveryNTicks = 40;
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
