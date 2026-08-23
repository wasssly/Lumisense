using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundTouch.Net.NAudioSupport;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace AudioPlayer;

public partial class MainWindow : FluentWindow
{
    private enum RepeatMode { Off, All, One }

    // Три вида плеера, переключаемые через контекстное меню по клику на заголовок
    // "Lumisense" (см. TitleClickArea в XAML): обычный/квадратный (без плейлиста,
    // Width == Height), прямоугольный (с плейлистом — прежнее поведение по умолчанию)
    // и мини-плеер (отдельное окно MiniPlayerWindow).
    private enum PlayerViewMode { Square, Rectangular, Mini }

    // Направление анимации смены обложки (см. AnimateAlbumArtTransition): Next — старая
    // обложка "улетает" влево, новая "влетает" справа; Previous — наоборот. None — без
    // анимации (например, самая первая загрузка обложки при старте приложения).
    private enum AlbumArtTransitionDirection { None, Next, Previous }

    // Поддерживаемые расширения — используются при сканировании папок
    private static readonly string[] SupportedExtensions = { ".mp3", ".wav", ".wma", ".flac", ".m4a", ".aac", ".ogg" };

    // AudioFileReader умеет читать mp3/wav/wma и сразу даёт регулировку громкости
    private AudioFileReader? _audioFile;
    private SoundTouchWaveStream? _tempoStream;

    // Живёт одно на всё время работы приложения (см. OnClosed) вместо пересоздания на каждый
    // трек — StopPlayback останавливает и переиспользует его, LoadAndPlay зовёт Init(...)
    // заново. Пересоздание WaveOutEvent на каждый трек раньше давало щелчок при открытии
    // аудиоустройства на уровне драйвера — отдельно от щелчка "холодного старта" эквалайзера,
    // который маскирует fade-in в LoadAndPlay.
    private WaveOutEvent? _outputDevice;
    private bool _isOutputRecoveryInProgress;

    // Сидит между _audioFile и _outputDevice в цепочке ISampleProvider (см. LoadAndPlay) —
    // громкость (AudioFileReader.Volume) применяется ДО эквалайзера, он только красит частоты.
    private EqualizerSampleProvider? _equalizer;
    // Измеряет уже обработанный эквалайзером сигнал для визуальной реакции Now Playing.
    private AudioLevelSampleProvider? _audioLevelMeter;
    private FadeInOutSampleProvider? _activeFade;

    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _playbackRatePersistenceTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    // Большинство UI-настроек меняются сразу в памяти, а не по отдельному Save на каждое
    // движение слайдера. Короткий checkpoint делает их устойчивыми к закрытию консоли, но
    // SettingsManager пропускает полностью неизменившийся JSON и не создаёт лишних записей.
    private readonly DispatcherTimer _settingsCheckpointTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    // Поиск не меняет UI на каждый символ: небольшая пауза объединяет быстрый ввод в один
    // запрос, а фильтрация снимка списка выполняется вне Dispatcher. Это предотвращает длинную
    // перестройку раскладки тысяч ListViewItem при каждом нажатии клавиши.
    private readonly DispatcherTimer _playlistSearchDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private CancellationTokenSource? _playlistSearchCts;
    private int _playlistSearchGeneration;
    private readonly Stopwatch _playbackClock = new();

    // Множитель громкости из ReplayGain-тегов текущего трека (см. ReplayGainReader,
    // AppSettings.ReplayGainEnabled), 1.0 если выключено/тегов нет. Домножается на обычную
    // громкость в ComputeAudioFileVolume — то же место конвейера (AudioFileReader.Volume, до
    // эквалайзера), отдельный ISampleProvider не нужен.
    private double _replayGainFactor = 1.0;
    private Color? _coverAccentColor;

    // ---------- Waveform-полоса воспроизведения (см. AppSettings.ProgressBarStyle) ----------
    // Кэш пиков по пути файла — трек может грузиться повторно, пересчитывать форму волны заново
    // незачем. Ограничен WaveformCacheLimit, это кэш сессии, не постоянное хранилище.
    private readonly Dictionary<string, float[]> _waveformCache = new();
    private readonly Queue<string> _waveformCacheOrder = new();
    private const int WaveformCacheLimit = 40;

    // Отменяет расчёт формы волны для ПРЕДЫДУЩЕГО трека при переключении на следующий раньше,
    // чем расчёт закончился — иначе устаревший результат может перезаписать уже показанную
    // форму волны нового трека.
    private CancellationTokenSource? _waveformCts;
    private CancellationTokenSource? _trackLoadCts;
    private CancellationTokenSource? _replayGainCts;
    private int _trackLoadGeneration;
    private readonly CancellationTokenSource _lifetimeCts = new();

    private sealed class PreparedTrack : IDisposable
    {
        public required AudioFileReader AudioFile { get; init; }
        public required SoundTouchWaveStream TempoStream { get; init; }
        public required EqualizerSampleProvider Equalizer { get; init; }
        public required double ReplayGainFactor { get; init; }
        public string? Title { get; init; }
        public string? Artist { get; init; }
        public BitmapImage? AlbumArt { get; init; }
        public byte[]? AlbumArtBytes { get; init; }
        public string? AlbumArtMimeType { get; init; }
        public TagLib.PictureType? AlbumArtPictureType { get; init; }

        public void Dispose()
        {
            try
            {
                TempoStream.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Error("Не удалось освободить подготовленный AudioFileReader", ex);
            }
        }
    }

    // Прослушивание засчитывается только когда реально воспроизведена (не перемотана) как
    // минимум половина композиции — см. ProgressTimer_Tick. Сбрасывается на каждую новую
    // загрузку, включая повтор того же трека (RepeatMode.One).
    private bool _halfPlayCounted;

    // ObservableCollection, а не List — PlaylistFoldersControl (см. RestoreSavedPlaylistAsync)
    // привязан к ней один раз и получает только реально новые/удалённые папки через
    // CollectionChanged, без пересоздания контейнеров всех папок при каждом добавлении.
    private readonly ObservableCollection<PlaylistFolder> _folders = new();

    // Отслеживание добавлений в дисковые папки плейлиста. FileSystemWatcher сообщает о
    // нескольких промежуточных событиях при копировании файла, поэтому объединяем их в один
    // повторный скан после короткой паузы, а не пересобираем список при каждом уведомлении.
    private readonly List<FileSystemWatcher> _folderWatchers = new();
    private readonly HashSet<string> _pendingFolderRefreshPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _folderRefreshDebounceTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _isFolderRefreshInProgress;

    // Пока конструктор не завершил перенос SavedPlaylistFolders в _folders, любое сохранение
    // пустой коллекции опасно: при исключении в ранней инициализации оно могло затереть
    // реальный плейлист пользователя в settings.json. Флаг становится true только после
    // успешного восстановления либо подтверждённого отсутствия сохранённых групп.
    private bool _playlistRestoreCompleted;

    // Виртуальная группа "Избранное" — не входит в _folders (это не настоящая группа плейлиста,
    // её незачем сохранять в SavedPlaylistFolders), а собирается на лету из FavoritesManager
    // каждый раз перед показом (см. RefreshPlaylistView). Единственный экземпляр переиспользуется,
    // чтобы не пересоздавать PlaylistFolder (и, как следствие, не терять IsExpanded) при каждом
    // обновлении списка избранного.
    private readonly PlaylistFolder _favoritesFolder = new()
    {
        DisplayName = "Избранное",
        IsFavoritesGroup = true
    };

    // true, пока на месте основного плейлиста показан виртуальный плейлист "Избранное"
    // (см. FavoritesButton_Click/SetFavoritesViewActive) — влияет и на то, что показывает
    // PlaylistFoldersControl, и на то, какой список треков используют "Далее"/"Назад"/шафл
    // (см. FlattenAll/FlattenActive).
    private bool _isFavoritesView;

    // Панель текста занимает место плейлиста, не создавая второго окна. Отдельный CTS
    // отменяет локальное чтение и онлайн-поиск при смене трека, скрытии панели или закрытии.
    private bool _isLyricsPanelActive;
    private CancellationTokenSource? _mainWindowLyricsCts;
    private string? _mainWindowLyricsTrackPath;
    private LyricsDocument _mainWindowLyrics = LyricsDocument.Empty;
    private readonly ObservableCollection<MainWindowLyricLine> _mainWindowSyncedLyrics = new();

    // ScrollViewer не имеет анимируемого DependencyProperty для VerticalOffset. Небольшое
    // attached-свойство проксирует значение анимации в ScrollToVerticalOffset, поэтому активная
    // LRC-строка перемещается плавно, а не перескакивает при каждом timestamp.
    private static readonly DependencyProperty AnimatedScrollOffsetProperty = DependencyProperty.RegisterAttached(
        "AnimatedScrollOffset", typeof(double), typeof(MainWindow),
        new PropertyMetadata(0.0, OnAnimatedScrollOffsetChanged));
    private int _activeMainWindowLyricIndex = -2;

    private sealed class MainWindowLyricLine : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isActive;
        private double _fontSize = 14;
        private double _lineHeight = 23;

        public required TimeSpan Time { get; init; }
        public required string Text { get; init; }
        public SolidColorBrush Foreground { get; } = new(Color.FromRgb(142, 142, 142));
        public ScaleTransform ScaleTransform { get; } = new(1, 1);
        public System.Windows.Media.Effects.DropShadowEffect GlowEffect { get; } = new()
        {
            Color = Colors.White,
            BlurRadius = 16,
            ShadowDepth = 0,
            Opacity = 0
        };

        public bool IsActive
        {
            get => _isActive;
            set => Set(ref _isActive, value, nameof(IsActive));
        }

        public double FontSize
        {
            get => _fontSize;
            set => Set(ref _fontSize, value, nameof(FontSize));
        }

        public double LineHeight
        {
            get => _lineHeight;
            set => Set(ref _lineHeight, value, nameof(LineHeight));
        }

        private void Set<T>(ref T field, T value, string propertyName)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private List<string>? _allTracksCache;
    private List<string>? _activeTracksCache;
    private bool _trackCachesAreFavoritesView;

    // Нефильтрованные снимки строк для текущего вида. В отличие от прежнего Binding-конвертера,
    // поиск строит из них отдельный ItemsSource и не запускает массовую смену Visibility.
    private List<object> _playlistDisplayItems = new();
    private List<object> _favoriteDisplayItems = new();

    private readonly Random _random = new();

    // Проверяется ДО загрузки настроек (сама загрузка ничего не создаёт на диске, поэтому
    // порядок не важен) — true, если settings.json ещё ни разу не сохранялся, то есть это
    // самый первый запуск плеера. Используется, чтобы решить, каким видом плеера открыться
    // (см. RestorePlayerViewMode).
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
    private bool _isShuffleEnabled;

    // История треков, сыгранных в режиме шафла: "Вперёд" на новом месте генерирует
    // случайный трек и дописывает его в конец, а "Назад" не генерирует ничего нового,
    // а просто возвращается на шаг назад по этому списку (как в браузере) — иначе
    // "назад" в шафле оказывалось таким же случайным выбором, как и "вперёд", и не давало
    // вернуться к реально предыдущему треку.
    private readonly List<string> _shuffleHistory = new();
    private int _shuffleHistoryIndex = -1;
    private const int MaxPersistedShuffleHistory = 512;

    // "Колода" для шаффла без повторов (см. GetNextShuffleTrack) — активна только когда
    // включена настройка Settings.UseImprovedShuffle.
    private List<string> _shuffleBag = new();
    private bool _isMiniMode;

    // Отличает обычное свёрнутое окно от главного окна, только что открытого внешней
    // активацией из мини-плеера. Нужен, чтобы следующий клик по его кнопке в панели задач
    // вернул мини-плеер, не меняя поведение обычной кнопки «Свернуть».
    private bool _returnToMiniOnNextTaskbarMinimize;

    // Устанавливается строго на время синхронной обработки системной кнопки «Свернуть»
    // в TitleBar. Нужен, чтобы эта кнопка никогда не считалась кликом по панели задач.
    private bool _isSystemTitleBarMinimize;
    private RepeatMode _repeatMode = RepeatMode.Off;

    // Текущий вид плеера (см. PlayerViewMode) и вид, в котором плеер был непосредственно
    // перед переходом в мини-режим — нужен, чтобы при "развернуть" из мини-плеера вернуть
    // не какой-то один вид по умолчанию, а именно тот, из которого в мини-плеер и ушли.
    private PlayerViewMode _viewMode = PlayerViewMode.Square;
    private PlayerViewMode _preMiniViewMode = PlayerViewMode.Square;

    private const double DefaultWindowWidth = 440; // как задана ширина окна в XAML
    private double _lastNonZeroVolume = 0.3;

    private GlobalMediaHotKeys? _mediaHotKeys;
    private TrayIconManager? _trayIconManager;
    private NowPlayingIntegration? _nowPlaying;
    private readonly DiscordRichPresenceManager _discordRichPresence = new();
    private MiniPlayerWindow? _miniPlayerWindow;

    private SettingsWindow? _settingsWindow;
    private StatisticsWindow? _statisticsWindow;
    private readonly TrackChangeToastController _trackChangeToastController = new();
    private CoverArtWindow? _coverArtWindow;
    private NowPlayingWindow? _nowPlayingWindow;

    // Жесты на обложке: короткое касание управляет паузой, а сдвиг не меньше 28 DIP
    // распознаётся как смена трека (горизонталь) или изменение громкости (вертикаль).
    private Point _albumArtGestureStart;
    private bool _isAlbumArtGestureActive;
    private bool _albumArtGestureMoved;
    private const double AlbumArtGestureThreshold = 28.0;

    private bool _isExiting;
    // Не сохраняем стартовые значения Slider из XAML до того, как ApplySettingsOnStartup
    // восстановит значения из settings.json. После запуска изменения пользователя сохраняются
    // асинхронно, чтобы движение ползунка не блокировало UI.
    private bool _isApplyingStartupSettings = true;
    private bool _isOpeningPlaybackControlPopup;
    // Единственный runtime-источник скорости. До завершения InitializeComponent Slider не
    // имеет права менять его: XAML всегда создаёт Slider с Value=1.0.
    private double _runtimePlaybackRate;
    private bool _playbackRateIsReady;
    private bool _isUpdatingPlaybackRateControl;

    // ---------- Полноэкранный режим ----------
    // Обычная (не полноэкранная) ширина ContentHost — совпадает со стартовой шириной окна,
    // чтобы в исходном размере интерфейс выглядел ровно так же, как и раньше.
    private const double NormalContentMaxWidth = 440;
    private bool _isFullscreenLayout;

    // Фиксированная ширина рабочей области для квадратного вида плеера (PlayerViewMode.Square)
    // — в отличие от настоящего полноэкранного режима, где она подстраивается под ширину
    // монитора, здесь окно хоть и увеличенное, но обычное, поэтому и предел ширины фиксирован.
    private const double SquareContentMaxWidth = 560;

    // События для внешнего окна мини-плеера (MiniPlayerWindow), которое не является частью
    // этого окна и получает обновления только через них
    public event Action<string, string, Brush?>? TrackInfoChanged;
    public event Action<double, double>? ProgressChanged;
    public event Action<bool>? PlaybackStateChanged;

    // Единый независимый снимок для мини-плеера, Now Playing и будущих интеграций. Старые
    // узкие события ниже сохраняются как совместимый фасад для уже существующих подписчиков.
    public PlaybackStateStore PlaybackState { get; } = new();

    // Тоже только для мини-плеера — у него теперь своя кнопка повтора (см.
    // MiniPlayerWindow.RepeatButton_Click), и её вид должен оставаться в синхроне с основным
    // окном, чем бы режим ни переключили: этой кнопкой, кнопкой в основном окне или хоткеем.
    public event Action<string>? RepeatModeChanged;

    // Зеркальный аналог RepeatModeChanged для кнопки "Перемешать" — тоже нужен мини-плееру
    // (см. AppSettings.MiniPlayerSecondaryButton: он может показывать либо повтор, либо
    // перемешать), и по той же причине: состояние может поменяться откуда угодно — кнопкой
    // в основном окне, кнопкой в мини-плеере или хоткеем.
    public event Action<bool>? ShuffleStateChanged;

    // Отдельно от VolumeSlider_ValueChanged (который дёргается и при загрузке сохранённой
    // громкости на старте) — только для мини-плеера, который показывает всплывающий
    // индикатор процентов при изменении громкости хоткеями/скроллом. Аргумент — итоговая
    // громкость 0..1, как в VolumeSlider.Value.
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

    // Применяет изменённый масштаб/режим движения к уже открытым окнам, не создавая новых
    // экземпляров и не меняя состояние воспроизведения.
    public void ApplyAccessibilityPreferences()
    {
        AccessibilityPreferences.ApplyToWindow(this, _settings);
        _settingsWindow?.ApplyAccessibilityPreferences();
        _miniPlayerWindow?.ApplyAccessibilityPreferences();
        _nowPlayingWindow?.ApplyAccessibilityPreferences();
    }

    public string CurrentTitle => TrackTitleText.Text;
    public string CurrentArtist => TrackArtistText.Text;
    public Brush? CurrentArtBrush => AlbumArtIcon.Visibility == Visibility.Visible ? null : AlbumArtBorder.Background;

    // Сырые байты текущей обложки (JPEG/PNG прямо из тега) — специально байты, а не сам
    // Brush/BitmapImage: TrayIconManager живёт в WinForms-стеке (NotifyIcon), и из сырых байт
    // он сам декодирует System.Drawing.Bitmap для миниатюры в меню трея (см.
    // TrayIconManager.SetNowPlaying), не завися от WPF-типов вроде BitmapSource/ImageBrush.
    public byte[]? CurrentAlbumArtBytes => AlbumArtIcon.Visibility == Visibility.Visible ? null : _currentAlbumArtBytes;
    public BitmapImage? CurrentAlbumArt => _currentAlbumArt;
    public double CurrentPlaybackSeconds => _audioFile?.CurrentTime.TotalSeconds ?? 0;
    public double CurrentTrackDurationSeconds => _audioFile?.TotalTime.TotalSeconds ?? 0;
    public bool IsPlayingNow => _isPlaying;
    public AudioLevelSampleProvider? AudioLevelMeter => _audioLevelMeter;

    // Для мини-плеера — узнать текущий режим повтора сразу при открытии, до первого события
    // RepeatModeChanged (тем же способом, каким мини-плеер узнаёт текущий трек/состояние
    // воспроизведения при своём создании — см. конструктор MiniPlayerWindow).
    public string CurrentRepeatModeName => _repeatMode.ToString();

    // Зеркальный аналог CurrentRepeatModeName для перемешивания — см. ShuffleStateChanged.
    public bool CurrentIsShuffleEnabled => _isShuffleEnabled;

    // Текущий путь к файлу — нужен мини-плееру для варианта "Избранное" второй кнопки (см.
    // MiniPlayerWindow.UpdateFavoriteSecondaryButtonVisual), чтобы понять, какой именно трек
    // сейчас проверять на признак избранного. Null, пока ничего не загружено (самый первый
    // запуск без сохранённого последнего трека).
    public string? CurrentTrackPath => _currentTrackPath;

    // Полноразмерная обложка текущего трека (или null, если у трека нет обложки/тегов).
    // Хранится отдельно от ImageBrush, которым залит AlbumArtBorder, потому что окну
    // просмотра обложки (CoverArtWindow) нужен именно исходный BitmapImage, а не Brush.
    private BitmapImage? _currentAlbumArt;

    // Исходные байты обложки и её MIME-тип из тега — нужны отдельно от BitmapImage для
    // контекстного меню по обложке: "Скачать изображение" пишет на диск именно эти байты
    // как есть (без перекодирования), а "Свойства" показывает реальные формат и размер файла.
    private byte[]? _currentAlbumArtBytes;
    private string? _currentAlbumArtMimeType;
    private TagLib.PictureType? _currentAlbumArtPictureType;

    // Оборачивает fire-and-forget async-вызовы (несколько мест в этом файле: восстановление
    // плейлиста, проверка обновлений при старте, фоновая проверка существования файлов, расчёт
    // формы волны) логированием исключения сразу же, а не только когда сборщик мусора когда-
    // нибудь уничтожит забытую задачу (TaskScheduler.UnobservedTaskException в App.xaml.cs —
    // тот тоже сработает, но не гарантированно быстро, а иногда и вовсе не успевает до
    // закрытия процесса).
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

    // Вызывается из last-chance обработчиков AppDomain/консоли. Не читает UI или аудио-объекты,
    // поэтому безопасен как best-effort snapshot даже когда штатный WPF shutdown уже не начался.
    internal void SaveSettingsForUnexpectedTermination() => SettingsManager.Save(_settings);

    public MainWindow()
    {
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

        _progressTimer.Tick += ProgressTimer_Tick;
        _playlistSearchDebounceTimer.Tick += PlaylistSearchDebounceTimer_Tick;
        _folderRefreshDebounceTimer.Tick += FolderRefreshDebounceTimer_Tick;
        _hotkeyTrackStepTimer.Tick += HotkeyTrackStepTimer_Tick;
        _playbackRatePersistenceTimer.Tick += PlaybackRatePersistenceTimer_Tick;
        _settingsCheckpointTimer.Tick += SettingsCheckpointTimer_Tick;

        // Rich Presence использует те же единые события, что и мини-плеер: это исключает
        // отдельный таймер, опрос UI и расхождение со сменой состояния аудиоустройства.
        TrackInfoChanged += (_, _, _) => UpdateDiscordRichPresence(force: true);
        PlaybackStateChanged += _ => UpdateDiscordRichPresence(force: true);
        ProgressChanged += (_, _) => UpdateDiscordRichPresence(force: false);
        ApplySettingsOnStartup();
        _playbackRatePersistenceTimer.Start();
        _settingsCheckpointTimer.Start();

        // Не await — намеренно "запустили и забыли": файловая проверка треков и загрузка
        // последнего трека идут в фоне, окно тем временем показывается сразу, без ожидания
        // (см. подробный комментарий над RestoreSavedPlaylistAsync).
        FireAndForget(RestoreSavedPlaylistAsync(), "RestoreSavedPlaylistAsync");

        StateChanged += MainWindow_StateChanged;
        SizeChanged += MainWindow_SizeChanged;

        // Повторно применяем акцент уже ПОСЛЕ того, как окно реально отрисовано (см.
        // MainWindow_Loaded) — иначе на некоторых машинах при запуске приложения с системным
        // акцентом (AccentColorMode == "System") часть визуальных элементов не подхватывает
        // акцент, хотя он совершенно корректно применён по коду (первый ApplyAccentColor() в
        // ApplySettingsOnStartup выше отрабатывает ДО показа окна).
        Loaded += MainWindow_Loaded;

        // Подстраховка для завершения сеанса Windows (выключение/перезагрузка/выход из
        // системы) — в этот момент OnClosing/OnClosed могут не успеть отработать штатно, а
        // сворачивание в трей само по себе новых сохранений после первого раза не вызывает.
        // Без этого позиция трека, начатого прямо перед выключением компьютера, терялась бы
        // до следующего периодического автосохранения (см. ProgressTimer_Tick).
        System.Windows.Application.Current.SessionEnding += (_, _) => PersistPlaybackAndPlaylistState();
    }

    private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
    {
        foreach (PlaylistFolder folder in _folders)
            folder.RefreshLocalizedSubtitle();
        _favoritesFolder.RefreshLocalizedSubtitle();
        UpdateTrackUserStatePresentation();
    }

    // Первое применение акцента в ApplySettingsOnStartup происходит в конструкторе, до Show()
    // — окно ещё не отрисовано. WPF-UI 3.0.5 не перечитывает DynamicResource-акцент для части
    // элементов (выделение строки плейлиста и т.п.) до первой полной отрисовки дерева (issues
    // #965/#981 у github.com/lepoco/wpfui). Кнопок плеера это не касается — их красим вручную,
    // см. RefreshAccentDependentIcons. Повторяем ApplyAccentColor() после первого Loaded и сразу
    // отписываемся — иначе акцент пересчитывался бы на каждый Loaded (например, при разворачивании).
    private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            // settings.json уже содержит последнее значение (например, 0.75), но Popup и
            // Slider были созданы из XAML раньше. Повторно применяем настройки после полной
            // загрузки визуального дерева, чтобы поздняя инициализация WPF не вернула 1.0.
            SetPlaybackRate(_settings.PlaybackSpeed, persist: false);
            ApplyAccentColor();
        }), DispatcherPriority.Loaded);
    }

    // ---------- Полноэкранный режим ----------
    // Срабатывает при разворачивании окна кнопкой "Развернуть" в заголовке (или двойным
    // кликом по заголовку/системными средствами) — в обоих случаях WindowState становится
    // Maximized, и это единственное, что нам нужно отследить.
    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        var fullscreen = WindowState == WindowState.Maximized;
        if (fullscreen != _isFullscreenLayout)
        {
            _isFullscreenLayout = fullscreen;
            ApplyFullscreenLayout(fullscreen);
        }

        // Только главный вид, восстановленный из мини-плеера внешней активацией, должен
        // вернуть мини-плеер следующим обычным сворачиванием через кнопку панели задач.
        // Системная кнопка «Свернуть» явно исключена: она всегда оставляет обычное окно
        // свёрнутым, даже если оно было перед этим восстановлено из мини-плеера.
        if (WindowState == WindowState.Minimized
            && _returnToMiniOnNextTaskbarMinimize
            && !_isSystemTitleBarMinimize
            && !_isMiniMode)
        {
            _returnToMiniOnNextTaskbarMinimize = false;
            SetPlayerViewMode(PlayerViewMode.Mini);
        }
    }

    // Пересчитывает ширину ContentHost при изменении размеров окна — иначе после
    // растягивания квадратного окна мышью или переноса на другой монитор контент оставался
    // бы прежней узкой ширины с пустыми полями по бокам.
    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isFullscreenLayout || _viewMode == PlayerViewMode.Square) UpdateContentMaxWidth();
    }

    private void ApplyFullscreenLayout(bool fullscreen)
    {
        UpdateContentMaxWidth();
        ApplyContentScale(fullscreen || _viewMode == PlayerViewMode.Square);
    }

    // Тот же крупный стиль используется и квадратным видом плеера (PlayerViewMode.Square, см.
    // SetPlayerViewMode) — разница только в том, что полноэкранный занимает весь монитор, а
    // квадратный — увеличенное окно.
    private void ApplyContentScale(bool big)
    {
        AlbumArtContainer.Width = big ? 260 : 150;
        AlbumArtContainer.Height = big ? 260 : 150;
        AlbumArtBorder.Width = big ? 260 : 150;
        AlbumArtBorder.Height = big ? 260 : 150;
        AlbumArtIcon.Size = big ? 64 : 36;
        AlbumArtPanel.Margin = big ? new Thickness(0, 32, 0, 20) : new Thickness(0, 8, 0, 8);

        TrackTitleText.FontSize = big ? 24 : 17;
        TrackTitleText.MaxWidth = big ? 560 : 360;
        TrackArtistText.FontSize = big ? 15 : 12;

        var controlsScale = big ? 1.25 : 1.0;
        ShuffleButton.Width = ShuffleButton.Height = 40 * controlsScale;
        RepeatButton.Width = RepeatButton.Height = 40 * controlsScale;
        PrevButton.Width = PrevButton.Height = 44 * controlsScale;
        NextButton.Width = NextButton.Height = 44 * controlsScale;
        StopButton.Width = StopButton.Height = 40 * controlsScale;
        MiniModeButton.Width = MiniModeButton.Height = 40 * controlsScale;
        PlayPauseButton.Width = PlayPauseButton.Height = 54 * controlsScale;

        ControlsPanel.Margin = big ? new Thickness(0, 22, 0, 10) : new Thickness(0, 14, 0, 6);
    }

    // ContentHost.MaxWidth считается от реальной ширины окна, а не жёстко зашитым числом —
    // на широком мониторе интерфейс шире, на ноутбучном экране не раздувается зря.
    // SquareContentMaxWidth остался только нижней границей на случай, если окно квадратного
    // вида по какой-то причине окажется уже неё.
    private void UpdateContentMaxWidth()
    {
        ContentHost.MaxWidth = _isFullscreenLayout
            ? Math.Clamp(ActualWidth * 0.55, NormalContentMaxWidth, 760)
            : _viewMode == PlayerViewMode.Square
                ? Math.Clamp(Width - 40, SquareContentMaxWidth, 900)
                : NormalContentMaxWidth;
    }

    // Единственная точка входа для первого показа окна — вызывается один раз из
    // App.OnStartup вместо автоматического Show() через StartupUri. EnsureHandle() создаёт
    // нативный HWND без показа окна (нужен хоткеям/Now Playing/трею), Show() на самом
    // MainWindow вызывается только если по итогу не мини-режим.
    public void StartupPresent()
    {
        new WindowInteropHelper(this).EnsureHandle();

        RestorePlayerViewMode();

        if (_settings.StartHiddenInTray)
        {
            // RestorePlayerViewMode() выше мог уже создать и показать MiniPlayerWindow
            // (EnterMiniMode вызывает Show() безусловно, не зная про эту настройку) — прячем и его.
            _miniPlayerWindow?.Hide();

            // Если стартовый вид — мини-плеер, EnterMiniMode уже показал значок в трее с более
            // информативной подсказкой ("Lumisense — Название трека"), не перезатираем её.
            if (!_isMiniMode) _trayIconManager?.Show("Lumisense");
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

    // Windows иногда не даёт свежезапущенному процессу забрать фокус (защита от "кражи
    // фокуса", особенно заметно при запуске с закреплённого ярлыка) — обычного Activate() не
    // всегда достаточно. Кратковременное включение-выключение Topmost — стандартный обходной
    // путь, ставит окно наверх Z-порядка без побочного эффекта постоянного Topmost=true.
    // Возвращаем именно исходное значение Topmost, а не жёстко false, чтобы не выключить
    // случайно уже включённое "поверх всех окон".
    private static void ForceForeground(Window window)
    {
        bool wasTopmost = window.Topmost;
        window.Topmost = true;
        window.Topmost = wasTopmost;
        window.Activate();
    }

    // Раньше здесь ещё был WarmUpMainWindowLayout(): прогревал layout обычного окна заранее в
    // фоне, чтобы клик "Развернуть плеер" из мини-режима ощущался мгновенным. На практике для
    // окна без виртуализации вложенных списков это просто переносило ту же тяжёлую блокирующую
    // работу (Show()+UpdateLayout() одним атомарным проходом) с клика на сам старт — зависание
    // просто переехало на запуск плеера. Убрали совсем: старт снова отзывчивый при любом
    // стартовом виде, а первый разворот из мини-режима на большой библиотеке может занять
    // время — но это уже осознанный отклик на действие пользователя, а не тишина после запуска.

    // Тихая проверка обновлений на старте: не блокирует запуск (полностью в фоне, с задержкой,
    // чтобы не отвлекать ресурсы от первых секунд загрузки плейлиста/обложки) и не показывает
    // диалог повторно для версии, которую пользователь уже отклонил кнопкой "Позже" (см.
    // AppSettings.SkippedUpdateVersion и UpdateAvailableWindow.LaterButton_Click). Любые ошибки
    // (нет сети, репозиторий недоступен и т.п.) молча проглатываются — ручная проверка кнопкой
    // в настройках, в отличие от этой, ошибку покажет.
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

    // Восстанавливает режим отображения плеера, в котором он был на момент прошлого
    // закрытия: скрытую панель плейлиста и/или сам режим мини-плеера. Вызывается из
    // StartupPresent (см. выше) — уже после того, как EnsureHandle() создал HWND, но ДО
    // того, как окно вообще может стать видимым пользователю: сам StartupPresent решает,
    // вызывать ли Show(), уже ПОСЛЕ этого метода. Поэтому если стартовый вид — мини-режим,
    // окно ни разу не успевает появиться на экране в каком-либо виде.
    private void RestorePlayerViewMode()
    {
        PlayerViewMode startupMode;
        bool? legacyPlaylistVisible = null;

        if (_settings.PlayerViewMode == nameof(PlayerViewMode.Square))
            startupMode = PlayerViewMode.Square;
        else if (_settings.PlayerViewMode == nameof(PlayerViewMode.Rectangular))
            startupMode = PlayerViewMode.Rectangular;
        else if (_settings.PlayerViewMode == nameof(PlayerViewMode.Mini))
            startupMode = PlayerViewMode.Mini;
        else if (_isFirstLaunch)
        {
            // Вид плеера ещё ни разу не сохранялся, и настроек вообще никогда не было —
            // самый первый запуск: открываем обычный (квадратный) вид.
            startupMode = PlayerViewMode.Square;
        }
        else
        {
            // settings.json уже существует, но вид плеера ещё не сохранялся — версия плеера
            // до появления этой настройки. Открываем квадратный вид по умолчанию, единообразно
            // с первым запуском; видимость плейлиста восстанавливается отдельно ниже, так что
            // у существующих пользователей она не меняется сама по себе.
            startupMode = _settings.WasMiniPlayerOnClose ? PlayerViewMode.Mini : PlayerViewMode.Square;
            legacyPlaylistVisible = _settings.IsPlaylistVisible;
        }

        if (startupMode == PlayerViewMode.Mini)
        {
            // Сначала приводим "скрытое под мини-плеером" окно к прямоугольному виду (так
            // было и раньше — старая версия не различала квадратный/прямоугольный вид), а
            // уже потом сворачиваем в мини-режим. Так EnterMiniMode запоминает корректный
            // _preMiniViewMode, и "развернуть" из мини-плеера возвращает прямоугольный вид,
            // а не квадратный по умолчанию.
            SetPlayerViewMode(PlayerViewMode.Rectangular, persist: false);
            if (legacyPlaylistVisible == false) SetPlaylistVisibility(false);
            SetPlayerViewMode(PlayerViewMode.Mini, persist: false);
        }
        else
        {
            SetPlayerViewMode(startupMode, persist: false);
            if (legacyPlaylistVisible == false) SetPlaylistVisibility(false);
        }
    }

    // ---------- Плоские представления плейлиста ----------
    // Плейлист хранится по группам, но воспроизведение (индекс текущего трека, next/prev,
    // сохранение между запусками) работает с обычным путём к файлу, поэтому здесь считаем
    // "плоские" списки на лету из групп. Группы почти никогда не бывают настолько большими,
    // чтобы это было заметно по производительности.

    // Пока открыт виртуальный плейлист "Избранное" (см. SetFavoritesViewActive), "Далее"/"Назад"/
    // шафл и автопереход к следующему треку должны листать именно его, а не основной плейлист,
    // который в этот момент даже не показан на экране — поэтому обе "плоские" версии плейлиста,
    // от которых зависит вся навигация по трекам, подменяются списком избранного целиком.
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

    // Восстанавливает сохранённый плейлист и последний трек. Звук запускается только если
    // предыдущий сеанс действительно был активен и пользователь не включил запрет автозапуска.
    // Загрузка не делает массовых обращений к диску: раньше File.Exists по каждому треку выполнялся синхронно до показа
    // окна и был основной причиной долгого "чёрного экрана" при запуске. Устаревшие записи
    // тихо убираются позже, уже после показа (см. VerifyTrackExistenceInBackgroundAsync).
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

    // Единственное место, где восстановленный плейлист трогает диск — запускается уже после
    // показа списка (см. RestoreSavedPlaylistAsync), так что не влияет на скорость появления
    // окна. AsParallel() — File.Exists по разным путям независим, важно на HDD/сетевых путях
    // с высокой задержкой на обращение; AsOrdered() тут не нужен, порядок удаления не важен.
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

            // Была непустой изначально, а теперь опустела целиком (все файлы удалены с диска) —
            // убираем саму папку, а не оставляем пустой заголовок. См. комментарий у
            // foldersThatWereNonEmpty в RestoreSavedPlaylistAsync.
            if (folder.Tracks.Count == 0 && foldersThatWereNonEmpty.Contains(folder))
            {
                _folders.Remove(folder);
                folderWasRemoved = true;
            }
        }

        // folder.Tracks — ObservableCollection<string>, а не элемент плоского отображаемого
        // списка PlaylistFoldersControl.ItemsSource напрямую (см. PlaylistTrackRow) — точечные
        // изменения в ней сами по себе не отражаются на уже построенном плоском списке. Нужен
        // один пересбор в конце, и только если реально что-то изменилось.
        if (anyChanged) RefreshPlaylistView();
        if (folderWasRemoved) StartFolderWatchers();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Глобальные медиаклавиши (Play/Pause, Next, Prev, Stop) — работают даже без фокуса на окне.
        // В try/catch — RegisterHotKey может отказать, если тот же самый хоткей уже занят другим
        // приложением (не редкость для медиаклавиш и особенно для пользовательских комбинаций из
        // настроек); раньше необработанное исключение здесь роняло весь плеер ещё до того, как
        // он успевал показаться на экране.
        try
        {
            _mediaHotKeys = new GlobalMediaHotKeys(this);
            _mediaHotKeys.PlayPausePressed += () => Dispatcher.BeginInvoke(() => PlayPauseButton_Click(this, new RoutedEventArgs()));
            _mediaHotKeys.NextPressed += virtualKey => Dispatcher.BeginInvoke(() => HandleHotkeyNext(virtualKey));
            _mediaHotKeys.PreviousPressed += virtualKey => Dispatcher.BeginInvoke(() => HandleHotkeyPrevious(virtualKey));
            _mediaHotKeys.StopPressed += () => Dispatcher.BeginInvoke(() => StopButton_Click(this, new RoutedEventArgs()));
            _mediaHotKeys.VolumeUpPressed += () => Dispatcher.BeginInvoke(() => ChangeVolumeBy(0.02));
            _mediaHotKeys.VolumeDownPressed += () => Dispatcher.BeginInvoke(() => ChangeVolumeBy(-0.02));
            _mediaHotKeys.MutePressed += () => Dispatcher.BeginInvoke(ToggleMute);
            _mediaHotKeys.ShufflePressed += () => Dispatcher.BeginInvoke(() => ShuffleButton_Click(this, new RoutedEventArgs()));
            _mediaHotKeys.RepeatPressed += () => Dispatcher.BeginInvoke(() => RepeatButton_Click(this, new RoutedEventArgs()));
            _mediaHotKeys.DeleteTrackPressed += () => Dispatcher.BeginInvoke(DeleteCurrentTrackFromDiskHotkey);
            _mediaHotKeys.SeekForwardPressed += () => Dispatcher.BeginInvoke(() => SeekBy(5));
            _mediaHotKeys.SeekBackwardPressed += () => Dispatcher.BeginInvoke(() => SeekBy(-5));
            _mediaHotKeys.ApplyCustomHotkeys(_settings);
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось зарегистрировать глобальные горячие клавиши — возможно, какая-то из комбинаций уже занята другим приложением", ex);
            _mediaHotKeys = null;
        }

        // Интеграция с Now Playing Windows 11 (панель задач, блокировка экрана, наушники с кнопками)
        try
        {
            _nowPlaying = new NowPlayingIntegration(this);
            _nowPlaying.PlayRequested += () => Dispatcher.BeginInvoke(() =>
            {
                if (!_isPlaying) PlayPauseButton_Click(this, new RoutedEventArgs());
            });
            _nowPlaying.PauseRequested += () => Dispatcher.BeginInvoke(() =>
            {
                if (_isPlaying) PlayPauseButton_Click(this, new RoutedEventArgs());
            });
            _nowPlaying.NextRequested += () => Dispatcher.BeginInvoke(PlayNextTrack);
            _nowPlaying.PreviousRequested += () => Dispatcher.BeginInvoke(() => PrevButton_Click(this, new RoutedEventArgs()));
            _nowPlaying.StopRequested += () => Dispatcher.BeginInvoke(() => StopButton_Click(this, new RoutedEventArgs()));
        }
        catch (Exception ex)
        {
            // SMTC недоступен в некоторых окружениях (например, без нужного Windows SDK
            // на машине сборки) — в этом случае просто отключаем интеграцию, плеер работает дальше
            Logger.Error("Не удалось включить интеграцию с Now Playing (SMTC)", ex);
            _nowPlaying = null;
        }

        // Системный трей — тоже в try/catch, по той же причине, что и два блока выше: иконка в
        // трее не критична для работы плеера как такового, а вот необработанное исключение
        // здесь роняло бы всё окно ещё до первого показа.
        try
        {
            _trayIconManager = new TrayIconManager();
            _trayIconManager.OpenRequested += RestoreFromTray;
            _trayIconManager.ExitRequested += ExitApplicationCompletely;
            _trayIconManager.PlayPauseRequested += () => Dispatcher.BeginInvoke(() => PlayPauseButton_Click(this, new RoutedEventArgs()));
            _trayIconManager.NextRequested += () => Dispatcher.BeginInvoke(PlayNextTrack);
            _trayIconManager.PreviousRequested += () => Dispatcher.BeginInvoke(() => PrevButton_Click(this, new RoutedEventArgs()));
            PlaybackStateChanged += isPlaying => _trayIconManager?.SetPlayingState(isPlaying);
            TrackInfoChanged += (title, artist, _) => _trayIconManager?.SetNowPlaying(title, artist, CurrentAlbumArtBytes);
            _trayIconManager.SetPlayingState(_isPlaying);
            _trayIconManager.ApplyTheme(isLight: _settings.IsLightThemeResolved());
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось создать значок в трее", ex);
            _trayIconManager = null;
        }

        ApplyPlaybackButtonsVisibility();
    }

    // Settings.HidePlaybackButtons — кнопки остаются видимыми и кликабельными, но без
    // собственного фона: видна только иконка, сливающаяся с фоном плеера. Ховер/нажатие у
    // ui:Button — отдельный слой поверх Background, так что подсветка продолжает работать и
    // с прозрачным фоном.
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

    // Перекрашивает меню трея под текущую тему — WinForms-меню живёт в отдельном UI-стеке
    // и не подхватывает Fluent-тему WPF-UI автоматически, без явного вызова осталось бы
    // в прежней палитре после переключения темы в настройках
    public void ApplyTrayTheme(bool isLight) => _trayIconManager?.ApplyTheme(isLight);

    // WPF-UI передаёт клик по системной кнопке сворачивания в MinimizeActionOverride.
    // Не полагаемся на поведение TitleBar по умолчанию: при нажатии «Свернуть» главное окно
    // всегда остаётся главным окном и уходит только в панель задач. Переход в мини-плеер
    // возможен исключительно по отдельной кнопке/пункту вида либо повторной активации ярлыка.
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

    private void RestoreFromTray()
    {
        Dispatcher.BeginInvoke(() =>
        {
            // Если сейчас активен мини-плеер, у MainWindow нет валидного показанного состояния
            // (оно скрыто через Hide() — см. EnterMiniMode) — обычный Show() здесь показал бы
            // его ПОВЕРХ ещё открытого окошка мини-плеера, то есть оба сразу на экране разом.
            // Разворачиваем полноценно через тот же путь, что и кнопка "развернуть" в самом
            // мини-плеере — это и закрывает мини-плеер, и корректно поднимает основное окно.
            if (_isMiniMode)
            {
                ExitMiniMode();
                return;
            }

            Show();
            WindowState = WindowState.Normal;
            ForceForeground(this);
            _trayIconManager?.Hide();
        });
    }

    private void ExitApplicationCompletely()
    {
        Dispatcher.BeginInvoke(() =>
        {
            _isExiting = true;
            Close();
        });
    }

    // Вызывается из App при повторной попытке запуска плеера (например, повторным нажатием
    // на ярлык плеера на панели задач/в меню Пуск), пока он уже работает — см.
    // App.OnStartup/WaitForToggleSignal. В отличие от RestoreFromTray (которая всегда просто
    // ПОКАЗЫВАЕТ окно) здесь именно переключение: мини-плеер активен — открываем обычное окно
    // (как кнопкой "развернуть"), обычное окно уже открыто и видимо — сворачиваем в мини-плеер
    // (как кнопкой "мини-плеер"). Если главное окно скрыто в трее ИЛИ свёрнуто в панели задач,
    // внешняя активация только восстанавливает его и никогда не переводит в мини-режим.
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
                ForceForeground(this);
                _trayIconManager?.Hide();
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
            _trayIconManager?.Show($"Lumisense — {TrackTitleText.Text}");

            // MinimizeToTrayOnClose включён по умолчанию, так что обычное закрытие крестиком
            // почти всегда идёт сюда, а не в OnClosed — без явного сохранения здесь позиция
            // трека могла не обновляться месяцами. См. PersistPlaybackAndPlaylistState.
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

    // Переключает вид полосы воспроизведения (см. AppSettings.ProgressBarStyle) — вызывается на
    // старте и заново из окна настроек при переключении этой настройки (см.
    // SettingsWindow.ProgressBarStyleRadio_Changed), пока плеер уже открыт. ProgressSlider
    // остаётся источником истины для позиции/перемотки в любом случае — переключается только
    // то, что видно (см. подробный комментарий в MainWindow.xaml у ProgressWaveform).
    public void ApplyProgressBarStyle()
    {
        bool isWaveform = _settings.ProgressBarStyle == "Waveform";

        ProgressSlider.Visibility = isWaveform ? Visibility.Collapsed : Visibility.Visible;
        ProgressWaveform.Visibility = isWaveform ? Visibility.Visible : Visibility.Collapsed;

        if (isWaveform)
            FireAndForget(EnsureWaveformForCurrentTrackAsync(), "EnsureWaveformForCurrentTrackAsync");
    }

    // Считает (или достаёт из кэша) форму волны для трека, который сейчас загружен — вызывается
    // и из LoadAndPlay при каждой новой загрузке трека (если в этот момент уже выбран режим
    // "Waveform"), и из ApplyProgressBarStyle при переключении НА этот режим для уже играющего
    // трека (до этого момента считать было незачем — режим мог быть выключен всю сессию).
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
            if (_currentTrackPath != filePath || !ReferenceEquals(_waveformCts, cts)) return;

            if (peaks != null)
            {
                _waveformCache[filePath] = peaks;
                _waveformCacheOrder.Enqueue(filePath);
                while (_waveformCacheOrder.Count > WaveformCacheLimit)
                    _waveformCache.Remove(_waveformCacheOrder.Dequeue());
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


    // Применяет акцентный цвет из настроек (см. AppSettings.AccentColorMode/AccentColorHex) —
    // вызывается при старте (ApplySettingsOnStartup) и заново при каждой смене этой настройки
    // или темы (см. SettingsWindow.AccentColorMode/ThemeRadio_Changed) — Apply() учитывает
    // текущую тему, чтобы подобрать светлые/тёмные варианты акцента (SystemAccentColorLight1
    // и т.п.), поэтому пересчитывать нужно и при переключении темы, не только цвета.

    // Не полагаемся на ControlAppearance.Primary у WPF-UI для "включённого" вида этих кнопок —
    // подтверждённый баг библиотеки (github.com/lepoco/wpfui issues #965/#981): она не
    // подхватывает смену акцента вживую. Красим Background вручную — обычное присваивание
    // DependencyProperty, WPF гарантированно применяет и перерисовывает его сразу.
    private void SetAccentButtonActive(Wpf.Ui.Controls.Button button, bool active)
    {
        button.Appearance = ControlAppearance.Secondary;

        if (active)
            button.Background = new SolidColorBrush(GetResolvedAccentColor());
        else
            button.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
    }

    // Возвращает акцент, реально применённый прямо сейчас — тот же цвет, который бы выбрал
    // ApplyAccentColor: свой (AppSettings.AccentColorHex), либо, если он почему-то
    // повреждён, или выбран режим "Системный", актуальный SystemAccentColor (его в ресурсы
    // приложения кладёт сама ApplicationAccentColorManager.ApplySystemAccent). Публичный —
    // им же пользуется и мини-плеер для покраски своих кнопок (см. MiniPlayerWindow.
    // SetAccentButtonActive), чтобы не дублировать эту же логику там ещё раз.
    public Color GetResolvedAccentColor()
    {
        if (_settings.AccentColorMode == "Manual")
        {
            try { return (Color)ColorConverter.ConvertFromString(_settings.AccentColorHex); }
            catch { /* некорректный hex — откатываемся на системный акцент ниже */ }
        }

        if (_settings.AccentColorMode == "Cover" && _coverAccentColor is Color coverColor)
            return coverColor;

        return Application.Current.Resources["SystemAccentColor"] is Color color
            ? color
            : Color.FromRgb(0x00, 0x78, 0xD4);
    }

    private void RefreshCoverThemeColor()
    {
        _coverAccentColor = _currentAlbumArt is null
            ? null
            : ExtractCoverAccentColor(_currentAlbumArt);
    }

    private void ApplySelectableControlAccentResources(Color accent)
    {
        // Явные стили SettingsWindow используют эти DynamicResource. Меняем значения в
        // Application и уже открытых окнах, но не переустанавливаем Template вручную: это
        // вызывало артефакты у Thumb Slider при смене обложки.
        var accentBrush = new SolidColorBrush(accent);
        accentBrush.Freeze();
        var contrastBrush = new SolidColorBrush(GetAccentContrastColor(accent));
        contrastBrush.Freeze();

        void ApplyResources(ResourceDictionary resources)
        {
            resources["AccentFillColorDefaultBrush"] = accentBrush;
            resources["AccentFillColorSecondaryBrush"] = accentBrush;
            resources["AccentTextFillColorPrimaryBrush"] = accentBrush;
            resources["TextOnAccentFillColorPrimaryBrush"] = contrastBrush;
        }

        ApplyResources(Application.Current.Resources);
        foreach (Window window in Application.Current.Windows.OfType<Window>())
            ApplyResources(window.Resources);
    }

    public void ApplyAccentColor()
    {
        RefreshCoverThemeColor();
        Color appliedAccent;

        if (_settings.AccentColorMode == "Cover" && _coverAccentColor is Color coverColor)
        {
            appliedAccent = coverColor;
            ApplicationAccentColorManager.Apply(appliedAccent,
                _settings.IsLightThemeResolved() ? ApplicationTheme.Light : ApplicationTheme.Dark);
        }
        else if (_settings.AccentColorMode == "Manual")
        {
            try
            {
                appliedAccent = (Color)ColorConverter.ConvertFromString(_settings.AccentColorHex);
                ApplicationAccentColorManager.Apply(appliedAccent,
                    _settings.IsLightThemeResolved() ? ApplicationTheme.Light : ApplicationTheme.Dark);
            }
            catch
            {
                ApplicationAccentColorManager.ApplySystemAccent();
                appliedAccent = GetResolvedAccentColor();
            }
        }
        else
        {
            ApplicationAccentColorManager.ApplySystemAccent();
            appliedAccent = GetResolvedAccentColor();
        }

        IconResources.AccentContrastBrush = new SolidColorBrush(GetAccentContrastColor(appliedAccent));
        ApplySelectableControlAccentResources(appliedAccent);
        RefreshAccentDependentIcons();
        _miniPlayerWindow?.ApplyArtworkProgressColor();
        ApplyCoverBaseBackground();
    }

    // Вызывается из SettingsWindow отдельно от ApplyAccentColor: окраска основы больше не
    // зависит от того, выбран ли акцент от обложки.
    public void ApplyCoverBaseTheme() => ApplyCoverBaseBackground();

    // Добавляет к системному Mica/Acrylic очень прозрачный слой текущей обложки. Эта настройка
    // независима от AccentColorMode: акцент и основа окна могут использовать разные источники.
    private void ApplyCoverBaseBackground()
    {
        if (!_settings.CoverBaseFromCover || _currentAlbumArt is null)
        {
            RootGrid.Background = Brushes.Transparent;
            return;
        }

        RefreshCoverThemeColor();
        if (_coverAccentColor is not Color cover)
        {
            RootGrid.Background = Brushes.Transparent;
            return;
        }

        byte r = (byte)Math.Clamp((int)Math.Round(cover.R * 0.52), 0, 255);
        byte g = (byte)Math.Clamp((int)Math.Round(cover.G * 0.52), 0, 255);
        byte b = (byte)Math.Clamp((int)Math.Round(cover.B * 0.52), 0, 255);
        RootGrid.Background = new SolidColorBrush(Color.FromArgb(0x4A, r, g, b));
    }

    // Чёрный или белый — по относительной яркости акцента (тот же принцип, что и в
    // рекомендациях WCAG для контраста текста, без гамма-коррекции — для выбора между двумя
    // вариантами такая упрощённая формула более чем достаточна). Порог 0.6, а не ровно 0.5, —
    // чтобы на пограничных, но всё ещё достаточно ярких акцентах (жёлтый/оранжевый и подобные)
    // увереннее склоняться к тёмному варианту, а не оставлять белый там, где он уже еле читается.
    private static Color? ExtractCoverAccentColor(BitmapSource source)
    {
        try
        {
            const int size = 32;
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
                context.DrawImage(source, new Rect(0, 0, size, size));

            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var pixels = new byte[size * size * 4];
            bitmap.CopyPixels(pixels, size * 4, 0);

            double red = 0, green = 0, blue = 0, weightSum = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                double blueValue = pixels[i] / 255.0;
                double greenValue = pixels[i + 1] / 255.0;
                double redValue = pixels[i + 2] / 255.0;
                double brightness = Math.Max(redValue, Math.Max(greenValue, blueValue));
                double minimum = Math.Min(redValue, Math.Min(greenValue, blueValue));
                double saturation = brightness <= 0 ? 0 : (brightness - minimum) / brightness;
                if (brightness < 0.08 || saturation < 0.12) continue;

                double weight = 0.25 + saturation * 0.75;
                red += redValue * weight;
                green += greenValue * weight;
                blue += blueValue * weight;
                weightSum += weight;
            }

            if (weightSum <= 0) return null;
            return Color.FromRgb(
                (byte)Math.Clamp(red / weightSum * 255, 0, 255),
                (byte)Math.Clamp(green / weightSum * 255, 0, 255),
                (byte)Math.Clamp(blue / weightSum * 255, 0, 255));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось извлечь цвет из обложки: {ex.Message}");
            return null;
        }
    }

    private static Color GetAccentContrastColor(Color accent)
    {
        double luminance = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255.0;
        return luminance > 0.6 ? Colors.Black : Colors.White;
    }

    // IconResources.AccentContrastBrush задаёт цвет ТОЛЬКО для новых/только что назначаемых
    // иконок (см. IconResources.SetOnAccent) — уже показанные на постоянно акцентных кнопках
    // иконки (Пуск/Пауза, включённые Шаффл/Повтор) сами по себе не перекрасятся только от
    // смены этого статического свойства, их нужно переприсвоить явно. Вызывается сразу после
    // пересчёта AccentContrastBrush выше — и на старте, и при каждой смене акцента в
    // настройках (см. SettingsWindow.AccentModeRadio_Changed/ApplyAccentHex).
    private void RefreshAccentDependentIcons()
    {
        PlayPauseButton.Icon = IconResources.MakeOnAccent(_isPlaying ? "IconPause" : "IconPlay", 15);
        PlayPauseButton.Background = new SolidColorBrush(GetResolvedAccentColor()); // всегда акцентная, не переключается
        ProgressWaveform.PlayedBrush = new SolidColorBrush(GetResolvedAccentColor());

        SetAccentButtonActive(ShuffleButton, _isShuffleEnabled);
        IconResources.SetOnAccent(ShuffleIcon, _isShuffleEnabled);

        RepeatButton.Icon = _repeatMode switch
        {
            RepeatMode.All => IconResources.MakeOnAccent("IconRepeatAll"),
            RepeatMode.One => IconResources.MakeOnAccent("IconRepeatOne"),
            _ => RepeatButton.Icon
        };
        SetAccentButtonActive(RepeatButton, _repeatMode != RepeatMode.Off);
        SetAccentButtonActive(LyricsPanelButton, _isLyricsPanelActive);
        IconResources.SetOnAccent(LyricsPanelButtonIcon, _isLyricsPanelActive);

        if (_isFavoritesView)
        {
            IconResources.SetOnAccent(FavoritesButtonIcon, true);
            SetAccentButtonActive(FavoritesButton, true);
        }

        _miniPlayerWindow?.UpdateSecondaryButton();
        _miniPlayerWindow?.RefreshAccentButtons();
    }

    // Подложка главного окна (см. AppSettings.WindowBackdropType) — вызывается при старте и
    // заново из окна настроек при переключении этой настройки (см.
    // SettingsWindow.WindowBackdropRadio_Changed), пока главное окно уже открыто.
    public void ApplyWindowBackdrop()
    {
        WindowBackdropType = _settings.WindowBackdropType == "Acrylic"
            ? Wpf.Ui.Controls.WindowBackdropType.Acrylic
            : Wpf.Ui.Controls.WindowBackdropType.Mica;
        ApplyCoverBaseBackground();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettingsWindow();

    private void StatisticsButton_Click(object sender, RoutedEventArgs e) => ShowStatisticsWindow();

    // Открывает окно статистики (или активирует уже открытое, по тому же принципу, что и
    // у ShowSettingsWindow) — своё окно, а не страница внутри настроек: данные там строятся
    // асинхронно (чтение тегов, см. StatisticsWindow.LoadAsync) и логически не привязаны
    // к настройкам приложения, это именно просмотр накопленной статистики.
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

    // Открывает окно настроек (или активирует уже открытое). Вынесено из SettingsButton_Click
    // в отдельный публичный метод, чтобы то же самое можно было вызвать и не по клику на
    // кнопку — например, когда окно списка изменений закрывают, и настройки должны открыться
    // заново (см. ShowChangelogWindow ниже).
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
            // Окно уже открыто — возможно, на какой-то другой странице (например, было
            // открыто вручную из основного окна, а сейчас его просят открыть на "Мини-плеер"
            // из контекстного меню мини-плеера) — переключаем страницу и на уже открытом окне,
            // а не только при первом создании.
            if (section != null) _settingsWindow.NavigateToPage(section);
            _settingsWindow.Activate();
        }
    }

    private ChangelogWindow? _changelogWindow;

    // Список изменений и настройки не должны быть открыты одновременно: открытие списка
    // изменений закрывает окно настроек, а закрытие списка изменений открывает настройки
    // заново. Вызывается из SettingsWindow.ChangelogButton_Click.
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

    // ---------- Просмотр обложки ----------

    private void AlbumArtBorder_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_settings.AlbumArtGesturesEnabled)
        {
            OpenAlbumArtPreview();
            e.Handled = true;
            return;
        }

        _albumArtGestureStart = e.GetPosition(AlbumArtBorder);
        _albumArtGestureMoved = false;
        _isAlbumArtGestureActive = AlbumArtBorder.CaptureMouse();
    }

    private void AlbumArtBorder_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isAlbumArtGestureActive || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;

        Vector delta = e.GetPosition(AlbumArtBorder) - _albumArtGestureStart;
        if (delta.Length >= AlbumArtGestureThreshold)
            _albumArtGestureMoved = true;
    }

    private void AlbumArtBorder_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_isAlbumArtGestureActive) return;

        Point end = e.GetPosition(AlbumArtBorder);
        AlbumArtBorder.ReleaseMouseCapture();
        _isAlbumArtGestureActive = false;

        Vector delta = end - _albumArtGestureStart;
        if (!_albumArtGestureMoved || delta.Length < AlbumArtGestureThreshold)
        {
            ExternalPlayPause();
            e.Handled = true;
            return;
        }

        if (Math.Abs(delta.X) >= Math.Abs(delta.Y))
        {
            if (delta.X < 0) ExternalNext();
            else ExternalPrev();
        }
        else
        {
            ExternalChangeVolume(delta.Y < 0 ? 0.04 : -0.04);
        }

        e.Handled = true;
    }

    // Просмотр обложки остаётся доступен из контекстного меню. Так короткий клик на самой
    // обложке можно использовать как предсказуемый жест пуск/пауза, не теряя эту функцию.
    private void OpenAlbumArtPreview()
    {
        // У трека может не быть обложки (показан плейсхолдер-иконка) — тогда открывать нечего
        if (_currentAlbumArt is null) return;

        if (_coverArtWindow == null)
        {
            _coverArtWindow = new CoverArtWindow(_currentAlbumArt, TrackTitleText.Text, _settings)
            {
                Owner = this
            };

            // Screen.WorkingArea возвращает физические пиксели, а Left/Top/Width/Height
            // WPF-окна задаются в DIP. Прямое присваивание давало окно больше рабочей области
            // на мониторах с масштабированием 125/150/200%. Переводим обе координаты и размер
            // через текущий DPI главного окна и открываем CoverArtWindow ровно по рабочей области.
            var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
            var workArea = screen.WorkingArea;
            var fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
                             ?? Matrix.Identity;
            var workTopLeft = fromDevice.Transform(new Point(workArea.Left, workArea.Top));

            _coverArtWindow.WindowState = WindowState.Normal;
            _coverArtWindow.WindowStartupLocation = WindowStartupLocation.Manual;
            _coverArtWindow.Left = workTopLeft.X;
            _coverArtWindow.Top = workTopLeft.Y;
            _coverArtWindow.Width = Math.Max(_coverArtWindow.MinWidth, workArea.Width * fromDevice.M11);
            _coverArtWindow.Height = Math.Max(_coverArtWindow.MinHeight, workArea.Height * fromDevice.M22);

            _coverArtWindow.Closed += (_, _) => _coverArtWindow = null;
            _coverArtWindow.Show();
        }
        else
        {
            _coverArtWindow.Activate();
        }
    }

    private void OpenAlbumArtMenuItem_Click(object sender, RoutedEventArgs e) => OpenAlbumArtPreview();

    // Обложки может не быть (плейсхолдер-иконка) — тогда контекстное меню показывать не о
    // чем, все четыре пункта всё равно ничего бы не сделали.
    private void AlbumArtBorder_ContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        if (_currentAlbumArt is null) e.Handled = true;
    }

    private static string MimeTypeToExtension(string? mimeType) => mimeType?.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/bmp" => ".bmp",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        _ => ".jpg"
    };

    private static string MimeTypeToFilter(string extension) => extension switch
    {
        ".png" => "Изображение PNG (*.png)|*.png",
        ".bmp" => "Изображение BMP (*.bmp)|*.bmp",
        ".gif" => "Изображение GIF (*.gif)|*.gif",
        ".webp" => "Изображение WebP (*.webp)|*.webp",
        _ => "Изображение JPEG (*.jpg)|*.jpg"
    };

    // Имя файла по умолчанию в диалоге сохранения — название трека (если есть), иначе
    // просто "Обложка", с заменой символов, недопустимых в имени файла Windows.
    private string SuggestAlbumArtFileName()
    {
        string baseName = !string.IsNullOrWhiteSpace(TrackTitleText.Text) && TrackTitleText.Text != "Файл не выбран"
            ? TrackTitleText.Text
            : "Обложка";

        foreach (char c in Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(c, '_');

        return baseName;
    }

    private void DownloadAlbumArtMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_currentAlbumArtBytes is null) return;

        string extension = MimeTypeToExtension(_currentAlbumArtMimeType);
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить обложку",
            FileName = SuggestAlbumArtFileName() + extension,
            Filter = MimeTypeToFilter(extension)
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllBytes(dialog.FileName, _currentAlbumArtBytes);
        }
        catch (Exception ex)
        {
            LocalizedMessageBox.Show(this, $"Не удалось сохранить изображение:\n{ex.Message}", "Ошибка",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void CopyAlbumArtMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_currentAlbumArt is null) return;

        try
        {
            System.Windows.Clipboard.SetImage(_currentAlbumArt);
        }
        catch (Exception ex)
        {
            LocalizedMessageBox.Show(this, $"Не удалось скопировать изображение:\n{ex.Message}", "Ошибка",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void AlbumArtPropertiesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_currentAlbumArt is null || _currentAlbumArtBytes is null) return;

        var propsWindow = new CoverArtPropertiesWindow(
            _currentAlbumArt, _currentAlbumArtBytes, _currentAlbumArtMimeType, _currentAlbumArtPictureType,
            TrackTitleText.Text, TrackArtistText.Text, _currentTrackPath, _settings)
        {
            Owner = this
        };
        propsWindow.ShowDialog();
    }

    // ---------- Скрыть/показать весь плейлист ----------

    private bool _isPlaylistVisible = true;
    private double _heightBeforeHidingPlaylist;

    private const double MinHeightWithPlaylist = 680; // как задан MinHeight окна в XAML

    // Квадратный вид использует более крупный стиль элементов (см. ApplyContentScale) —
    // без иного размера, кроме MinHeightWithPlaylist, всё, что не влезло в 680px, отнимало
    // бы место именно у плейлиста, вплоть до его исчезновения. Запас подобран так, чтобы
    // строка плейлиста осталась видна на несколько треков, а не сжалась до нуля.
    private const double SquareMinHeightWithPlaylist = 860;

    // Шеврон рядом с "Плейлист" — быстрый способ скрыть/показать панель плейлиста, никак не
    // связанный с видом плеера (PlayerViewMode): квадратный вид — это увеличенное окно с
    // крупным стилем, а не просто "плейлист скрыт", так что это две независимые настройки.
    private void TogglePlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        SetPlaylistVisibility(!_isPlaylistVisible);
    }

    // Показывает/скрывает панель плейлиста и подгоняет высоту окна под новое состояние.
    // Вынесено из TogglePlaylistButton_Click, чтобы то же самое можно было применить
    // при старте, восстанавливая состояние, сохранённое при прошлом закрытии.
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

            // Захардкоженное число тут оказывалось меньше, чем реально нужно для контента
            // (обложка, прогресс, кнопки, громкость) — всё обрезалось по нижнему краю. Вместо
            // гадания даём WPF самому измерить, сколько места нужно оставшимся строкам грида.
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
        TogglePlaylistButton.ToolTip = _isPlaylistVisible ? "Скрыть плейлист" : "Показать плейлист";
    }

    // Одна панель может показывать три взаимоисключающих представления: обычный плейлист,
    // избранное и текст композиции. Разделяем выбор содержимого и саму видимость панели: шеврон
    // продолжает сворачивать весь блок, а кнопка текста заменяет только его внутренности.
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
        SetAccentButtonActive(LyricsPanelButton, showLyrics);
        IconResources.SetOnAccent(LyricsPanelButtonIcon, showLyrics);
    }

    private void LyricsPanelButton_Click(object sender, RoutedEventArgs e)
    {
        SetLyricsPanelActive(!_isLyricsPanelActive);
    }

    private void SetLyricsPanelActive(bool active)
    {
        if (active && !_isPlaylistVisible)
            SetPlaylistVisibility(true);

        _isLyricsPanelActive = active;
        bool wasFavoritesView = _isFavoritesView;
        if (active)
        {
            _isFavoritesView = false;
            FavoritesButtonIcon.Icon = "IconHeart";
            IconResources.SetOnAccent(FavoritesButtonIcon, false);
        }

        UpdatePlaylistSurface();
        if (active && wasFavoritesView)
            QueuePlaylistSearch();

        if (active)
            FireAndForget(LoadMainWindowLyricsAsync(_currentTrackPath), "LoadMainWindowLyricsAsync");
        else
            CancelMainWindowLyricsLoad();
    }

    private void CancelMainWindowLyricsLoad()
    {
        CancellationTokenSource? previous = Interlocked.Exchange(ref _mainWindowLyricsCts, null);
        previous?.Cancel();
    }

    private async Task LoadMainWindowLyricsAsync(string? trackPath)
    {
        CancelMainWindowLyricsLoad();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _mainWindowLyricsCts = cts;
        CancellationToken token = cts.Token;
        _mainWindowLyricsTrackPath = trackPath;
        _mainWindowLyrics = LyricsDocument.Empty;
        ApplyMainWindowLyricsLoading();

        try
        {
            LyricsDocument document = await LyricsService.LoadAsync(trackPath, token);
            if (!IsMainWindowLyricsRequestCurrent(trackPath, token)) return;

            // Такое же безопасное автодополнение, как в Now Playing: онлайн-результат берём
            // только при точном совпадении title + artist и сохраняем в соседний LRC/TXT.
            if (document.Kind == LyricsKind.None && !string.IsNullOrWhiteSpace(trackPath))
            {
                LyricsPanelSourceText.Text = LocalizationService.Translate("Ищем текст…");
                IReadOnlyList<OnlineLyricsResult> results = await LyricsService.SearchOnlineAsync(
                    CurrentTitle, CurrentArtist, token);
                if (!IsMainWindowLyricsRequestCurrent(trackPath, token)) return;

                OnlineLyricsResult? exact = results.FirstOrDefault(result =>
                    SameLyricsTrackField(result.TrackName, CurrentTitle) &&
                    SameLyricsTrackField(result.ArtistName, CurrentArtist));
                if (exact is not null)
                {
                    await LyricsService.SaveOnlineResultAsync(trackPath, exact, token);
                    if (!IsMainWindowLyricsRequestCurrent(trackPath, token)) return;
                    document = LyricsService.CreateDocumentFromOnlineResult(exact);
                }
            }

            _mainWindowLyrics = document;
            ApplyMainWindowLyricsDocument(document);
        }
        catch (LyricsRateLimitException)
        {
            if (IsMainWindowLyricsRequestCurrent(trackPath, token))
                ApplyMainWindowLyricsEmpty("Поиск временно ограничен");
        }
        catch (OperationCanceledException)
        {
            // Нормально при смене трека, закрытии панели или завершении приложения.
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось загрузить текст для панели главного окна", ex);
            if (IsMainWindowLyricsRequestCurrent(trackPath, token))
                ApplyMainWindowLyricsEmpty("Текст не найден");
        }
        finally
        {
            if (ReferenceEquals(_mainWindowLyricsCts, cts))
                _mainWindowLyricsCts = null;
            cts.Dispose();
        }
    }

    private bool IsMainWindowLyricsRequestCurrent(string? trackPath, CancellationToken token) =>
        _isLyricsPanelActive && !token.IsCancellationRequested &&
        string.Equals(trackPath, _mainWindowLyricsTrackPath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(trackPath, _currentTrackPath, StringComparison.OrdinalIgnoreCase);

    private static bool SameLyricsTrackField(string left, string right)
    {
        static string Normalize(string value) => new(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

        string normalizedLeft = Normalize(left);
        string normalizedRight = Normalize(right);
        return normalizedLeft.Length > 0 && normalizedLeft == normalizedRight;
    }

    private void ApplyMainWindowLyricsLoading()
    {
        _mainWindowSyncedLyrics.Clear();
        _activeMainWindowLyricIndex = -2;
        ResetMainWindowSyncedLyricsScroll();
        LyricsPanelTitleText.Text = LocalizationService.Translate("Текст песни");
        LyricsPanelSourceText.Text = LocalizationService.Translate("Загружаем текст…");
        LyricsPanelText.Text = string.Empty;
        LyricsPanelSyncedList.Visibility = Visibility.Collapsed;
        LyricsPanelScrollViewer.Visibility = Visibility.Visible;
        LyricsPanelEmptyState.Visibility = Visibility.Collapsed;
    }

    private void ApplyMainWindowLyricsDocument(LyricsDocument document)
    {
        _mainWindowSyncedLyrics.Clear();
        _activeMainWindowLyricIndex = -2;
        ResetMainWindowSyncedLyricsScroll();
        if (document.Kind == LyricsKind.None)
        {
            ApplyMainWindowLyricsEmpty("Текст не найден");
            return;
        }

        LyricsPanelTitleText.Text = LocalizationService.Translate(document.Kind == LyricsKind.Synced
            ? "Синхронный текст" : "Текст песни");
        LyricsPanelSourceText.Text = LocalizationService.Translate(document.SourceLabel);
        LyricsPanelEmptyState.Visibility = Visibility.Collapsed;

        if (document.Kind == LyricsKind.Synced)
        {
            foreach (LyricLine line in document.Lines)
            {
                var lyricLine = new MainWindowLyricLine { Time = line.Time, Text = line.Text };
                ApplySyncedLyricsLineAppearance(lyricLine, active: false, animate: false);
                _mainWindowSyncedLyrics.Add(lyricLine);
            }

            LyricsPanelText.Text = string.Empty;
            LyricsPanelScrollViewer.Visibility = Visibility.Collapsed;
            LyricsPanelSyncedList.Visibility = Visibility.Visible;

            // Новый документ всегда начинается с первой LRC-строки: не считываем здесь
            // прежнюю позицию аудио/старого списка, иначе новая песня визуально открывалась
            // в середине. После layout повторяем ScrollToTop для уже видимого ListBox.
            UpdateMainWindowSyncedLyrics(TimeSpan.Zero, forceScroll: true);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (_isLyricsPanelActive && _mainWindowLyrics.Kind == LyricsKind.Synced)
                    ResetMainWindowSyncedLyricsScroll();
            }));
            return;
        }

        LyricsPanelText.Text = document.PlainText;
        LyricsPanelSyncedList.Visibility = Visibility.Collapsed;
        LyricsPanelScrollViewer.Visibility = Visibility.Visible;
        LyricsPanelScrollViewer.ScrollToTop();
    }

    private void ApplyMainWindowLyricsEmpty(string status)
    {
        _mainWindowSyncedLyrics.Clear();
        _activeMainWindowLyricIndex = -2;
        ResetMainWindowSyncedLyricsScroll();
        LyricsPanelTitleText.Text = LocalizationService.Translate("Текст песни");
        LyricsPanelSourceText.Text = LocalizationService.Translate(status);
        LyricsPanelText.Text = string.Empty;
        LyricsPanelSyncedList.Visibility = Visibility.Collapsed;
        LyricsPanelScrollViewer.Visibility = Visibility.Collapsed;
        LyricsPanelEmptyState.Visibility = Visibility.Visible;
    }

    private void UpdateMainWindowSyncedLyrics(TimeSpan position, bool forceScroll = false)
    {
        if (!_isLyricsPanelActive || _mainWindowLyrics.Kind != LyricsKind.Synced || _mainWindowSyncedLyrics.Count == 0)
            return;

        int activeIndex = LyricsService.FindActiveLineIndex(_mainWindowLyrics.Lines, position);
        if (!forceScroll && activeIndex == _activeMainWindowLyricIndex)
            return;

        if (_activeMainWindowLyricIndex >= 0 && _activeMainWindowLyricIndex < _mainWindowSyncedLyrics.Count)
        {
            MainWindowLyricLine previousLine = _mainWindowSyncedLyrics[_activeMainWindowLyricIndex];
            previousLine.IsActive = false;
            ApplySyncedLyricsLineAppearance(previousLine, active: false, animate: true);
        }

        _activeMainWindowLyricIndex = activeIndex;
        if (activeIndex < 0 || activeIndex >= _mainWindowSyncedLyrics.Count)
            return;

        MainWindowLyricLine activeLine = _mainWindowSyncedLyrics[activeIndex];
        activeLine.IsActive = true;
        ApplySyncedLyricsLineAppearance(activeLine, active: true, animate: true);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_isLyricsPanelActive && _activeMainWindowLyricIndex == activeIndex)
                SmoothScrollLyricsToActiveLine(activeLine);
        }));
    }

    private void ResetMainWindowSyncedLyricsScroll()
    {
        System.Windows.Controls.ScrollViewer? scrollViewer = FindVisualChild<System.Windows.Controls.ScrollViewer>(LyricsPanelSyncedList);
        if (scrollViewer is null) return;

        scrollViewer.BeginAnimation(AnimatedScrollOffsetProperty, null);
        scrollViewer.ScrollToTop();
    }

    private static void OnAnimatedScrollOffsetChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is System.Windows.Controls.ScrollViewer scrollViewer && args.NewValue is double offset)
            scrollViewer.ScrollToVerticalOffset(offset);
    }

    private void SmoothScrollLyricsToActiveLine(MainWindowLyricLine activeLine)
    {
        // CanContentScroll=False в XAML переводит ScrollViewer в пиксельные единицы. Поэтому
        // ExtentHeight, ViewportHeight и смещение ниже находятся в одной системе координат и
        // анимация не смешивает индекс элементов с пикселями (прежняя причина скачка в конец).
        LyricsPanelSyncedList.UpdateLayout();
        FrameworkElement? container = LyricsPanelSyncedList.ItemContainerGenerator.ContainerFromItem(activeLine) as FrameworkElement;
        if (container is null)
        {
            LyricsPanelSyncedList.ScrollIntoView(activeLine);
            LyricsPanelSyncedList.UpdateLayout();
            container = LyricsPanelSyncedList.ItemContainerGenerator.ContainerFromItem(activeLine) as FrameworkElement;
            if (container is null) return;
        }

        System.Windows.Controls.ScrollViewer? scrollViewer = FindVisualChild<System.Windows.Controls.ScrollViewer>(LyricsPanelSyncedList);
        if (scrollViewer is null || scrollViewer.ViewportHeight <= 0 || container.ActualHeight <= 0) return;

        try
        {
            Point itemTop = container.TranslatePoint(new Point(0, 0), LyricsPanelSyncedList);
            double viewportHeight = Math.Min(scrollViewer.ViewportHeight, LyricsPanelSyncedList.ActualHeight);
            double maxOffset = Math.Max(0, scrollViewer.ExtentHeight - scrollViewer.ViewportHeight);
            double targetOffset = Math.Clamp(
                scrollViewer.VerticalOffset + itemTop.Y - (viewportHeight - container.ActualHeight) / 2,
                0, maxOffset);

            var animation = new DoubleAnimation(scrollViewer.VerticalOffset, targetOffset,
                new Duration(TimeSpan.FromMilliseconds(360)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            scrollViewer.BeginAnimation(AnimatedScrollOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }
        catch (InvalidOperationException)
        {
            // В момент пересоздания контейнеров ListBox WPF может временно разорвать visual tree.
        }
    }

    private void LyricsPanelSyncedList_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_audioFile is null) return;
        if (System.Windows.Controls.ItemsControl.ContainerFromElement(LyricsPanelSyncedList, e.OriginalSource as DependencyObject)
            is not System.Windows.Controls.ListBoxItem { DataContext: MainWindowLyricLine line })
            return;

        TimeSpan target = line.Time;
        if (_audioFile.TotalTime > TimeSpan.Zero)
            target = target > _audioFile.TotalTime ? _audioFile.TotalTime : target;

        _audioFile.CurrentTime = target;

        // Обновляем полосу и активную строку сразу, не дожидаясь следующего тика таймера.
        _isSyncingProgressFromPlayback = true;
        try
        {
            ProgressSlider.Value = Math.Clamp(target.TotalSeconds, ProgressSlider.Minimum, ProgressSlider.Maximum);
        }
        finally
        {
            _isSyncingProgressFromPlayback = false;
        }
        UpdateMainWindowSyncedLyrics(target, forceScroll: true);
        LyricsPanelSyncedList.SelectedItem = null;
        e.Handled = true;
    }

    // Настройки из SettingsWindow меняются без перезапуска: применяем их ко всем уже
    // созданным строкам. Активная строка всегда белая, неактивные — серые; акцент приложения
    // здесь намеренно не используется, чтобы текст оставался нейтральным при любой теме.
    public void ApplySyncedLyricsAppearance()
    {
        for (int index = 0; index < _mainWindowSyncedLyrics.Count; index++)
            ApplySyncedLyricsLineAppearance(_mainWindowSyncedLyrics[index], index == _activeMainWindowLyricIndex, animate: true);
    }

    private void ApplySyncedLyricsLineAppearance(MainWindowLyricLine line, bool active, bool animate)
    {
        double fontSize = Math.Clamp(_settings.SyncedLyricsFontSize, 12, 20);
        line.FontSize = fontSize;
        line.LineHeight = Math.Max(21, Math.Round(fontSize * 1.58));

        // Масштабирование больше не предлагается в настройках: остаются нейтральный режим
        // и мягкое свечение. Старые значения Scale/GlowScale безопасно читаются как Glow.
        bool useScale = false;
        bool useGlow = active && _settings.SyncedLyricsHighlightEffect != "None";
        Color foreground = active ? Colors.White : Color.FromRgb(142, 142, 142);
        double scale = useScale ? 1.055 : 1.0;
        double glowOpacity = useGlow ? 0.62 : 0.0;

        if (!animate)
        {
            line.Foreground.BeginAnimation(SolidColorBrush.ColorProperty, null);
            line.Foreground.Color = foreground;
            line.ScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            line.ScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            line.ScaleTransform.ScaleX = scale;
            line.ScaleTransform.ScaleY = scale;
            line.GlowEffect.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, null);
            line.GlowEffect.Opacity = glowOpacity;
            return;
        }

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(230));
        line.Foreground.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(foreground, duration) { EasingFunction = easing });
        line.ScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(scale, duration) { EasingFunction = easing });
        line.ScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(scale, duration) { EasingFunction = easing });
        line.GlowEffect.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
            new DoubleAnimation(glowOpacity, duration) { EasingFunction = easing });
    }

    // ---------- Вид плеера (квадратный / прямоугольный / мини-плеер) ----------
    // Единая точка входа для переключения между тремя видами плеера — вызывается и из
    // контекстного меню по клику на заголовок "Lumisense" (см. TitleClickArea в XAML),
    // и из шеврона "скрыть/показать плейлист", и при восстановлении сохранённого вида
    // на старте (см. RestorePlayerViewMode).
    private void SetPlayerViewMode(PlayerViewMode mode, bool persist = true)
    {
        if (mode == PlayerViewMode.Mini)
        {
            // EnterMiniMode ещё внутри себя читает _viewMode (пока это старое значение) —
            // чтобы запомнить его в _preMiniViewMode, поэтому присваиваем новое значение
            // уже после вызова, а не до
            if (!_isMiniMode) EnterMiniMode();
            _viewMode = mode;
        }
        else
        {
            if (_isMiniMode) ExitMiniMode();

            _viewMode = mode;

            bool square = mode == PlayerViewMode.Square;

            // Порядок важен: сначала переключаем крупный/обычный стиль элементов управления,
            // и только потом подгоняем высоту под плейлист — SetPlaylistVisibility замеряет
            // нужную высоту окна ПОСЛЕ того, как контент уже стал крупнее.
            //
            // Плейлист теперь остаётся открытым по умолчанию и в квадратном виде — раньше он
            // автоматически скрывался, и в обычном (квадратном) окне плеера его приходилось
            // каждый раз открывать заново шевроном.
            ApplyContentScale(square || _isFullscreenLayout);
            SetPlaylistVisibility(true);

            if (square)
            {
                // Крупный контент квадратного вида занимает больше места, чем обычная
                // MinHeightWithPlaylist (680) предполагает для прямоугольного окна — без
                // этого запаса плейлисту не хватило бы места и он визуально сжался бы
                // почти до нуля вместо того, чтобы быть видимым. На маленьких экранах не
                // даём окну вылезти выше рабочей области — квадрат тогда получится чуть
                // меньше, но останется полностью на экране.
                if (Height < SquareMinHeightWithPlaylist)
                {
                    double screenLimit = SystemParameters.WorkArea.Height - 40;
                    double targetHeight = Math.Min(SquareMinHeightWithPlaylist, Math.Max(MinHeightWithPlaylist, screenLimit));
                    Height = targetHeight;
                    MinHeight = targetHeight;
                }
                MakeWindowSquare();
            }
            else
            {
                RestoreRectangularWidth();
            }

            // MakeWindowSquare/RestoreRectangularWidth растят окно вправо-вниз от текущего
            // угла — если оно стояло у правого/нижнего края экрана, могло вылезти за пределы
            // рабочей области. Просто клэмп в границы экрана, без магнитного прилипания.
            ClampWindowToWorkArea();

            // Считаем ширину контента ПОСЛЕ того, как Width/Height уже приведены к новому
            // виду (MakeWindowSquare/RestoreRectangularWidth выше) — иначе для квадратного
            // вида здесь использовалась бы ещё старая, дорезайзовая ширина окна, и контент
            // остался бы узким колонкой посреди широкого окна с пустыми полями по бокам.
            UpdateContentMaxWidth();
        }

        if (persist)
        {
            _settings.PlayerViewMode = mode.ToString();
            SettingsManager.Save(_settings);
        }

        UpdateViewModeMenuChecks();
        _settingsWindow?.RefreshViewModeRadios();
    }

    // К этому моменту стиль элементов уже переключён на крупный, плейлист виден, а Height
    // уже подогнана под него (см. SetPlayerViewMode, включая запас SquareMinHeightWithPlaylist).
    // Делаем Width равной этой высоте, чтобы получить настоящий квадрат.
    private void MakeWindowSquare()
    {
        double size = Math.Max(Height, MinWidth);
        MinWidth = size;
        Width = size;
    }

    // Возвращает ширину/минимальную ширину окна к обычным значениям прямоугольного вида.
    // Высотой уже занимается сам SetPlaylistVisibility(true) — он помнит, какой она была
    // до того, как плейлист в последний раз скрывали.
    private void RestoreRectangularWidth()
    {
        MinWidth = 400; // как задан MinWidth окна в XAML
        Width = DefaultWindowWidth;
    }

    // ---------- Не даём окну вылезти за экран при смене вида (Квадрат/Прямоугольный) ----------
    // См. вызов в SetPlayerViewMode. MakeWindowSquare/RestoreRectangularWidth меняют только
    // Width/Height, оставляя Left/Top как есть — окно растёт строго вправо-вниз от текущего
    // угла, и если оно стояло у самого правого/нижнего края экрана, выросшее окно могло
    // оказаться частично за пределами рабочей области. Это простой безусловный клэмп в
    // границы экрана — никакого "магнитного" примагничивания к краю тут нет и не было, только
    // гарантия, что окно останется полностью видимым и доступным для мыши.
    private void ClampWindowToWorkArea()
    {
        if (PresentationSource.FromVisual(this)?.CompositionTarget is not { } target) return;
        if (WindowState != WindowState.Normal) return;

        // Left/Top/ActualWidth/ActualHeight — DIP-единицы (96 DPI), Screen.WorkingArea —
        // физические пиксели; TransformToDevice — тот же пересчёт, которым WPF сам переводит
        // DIP в пиксели при отрисовке на текущем мониторе, поэтому клэмп корректен и на
        // мониторах с масштабированием, отличным от 100%.
        var transform = target.TransformToDevice;
        var topLeft = transform.Transform(new Point(Left, Top));
        var size = transform.Transform(new Point(ActualWidth, ActualHeight));

        int left = (int)Math.Round(topLeft.X);
        int top = (int)Math.Round(topLeft.Y);
        int width = (int)Math.Round(size.X);
        int height = (int)Math.Round(size.Y);

        var winBounds = new System.Drawing.Rectangle(left, top, width, height);
        var workArea = System.Windows.Forms.Screen.FromRectangle(winBounds).WorkingArea;

        int clampedLeft = Math.Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
        int clampedTop = Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));

        if (clampedLeft == left && clampedTop == top) return; // уже полностью на экране — трогать нечего

        var deviceToDip = transform;
        deviceToDip.Invert(); // Matrix — struct, копия; Invert() меняет её на месте, а не возвращает новую
        var newTopLeft = deviceToDip.Transform(new Point(clampedLeft, clampedTop));

        Left = newTopLeft.X;
        Top = newTopLeft.Y;
    }

    // Обработчик всех трёх пунктов контекстного меню вида плеера — какой именно вид
    // выбран, определяется по Tag пункта меню ("Square"/"Rectangular"/"Mini").
    private void ViewModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: string modeName }) return;
        if (Enum.TryParse<PlayerViewMode>(modeName, out var mode))
            SetPlayerViewMode(mode);
    }

    // Публичная обёртка над SetPlayerViewMode для окна настроек (PlayerViewMode — приватный
    // enum, наружу наружу торчать не должен) — тот же разбор строки "Square"/"Rectangular"/
    // "Mini", что и в ViewModeMenuItem_Click, только вызывается из SettingsWindow.
    public void SetPlayerViewModeByName(string modeName)
    {
        if (Enum.TryParse<PlayerViewMode>(modeName, out var mode))
            SetPlayerViewMode(mode);
    }

    // Текущий вид плеера строкой ("Square"/"Rectangular"/"Mini") — чтобы окно настроек могло
    // выставить нужную миниатюру выбранной при открытии, не имея доступа к самому enum.
    public string CurrentViewModeName => _viewMode.ToString();

    // Клик (левой кнопкой) по заголовку "Lumisense" в левом верхнем углу — открывает то же
    // самое контекстное меню, что показывается и по правому клику (ContextMenu на элементе
    // делает это автоматически, но левый клик нужно открыть вручную).
    private void TitleClickArea_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } element) return;
        menu.PlacementTarget = element;
        menu.IsOpen = true;
    }

    private void MainViewContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        ApplyMainViewContextMenuAccent();
        UpdateViewModeMenuChecks();

        // App.xaml локализует Popup в тот же момент. Повтор после ContextIdle гарантирует,
        // что новый локальный шаблон трёх MenuItem увидит окончательное IsChecked.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(UpdateViewModeMenuChecks));
    }

    // ContextMenu WPF открывается в собственном Popup-дереве и может не наследовать
    // application accent. Публикуем локальные ресурсы, чтобы Fluent CheckBox трёх пунктов
    // выбора вида брал реальный текущий цвет Lumisense.
    private void ApplyMainViewContextMenuAccent()
    {
        Color accent = GetResolvedAccentColor();
        MainViewContextMenu.Resources["SystemAccentColor"] = accent;
        MainViewContextMenu.Resources["AccentFillColorDefaultBrush"] = new SolidColorBrush(accent);
        MainViewContextMenu.Resources["AccentFillColorSecondaryBrush"] = new SolidColorBrush(accent);
    }

    private void UpdateViewModeMenuChecks()
    {
        SquareViewMenuItem.IsCheckable = true;
        RectangularViewMenuItem.IsCheckable = true;
        MiniViewMenuItem.IsCheckable = true;
        SquareViewMenuItem.IsChecked = _viewMode == PlayerViewMode.Square;
        RectangularViewMenuItem.IsChecked = _viewMode == PlayerViewMode.Rectangular;
        MiniViewMenuItem.IsChecked = _viewMode == PlayerViewMode.Mini;
    }

    // ---------- Добавление файлов и папок ----------

    // Drag & Drop файлов/папок из Проводника — тот же результат, что и кнопки "Добавить" выше:
    // папки становятся отдельными группами плейлиста (см. AddFolderPath), отдельные файлы —
    // собираются в общую группу "Отдельные файлы" (см. AddLooseFiles). Можно бросить и то, и
    // другое одним движением, вперемешку. DragEnter используется и для DragOver (см. XAML) —
    // WPF не запоминает e.Effects между вызовами, каждый DragOver должен выставлять его заново,
    // иначе курсор почти сразу покажет "нельзя" даже над принимаемым содержимым.
    private void MainWindow_DragEnter(object sender, System.Windows.DragEventArgs e)
    {
        bool hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        DragDropOverlay.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    // DragLeave срабатывает и при уходе курсора с окна совсем, и просто при переходе между
    // дочерними элементами внутри самого окна (тем не менее AllowDrop стоит только на корневом
    // ui:FluentWindow, а не на каком-то из его детей, так что здесь это равнозначно "курсор
    // покинул окно целиком") — прятать оверлей в обоих случаях правильно.
    private void MainWindow_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        DragDropOverlay.Visibility = Visibility.Collapsed;
    }

    private async void MainWindow_Drop(object sender, System.Windows.DragEventArgs e)
    {
        DragDropOverlay.Visibility = Visibility.Collapsed;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        var newFiles = new List<string>();
        bool foundAnyFolder = false;
        bool foundAnything = false;

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                foundAnyFolder = true;
                foundAnything = await AddFolderPathAsync(path) || foundAnything;
            }
            else if (File.Exists(path) && SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            {
                newFiles.Add(path);
                foundAnything = true;
            }
            // Прочие файлы (не аудио, не папка) — молча пропускаем: пользователь вполне мог
            // задеть при перетаскивании что-то лишнее вместе с музыкой, отдельно ругаться на
            // каждый такой файл не стоит, итоговое сообщение "ничего не найдено" ниже покрывает
            // только случай, когда В ИТОГЕ не добавилось вообще ничего.
        }

        if (_isExiting) return;
        if (newFiles.Count > 0)
            AddLooseFiles(newFiles);

        if (!foundAnything)
        {
            string message = foundAnyFolder
                ? "В перетащенных папках не найдено поддерживаемых аудиофайлов."
                : "Среди перетащенного не найдено ни поддерживаемых аудиофайлов, ни папок.";
            LocalizedMessageBox.Show(this, message,
                "Ничего не найдено", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
    }

    // Клик по объединённой кнопке "Добавить" открывает её собственное контекстное меню
    // (выбор "Файлы…" / "Папку…") прямо под кнопкой, как обычное выпадающее меню.
    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } button) return;

        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void AddFilesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Аудиофайлы (*.mp3;*.wav;*.wma;*.flac;*.m4a;*.aac;*.ogg)|*.mp3;*.wav;*.wma;*.flac;*.m4a;*.aac;*.ogg|Все файлы (*.*)|*.*",
            Multiselect = true,
            Title = "Выберите аудиофайлы"
        };

        if (dialog.ShowDialog() != true) return;

        AddLooseFiles(dialog.FileNames);
    }

    // ---------- Создание пустой ("временной") папки вручную — без привязки к диску ----------
    // Такую папку можно тут же начать наполнять файлами через кнопку в её заголовке
    // (см. AddFilesToFolderButton_Click) — удобно, например, чтобы собрать разовый плейлист
    // из файлов, разбросанных по разным местам, не трогая структуру папок на диске.
    private void CreateFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TextInputDialog("Новая папка", "Название папки:", settings: _settings) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var folder = new PlaylistFolder
        {
            SourcePath = null,
            DisplayName = dialog.ResultText,
            IsLooseFilesBucket = false
        };

        _folders.Add(folder);
        RefreshPlaylistView();
    }

    // Кнопка "Добавить файлы" в заголовке конкретной группы (видна только у "Отдельные
    // файлы" и у папок, созданных вручную, — см. PlaylistFolder.CanAddFilesDirectly) —
    // в отличие от общей кнопки "Добавить" в шапке плейлиста, добавляет файлы именно
    // в ту группу, на которой нажали, а не в общий список "Отдельные файлы".
    private void AddFilesToFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PlaylistFolder folder }) return;

        var dialog = new OpenFileDialog
        {
            Filter = "Аудиофайлы (*.mp3;*.wav;*.wma;*.flac;*.m4a;*.aac;*.ogg)|*.mp3;*.wav;*.wma;*.flac;*.m4a;*.aac;*.ogg|Все файлы (*.*)|*.*",
            Multiselect = true,
            Title = $"Добавить файлы в «{folder.DisplayName}»"
        };

        if (dialog.ShowDialog() != true) return;

        bool wasEmptyBeforeAdd = FlattenAll().Count == 0;

        var allExisting = FlattenAll();
        var actuallyNew = dialog.FileNames.Where(f => !allExisting.Contains(f)).ToList();
        if (actuallyNew.Count == 0) return;

        folder.Tracks.AddRange(actuallyNew);
        RefreshPlaylistView();

        if (wasEmptyBeforeAdd)
            LoadAndPlay(actuallyNew[0]);
    }

    // ---------- Автоматическое обновление дисковых папок плейлиста ----------

    private void StartFolderWatchers()
    {
        StopFolderWatchers();
        if (_isExiting || !_settings.AutoRefreshPlaylistFolders) return;

        foreach (string folderPath in _folders
                     .Select(folder => folder.SourcePath)
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Cast<string>()
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(folderPath)) continue;

                var watcher = new FileSystemWatcher(folderPath, "*.*")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                };
                watcher.Created += FolderWatcher_FileChanged;
                watcher.Renamed += FolderWatcher_FileChanged;
                watcher.Error += FolderWatcher_Error;
                watcher.EnableRaisingEvents = true;
                _folderWatchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Одна недоступная сетевая/удалённая папка не должна ломать слежение за остальными.
                Logger.Warn($"Не удалось включить автообновление папки {folderPath}: {ex.Message}");
            }
        }
    }

    private void StopFolderWatchers()
    {
        _folderRefreshDebounceTimer.Stop();
        _pendingFolderRefreshPaths.Clear();

        foreach (FileSystemWatcher watcher in _folderWatchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= FolderWatcher_FileChanged;
            watcher.Renamed -= FolderWatcher_FileChanged;
            watcher.Error -= FolderWatcher_Error;
            watcher.Dispose();
        }
        _folderWatchers.Clear();
    }

    private void FolderWatcher_FileChanged(object sender, FileSystemEventArgs e)
    {
        // Во время копирования большого файла событие может приходить несколько раз, а при
        // переносе целого каталога — только для него. В обоих случаях повторный скан корневой
        // папки после debounce найдёт все действительно готовые поддерживаемые файлы.
        bool isDirectory = Directory.Exists(e.FullPath);
        bool isSupportedAudio = SupportedExtensions.Contains(Path.GetExtension(e.FullPath), StringComparer.OrdinalIgnoreCase);
        if (!isDirectory && !isSupportedAudio) return;
        if (sender is not FileSystemWatcher watcher) return;

        QueueFolderRefresh(watcher.Path);
    }

    private void FolderWatcher_Error(object sender, ErrorEventArgs e)
    {
        if (sender is not FileSystemWatcher watcher) return;
        Logger.Warn($"Буфер отслеживания папки {watcher.Path} переполнен или недоступен: {e.GetException().Message}");
        QueueFolderRefresh(watcher.Path);
    }

    private void QueueAllFolderRefreshes()
    {
        if (!_settings.AutoRefreshPlaylistFolders) return;

        foreach (string folderPath in _folders
                     .Select(folder => folder.SourcePath)
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Cast<string>()
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // FileSystemWatcher не знает о событиях, произошедших до запуска приложения.
            // Один отложенный скан после восстановления закрывает этот случай и использует
            // ту же дедупликацию AddFolderPathAsync, что и уведомления в текущем сеансе.
            QueueFolderRefresh(folderPath);
        }
    }

    private void QueueFolderRefresh(string folderPath)
    {
        if (_isExiting || !_settings.AutoRefreshPlaylistFolders || !_playlistRestoreCompleted || Dispatcher.HasShutdownStarted)
            return;

        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (_isExiting || !_settings.AutoRefreshPlaylistFolders || !_playlistRestoreCompleted) return;
                _pendingFolderRefreshPaths.Add(folderPath);
                _folderRefreshDebounceTimer.Stop();
                _folderRefreshDebounceTimer.Start();
            }));
        }
        catch (InvalidOperationException)
        {
            // Dispatcher уже завершает работу приложения; очищать watcher будет OnClosed.
        }
    }

    private async void FolderRefreshDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _folderRefreshDebounceTimer.Stop();
        if (_isExiting || !_settings.AutoRefreshPlaylistFolders || _pendingFolderRefreshPaths.Count == 0)
            return;

        // Новые события, пришедшие пока Directory.EnumerateFiles работает в фоне, будут
        // обработаны следующим debounce-циклом, а не потеряны.
        if (_isFolderRefreshInProgress)
        {
            _folderRefreshDebounceTimer.Start();
            return;
        }

        string[] pathsToRefresh = _pendingFolderRefreshPaths.ToArray();
        _pendingFolderRefreshPaths.Clear();
        _isFolderRefreshInProgress = true;
        try
        {
            foreach (string folderPath in pathsToRefresh)
            {
                if (_isExiting || _lifetimeCts.IsCancellationRequested) break;
                if (!_folders.Any(folder => string.Equals(folder.SourcePath, folderPath, StringComparison.OrdinalIgnoreCase)))
                    continue;

                await AddFolderPathAsync(folderPath);
            }
        }
        catch (OperationCanceledException)
        {
            // Нормальный путь при выходе из приложения.
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось автоматически обновить папку плейлиста", ex);
        }
        finally
        {
            _isFolderRefreshInProgress = false;
            if (!_isExiting && _settings.AutoRefreshPlaylistFolders && _pendingFolderRefreshPaths.Count > 0)
                _folderRefreshDebounceTimer.Start();
        }
    }

    // ---------- Добавление папки (рекурсивно), в том числе нескольких сразу ----------

    private async void AddFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите папку с музыкой",
            Multiselect = true
        };

        if (dialog.ShowDialog() != true) return;

        // Каждая выбранная папка становится отдельной группой плейлиста, которую
        // потом можно независимо включать/выключать
        bool foundAnything = false;
        foreach (var folderPath in dialog.FolderNames)
            foundAnything = await AddFolderPathAsync(folderPath) || foundAnything;

        if (_isExiting) return;
        if (!foundAnything)
        {
            LocalizedMessageBox.Show(this, "В выбранной папке не найдено поддерживаемых аудиофайлов.",
                "Ничего не найдено", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
    }

    // Сканирует папку рекурсивно и добавляет её как отдельную группу плейлиста.
    // Возвращает false, если в ней не нашлось ни одного поддерживаемого аудиофайла
    // (например, нет доступа или папка пустая) — используется, чтобы решить, показывать
    // ли предупреждение "ничего не найдено".
    private async Task<bool> AddFolderPathAsync(string folderPath)
    {
        try
        {
            var filesInFolder = await Task.Run(() => Directory.EnumerateFiles(folderPath, "*.*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false
                })
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList(), _lifetimeCts.Token);

            if (filesInFolder.Count == 0) return false;
            AddFolderGroup(folderPath, filesInFolder);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (PathTooLongException ex)
        {
            Logger.Warn($"Слишком длинный путь при сканировании {folderPath}: {ex.Message}");
            return false;
        }
        catch (IOException ex)
        {
            Logger.Warn($"Не удалось просканировать папку {folderPath}: {ex.Message}");
            return false;
        }
    }

    // Добавляет папку как отдельную группу плейлиста. Если такая папка (по пути) уже есть
    // в плейлисте — просто добавляет в неё новые файлы, которых там ещё не было, вместо
    // создания дубликата группы.
    private void AddFolderGroup(string folderPath, List<string> filesInFolder)
    {
        bool wasEmptyBeforeAdd = FlattenAll().Count == 0;

        var existingFolder = _folders.FirstOrDefault(f =>
            f.SourcePath != null && string.Equals(f.SourcePath, folderPath, StringComparison.OrdinalIgnoreCase));

        string? firstNewTrack = null;
        bool createdFolder = false;

        if (existingFolder != null)
        {
            var newOnes = filesInFolder.Where(f => !existingFolder.Tracks.Contains(f)).ToList();
            if (newOnes.Count == 0) return;

            firstNewTrack = newOnes[0];
            existingFolder.Tracks.AddRange(newOnes);
        }
        else
        {
            string displayName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(displayName)) displayName = folderPath;

            var folder = new PlaylistFolder
            {
                SourcePath = folderPath,
                DisplayName = displayName
            };

            folder.Tracks.AddRange(filesInFolder);
            firstNewTrack = filesInFolder[0];
            _folders.Add(folder);
            createdFolder = true;
        }

        RefreshPlaylistView();
        if (createdFolder)
            StartFolderWatchers();

        // Если до этого ничего не играло — сразу запускаем первый добавленный трек
        if (wasEmptyBeforeAdd && firstNewTrack != null)
        {
            LoadAndPlay(firstNewTrack);
        }
    }

    // Отдельно выбранные файлы (не через папку) собираются в одну общую группу "Отдельные файлы"
    private void AddLooseFiles(IEnumerable<string> filePaths)
    {
        var newTracks = filePaths.ToList();
        if (newTracks.Count == 0) return;

        bool wasEmptyBeforeAdd = FlattenAll().Count == 0;

        var looseFolder = _folders.FirstOrDefault(f => f.IsLooseFilesBucket);
        if (looseFolder == null)
        {
            looseFolder = new PlaylistFolder
            {
                SourcePath = null,
                DisplayName = LocalizationService.Get(LocalizationKey.PlaylistLooseFiles),
                IsLooseFilesBucket = true
            };
            _folders.Add(looseFolder);
        }

        var allExisting = FlattenAll();
        var actuallyNew = newTracks.Where(f => !allExisting.Contains(f)).ToList();
        if (actuallyNew.Count == 0) return;

        looseFolder.Tracks.AddRange(actuallyNew);
        RefreshPlaylistView();

        if (wasEmptyBeforeAdd)
        {
            LoadAndPlay(actuallyNew[0]);
        }
    }

    // Нормализация имён запускается только вручную — из настроек для всех добавленных файлов
    // или из контекстного меню для одного выбранного трека. В обоих случаях сначала строится
    // предпросмотр по тегам, затем пользователь видит примеры и явно подтверждает изменение.
    // Текущий трек исключается, потому что его дескриптор может быть открыт AudioFileReader и
    // Windows не позволит безопасно переместить файл.
    public Task<FileNameNormalizer.RenameResult?> NormalizePlaylistFileNamesAsync(System.Windows.Window dialogOwner) =>
        NormalizeTrackFileNamesAsync(_folders.SelectMany(folder => folder.Tracks), dialogOwner);

    public bool IsTrackContextMenuActionDisabled(string actionId) =>
        TrackContextMenuActions.Instance.IsDisabled(actionId);

    // Изменение тут же поднимает Epoch у TrackContextMenuActions.Instance, поэтому даже
    // созданные строки плейлиста пересчитывают Visibility без пересборки всего списка.
    public void SetTrackContextMenuActionDisabled(string actionId, bool disabled)
    {
        TrackContextMenuActions.Instance.SetDisabled(actionId, disabled);
        _settings.DisabledTrackContextMenuActions = TrackContextMenuActions.Instance.GetDisabledActionIds();
    }

    private async Task<FileNameNormalizer.RenameResult?> NormalizeTrackFileNamesAsync(
        IEnumerable<string> requestedPaths, System.Windows.Window dialogOwner)
    {
        if (_isExiting) return null;

        var sourcePaths = requestedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sourcePaths.Count == 0)
        {
            LocalizedMessageBox.Show(dialogOwner, "Нет доступных файлов для нормализации.",
                "Нормализация имён", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return null;
        }

        string? currentPath = _currentTrackPath;
        IReadOnlyList<FileNameNormalizer.RenamePreview> preview;
        try
        {
            preview = await Task.Run(() => FileNameNormalizer.BuildPreview(
                sourcePaths,
                _settings.FileNameNormalizationTemplate,
                string.IsNullOrWhiteSpace(currentPath) ? null : new[] { currentPath }), _lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        if (_isExiting) return null;

        var candidates = preview.Where(item => item.CanRename).ToList();
        if (candidates.Count == 0)
        {
            string reason = preview.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.SkipReason))?.SkipReason
                            ?? "нет файлов, подходящих для переименования";
            string message = sourcePaths.Count == 1 && reason == "уже соответствует шаблону"
                ? "Имя файла уже соответствует выбранному шаблону. Переименование не требуется."
                : $"Ни один файл не будет переименован: {reason}.";
            LocalizedMessageBox.Show(dialogOwner, message,
                "Нормализация имён", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);

            // Выбранный текущий трек мог не требовать File.Move, но всё равно нуждается в
            // обновлённом fallback исполнителя/названия из имени файла.
            if (_currentTrackPath is not null && sourcePaths.Any(path =>
                    string.Equals(path, _currentTrackPath, StringComparison.OrdinalIgnoreCase)))
            {
                RefreshCurrentTrackMetadataFromFileName();
            }

            return new FileNameNormalizer.RenameResult(0, preview.Count, 0,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), Array.Empty<string>());
        }

        string examples = string.Join(Environment.NewLine, candidates.Take(5).Select(item =>
            $"• {item.SourceFileName} → {item.TargetFileName}"));
        int skipped = preview.Count - candidates.Count;
        string skippedText = skipped > 0 ? $"\n\nПропущено: {skipped} (уже соответствует шаблону, конфликтует или играет сейчас)." : string.Empty;

        var confirmation = LocalizedMessageBox.Show(dialogOwner,
            $"Переименовать файлов: {candidates.Count}.\n\n{examples}{skippedText}\n\n" +
            "Файлы останутся в исходных папках; изменятся только имена. Продолжить?",
            "Нормализация имён", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirmation != System.Windows.MessageBoxResult.Yes) return null;

        FileNameNormalizer.RenameResult result;
        try
        {
            result = await Task.Run(() => FileNameNormalizer.Execute(preview), _lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        if (_isExiting) return null;

        if (result.RenamedCount > 0)
        {
            ApplyNormalizedTrackPaths(result.RenamedPaths);
            PersistPlaybackAndPlaylistState();
        }

        // Текущий трек намеренно исключается из File.Move, но его подписи не должны оставаться
        // с устаревшим fallback вида «имя папки». Если путь был в запросе, пересчитываем UI из
        // тех же тегов и имени файла даже при результате «уже соответствует шаблону».
        if (_currentTrackPath is not null && sourcePaths.Any(path =>
                string.Equals(path, _currentTrackPath, StringComparison.OrdinalIgnoreCase)))
        {
            RefreshCurrentTrackMetadataFromFileName();
        }

        return result;
    }

    private void RefreshCurrentTrackMetadataFromFileName()
    {
        if (string.IsNullOrWhiteSpace(_currentTrackPath)) return;

        var metadata = FileNameNormalizer.ResolveArtistAndTitle(
            _currentTrackPath, _currentTrackTaggedArtist, _currentTrackTaggedTitle, "—");
        SetTrackInfoText(metadata.Title, metadata.Artist);
        _nowPlaying?.UpdateTrackInfo(metadata.Title, metadata.Artist);
        RaiseTrackInfoChanged(metadata.Title, metadata.Artist, CurrentArtBrush);
    }

    private void ApplyNormalizedTrackPaths(IReadOnlyDictionary<string, string> renamedPaths)
    {
        if (renamedPaths.Count == 0) return;

        string Remap(string path) => renamedPaths.TryGetValue(path, out string? renamed) ? renamed : path;

        foreach (var folder in _folders)
        {
            for (int index = 0; index < folder.Tracks.Count; index++)
                folder.Tracks[index] = Remap(folder.Tracks[index]);
        }

        // В нормальном сценарии текущий трек исключён из плана, но обновление оставляем как
        // защиту от будущих способов запуска нормализации или от момента между переключениями.
        if (_currentTrackPath != null)
            _currentTrackPath = Remap(_currentTrackPath);
        if (_settings.LastTrackPath != null)
            _settings.LastTrackPath = Remap(_settings.LastTrackPath);

        var favoriteOrder = FavoritesManager.GetOrder().Select(Remap).ToList();
        var pinnedFavorites = FavoritesManager.GetPinnedPaths().Select(Remap).ToList();
        FavoritesManager.Initialize(favoriteOrder, pinnedFavorites);
        FavoritesChangeNotifier.Instance.Bump();

        var remappedCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, count) in PlayCountManager.GetAll())
        {
            string remapped = Remap(path);
            remappedCounts[remapped] = remappedCounts.TryGetValue(remapped, out int existing)
                ? existing + count
                : count;
        }
        PlayCountManager.Initialize(remappedCounts);
        PlayCountChangeNotifier.Instance.Bump();

        // Очередь и история шаффла содержат абсолютные пути. Перезапускаем их вместо частичного
        // исправления, чтобы кнопки «следующий» и «предыдущий» никогда не ссылались на старое имя.
        ResetShuffleState();
        RefreshPlaylistView();
        if (_isFavoritesView) RefreshFavoritesTrackList();
    }

    // Ручное обновление остаётся доступным как запасной вариант для сетевых папок и файловых
    // систем, которые не посылают события FileSystemWatcher. Как и автообновление, оно
    // переиспользует AddFolderPathAsync и добавляет только отсутствующие файлы.
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

        // Просто убираем группу из плейлиста. Если сейчас играет трек именно из неё —
        // не трогаем воспроизведение: пусть доигрывает, он уже загружен в память и
        // никак не зависит от списка. При следующем "Далее/Назад" плеер перейдёт
        // к первому доступному активному треку, раз текущего уже нет в списке.
        _folders.Remove(folder);
        RefreshPlaylistView();
        StartFolderWatchers();
    }

    // В отличие от удаления одной группы (см. выше), очистка плейлиста целиком не оставляет
    // вообще ничего, на что можно было бы переключиться дальше — поэтому, если что-то играло,
    // останавливаем воспроизведение и возвращаем плеер к пустому состоянию ("Файл не выбран"),
    // а не оставляем текущий трек тихо доигрывать сам по себе.
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

    // Полный пересбор списка при каждом изменении _folders — дёшево благодаря виртуализации
    // (PlaylistFoldersControl.ItemsSource — плоский список PlaylistFolder/PlaylistTrackRow,
    // см. PlaylistDisplaySelectors.cs), реассайн не создаёт контейнеры для всех элементов, а
    // только для видимых. Свёрнутые папки не кладут строки треков в список вовсе.
    private void RefreshPlaylistView()
    {
        _allTracksCache = null;
        _activeTracksCache = null;

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

    // Обычный плейлист — смешанный список заголовков папок и строк. При поиске оставляем
    // заголовок только у папки, в которой есть совпадения; «Избранное» содержит только строки
    // и проходит тем же методом без лишней обёртки. Функция работает над неизменяемым снимком
    // и вызывается в фоне, поэтому обращений к WPF или к диску здесь нет.
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

    // ---------- Избранное ----------

    private void FavoritesButton_Click(object sender, RoutedEventArgs e) => SetFavoritesViewActive(!_isFavoritesView);

    // Оба списка лежат в разметке друг на друге, переключается только Visibility —
    // PlaylistFoldersControl не перепривязывается и не пересоздаёт контейнеры.
    // "Добавить"/"Очистить" скрыты в режиме избранного — в виртуальную группу нельзя добавлять
    // файлы напрямую, а "очищать" там нечего.
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

    // Пересобирает СОДЕРЖИМОЕ только виртуального плейлиста "Избранное" — не трогая
    // PlaylistFoldersControl вообще. Стоимость пропорциональна числу избранных треков, а не
    // размеру всей библиотеки, поэтому вызывать его можно гораздо чаще, чем раньше можно было
    // позволить себе полный RefreshPlaylistView().
    private void RefreshFavoritesTrackList()
    {
        var favorites = FavoritesManager.GetAll();

        _favoritesFolder.Tracks.Clear();
        _favoritesFolder.Tracks.AddRange(favorites);

        // Тот же PlaylistTrackRow, что и у обычного плейлиста (см. TrackItemTemplate в
        // MainWindow.xaml — общий шаблон для обоих списков) — Folder указывает на
        // _favoritesFolder для всех строк, группировки тут нет, просто плоский список без
        // заголовков.
        var items = new List<object>(favorites.Count);
        for (int i = 0; i < favorites.Count; i++)
            items.Add(new PlaylistTrackRow { Folder = _favoritesFolder, FilePath = favorites[i], IndexInFolder = i + 1 });

        _favoriteDisplayItems = items;
        if (_isFavoritesView)
            QueuePlaylistSearch();
    }

    // Сердечко на строке трека (см. TrackFavoriteButton в MainWindow.xaml) и одноимённый пункт
    // контекстного меню приводят сюда же — оба просто переключают избранное для того же трека,
    // единственная разница в том, откуда берётся путь к файлу (DataContext кнопки против
    // DataContext пункта меню — в обоих случаях это унаследованный DataContext строки, то есть
    // PlaylistTrackRow, см. подробный комментарий у TrackItemTemplate в MainWindow.xaml).
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

    // Переключает избранное и обновляет UI минимально: сердечки во всех показанных строках
    // перерисовываются сами через FavoritesChangeNotifier (см. DataTrigger в TrackItemTemplate,
    // MainWindow.xaml). Вручную пересобирается только список виртуального плейлиста
    // "Избранное" — и то лишь пока он открыт, чтобы трек тут же исчез при снятии сердечка.
    private void ToggleFavoriteAndRefresh(string filePath)
    {
        FavoritesManager.Toggle(filePath);

        if (_isFavoritesView)
            RefreshFavoritesTrackList();
    }

    // Закрепление трека наверху "Избранного" (см. FavoritesManager.TogglePin) — кнопка и пункт
    // меню видны только в самом "Избранном" (Folder.IsFavoritesGroup, см. привязку Visibility в
    // MainWindow.xaml), закреплять что-либо в обычном плейлисте нельзя и незачем: смысл
    // закрепления — порядок показа именно на странице "Избранное".
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

    // Тот же принцип минимального обновления UI, что и у ToggleFavoriteAndRefresh выше —
    // FavoritesChangeNotifier сам поднимает перерисовку иконки закрепления (см.
    // IsPinnedMultiConverter/TrackPinIcon в MainWindow.xaml), а вот сам ПОРЯДОК строк
    // "Избранного" от закрепления меняется, поэтому список нужно пересобрать явно — так же,
    // как и при добавлении/удалении из избранного.
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

        // Раньше сворачивание работало через обычный биндинг Visibility вложенного ListView
        // на IsExpanded. Теперь треки папки — отдельные элементы плоского списка (PlaylistTrackRow),
        // а не содержимое своего вложенного контрола — нужно физически добавить/убрать их
        // из ItemsSource, отсюда явный пересбор.
        RefreshPlaylistView();
    }

    // PlaylistFoldersControl/FavoritesTrackListView — самостоятельные ListView со своим
    // скроллом (VerticalScrollBarVisibility="Hidden"); "общий скролл" визуально сохраняется тем,
    // что оба показывают один и тот же кастомный PlaylistScrollTrack/Thumb, подключённый к
    // ScrollViewer текущего видимого списка. ScrollViewer не именован в XAML — достаём и кэшируем
    // через обход визуального дерева.
    private System.Windows.Controls.ScrollViewer? _playlistFoldersScrollViewer;
    private System.Windows.Controls.ScrollViewer? _favoritesScrollViewer;

    private System.Windows.Controls.ScrollViewer? GetActivePlaylistScrollViewer()
        => _isFavoritesView
            ? _favoritesScrollViewer ??= FindVisualChild<System.Windows.Controls.ScrollViewer>(FavoritesTrackListView)
            : _playlistFoldersScrollViewer ??= FindVisualChild<System.Windows.Controls.ScrollViewer>(PlaylistFoldersControl);

    // PreviewMouseWheel идёт по дереву раньше bubbling MouseWheel, на которое реагирует
    // встроенный скролл — e.Handled не даёт этому более резкому (~3 строки за деление) скроллу
    // сработать вдобавок к нашему.
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

    // ---------- Свой скроллбар плейлиста (с нуля, без ScrollBar/Track) ----------
    // PlaylistScrollTrack (дорожка) и PlaylistScrollThumb (ползунок) из XAML:
    //  - PlaylistScrollViewer_ScrollChanged/PlaylistScrollTrack_SizeChanged пересчитывают
    //    высоту и позицию ползунка при изменении контента/офсета/размера;
    //  - клик по дорожке мимо ползунка прыгает туда, куда кликнули;
    //  - перетаскивание ползунка двигает прокрутку один в один за мышью (ручной MouseCapture).
    private bool _isDraggingPlaylistThumb;
    private double _playlistThumbDragStartMouseY;
    private double _playlistThumbDragStartOffset;

    private void PlaylistScrollViewer_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        UpdatePlaylistScrollThumb();
    }

    // ScrollViewer обрезает содержимое прямоугольно по своим границам — незаметно, пока список
    // не прокручен, но при прокрутке карточки у края становятся видны с чётким прямоугольным
    // обрезом, спорящим со скруглённой рамкой PlaylistBorder вокруг. Свой Clip со скруглением
    // решает это. Радиус 8 — как у самих карточек, а не как у внешней рамки (10), иначе
    // скругление не концентрично и режет по углам. Общий обработчик на оба ListView — клипует
    // sender, а не именованный элемент.
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

    // ---------- Удаление выбранных треков клавишей Delete + отмена (Ctrl+Z) ----------
    // SelectionMode="Extended" у обоих ListView — можно выделять несколько строк (Ctrl+клик,
    // Shift+клик, Ctrl+A), при этом "текущий играющий трек" как единственный SelectedItem не
    // страдает: LoadAndPlay просто переприсваивает SelectedItem, WPF сам снимает выделение с
    // остальных.
    //
    // Стек отмены — только для удаления треков, не общий undo-фреймворк. Каждый Delete кладёт
    // одно замыкание, полностью восстанавливающее удалённое; Ctrl+Z снимает и выполняет верхнее.
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

    // Убирает переданные строки из плейлиста пачкой (тот же смысл, что и "Убрать из плейлиста"
    // в контекстном меню, см. RemoveTrackMenuItem_Click) и кладёт в _playlistDeleteUndoStack
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

    // Ctrl+Z — на уровне всего окна, не только списков плейлиста, т.к. фокус между удалением
    // и Ctrl+Z мог уйти куда угодно. Пропускаем, если фокус в текстовом поле — там Ctrl+Z должен
    // работать как отмена ввода текста.
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

    // ---------- Контекстное меню трека (правый клик по строке в плейлисте) ----------
    // DataContext каждого пункта меню унаследован от ContextMenu.PlacementTarget по логическому
    // дереву (WPF пробрасывает его и через Popup) — это сам PlaylistTrackRow (row.FilePath и
    // row.Folder приходят вместе, без Tag/CommandParameter-трюков).

    private void PlayTrackMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: PlaylistTrackRow row }) return;
        LoadAndPlay(row.FilePath);
    }

    private void ShowInExplorerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: PlaylistTrackRow row }) return;
        if (!File.Exists(row.FilePath)) return;

        // /select, выделяет сам файл в открывшемся окне проводника, а не просто открывает папку
        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{row.FilePath}\"");
    }

    private void CopyTrackNameMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: PlaylistTrackRow row }) return;

        // То же самое имя, что видно строкой в плейлисте — просто имя файла без расширения и
        // пути (см. FileNameConverter), а не название/исполнитель из тегов: плейлист их не
        // показывает, так что и тут копируем ровно то, что человек видит на экране.
        System.Windows.Clipboard.SetText(Path.GetFileNameWithoutExtension(row.FilePath));
    }

    private void CopyPathMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: PlaylistTrackRow row }) return;
        System.Windows.Clipboard.SetText(row.FilePath);
    }

    private void CopyFileMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: PlaylistTrackRow row }) return;
        if (!File.Exists(row.FilePath)) return;

        // Кладём в буфер обмена сам файл (а не просто его путь текстом), чтобы можно было
        // вставить (Ctrl+V) прямо в проводник или другую папку — как при обычном Ctrl+C по файлу.
        var files = new System.Collections.Specialized.StringCollection();
        files.Add(row.FilePath);
        System.Windows.Clipboard.SetFileDropList(files);
    }

    // Раньше здесь был системный shell-диалог "Свойства" через ShellExecute — но для многих
    // типов аудиофайлов Windows не регистрирует обработчик этого verb-а, и вызов молча ничего
    // не делал. Вместо системного — своё окно в стиле плеера (TrackPropertiesWindow),
    // не зависящее от того, что зарегистрировано в реестре у конкретного пользователя.
    private void TrackPropertiesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: PlaylistTrackRow row }) return;
        if (!File.Exists(row.FilePath)) return;

        new TrackPropertiesWindow(row.FilePath, _settings) { Owner = this }.ShowDialog();
    }

    // Отдельное окно редактирования тегов (название/исполнитель/альбом/год/трек/жанр/
    // комментарий) — пишет прямо в файл через TagLib#. Если отредактированный файл — это
    // как раз сейчас играющий трек, обновляем название/исполнителя/обложку в самом плеере
    // сразу же, не дожидаясь следующего переключения трека.
    private void EditTagsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: PlaylistTrackRow row }) return;
        string filePath = row.FilePath;
        if (!File.Exists(filePath)) return;

        var tagsWindow = new TrackTagsWindow(filePath, this) { Owner = this };
        tagsWindow.ShowDialog();

        if (tagsWindow.Saved && filePath == _currentTrackPath)
        {
            LoadAlbumArt(filePath);
            _nowPlaying?.UpdateTrackInfo(TrackTitleText.Text, TrackArtistText.Text);
            RaiseTrackInfoChanged(TrackTitleText.Text, TrackArtistText.Text, CurrentArtBrush);
        }
    }

    private async void NormalizeTrackFileNameMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: PlaylistTrackRow row }) return;

        try
        {
            FileNameNormalizer.RenameResult? result = await NormalizeTrackFileNamesAsync(new[] { row.FilePath }, this);
            if (result is null || result.RenamedCount == 0) return;

            string errors = result.Errors.Count > 0
                ? $"\n\nОшибок: {result.Errors.Count}. {string.Join(" ", result.Errors.Take(2))}"
                : string.Empty;
            LocalizedMessageBox.Show(this,
                $"Имя файла нормализовано. Переименовано: {result.RenamedCount}; пропущено: {result.SkippedCount}.{errors}",
                "Нормализация имён", System.Windows.MessageBoxButton.OK,
                result.Errors.Count == 0 ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            LocalizedMessageBox.Show(this, $"Не удалось нормализовать имя файла:\n{ex.Message}",
                "Нормализация имён", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void RemoveTrackMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: PlaylistTrackRow row }) return;

        // В виртуальной группе "Избранное" своего списка треков по сути нет — она каждый раз
        // пересобирается из FavoritesManager (см. RefreshFavoritesTrackList), поэтому "убрать
        // из плейлиста" здесь означает "снять сердечко", а не удаление из folder.Tracks — иначе
        // трек тут же вернулся бы в список при следующем обновлении.
        if (row.Folder.IsFavoritesGroup)
        {
            FavoritesManager.SetFavorite(row.FilePath, false);
            if (_isFavoritesView) RefreshFavoritesTrackList();
            return;
        }

        // Если убираемый трек сейчас играет — не прерываем воспроизведение (он уже
        // загружен в память и от списка не зависит), просто убираем строку из плейлиста.
        row.Folder.Tracks.Remove(row.FilePath);
        RefreshPlaylistView();
    }

    // Безвозвратно удаляет файл трека с диска (не просто убирает из плейлиста). В отличие от
    // "Убрать из плейлиста", это затрагивает реальный файл — поэтому сначала спрашиваем
    // подтверждение и удаляем через корзину (Microsoft.VisualBasic.FileIO), а не File.Delete,
    // чтобы у пользователя оставался шанс восстановить файл в случае ошибки.
    private void DeleteTrackFromDiskMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: PlaylistTrackRow row }) return;

        DeleteTrackFromDisk(row.FilePath);
    }

    // Хоткей "Удалить трек с диска" (см. AppSettings.HotkeyDeleteTrack и GlobalMediaHotKeys) —
    // без выключенной по умолчанию комбинации; пользователь должен сам назначить её в
    // настройках. Удаляет ИМЕННО текущий загруженный/играющий трек тем же путём, что и
    // одноимённый пункт контекстного меню плейлиста (см. DeleteTrackFromDiskMenuItem_Click) —
    // с тем же подтверждением и той же отправкой в корзину, просто без необходимости сначала
    // искать трек в списке и кликать по нему правой кнопкой.
    private void DeleteCurrentTrackFromDiskHotkey()
    {
        if (_currentTrackPath == null) return;
        DeleteTrackFromDisk(_currentTrackPath);
    }

    private void DeleteTrackFromDisk(string filePath)
    {
        var trackName = Path.GetFileName(filePath);

        // Владелец диалога — то из окон приложения, что сейчас реально видно на экране.
        // Обычно это само MainWindow, но в мини-режиме оно спрятано (см. Hide() в
        // ShowMiniPlayer) — диалог, привязанный к невидимому окну, не может нормально выйти
        // на передний план сам по себе, каким бы owner'ом он ни был. Берём мини-плеер в
        // качестве владельца в этом случае — это и есть то, что сейчас видно.
        Window ownerWindow = _isMiniMode && _miniPlayerWindow != null ? _miniPlayerWindow : this;

        // Хоткей удаления — глобальный, срабатывает независимо от того, какое окно активно,
        // так что плеер в этот момент почти наверняка не в фокусе, и обычный MessageBox.Show
        // оказался бы под активным окном другого приложения (Windows блокирует "кражу" фокуса
        // фоновыми процессами). Тот же приём (Topmost-моргание), что и при разворачивании
        // мини-плеера, чинит и это; для контекстного меню — просто безвредный no-op.
        ForceForeground(ownerWindow);

        var confirm = LocalizedMessageBox.Show(
            ownerWindow,
            $"Удалить файл «{trackName}» с диска?\n\nФайл будет перемещён в корзину, а трек — убран из всех плейлистов.",
            "Удаление трека с диска",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.Yes);

        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        bool isCurrentlyLoaded = filePath == _currentTrackPath && _audioFile != null;
        string? nextPath = null;
        bool wasPlaying = false;
        TimeSpan previousPosition = TimeSpan.Zero;

        if (isCurrentlyLoaded)
        {
            previousPosition = _audioFile!.CurrentTime;
            wasPlaying = _isPlaying;

            // Считаем "следующий трек в очереди" ДО того, как уберём удаляемый из плейлиста
            // ниже — иначе ComputeNextTrackPath отсчитывал бы позицию уже без него и с этого
            // же места начал бы играть что-то не то (или сначала списка). Если следующий
            // трек — это тот же самый файл (он был единственным в очереди), играть больше
            // нечего.
            nextPath = ComputeNextTrackPath(_currentTrackPath);
            if (nextPath == filePath) nextPath = null;

            // Файл у играющего трека открыт NAudio-потоком, поэтому удаление ниже упадёт с
            // "файл занят другим процессом", пока мы явно не остановим воспроизведение и не
            // освободим хендл.
            StopPlayback();
        }

        try
        {
            if (File.Exists(filePath))
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    filePath,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
        }
        catch (Exception ex)
        {
            // StopPlayback был нужен для освобождения file handle, но при ошибке удаления
            // пользовательский трек всё ещё существует. Возвращаем его на прежнюю позицию,
            // чтобы не превращать временную ошибку корзины/прав доступа в потерю воспроизведения.
            if (isCurrentlyLoaded && File.Exists(filePath))
                LoadAndPlay(filePath, autoPlay: wasPlaying, startPosition: previousPosition,
                    changeOrigin: TrackChangeOrigin.ExternalEdit);

            LocalizedMessageBox.Show(ownerWindow, $"Не удалось удалить файл:\n{filePath}\n\n{ex.Message}",
                "Ошибка удаления", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return;
        }

        // Файла больше нет — убираем эту дорожку из ВСЕХ плейлистов, где она встречается,
        // а не только из того, где был вызван правый клик (иначе в других группах осталась
        // бы "битая" ссылка на несуществующий файл). Избранное — туда же, по той же причине.
        foreach (var folder in _folders)
            folder.Tracks.RemoveAll(t => t == filePath);
        FavoritesManager.SetFavorite(filePath, false);

        // Действие редкое (явное подтверждённое удаление файла с диска, не частый клик по
        // сердечку) — полный пересбор обоих списков здесь не проблема с точки зрения
        // производительности, а вот забыть обновить один из них было бы багом.
        RefreshPlaylistView();
        if (_isFavoritesView) RefreshFavoritesTrackList();

        // Играл именно удалённый трек и в очереди был кто-то ещё — переключаемся дальше,
        // сохраняя состояние "играло/было на паузе", а не просто останавливаемся на месте
        // удалённого трека.
        if (nextPath != null)
            LoadAndPlay(nextPath, autoPlay: wasPlaying, changeOrigin: TrackChangeOrigin.Automatic);
    }

    // ---------- Загрузка и воспроизведение ----------

    private async void LoadAndPlay(string filePath, bool autoPlay = true, TimeSpan? startPosition = null,
        AlbumArtTransitionDirection albumArtDirection = AlbumArtTransitionDirection.Next,
        TrackChangeOrigin changeOrigin = TrackChangeOrigin.User)
    {
        var previousLoad = Interlocked.Exchange(ref _trackLoadCts, null);
        previousLoad?.Cancel();
        var previousGain = Interlocked.Exchange(ref _replayGainCts, null);
        previousGain?.Cancel();

        int generation = Interlocked.Increment(ref _trackLoadGeneration);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _trackLoadCts = cts;
        PreparedTrack? prepared = null;
        var performance = new TrackLoadPerformanceMeasurement();

        try
        {
            SetTrackUserState(TrackUserState.Loading);
            await FadeOutBeforeTrackChangeAsync(cts.Token);
            performance.MarkStage("fade-out");
            if (generation != Volatile.Read(ref _trackLoadGeneration) || _isExiting)
                return;

            double volumeSliderValue = VolumeSlider.Value;
            bool replayGainEnabled = _settings.ReplayGainEnabled;
            bool equalizerEnabled = _settings.EqualizerEnabled;
                    double[] equalizerGains = (double[])_settings.EqualizerBandGainsDb.Clone();
        double playbackSpeed = _runtimePlaybackRate;
        double playbackPitch = Math.Clamp(_settings.PlaybackPitchSemitones, -12.0, 12.0);
        prepared = await PrepareTrackAsync(filePath, volumeSliderValue, replayGainEnabled,
            equalizerEnabled, equalizerGains, playbackSpeed, playbackPitch, cts.Token);
            performance.MarkStage("prepare-audio-and-metadata");

            cts.Token.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _trackLoadGeneration) || _isExiting)
                return;

            _audioFile = prepared.AudioFile;
            _tempoStream = prepared.TempoStream;
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
            _halfPlayCounted = false;
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
            _nowPlaying?.UpdateTrackInfo(TrackTitleText.Text, TrackArtistText.Text);
            RaiseTrackInfoChanged(TrackTitleText.Text, TrackArtistText.Text, CurrentArtBrush);
            RaiseProgressChanged(position.TotalSeconds, _audioFile.TotalTime.TotalSeconds);

            // Панель текста не выполняет работу в фоне, пока скрыта. Если пользователь уже
            // открыл её, новая композиция сразу отменяет предыдущий запрос и загружает свой
            // LRC/TXT/кэш или точное онлайн-совпадение.
            if (_isLyricsPanelActive)
                FireAndForget(LoadMainWindowLyricsAsync(filePath), "LoadMainWindowLyricsAsync");

            _audioLevelMeter = new AudioLevelSampleProvider(_equalizer!);
            var fadeIn = new FadeInOutSampleProvider(_audioLevelMeter, initiallySilent: true);
            fadeIn.BeginFadeIn(70);
            _activeFade = fadeIn;
            InitializeOutputDevice(fadeIn);
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
                _nowPlaying?.SetPlaybackStatus(Windows.Media.MediaPlaybackStatus.Playing);
                RaisePlaybackStateChanged(true);
            }
            else
            {
                _isPlaying = false;
                PlayPauseButton.Icon = IconResources.MakeOnAccent("IconPlay", 15);
                _nowPlaying?.SetPlaybackStatus(Windows.Media.MediaPlaybackStatus.Paused);
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
            _outputDevice?.Dispose();
            _outputDevice = null;
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
            if (ReferenceEquals(_trackLoadCts, cts)) _trackLoadCts = null;
            cts.Dispose();
        }
    }

    private async Task<PreparedTrack> PrepareTrackAsync(string filePath, double volumeSliderValue,
        bool replayGainEnabled, bool equalizerEnabled, double[] equalizerGains, double playbackSpeed,
        double playbackPitch, CancellationToken token)
    {
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            double replayGain = replayGainEnabled ? ReplayGainReader.GetTrackGainLinear(filePath) : 1.0;
            token.ThrowIfCancellationRequested();

            string? title = null;
            string? artist = null;
            BitmapImage? albumArt = null;
            byte[]? albumArtBytes = null;
            string? albumArtMimeType = null;
            TagLib.PictureType? albumArtPictureType = null;

            try
            {
                using var tagFile = TagLib.File.Create(filePath);
                title = tagFile.Tag.Title;
                artist = !string.IsNullOrWhiteSpace(tagFile.Tag.FirstPerformer)
                    ? tagFile.Tag.FirstPerformer
                    : tagFile.Tag.FirstAlbumArtist;
                if (tagFile.Tag.Pictures.Length > 0)
                {
                    var picture = tagFile.Tag.Pictures[0];
                    albumArtBytes = picture.Data.Data;
                    using var stream = new MemoryStream(albumArtBytes);
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    albumArt = bitmap;
                    albumArtMimeType = string.IsNullOrWhiteSpace(picture.MimeType) ? "image/jpeg" : picture.MimeType;
                    albumArtPictureType = picture.Type;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Не удалось прочитать metadata или embedded cover для файла {filePath}: {ex.Message}");
            }

            token.ThrowIfCancellationRequested();
            var reader = new AudioFileReader(filePath)
            {
                Volume = ComputeAudioFileVolume(volumeSliderValue, replayGain)
            };
            try
            {
                var tempoStream = new SoundTouchWaveStream(reader)
                {
                    Tempo = Math.Clamp(playbackSpeed, 0.5, 2.0),
                    PitchSemiTones = Math.Clamp(playbackPitch, -12.0, 12.0)
                };
                var equalizer = new EqualizerSampleProvider(tempoStream.ToSampleProvider()) { Enabled = equalizerEnabled };
                for (int band = 0; band < EqualizerSampleProvider.BandFrequencies.Length; band++)
                    equalizer.SetBandGain(band, band < equalizerGains.Length ? equalizerGains[band] : 0);

                return new PreparedTrack
                {
                    AudioFile = reader,
                    TempoStream = tempoStream,
                    Equalizer = equalizer,
                    ReplayGainFactor = replayGain,
                    Title = title,
                    Artist = artist,
                    AlbumArt = albumArt,
                    AlbumArtBytes = albumArtBytes,
                    AlbumArtMimeType = albumArtMimeType,
                    AlbumArtPictureType = albumArtPictureType
                };
            }
            catch
            {
                reader.Dispose();
                throw;
            }
        }, token).ConfigureAwait(true);
    }


    // ---------- Подсветка и автопрокрутка плейлиста к текущему треку ----------
    // Подсветка — обычное выделение строки (ListViewItem.IsSelected), то же самое, что при
    // клике мышью. При смене трека просто выставляем SelectedItem нужной строки, разворачиваем
    // её группу, если была свёрнута, и прокручиваем список, чтобы строка была видна.

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

            // Строка трека появится в плоском отображаемом списке (PlaylistTrackRow, см.
            // RefreshPlaylistView) только ПОСЛЕ пересборки — раньше разворачивание работало
            // само, через обычный WPF-биндинг Visibility вложенного ListView на IsExpanded;
            // теперь список плоский, и нужен явный пересбор.
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
            if (item is PlaylistTrackRow candidate && ReferenceEquals(candidate.Folder, folder) && candidate.FilePath == trackPath)
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

        // ScrollIntoView — встроенный способ для виртуализированных списков и прокрутить
        // к элементу, и заставить WPF реализовать его контейнер (ContainerFromItem для элемента
        // вне видимой области иначе вернул бы null). Раньше, когда каждая папка была отдельным
        // невиртуализированным ListView, все контейнеры существовали всегда, и прокрутку
        // приходилось считать вручную через координаты — теперь в этом нет нужды.
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
        _trackUserState = state;
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

    private void ApplyPreparedAlbumArt(PreparedTrack loaded, AlbumArtTransitionDirection direction)
    {
        if (loaded.AlbumArt is not null)
        {
            ApplyAlbumArtBrush(new ImageBrush(loaded.AlbumArt) { Stretch = Stretch.UniformToFill }, direction);
            _currentAlbumArt = loaded.AlbumArt;
            _currentAlbumArtBytes = loaded.AlbumArtBytes;
            _currentAlbumArtMimeType = loaded.AlbumArtMimeType;
            _currentAlbumArtPictureType = loaded.AlbumArtPictureType;
        }
        else
        {
            ResetAlbumArtPlaceholder(direction);
        }

        if (_settings.AccentColorMode == "Cover" || _settings.CoverBaseFromCover)
            ApplyAccentColor();
    }

    private void LoadAlbumArt(string filePath, AlbumArtTransitionDirection direction = AlbumArtTransitionDirection.None)
    {
        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            var pictures = tagFile.Tag.Pictures;

            if (pictures.Length > 0)
            {
                var bytes = pictures[0].Data.Data;
                using var ms = new MemoryStream(bytes);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze();

                ApplyAlbumArtBrush(new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill }, direction);
                _currentAlbumArt = bitmap;
                _currentAlbumArtBytes = bytes;
                _currentAlbumArtMimeType = string.IsNullOrWhiteSpace(pictures[0].MimeType) ? "image/jpeg" : pictures[0].MimeType;
                _currentAlbumArtPictureType = pictures[0].Type;
            }
            else
            {
                ResetAlbumArtPlaceholder(direction);
            }

            // Если в тегах есть название и исполнитель — покажем их вместо имени файла/папки
            if (!string.IsNullOrWhiteSpace(tagFile.Tag.Title) || !string.IsNullOrWhiteSpace(tagFile.Tag.FirstPerformer))
            {
                SetTrackInfoText(
                    !string.IsNullOrWhiteSpace(tagFile.Tag.Title) ? tagFile.Tag.Title : TrackTitleText.Text,
                    !string.IsNullOrWhiteSpace(tagFile.Tag.FirstPerformer) ? tagFile.Tag.FirstPerformer : TrackArtistText.Text);
            }
        }
        catch
        {
            // Файл без тегов, повреждённые метаданные и т.п. — просто показываем плейсхолдер
            ResetAlbumArtPlaceholder(direction);
        }

        if (_settings.AccentColorMode == "Cover" || _settings.CoverBaseFromCover)
            ApplyAccentColor();
    }

    private void ApplyAlbumArtBrush(Brush brush, AlbumArtTransitionDirection direction = AlbumArtTransitionDirection.None)
    {
        AnimateAlbumArtTransition(direction, () =>
        {
            AlbumArtBorder.Background = brush;
            AlbumArtIcon.Visibility = Visibility.Collapsed;
        });
    }

    private void ResetAlbumArtPlaceholder(AlbumArtTransitionDirection direction = AlbumArtTransitionDirection.None)
    {
        AnimateAlbumArtTransition(direction, () =>
        {
            AlbumArtBorder.Background = (Brush)FindResource("ControlFillColorSecondaryBrush");
            AlbumArtIcon.Visibility = Visibility.Visible;
        });
        _currentAlbumArt = null;
        _currentAlbumArtBytes = null;
        _currentAlbumArtMimeType = null;
        _currentAlbumArtPictureType = null;

        if (_settings.AccentColorMode == "Cover" || _settings.CoverBaseFromCover)
            ApplyAccentColor();
    }

    // Смена обложки в духе iTunes: снимок прежней обложки "улетает" в сторону с затуханием,
    // новая в этот момент "влетает" с противоположной стороны. direction == None — новое
    // изображение применяется мгновенно, без анимации (первая загрузка, анимация выключена
    // в настройках и т.п.).
    private void AnimateAlbumArtTransition(AlbumArtTransitionDirection direction, Action applyNewArt)
    {
        if (direction == AlbumArtTransitionDirection.None || !_settings.AlbumArtTransitionEnabled ||
            AccessibilityPreferences.ShouldReduceMotion(_settings) || !IsLoaded)
        {
            applyNewArt();
            return;
        }

        // Останавливаем анимации предыдущего перехода, если он ещё не успел доиграть
        // (быстрое переключение треков подряд) — иначе они будут конфликтовать за одни и
        // те же свойства.
        AlbumArtGhostTransform.BeginAnimation(TranslateTransform.XProperty, null);
        AlbumArtGhostScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        AlbumArtGhostScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        AlbumArtGhostBorder.BeginAnimation(OpacityProperty, null);
        AlbumArtBorderTransform.BeginAnimation(TranslateTransform.XProperty, null);
        AlbumArtBorderScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        AlbumArtBorderScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        double size = AlbumArtBorder.ActualWidth > 0 ? AlbumArtBorder.ActualWidth : AlbumArtBorder.Width;
        double distance = size + 24;
        double exitX = direction == AlbumArtTransitionDirection.Next ? -distance : distance;
        double enterFromX = direction == AlbumArtTransitionDirection.Next ? distance : -distance;

        // "Призрак" — снимок ТЕКУЩЕЙ (ещё старой) обложки, показанный поверх основной, пока та
        // подменяется на новую и стартует за кадром с противоположной стороны.
        AlbumArtGhostBorder.Width = AlbumArtBorder.Width;
        AlbumArtGhostBorder.Height = AlbumArtBorder.Height;
        AlbumArtGhostBorder.CornerRadius = AlbumArtBorder.CornerRadius;
        AlbumArtGhostBorder.Background = AlbumArtIcon.Visibility == Visibility.Visible ? null : AlbumArtBorder.Background;
        AlbumArtGhostIcon.Visibility = AlbumArtIcon.Visibility;
        AlbumArtGhostTransform.X = 0;
        AlbumArtGhostScale.ScaleX = 1;
        AlbumArtGhostScale.ScaleY = 1;
        AlbumArtGhostBorder.Opacity = 1;
        AlbumArtGhostBorder.Visibility = Visibility.Visible;

        applyNewArt();
        AlbumArtBorderTransform.X = enterFromX;
        AlbumArtBorderScale.ScaleX = 0.88;
        AlbumArtBorderScale.ScaleY = 0.88;

        // Плавные, но разные кривые для "туда" и "оттуда": уезжающая обложка стартует резче и
        // ускоряется (EaseIn), а влетающая — наоборот, гасит скорость к концу и мягко
        // "садится" на место (EaseOut). Такое сочетание выглядит естественнее одинаковой
        // кривой в обе стороны и меньше похоже на дёрганый слайд.
        var duration = TimeSpan.FromMilliseconds(460);
        var exitEase = new CubicEase { EasingMode = EasingMode.EaseIn };
        var enterEase = new CubicEase { EasingMode = EasingMode.EaseOut };

        var ghostSlide = new DoubleAnimation(0, exitX, duration) { EasingFunction = exitEase };
        var ghostScaleAnim = new DoubleAnimation(1, 0.88, duration) { EasingFunction = exitEase };
        var ghostFade = new DoubleAnimation(1, 0, duration) { EasingFunction = exitEase };
        ghostSlide.Completed += (_, _) => AlbumArtGhostBorder.Visibility = Visibility.Collapsed;

        var enterSlide = new DoubleAnimation(enterFromX, 0, duration) { EasingFunction = enterEase };
        var enterScaleAnim = new DoubleAnimation(0.88, 1, duration) { EasingFunction = enterEase };

        AlbumArtGhostTransform.BeginAnimation(TranslateTransform.XProperty, ghostSlide);
        AlbumArtGhostScale.BeginAnimation(ScaleTransform.ScaleXProperty, ghostScaleAnim);
        AlbumArtGhostScale.BeginAnimation(ScaleTransform.ScaleYProperty, ghostScaleAnim);
        AlbumArtGhostBorder.BeginAnimation(OpacityProperty, ghostFade);
        AlbumArtBorderTransform.BeginAnimation(TranslateTransform.XProperty, enterSlide);
        AlbumArtBorderScale.BeginAnimation(ScaleTransform.ScaleXProperty, enterScaleAnim);
        AlbumArtBorderScale.BeginAnimation(ScaleTransform.ScaleYProperty, enterScaleAnim);
    }

    private void OutputDevice_PlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // Сохраняем generation и путь именно того reader, который остановился. Callback
        // приходит с audio thread, а Dispatcher может выполнить его уже после быстрой загрузки
        // следующего трека.
        int generation = Volatile.Read(ref _trackLoadGeneration);
        string? stoppedPath = _currentTrackPath;
        Exception? playbackError = e.Exception;
        if (_isExiting || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;

        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (_isExiting || generation != Volatile.Read(ref _trackLoadGeneration) ||
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
                bool isLastTrack = !_isShuffleEnabled && (posInActive < 0 || posInActive == active.Count - 1);
                if (isLastTrack)
                    StopPlayback();
                else
                    PlayNextTrack(TrackChangeOrigin.Automatic);
                break;
        }
    }

    // ---------- Кнопки управления ----------

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        // _audioFile, а не _outputDevice — устройство вывода теперь живёт всю сессию
        // приложения и не обнуляется между треками (см. подробный комментарий у поля
        // _outputDevice в начале файла), так что null у него означал бы буквально "ничего не
        // загружали ни разу с самого запуска", а не "сейчас ничего не загружено".
        if (_audioFile == null)
        {
            var active = FlattenActive();
            if (active.Count > 0)
            {
                LoadAndPlay(active[0]);
            }
            return;
        }

        if (_isPlaying)
        {
            try
            {
                _outputDevice?.Pause();
            }
            catch (Exception ex)
            {
                // Устройство вывода могло исчезнуть прямо во время работы (наушники/колонки
                // отключили, драйвер упал). Не выдаём желаемое состояние «На паузе» за факт:
                // аудио могло продолжить играть, поэтому оставляем существующий playback state
                // и показываем понятную ошибку с подсказкой в индикаторе трека.
                RecoverOutputDeviceAfterFailure(ex, resumePlayback: true);
                return;
            }

            PlayPauseButton.Icon = IconResources.MakeOnAccent("IconPlay", 15);
            StopProgressTimerAndAnimation();
            _nowPlaying?.SetPlaybackStatus(Windows.Media.MediaPlaybackStatus.Paused);
            RaisePlaybackStateChanged(false);
            SetTrackUserState(TrackUserState.Paused);

            // На паузе часто и надолго оставляют трек, не закрывая плеер вовсе — сохраняем
            // позицию сразу же, а не ждём следующего реального закрытия (см. PersistPlaybackAndPlaylistState).
            PersistPlaybackAndPlaylistState();
        }
        else
        {
            try
            {
                _outputDevice?.Play();
            }
            catch (Exception ex)
            {
                // См. комментарий у Pause() выше — та же защита от падения из-за проблем с
                // самим устройством вывода, а не с плеером как таковым.
                RecoverOutputDeviceAfterFailure(ex, resumePlayback: true);
                return;
            }

            PlayPauseButton.Icon = IconResources.MakeOnAccent("IconPause", 15);
            _progressTimer.Start();
            _playbackClock.Start();
            _nowPlaying?.SetPlaybackStatus(Windows.Media.MediaPlaybackStatus.Playing);
            RaisePlaybackStateChanged(true);
            SetTrackUserState(TrackUserState.Playing);
        }
        _isPlaying = !_isPlaying;
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) => StopPlayback();

    private async Task FadeOutBeforeTrackChangeAsync(CancellationToken token)
    {
        if (_activeFade is not null && _isPlaying)
        {
            // Даем текущему аудиографу завершить короткий fade-out, чтобы не обрывать
            // ненулевой сэмпл перед Stop()/Init() нового reader. Delay асинхронный и не
            // блокирует Dispatcher.
            _activeFade.BeginFadeOut(24);
            try
            {
                await Task.Delay(30, token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        StopPlayback(disposeOnly: true);
    }

    private void InitializeOutputDevice(ISampleProvider waveProvider)
    {
        EnsureOutputDevice();
        try
        {
            _outputDevice!.Init(waveProvider);
        }
        catch (Exception ex) when (!string.IsNullOrWhiteSpace(_settings.OutputDeviceName))
        {
            // Сохранённое устройство всё ещё могло присутствовать в WaveOut-списке, но уже
            // отказаться открываться (например, Bluetooth гарнитура отключилась между
            // перечислением и Init). Однократно пробуем системный audio mapper.
            Logger.Error("Не удалось инициализировать выбранное устройство вывода; используется системное устройство", ex);
            DisposeOutputDeviceSafely();
            _settings.OutputDeviceName = AudioOutputDeviceService.SystemDefaultDeviceName;
            _ = SettingsManager.SaveAsync(_settings);
            _settingsWindow?.RefreshOutputDeviceSelection();
            EnsureOutputDevice();
            _outputDevice!.Init(waveProvider);
        }
    }

    private void EnsureOutputDevice()
    {
        if (_outputDevice is not null) return;

        int deviceNumber = AudioOutputDeviceService.ResolveDeviceNumber(_settings.OutputDeviceName, out bool usedFallback);
        if (usedFallback)
        {
            Logger.Warn($"Выбранное устройство вывода недоступно: {_settings.OutputDeviceName}. Используется системное устройство Windows.");
            _settings.OutputDeviceName = AudioOutputDeviceService.SystemDefaultDeviceName;
            _ = SettingsManager.SaveAsync(_settings);
            _settingsWindow?.RefreshOutputDeviceSelection();
        }

        _outputDevice = new WaveOutEvent { DeviceNumber = deviceNumber };
    }

    // Вызывается из SettingsWindow сразу после выбора устройства. Если трек уже загружен,
    // сохраняем позицию/состояние, создаём новый вывод и возвращаемся к тому же месту.
    public void ApplyOutputDeviceSelection()
    {
        if (_isExiting) return;

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

    // Устройство могло исчезнуть в процессе Play/Pause или прислать PlaybackStopped с ошибкой.
    // Один controlled retry через Windows audio mapper лучше, чем повторные сообщения об ошибке:
    // при отключении USB/Bluetooth это обычно уже новое системное устройство Windows.
    private void RecoverOutputDeviceAfterFailure(Exception error, bool resumePlayback)
    {
        Logger.Error("Ошибка устройства вывода; выполняется восстановление через системное устройство", error);
        if (_isOutputRecoveryInProgress || _isExiting) return;

        string? currentPath = _currentTrackPath;
        TimeSpan position = _audioFile?.CurrentTime ?? TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(currentPath) || !File.Exists(currentPath))
        {
            StopPlayback();
            DisposeOutputDeviceSafely();
            SetTrackUserState(TrackUserState.Error);
            return;
        }

        _isOutputRecoveryInProgress = true;
        try
        {
            StopPlayback(disposeOnly: true);
            DisposeOutputDeviceSafely();
            _settings.OutputDeviceName = AudioOutputDeviceService.SystemDefaultDeviceName;
            _ = SettingsManager.SaveAsync(_settings);
            _settingsWindow?.RefreshOutputDeviceSelection();
            LoadAndPlay(currentPath, autoPlay: resumePlayback, startPosition: position,
                changeOrigin: TrackChangeOrigin.Automatic);
        }
        finally
        {
            _isOutputRecoveryInProgress = false;
        }
    }

    private void DisposeOutputDeviceSafely()
    {
        if (_outputDevice is null) return;

        try
        {
            _outputDevice.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось освободить WaveOutEvent", ex);
        }
        finally
        {
            _outputDevice = null;
        }
    }

    // См. TrackTagsWindow.SaveButton_Click — координация с внешней записью в файл (изменение
    // тегов/обложки), пока он может быть открыт живым NAudio-потоком на чтение. Возвращает
    // null, если сейчас играет не этот файл вовсе (ничего останавливать не нужно, запись
    // пройдёт спокойно параллельно с воспроизведением ДРУГОГО трека); если это именно
    // filePath — временно освобождает хендл и возвращает точку восстановления, которую нужно
    // передать в ResumeAfterExternalWrite после того, как запись закончится (успешно или нет).
    public (TimeSpan Position, bool WasPlaying)? ReleaseFileForExternalWrite(string filePath)
    {
        if (filePath != _currentTrackPath || _audioFile == null) return null;

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
        StopProgressTimerAndAnimation();

        // Stop()/Dispose() ниже сами поднимают PlaybackStopped — срабатывает и на естественное
        // завершение, и на ручную остановку. Без отписки заранее автопереключение из
        // OutputDevice_PlaybackStopped вызывало бы этот же обработчик повторно для СТАРОГО
        // _audioFile, и автопереход срабатывал через раз вместо каждого трека.
        if (_outputDevice != null)
            _outputDevice.PlaybackStopped -= OutputDevice_PlaybackStopped;

        // _outputDevice не останавливается через Dispose и не обнуляется — живёт до закрытия
        // приложения (см. OnClosed) и переиспользуется через Init(...) в LoadAndPlay.
        try
        {
            _outputDevice?.Stop();
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось корректно остановить устройство вывода", ex);
        }

        try
        {
            _tempoStream?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось освободить AudioFileReader", ex);
        }
        finally
        {
            _tempoStream = null;
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
            _nowPlaying?.SetPlaybackStatus(Windows.Media.MediaPlaybackStatus.Stopped);
            RaisePlaybackStateChanged(false);
            _discordRichPresence.ClearAndDispose();
            SetTrackUserState(TrackUserState.Stopped);
        }
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e)
    {
        if (ComputePreviousTrackPath(GetCurrentTrackPath()) is { } prevPath)
            LoadAndPlay(prevPath, autoPlay: _isPlaying, albumArtDirection: AlbumArtTransitionDirection.Previous);
    }

    private void NextButton_Click(object sender, RoutedEventArgs e) => PlayNextTrack();

    private void PlayNextTrack(TrackChangeOrigin changeOrigin = TrackChangeOrigin.User)
    {
        if (ComputeNextTrackPath(GetCurrentTrackPath()) is { } nextPath)
            LoadAndPlay(nextPath, autoPlay: _isPlaying, changeOrigin: changeOrigin);
    }

    // Чистое вычисление пути к следующему/предыдущему треку — без загрузки и воспроизведения.
    // Вынесено из PlayNextTrack/PrevButton_Click, чтобы им же можно было "прокрутить" несколько
    // шагов вперёд/назад подряд (см. HandleHotkeyNext/HandleHotkeyPrevious) не запуская
    // декодирование аудио на каждый промежуточный шаг — сама эта функция ничего не декодирует,
    // только двигает индекс по активному плейлисту либо по истории шафла (см. комментарий
    // над GetShuffleHistoryTrack — там же и мутация истории, она достаточно дешёвая, чтобы
    // звать её хоть на каждое сообщение WM_HOTKEY при зажатой клавише).
    private string? ComputeNextTrackPath(string? currentPath)
    {
        var active = FlattenActive();
        if (active.Count == 0) return null;

        if (_isShuffleEnabled)
        {
            // Если перед этим переключались назад по истории шафла, "вперёд" сначала
            // возвращает туда, откуда уходили назад, а не сразу к новому случайному треку —
            // и только когда история исчерпана, генерируем новый случайный трек и дописываем
            // его в конец.
            return GetShuffleHistoryTrack(+1, active, currentPath)
                   ?? AppendNewShuffleTrack(active, currentPath);
        }

        int posInActive = currentPath != null ? active.IndexOf(currentPath) : -1;
        int nextPos = posInActive < 0 ? 0 : (posInActive + 1) % active.Count;
        return active[nextPos];
    }

    private string? ComputePreviousTrackPath(string? currentPath)
    {
        var active = FlattenActive();
        if (active.Count == 0) return null;

        if (_isShuffleEnabled)
        {
            // Не генерируем новый случайный трек, а идём на шаг назад по уже пройденной
            // истории шафла — и только если двигаться назад больше некуда (это самый первый
            // "назад", раньше которого история не заходит), подбираем случайный трек и
            // дописываем его в начало истории, чтобы дальнейшие "вперёд"/"назад" оставались
            // последовательными.
            return GetShuffleHistoryTrack(-1, active, currentPath)
                   ?? PrependNewShuffleTrack(active, currentPath);
        }

        int posInActive = currentPath != null ? active.IndexOf(currentPath) : -1;
        int prevPos = posInActive <= 0 ? active.Count - 1 : posInActive - 1;
        return active[prevPos];
    }

    // ---------- Быстрое переключение треков зажатой хоткей-клавишей ----------
    // Первый переход выполняется сразу. Если клавиша остаётся зажатой, после 280 мс запускается
    // независимый от системных WM_HOTKEY повтор каждые 140 мс (максимум около 7 треков/сек).
    // Это устраняет зависимость от настроек автоповтора Windows и не позволяет породить десятки
    // одновременных загрузок: LoadAndPlay отменяет предыдущую подготовку, а между шагами есть
    // жёсткое ограничение частоты.
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

    private void HandleHotkeyNext(int virtualKey) => HandleHotkeyTrackStep(+1, virtualKey);

    private void HandleHotkeyPrevious(int virtualKey) => HandleHotkeyTrackStep(-1, virtualKey);

    private void HandleHotkeyTrackStep(int stepDirection, int virtualKey)
    {
        // После первого WM_HOTKEY система продолжит присылать повторы, пока клавиша нажата.
        // Их игнорируем целиком: собственный таймер уже опрашивает физическое отпускание и
        // выдаёт шаги с фиксированной безопасной частотой, независимо от настроек Windows.
        bool sameHeldKey = _hotkeyTrackStepTimer.IsEnabled
            && _heldHotkeyVirtualKey == virtualKey && _heldHotkeyDirection == stepDirection;
        if (sameHeldKey)
            return;

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
    }

    private void CommitPendingHotkeyTrackStep()
    {
        int steps = _pendingHotkeyNetSteps;
        _pendingHotkeyNetSteps = 0;
        if (steps == 0) return;

        // NavigationCursor хранит уже запрошенный путь, пока асинхронная загрузка ещё не успела
        // сделать его CurrentTrackPath. Благодаря этому удержание действительно проходит по
        // последовательности треков, а не повторно запрашивает один и тот же следующий файл.
        var direction = steps > 0 ? AlbumArtTransitionDirection.Next : AlbumArtTransitionDirection.Previous;
        string? path = _hotkeyTrackNavigationCursor ?? GetCurrentTrackPath();
        string? targetPath = null;

        for (int i = 0; i < Math.Abs(steps); i++)
        {
            string? next = steps > 0 ? ComputeNextTrackPath(path) : ComputePreviousTrackPath(path);
            if (next == null) break;
            targetPath = next;
            path = next;
        }

        if (targetPath == null) return;
        _hotkeyTrackNavigationCursor = targetPath;
        LoadAndPlay(targetPath, autoPlay: _isPlaying, albumArtDirection: direction);
    }

    private string GetRandomTrack(List<string> activeTracks, string? excludePath)
    {
        if (activeTracks.Count <= 1) return activeTracks[0];

        // Не даём случайно выпасть тому же треку два раза подряд
        string candidate;
        do
        {
            candidate = activeTracks[_random.Next(activeTracks.Count)];
        } while (candidate == excludePath);

        return candidate;
    }

    // ---------- Шаффл без повторов (Settings.UseImprovedShuffle) ----------
    // "Колода" (bag shuffle) вместо чисто случайного выбора на каждом шаге: тасуем весь активный
    // плейлист один раз и идём по нему по порядку — так каждый трек играет ровно один раз,
    // прежде чем что-то повторится. Первый трек новой колоды не должен совпасть с последним
    // треком предыдущей, иначе один и тот же трек мог бы прозвучать дважды подряд на стыке.
    private string GetNextShuffleTrack(List<string> activeTracks, string? excludePath)
    {
        if (!_settings.UseImprovedShuffle)
            return GetRandomTrack(activeTracks, excludePath);

        if (activeTracks.Count <= 1) return activeTracks[0];

        // Убираем из колоды треки, которых уже нет в активном плейлисте (сняли галочку с
        // папки, удалили файл и т.п.)
        _shuffleBag.RemoveAll(t => !activeTracks.Contains(t));

        if (_shuffleBag.Count == 0)
        {
            _shuffleBag = new List<string>(activeTracks);
            ShuffleInPlace(_shuffleBag);

            if (excludePath != null && _shuffleBag.Count > 1 && _shuffleBag[0] == excludePath)
            {
                int swapIndex = _random.Next(1, _shuffleBag.Count);
                (_shuffleBag[0], _shuffleBag[swapIndex]) = (_shuffleBag[swapIndex], _shuffleBag[0]);
            }
        }

        var next = _shuffleBag[0];
        _shuffleBag.RemoveAt(0);
        return next;
    }

    // Классическая тасовка Фишера-Йейтса — равновероятная случайная перестановка списка на месте.
    private void ShuffleInPlace(List<string> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    // Двигается по уже накопленной истории шафла на shift (-1 — назад, +1 — вперёд) и
    // возвращает трек по новому положению, либо null, если двигаться в эту сторону
    // больше некуда (истории ещё нет, или она уже кончилась). Трек, который мог быть
    // удалён из плейлиста с момента проигрывания, пропускается вместе с "хвостом"
    // истории после него.
    private string? GetShuffleHistoryTrack(int shift, List<string> activeTracks, string? currentPath)
    {
        if (_shuffleHistory.Count == 0 && currentPath != null)
        {
            // Первое переключение в шафле: заводим историю с текущего трека, чтобы было
            // куда возвращаться "назад" после первого же "вперёд".
            _shuffleHistory.Add(currentPath);
            _shuffleHistoryIndex = 0;
        }

        int newIndex = _shuffleHistoryIndex + shift;
        if (newIndex < 0 || newIndex >= _shuffleHistory.Count) return null;

        var path = _shuffleHistory[newIndex];
        if (!activeTracks.Contains(path))
        {
            // Трек пропал из активного плейлиста — обрезаем историю на этом месте и
            // считаем, что дальше в эту сторону двигаться некуда.
            if (shift > 0)
                _shuffleHistory.RemoveRange(newIndex, _shuffleHistory.Count - newIndex);
            else
                _shuffleHistory.RemoveRange(0, newIndex + 1);
            _shuffleHistoryIndex = Math.Clamp(_shuffleHistoryIndex, -1, _shuffleHistory.Count - 1);
            return null;
        }

        _shuffleHistoryIndex = newIndex;
        return path;
    }

    // Генерирует новый случайный трек и дописывает его в конец истории шафла — вызывается
    // только когда двигаться вперёд по уже существующей истории больше некуда.
    private string AppendNewShuffleTrack(List<string> activeTracks, string? currentPath)
    {
        var next = GetNextShuffleTrack(activeTracks, currentPath);

        if (_shuffleHistory.Count == 0 && currentPath != null)
            _shuffleHistory.Add(currentPath);

        _shuffleHistory.Add(next);
        _shuffleHistoryIndex = _shuffleHistory.Count - 1;
        return next;
    }

    // Зеркальный аналог AppendNewShuffleTrack для случая "назад" — вызывается только когда
    // в истории шафла ещё нет ничего раньше текущего трека.
    private string PrependNewShuffleTrack(List<string> activeTracks, string? currentPath)
    {
        var prev = GetNextShuffleTrack(activeTracks, currentPath);

        if (_shuffleHistory.Count == 0 && currentPath != null)
            _shuffleHistory.Add(currentPath);

        _shuffleHistory.Insert(0, prev);
        _shuffleHistoryIndex = 0;
        return prev;
    }

    private void ShuffleButton_Click(object sender, RoutedEventArgs e) => SetShuffleEnabled(!_isShuffleEnabled);

    // Вынесено из ShuffleButton_Click, чтобы этим же кодом (смена состояния + иконки кнопки)
    // можно было воспользоваться и при восстановлении сохранённого состояния при запуске
    // (см. ApplySettingsOnStartup), не эмулируя клик по кнопке.
    private void SetShuffleEnabled(bool enabled, bool resetSessionHistory = true)
    {
        _isShuffleEnabled = enabled;
        SetAccentButtonActive(ShuffleButton, _isShuffleEnabled);
        IconResources.SetOnAccent(ShuffleIcon, _isShuffleEnabled);
        ShuffleStateChanged?.Invoke(_isShuffleEnabled);

        // Смена пользователем режима шаффла начинает новый заезд. Исключение — старт
        // приложения: там восстановим ранее сохранённую историю после загрузки плейлиста.
        if (resetSessionHistory)
            ResetShuffleState();
    }

    // Вызывается из окна настроек при переключении настройки "Шаффл без повторов" — колода
    // от старого/нового алгоритма не имеет смысла продолжать использовать после смены режима
    // на лету, поэтому просто начинаем её заново.
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

    // Возвращает последний полный снимок, созданный перед явным сбросом. Здесь восстанавливаем
    // не только AppSettings, но и runtime-коллекции, которые при полном сбросе уже были очищены.
    // Сохранённый трек загружается на паузе: возврат настроек не должен внезапно начать музыку.
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

    public void ResetShuffleState()
    {
        _shuffleHistory.Clear();
        _shuffleHistoryIndex = -1;
        _shuffleBag.Clear();
    }

    // Вызывается только после восстановления SavedPlaylistFolders. Повреждённые, удалённые
    // или выключенные пути не должны делать кнопку «Назад» непредсказуемой, поэтому берём лишь
    // актуальные активные треки и аккуратно ограничиваем сохранённый индекс.
    private void PersistShuffleSessionState()
    {
        if (!_isShuffleEnabled || _shuffleHistory.Count == 0)
        {
            _settings.ShuffleHistory = new List<string>();
            _settings.ShuffleHistoryIndex = -1;
            _settings.ShuffleBag = new List<string>();
            return;
        }

        int firstPersistedIndex = Math.Max(0, _shuffleHistory.Count - MaxPersistedShuffleHistory);
        _settings.ShuffleHistory = _shuffleHistory.Skip(firstPersistedIndex).ToList();
        _settings.ShuffleHistoryIndex = Math.Clamp(
            _shuffleHistoryIndex - firstPersistedIndex, 0, _settings.ShuffleHistory.Count - 1);
        _settings.ShuffleBag = _shuffleBag.Take(MaxPersistedShuffleHistory).ToList();
    }

    private void RestoreShuffleSessionState()
    {
        ResetShuffleState();
        if (!_settings.IsShuffleEnabled) return;

        var activePaths = new HashSet<string>(FlattenActive(), StringComparer.OrdinalIgnoreCase);
        if (activePaths.Count == 0) return;

        _shuffleHistory.AddRange((_settings.ShuffleHistory ?? new List<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path) && activePaths.Contains(path))
            .TakeLast(MaxPersistedShuffleHistory));

        if (_shuffleHistory.Count > 0)
        {
            _shuffleHistoryIndex = Math.Clamp(_settings.ShuffleHistoryIndex, 0, _shuffleHistory.Count - 1);
            if (_settings.LastTrackPath is { } lastTrackPath)
            {
                int lastTrackIndex = _shuffleHistory.FindLastIndex(path =>
                    string.Equals(path, lastTrackPath, StringComparison.OrdinalIgnoreCase));
                if (lastTrackIndex >= 0)
                    _shuffleHistoryIndex = lastTrackIndex;
            }
        }

        _shuffleBag = (_settings.ShuffleBag ?? new List<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path) && activePaths.Contains(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

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

    // ---------- Мини-плеер (отдельное окно с настоящей прозрачностью) ----------

    private void MiniModeButton_Click(object sender, RoutedEventArgs e) => SetPlayerViewMode(PlayerViewMode.Mini);

    // Переключает в мини-плеер. Вызывается из SetPlayerViewMode — как по кнопке/пункту
    // меню, так и при восстановлении сохранённого состояния на старте.
    private void EnterMiniMode()
    {
        // После фактического возврата в мини-плеер отложенный маркер больше не нужен.
        _returnToMiniOnNextTaskbarMinimize = false;

        // На этот момент _viewMode ещё хранит вид ДО перехода в мини-режим (SetPlayerViewMode
        // присваивает новое значение уже после вызова этого метода) — запоминаем его, чтобы
        // при "развернуть" в ExitMiniMode вернуться именно туда, откуда ушли.
        _preMiniViewMode = _viewMode;

        _miniPlayerWindow = new MiniPlayerWindow(this)
        {
            Topmost = _settings.MiniPlayerAlwaysOnTop
        };

        // Возвращаем мини-плеер туда, куда его в прошлый раз поставил пользователь.
        // Если позиция ещё ни разу не задавалась — ставим его в правый нижний угол
        // рабочей области экрана (стандартное место для мини-плеера).
        if (_settings.MiniPlayerLeft.HasValue && _settings.MiniPlayerTop.HasValue)
        {
            _miniPlayerWindow.Left = _settings.MiniPlayerLeft.Value;
            _miniPlayerWindow.Top = _settings.MiniPlayerTop.Value;
        }
        else
        {
            var workArea = SystemParameters.WorkArea;
            _miniPlayerWindow.Left = workArea.Right - _miniPlayerWindow.Width - 24;
            _miniPlayerWindow.Top = workArea.Bottom - _miniPlayerWindow.Height - 24;
        }

        _miniPlayerWindow.Closed += (_, _) => _miniPlayerWindow = null;
        _miniPlayerWindow.Show();
        ForceForeground(_miniPlayerWindow);

        _isMiniMode = true;
        Hide();

        // У мини-плеера ShowInTaskbar="False" (см. MiniPlayerWindow.xaml) — в мини-режиме у
        // приложения вообще нет никакого присутствия ни в панели задач, ни в трее, кроме самого
        // окошка мини-плеера. Показываем иконку в трее и здесь, а не только при закрытии
        // основного окна в трей (см. OnClosing) — иначе, свернув плеер в мини-режим, до него
        // потом никак не добраться, кроме как найти и кликнуть само окошко мини-плеера.
        _trayIconManager?.Show($"Lumisense — {TrackTitleText.Text}");
    }

    // Вызывается из MiniPlayerWindow при нажатии кнопки "развернуть".
    // Внешняя активация ярлыка передаёт true, чтобы следующий клик по кнопке панели задач
    // снова вернул мини-плеер; обычное разворачивание мини-плеера этот маркер не устанавливает.
    public void ExitMiniMode(bool returnToMiniOnNextTaskbarMinimize = false)
    {
        _isMiniMode = false;
        _returnToMiniOnNextTaskbarMinimize = returnToMiniOnNextTaskbarMinimize;

        _miniPlayerWindow?.Close();
        _miniPlayerWindow = null;

        Show();
        WindowState = WindowState.Normal;
        ForceForeground(this);
        _trayIconManager?.Hide();

        // Ширина/высота окна не менялись, пока плеер был свёрнут в мини-режим — они уже
        // соответствуют тому виду, в котором плеер был до сворачивания. Здесь только
        // возвращаем сам флаг вида плеера (для галочки в контекстном меню и настроек) —
        // без повторного SetPlayerViewMode, чтобы не запускать пересчёт размеров заново.
        _viewMode = _preMiniViewMode;
        _settings.PlayerViewMode = _viewMode.ToString();
        SettingsManager.Save(_settings);
        UpdateViewModeMenuChecks();
    }

    // Вызывается из MiniPlayerWindow при перемещении окна пользователем — запоминаем
    // положение в общих настройках, чтобы при следующем сворачивании в мини-плеер
    // окно появилось на том же месте (в том числе и после перезапуска приложения)
    public void SaveMiniPlayerPosition(double left, double top)
    {
        _settings.MiniPlayerLeft = left;
        _settings.MiniPlayerTop = top;
    }

    // Позволяет окну настроек мгновенно применить изменения прозрачности/поверх окон,
    // если мини-плеер сейчас открыт
    public void ApplyMiniPlayerOpacityLive(double opacity)
    {
        // _settings.MiniPlayerOpacity уже обновлён вызывающей стороной (см.
        // SettingsWindow.MiniOpacitySlider_ValueChanged) — ApplyOpacityLive просто
        // перечитывает его и пересчитывает альфа-канал фона мини-плеера.
        if (_miniPlayerWindow != null) _miniPlayerWindow.ApplyOpacityLive();
    }

    public void ApplyMiniPlayerTopmostLive(bool topmost)
    {
        if (_miniPlayerWindow != null) _miniPlayerWindow.Topmost = topmost;
    }

    // Позволяет окну настроек мгновенно применить смену светлой/тёмной темы к мини-плееру,
    // если он сейчас открыт — иначе мини-плеер узнал бы о новой теме только при следующем
    // открытии (пересоздании окна).
    public void ApplyMiniPlayerThemeLive()
    {
        if (_miniPlayerWindow != null) _miniPlayerWindow.ApplyThemeLive();
    }

    // Позволяет окну настроек мгновенно переключить, какую функцию выполняет вторая кнопка
    // мини-плеера (повтор/перемешать — см. AppSettings.MiniPlayerSecondaryButton), если он
    // сейчас открыт, не дожидаясь его переоткрытия.
    public void ApplyMiniPlayerSecondaryButtonLive()
    {
        _miniPlayerWindow?.UpdateSecondaryButton();
    }

    // Единая точка изменения режима второй кнопки для страницы настроек и контекстного меню
    // мини-плеера. Открытое SettingsWindow синхронизируется без повторного вызова обработчика.
    public void SetMiniPlayerSecondaryButtonMode(string? mode)
    {
        _settings.MiniPlayerSecondaryButton = mode switch
        {
            "Shuffle" => "Shuffle",
            "Favorite" => "Favorite",
            _ => "Repeat"
        };
        SettingsManager.Save(_settings);

        ApplyMiniPlayerSecondaryButtonLive();
        _settingsWindow?.RefreshMiniPlayerToggles();
    }

    // Аналог ApplyMiniPlayerSecondaryButtonLive для настройки "расположение кнопок" (снизу /
    // на месте обложки, см. AppSettings.MiniPlayerButtonsLayout).
    public void ApplyMiniPlayerButtonsLayoutLive()
    {
        _miniPlayerWindow?.ApplyButtonsLayoutMode();
    }

    // Аналог ApplyMiniPlayerSecondaryButtonLive для настройки "показывать полосу прогресса"
    // (см. AppSettings.MiniPlayerShowProgress).
    public void ApplyMiniPlayerProgressBarVisibilityLive()
    {
        _miniPlayerWindow?.ApplyProgressBarVisibility();
    }

    // Аналог ApplyMiniPlayerProgressBarVisibilityLive для настройки "прогресс вокруг
    // обложки" (см. AppSettings.MiniPlayerShowArtworkProgress).
    public void ApplyMiniPlayerArtworkProgressVisibilityLive()
    {
        _miniPlayerWindow?.ApplyArtworkProgressVisibility();
    }

    // Применяет выбранный вид обложки (обычный / винил) немедленно, без закрытия мини-плеера.
    public void ApplyMiniPlayerArtworkStyleLive()
    {
        _miniPlayerWindow?.ApplyArtworkStyle();
    }

    // Применяет выбранный источник цвета (акцент оформления или фиксированный цвет) к уже
    // открытому мини-плееру без необходимости переоткрывать его.
    public void ApplyMiniPlayerArtworkProgressColorLive()
    {
        _miniPlayerWindow?.ApplyArtworkProgressColor();
    }

    // Аналог ApplyMiniPlayerSecondaryButtonLive для настройки "что показывать во второй
    // строке" (исполнитель / ничего / оставшееся время, см. AppSettings.MiniPlayerInfoMode).
    public void ApplyMiniPlayerInfoModeLive()
    {
        _miniPlayerWindow?.ApplyInfoModeLive();
    }

    // Всплывающая карточка показывается только после готовности метаданных и обложки. Решение
    // принимает явная политика: каждый новый трек, только фактический старт или лишь ручной
    // выбор/переключение. Возобновление той же композиции не вызывает этот метод вообще.
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


    // ---------- Эквалайзер (см. EqualizerSampleProvider) ----------
    // Настройки читаются/пишутся здесь, а не прямо из SettingsWindow — EqualizerSampleProvider
    // существует только пока что-то играет (пересоздаётся в LoadAndPlay), а
    // AppSettings.EqualizerEnabled/EqualizerBandGainsDb должны сохраняться и применяться даже
    // без активного воспроизведения.

    // Заполняет только что созданный _equalizer сохранёнными настройками — вызывается из
    // LoadAndPlay при каждой смене трека, потому что сам _equalizer живёт не дольше трека.
    private void ApplyEqualizerGainsFromSettings()
    {
        if (_equalizer == null) return;

        var saved = _settings.EqualizerBandGainsDb;
        for (int band = 0; band < EqualizerSampleProvider.BandFrequencies.Length; band++)
            _equalizer.SetBandGain(band, band < saved.Length ? saved[band] : 0);

        _equalizer.Enabled = _settings.EqualizerEnabled && !_settings.EqualizerBypass;
    }

    // Анимация смены обложки (см. AnimateAlbumArtTransition) — переключатель в настройках,
    // "Оформление". Хранится и читается напрямую из _settings, отдельного применения к
    // "живому" состоянию не требуется: флаг просто проверяется при каждом следующем вызове
    // AnimateAlbumArtTransition.
    public bool IsAlbumArtTransitionEnabled => _settings.AlbumArtTransitionEnabled;

    public void SetAlbumArtTransitionEnabled(bool enabled) => _settings.AlbumArtTransitionEnabled = enabled;

    public bool IsEqualizerEnabled => _settings.EqualizerEnabled;

    private void ApplyPlaybackRateToCurrentStream()
    {
        if (_tempoStream != null)
            _tempoStream.Tempo = _runtimePlaybackRate;
    }

    private void ReapplySavedPlaybackRateAfterTrackReady(int generation)
    {
        // SoundTouch создаётся в фоне, затем WaveOut инициализируется на UI-потоке. Повторяем
        // установку уже после Init через очередь Dispatcher, чтобы исключить поздний сброс
        // Tempo во время восстановления последнего трека при запуске приложения.
        Dispatcher.BeginInvoke(() =>
        {
            if (_isExiting || generation != Volatile.Read(ref _trackLoadGeneration) || _tempoStream == null)
                return;

            ApplyPlaybackRateToCurrentStream();
        }, DispatcherPriority.ContextIdle);
    }

    private static double NormalizePlaybackRate(double speed) =>
        Math.Round(Math.Clamp(speed, 0.5, 2.0), 2);

    private void SetPlaybackRate(double speed, bool persist)
    {
        double clamped = NormalizePlaybackRate(speed);
        _runtimePlaybackRate = clamped;
        _settings.PlaybackSpeed = clamped;

        ApplyPlaybackRateToCurrentStream();

        if (PlaybackRateValueText != null)
            PlaybackRateValueText.Text = FormatPlaybackRate(clamped);

        if (PlaybackRateSlider != null && Math.Abs(PlaybackRateSlider.Value - clamped) > 0.0001)
        {
            _isUpdatingPlaybackRateControl = true;
            try { PlaybackRateSlider.Value = clamped; }
            finally { _isUpdatingPlaybackRateControl = false; }
        }

        if (persist && !_isApplyingStartupSettings && !_isExiting)
            SettingsManager.Save(_settings);
    }

    public void ApplyPlaybackRateLive(double speed) => SetPlaybackRate(speed, persist: false);

    // Мини-плеер использует тот же путь, что и главный ползунок: темп применяется к текущему
    // SoundTouch-потоку, сохраняется и синхронизирует основной контрол без отдельной логики.
    public void SetPlaybackRateFromMiniPlayer(double speed) => SetPlaybackRate(speed, persist: true);

    // Аналогичная точка входа для тона. Если главный Slider существует, его ValueChanged уже
    // обновит SoundTouch, текст и сохранение; до его создания применяем всё напрямую.
    public void SetPlaybackPitchFromMiniPlayer(double semitones)
    {
        double clamped = Math.Clamp(semitones, -12.0, 12.0);
        if (PlaybackPitchSlider != null && Math.Abs(PlaybackPitchSlider.Value - clamped) > 0.0001)
        {
            PlaybackPitchSlider.Value = clamped;
            return;
        }

        ApplyPlaybackPitchLive(clamped);
        if (PlaybackPitchValueText != null)
            PlaybackPitchValueText.Text = FormatPlaybackPitch(clamped);
        PersistPlaybackSettingsAfterUserChange();
    }

    public void ApplyPlaybackPitchLive(double semitones)
    {
        double clamped = Math.Clamp(semitones, -12.0, 12.0);
        _settings.PlaybackPitchSemitones = clamped;
        if (_tempoStream != null)
            _tempoStream.PitchSemiTones = clamped;
    }

    public bool IsEqualizerBypass => _settings.EqualizerBypass;

    public void SetEqualizerEnabled(bool enabled)
    {
        _settings.EqualizerEnabled = enabled;
        if (_equalizer != null)
            _equalizer.Enabled = enabled && !_settings.EqualizerBypass;
    }

    // Bypass не сбрасывает ни флаг включения EQ, ни полосы, ни пресеты. Благодаря этому
    // пользователь может сравнить звук «с EQ / без EQ» и вернуть обработку одним кликом.
    public void SetEqualizerBypass(bool bypass)
    {
        _settings.EqualizerBypass = bypass;
        if (_equalizer != null)
            _equalizer.Enabled = _settings.EqualizerEnabled && !bypass;
    }

    public double GetEqualizerBandGain(int band) =>
        band >= 0 && band < _settings.EqualizerBandGainsDb.Length ? _settings.EqualizerBandGainsDb[band] : 0;

    // Вызывается из SettingsWindow при каждом движении слайдера одной полосы — сразу и
    // сохраняет значение в настройки, и (если сейчас что-то играет) применяет его к реальному
    // фильтру, чтобы звук менялся вживую, а не только после следующего перезапуска трека.
    public void SetEqualizerBandGain(int band, double gainDb)
    {
        if (band < 0 || band >= EqualizerSampleProvider.BandFrequencies.Length) return;

        // EqualizerBandGainsDb у уже существующих settings.json мог быть сохранён с ДРУГИМ
        // количеством полос более старой/новой версией плеера — расширяем массив, а не падаем
        // с IndexOutOfRange, если он окажется короче текущего набора полос.
        if (_settings.EqualizerBandGainsDb.Length <= band)
        {
            var resized = new double[EqualizerSampleProvider.BandFrequencies.Length];
            Array.Copy(_settings.EqualizerBandGainsDb, resized, _settings.EqualizerBandGainsDb.Length);
            _settings.EqualizerBandGainsDb = resized;
        }

        _settings.EqualizerBandGainsDb[band] = gainDb;
        _equalizer?.SetBandGain(band, gainDb);
    }

    // Кнопка "Сбросить" в настройках — обнуляет все полосы разом.
    public void ResetEqualizer()
    {
        _settings.EqualizerBandGainsDb = new double[EqualizerSampleProvider.BandFrequencies.Length];
        ApplyEqualizerGainsFromSettings();
    }

    // ---------- Пресеты эквалайзера ----------

    public IReadOnlyList<EqualizerPreset> EqualizerPresets => _settings.EqualizerPresets;

    // Сохраняет ТЕКУЩИЕ значения полос (EqualizerBandGainsDb) как пресет с этим именем.
    // Имя уже занято — тихо перезаписывает существующий пресет, а не плодит дубликаты:
    // пользователь чаще всего "пересохраняет" под тем же названием, донастроив что-то,
    // а не специально хочет несколько пресетов с одинаковым именем.
    public void SaveEqualizerPreset(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return;

        var gains = (double[])_settings.EqualizerBandGainsDb.Clone();
        var existing = _settings.EqualizerPresets.FirstOrDefault(p => p.Name == name);
        if (existing != null)
            existing.GainsDb = gains;
        else
            _settings.EqualizerPresets.Add(new EqualizerPreset { Name = name, GainsDb = gains });

        SettingsManager.Save(_settings);
    }

    // Применяет пресет к текущим настройкам эквалайзера — через SetEqualizerBandGain
    // по каждой полосе, чтобы (если сейчас что-то играет) звук изменился сразу же, живьём.
    public void ApplyEqualizerPreset(EqualizerPreset preset)
    {
        for (int band = 0; band < EqualizerSampleProvider.BandFrequencies.Length; band++)
            SetEqualizerBandGain(band, band < preset.GainsDb.Length ? preset.GainsDb[band] : 0);

        SettingsManager.Save(_settings);
    }

    public void DeleteEqualizerPreset(EqualizerPreset preset)
    {
        _settings.EqualizerPresets.Remove(preset);
        SettingsManager.Save(_settings);
    }

    // Экспорт пресета в отдельный .json-файл — тот же формат, что и сам пресет в settings.json
    // (см. EqualizerPreset в AppSettings.cs), поэтому файл можно просто переслать кому-то ещё
    // и импортировать обратно тем же методом ниже, без специального протокола обмена.
    public void ExportEqualizerPreset(EqualizerPreset preset, string filePath)
    {
        string json = JsonSerializer.Serialize(preset, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);
    }

    // Импортирует пресет из файла, ранее сохранённого через ExportEqualizerPreset (в том числе
    // присланного кем-то другим). Имя уже занято среди существующих пресетов — добавляет
    // суффикс " (2)", " (3)" и т.д., а не молча перезаписывает чужую настройку.
    // Возвращает null, если файл повреждён или не похож на пресет.
    public EqualizerPreset? ImportEqualizerPresetFromFile(string filePath)
    {
        const long maxPresetBytes = 512 * 1024;
        EqualizerPreset? preset;
        try
        {
            if (!File.Exists(filePath) || new FileInfo(filePath).Length > maxPresetBytes)
                return null;

            string json = File.ReadAllText(filePath);
            preset = JsonSerializer.Deserialize<EqualizerPreset>(json, new JsonSerializerOptions { MaxDepth = 8 });
        }
        catch
        {
            return null;
        }

        if (preset == null || string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > 200 ||
            preset.GainsDb is null || preset.GainsDb.Length == 0 || preset.GainsDb.Length > 32 ||
            preset.GainsDb.Any(g => !double.IsFinite(g) || g < -100 || g > 100))
            return null;

        string baseName = preset.Name.Trim();
        string name = baseName;
        int suffix = 2;
        while (_settings.EqualizerPresets.Any(p => p.Name == name))
            name = $"{baseName} ({suffix++})";
        preset.Name = name;

        _settings.EqualizerPresets.Add(preset);
        SettingsManager.Save(_settings);
        return preset;
    }

    // Переключение "Закрепить" / "Поверх окон" прямо из контекстного меню мини-плеера
    // (ПКМ по мини-плееру). Работает с теми же настройками, что и чекбоксы в окне настроек —
    // если оно сейчас открыто, подтягиваем в нём актуальное состояние, чтобы оба места
    // управления не разъезжались друг с другом.
    public void SetMiniPlayerPinned(bool pinned)
    {
        _settings.MiniPlayerPinned = pinned;
        _settingsWindow?.RefreshMiniPlayerToggles();
    }

    public void SetMiniPlayerTopmost(bool topmost)
    {
        _settings.MiniPlayerAlwaysOnTop = topmost;
        ApplyMiniPlayerTopmostLive(topmost);
        _settingsWindow?.RefreshMiniPlayerToggles();
    }

    // Вызывается из контекстного меню мини-плеера (ПКМ → слайдер "Прозрачность"), когда её
    // меняют прямо там, а не через окно настроек — та же роль, что и у SetMiniPlayerPinned/
    // SetMiniPlayerTopmost выше: сохранить настройку, применить её вживую и подтянуть значение
    // в окне настроек, если оно сейчас открыто, чтобы два места редактирования одной и той же
    // настройки не разъезжались друг с другом.
    public void SetMiniPlayerOpacity(double opacity)
    {
        _settings.MiniPlayerOpacity = opacity;
        _miniPlayerWindow?.ApplyOpacityLive();
        _settingsWindow?.RefreshMiniPlayerToggles();
    }

    // Вызывается из окна настроек сразу после того, как пользователь записал новую
    // комбинацию клавиш (или очистил старую) — применяет её без перезапуска приложения
    public void ReapplyHotkeys() => _mediaHotKeys?.ApplyCustomHotkeys(_settings);

    // ---------- Управление плеером извне (из MiniPlayerWindow) ----------

    public void ExternalPlayPause() => PlayPauseButton_Click(this, new RoutedEventArgs());
    public void ExternalNext() => PlayNextTrack();
    public void ExternalPrev() => PrevButton_Click(this, new RoutedEventArgs());
    public void ExternalChangeVolume(double delta) => ChangeVolumeBy(delta);
    public void ExternalToggleRepeat() => RepeatButton_Click(this, new RoutedEventArgs());
    public void ExternalToggleShuffle() => ShuffleButton_Click(this, new RoutedEventArgs());
    public void ExternalToggleMute() => ToggleMute();

    // Для "второй кнопки" мини-плеера в режиме "Избранное" (см. AppSettings.
    // MiniPlayerSecondaryButton и MiniPlayerWindow.SecondaryButton_Click) — тот же метод,
    // которым пользуется сердечко в обычном плейлисте (см. ToggleFavoriteAndRefresh), просто
    // путь к файлу берётся из того, что сейчас играет, а не из DataContext строки плейлиста.
    // Ничего не делает, если сейчас ничего не загружено.
    public void ExternalToggleFavoriteCurrentTrack()
    {
        if (_currentTrackPath != null) ToggleFavoriteAndRefresh(_currentTrackPath);
    }

    public void ExternalSeekRatio(double ratio)
    {
        if (_audioFile == null) return;

        var newTime = TimeSpan.FromSeconds(_audioFile.TotalTime.TotalSeconds * Math.Clamp(ratio, 0.0, 1.0));
        _audioFile.CurrentTime = newTime;
        ProgressSlider.Value = newTime.TotalSeconds;
        CurrentTimeText.Text = newTime.ToString(@"mm\:ss");
    }

    // ---------- Прогресс и перемотка ----------

    // ---------- Перетаскивание ползунков через прозрачный слой поверх Slider ----------
    // Сам Slider сделан IsHitTestVisible="False" — он только рисует трек и шарик.
    // Всю мышь обрабатывает прозрачный Border поверх него, поэтому неважно, куда именно
    // кликнули: в любую точку трека или прямо в шарик — перетаскивание продолжается плавно
    // на всём протяжении зажатой кнопки мыши, без конфликтов со внутренней логикой Thumb.

    private bool _isDraggingProgressOverlay;
    private bool _isDraggingVolumeOverlay;

    private void ProgressOverlay_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var overlay = (FrameworkElement)sender;
        overlay.CaptureMouse();
        _isDraggingProgressOverlay = true;
        _isUserInteractingWithProgress = true;
        ProgressSlider.Focus();
        UpdateSliderValueFromMouse(ProgressSlider, e.GetPosition(overlay).X, overlay.ActualWidth);
    }

    private void ProgressOverlay_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDraggingProgressOverlay) return;
        var overlay = (FrameworkElement)sender;
        UpdateSliderValueFromMouse(ProgressSlider, e.GetPosition(overlay).X, overlay.ActualWidth);
    }

    private void ProgressOverlay_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var overlay = (FrameworkElement)sender;
        overlay.ReleaseMouseCapture();
        _isDraggingProgressOverlay = false;
        _isUserInteractingWithProgress = false;
    }

    // Перемотка колесом мыши над прогресс-баром и хоткеями "перемотка вперёд"/"назад" (см.
    // подписку на _mediaHotKeys.SeekForwardPressed/SeekBackwardPressed) — общий шаг в 5 секунд,
    // общий код клампинга по границам трека и обновления UI.
    private void SeekBy(double seconds)
    {
        if (_audioFile == null) return;

        var newTime = _audioFile.CurrentTime + TimeSpan.FromSeconds(seconds);
        if (newTime < TimeSpan.Zero) newTime = TimeSpan.Zero;
        if (newTime > _audioFile.TotalTime) newTime = _audioFile.TotalTime;

        _audioFile.CurrentTime = newTime;
        ProgressSlider.Value = newTime.TotalSeconds;
        CurrentTimeText.Text = newTime.ToString(@"mm\:ss");
        RaiseProgressChanged(newTime.TotalSeconds, _audioFile.TotalTime.TotalSeconds);
    }

    // Прокрутка колесом мыши над прогресс-баром — перемотка с тем же шагом, что и хоткеи
    // "вперёд"/"назад" (5 секунд за одно деление). e.Delta положителен при прокрутке "от себя"
    // (вверх) — это и есть перемотка вперёд, по аналогии с VolumeRow_MouseWheel.
    private void ProgressOverlay_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (_audioFile == null) return;

        SeekBy(Math.Sign(e.Delta) * 5);
        e.Handled = true;
    }

    private void VolumeOverlay_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var overlay = (FrameworkElement)sender;
        overlay.CaptureMouse();
        _isDraggingVolumeOverlay = true;
        VolumeSlider.Focus();
        UpdateSliderValueFromMouse(VolumeSlider, e.GetPosition(overlay).X, overlay.ActualWidth);
    }

    private void VolumeOverlay_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDraggingVolumeOverlay) return;
        var overlay = (FrameworkElement)sender;
        UpdateSliderValueFromMouse(VolumeSlider, e.GetPosition(overlay).X, overlay.ActualWidth);
    }

    private void VolumeOverlay_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var overlay = (FrameworkElement)sender;
        overlay.ReleaseMouseCapture();
        _isDraggingVolumeOverlay = false;
    }

    private static void UpdateSliderValueFromMouse(System.Windows.Controls.Slider slider, double positionX, double width)
    {
        if (width <= 0) return;

        double ratio = Math.Clamp(positionX / width, 0.0, 1.0);
        slider.Value = slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
    }

    // Раз в ~10 секунд во время игры (таймер тикает каждые 250мс — 40 тиков) сохраняем
    // текущий трек/позицию на диск, а не только по паузе/сворачиванию в трей/закрытию — так
    // даже при аварийном завершении процесса (зависание, "снять задачу" и т.п.) позиция
    // потеряется не больше чем на несколько секунд, а не полностью, как раньше (см.
    // PersistPlaybackAndPlaylistState).
    private const int AutoSaveEveryNTicks = 40;
    private int _ticksSinceLastAutoSave;

    // Обновляет позицию ползунка прогресса под текущее время воспроизведения. Флагом
    // _isSyncingProgressFromPlayback управляет сама — без него ProgressSlider_ValueChanged
    // принял бы это присвоение за перемотку пользователем и дёргал бы _audioFile.CurrentTime.
    private void SetProgressSliderValue(double seconds)
    {
        _isSyncingProgressFromPlayback = true;
        ProgressSlider.Value = seconds;
        _isSyncingProgressFromPlayback = false;
    }

    private void StopProgressTimerAndAnimation()
    {
        _progressTimer.Stop();
        FlushPlaybackClock();
        _isSyncingProgressFromPlayback = false;
    }

    private void FlushPlaybackClock()
    {
        if (!_playbackClock.IsRunning) return;
        _playbackClock.Stop();
        _settings.TotalListenSeconds += _playbackClock.Elapsed.TotalSeconds;
        _playbackClock.Reset();
    }

    private void ProgressTimer_Tick(object? sender, EventArgs e)
    {
        // Пока пользователь держит ползунок нажатым (клик или перетаскивание) — не трогаем его
        // значение автоматически, иначе неточность перемотки в mp3/aac будет сбивать позицию
        // прямо во время движения, и ползунок будет "дёргаться".
        if (_audioFile == null || _isUserInteractingWithProgress) return;

        // SetProgressSliderValue меняет ProgressSlider.Value, что синхронно вызывает
        // ProgressSlider_ValueChanged — там уже обновляются CurrentTimeText, waveform progress
        // и синхронный текст (UpdateMainWindowSyncedLyrics) для той же позиции. Повторный вызов
        // здесь ранее дублировал расчёт активной LRC-строки и scroll-логику до 4 раз в секунду.
        SetProgressSliderValue(_audioFile.CurrentTime.TotalSeconds);

        RaiseProgressChanged(_audioFile.CurrentTime.TotalSeconds, _audioFile.TotalTime.TotalSeconds);

        // Статистика (см. StatisticsWindow) — суммарное время реального воспроизведения.
        // Таймер тикает только пока трек действительно играет (см. _progressTimer.Start/Stop
        // вокруг пауз), поэтому просто прибавляем длину тика — надёжнее, чем пытаться
        // вычислить это позже из длительностей файлов и счётчиков (перемотка/повторы и так
        // никак не искажают эту сумму, ведь она набирается по факту реального проигрывания).
        // DispatcherTimer используется только для отображения. Статистика начисляется через
        // Stopwatch в StopProgressTimerAndAnimation, поэтому задержки UI не искажают время.
        _settings.StatsStartedAt ??= DateTime.Now.ToString("O");

        // Прослушивание засчитывается не при старте трека, а только когда реально
        // воспроизведена как минимум половина композиции — иначе быстрое переключение между
        // треками (превью, случайный клик не по тому треку и т.п.) накручивало бы счётчик
        // прослушиваний ровно так же, как и полноценное прослушивание. Флаг на трек
        // выставляется один раз (см. сброс в LoadAndPlay) — дальнейшая перемотка туда-сюда
        // после набора половины повторный инкремент не даёт.
        if (!_halfPlayCounted && _audioFile.TotalTime.TotalSeconds > 0
            && _audioFile.CurrentTime.TotalSeconds >= _audioFile.TotalTime.TotalSeconds / 2.0)
        {
            _halfPlayCounted = true;
            if (_currentTrackPath != null)
                PlayCountManager.Increment(_currentTrackPath);
        }

        if (++_ticksSinceLastAutoSave >= AutoSaveEveryNTicks)
        {
            _ticksSinceLastAutoSave = 0;
            PersistPlaybackAndPlaylistState(asyncSave: true);
        }
    }

    private void ProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        CurrentTimeText.Text = TimeSpan.FromSeconds(e.NewValue).ToString(@"mm\:ss");

        // Общая точка для ЛЮБОГО изменения позиции — ручная перемотка, таймер прогресса или
        // SeekBy — поэтому проще синхронизировать сюда прогресс waveform-полосы один раз, чем
        // дублировать это же присваивание в каждом из тех мест по отдельности.
        ProgressWaveform.Progress = ProgressSlider.Maximum > 0 ? e.NewValue / ProgressSlider.Maximum : 0;
        UpdateMainWindowSyncedLyrics(TimeSpan.FromSeconds(e.NewValue));

        // Пропускаем seek, если это сам таймер обновил слайдер под текущую позицию воспроизведения —
        // иначе будет лишняя перемотка 4 раза в секунду даже когда никто не трогает ползунок
        if (_isSyncingProgressFromPlayback) return;

        // Во всех остальных случаях — клик в любую точку трека, перетаскивание ползунка
        // или стрелки клавиатуры — сразу перематываем воспроизведение, точно как громкость
        if (_audioFile != null)
            _audioFile.CurrentTime = TimeSpan.FromSeconds(e.NewValue);
    }

    // ---------- Громкость ----------

    // Плавно меняет громкость на заданный шаг (используется хоткеями увеличения/уменьшения
    // громкости) — просто двигает тот же VolumeSlider, поэтому вся остальная логика
    // (сохранение в настройки, обновление подписи процентов) срабатывает как обычно.
    private void ChangeVolumeBy(double delta)
    {
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, VolumeSlider.Minimum, VolumeSlider.Maximum);
    }

    // Переводит положение ползунка (0..1, линейное) в множитель амплитуды. Выключено — как и
    // раньше, множитель совпадает с положением один в один. Включено — ползунок сначала
    // переводится в децибелы [MinDb, 0], потом в множитель амплитуды (10^(dB/20)), чтобы
    // движение ползунка воспринималось на слух равномерно, а не сжато в нижние 10-20% хода.
    private const double MinVolumeDb = -40.0; // тише практически не слышно — дальше просто тишина

    private float ToOutputVolume(double sliderValue)
    {
        sliderValue = Math.Clamp(sliderValue, 0.0, 1.0);

        if (!_settings.UseLogarithmicVolume)
            return (float)sliderValue;

        if (sliderValue <= 0.0) return 0f;

        double db = MinVolumeDb * (1.0 - sliderValue);
        double raw = Math.Pow(10.0, db / 20.0);

        // 10^(dB/20) при sliderValue → 0 стремится не к 0, а к "полу" в 10^(MinVolumeDb/20) —
        // без этой перенормировки последний отрезок хода ползунка перед нулём давал резкий
        // скачок к тишине вместо плавного затухания.
        double floor = Math.Pow(10.0, MinVolumeDb / 20.0);
        return (float)((raw - floor) / (1.0 - floor));
    }

    public void RefreshVolumeCurve()
    {
        if (_audioFile != null)
            _audioFile.Volume = ComputeAudioFileVolume(VolumeSlider.Value);
    }

    // Домножает обычную громкость (см. ToOutputVolume) на _replayGainFactor — то же место
    // конвейера (AudioFileReader.Volume, до эквалайзера).
    private float ComputeAudioFileVolume(double sliderValue) => ComputeAudioFileVolume(sliderValue, _replayGainFactor);

    private float ComputeAudioFileVolume(double sliderValue, double replayGainFactor) =>
        (float)(ToOutputVolume(sliderValue) * replayGainFactor);

    public void RefreshReplayGain()
    {
        var previous = Interlocked.Exchange(ref _replayGainCts, null);
        previous?.Cancel();

        string? path = _currentTrackPath;
        if (!_settings.ReplayGainEnabled || path == null)
        {
            _replayGainFactor = 1.0;
            if (_audioFile != null)
                _audioFile.Volume = ComputeAudioFileVolume(VolumeSlider.Value);
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _replayGainCts = cts;
        int generation = Volatile.Read(ref _trackLoadGeneration);
        FireAndForget(RefreshReplayGainAsync(path, generation, cts), "RefreshReplayGainAsync");
    }

    private async Task RefreshReplayGainAsync(string path, int generation, CancellationTokenSource cts)
    {
        try
        {
            double gain = await Task.Run(() => ReplayGainReader.GetTrackGainLinear(path), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (_isExiting || generation != Volatile.Read(ref _trackLoadGeneration) ||
                !string.Equals(path, _currentTrackPath, StringComparison.Ordinal)) return;

            _replayGainFactor = gain;
            if (_audioFile != null)
                _audioFile.Volume = ComputeAudioFileVolume(VolumeSlider.Value);
        }
        catch (OperationCanceledException)
        {
            // Новая загрузка, настройка или shutdown отменили устаревший расчёт.
        }
        catch (Exception ex)
        {
            Logger.Error($"Не удалось обновить ReplayGain для файла: {path}", ex);
        }
        finally
        {
            if (ReferenceEquals(_replayGainCts, cts)) _replayGainCts = null;
            cts.Dispose();
        }
    }


    // Одно деление колеса = 5%, как и хоткеи громкости. e.Delta положителен при прокрутке "от себя".
    private void VolumeRow_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        ChangeVolumeBy(Math.Sign(e.Delta) * 0.02);
        e.Handled = true;
    }

    private void ToggleMute()
    {
        if (VolumeSlider.Value > 0)
        {
            _lastNonZeroVolume = VolumeSlider.Value;
            VolumeSlider.Value = 0;
        }
        else
        {
            VolumeSlider.Value = _lastNonZeroVolume > 0 ? _lastNonZeroVolume : 0.3;
        }
    }

    private static string FormatPlaybackRate(double value) => $"{value:0.00}×";

    private void PlaybackRateSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // ValueChanged вызывается во время InitializeComponent для XAML Value=1.0.
        // Пока runtime-state не загружен из settings.json, это событие игнорируется.
        if (!_playbackRateIsReady || _isUpdatingPlaybackRateControl) return;
        SetPlaybackRate(e.NewValue, persist: true);
        ApplyPlaybackRateToCurrentStream();
    }

    private static string FormatPlaybackPitch(double semitones) =>
        $"{semitones:+0;-0;0} st";

    private void PlaybackRateSlider_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        SetPlaybackRate(1.0, persist: true);
        e.Handled = true;
    }

    private void PlaybackPitchSlider_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        PlaybackPitchSlider.Value = 0.0;
        e.Handled = true;
    }

    private void PlaybackPitchSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PlaybackPitchValueText != null)
            PlaybackPitchValueText.Text = FormatPlaybackPitch(e.NewValue);
        ApplyPlaybackPitchLive(e.NewValue);
        PersistPlaybackSettingsAfterUserChange();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_audioFile != null)
            _audioFile.Volume = ComputeAudioFileVolume(e.NewValue);

        if (VolumeValueText != null)
            VolumeValueText.Text = $"{(int)Math.Round(e.NewValue * 100)}%";

        if (e.NewValue > 0)
            _lastNonZeroVolume = e.NewValue;

        if (SpeakerIcon != null)
        {
            SpeakerIcon.Icon = e.NewValue <= 0.0 ? "IconSpeakerMute" : "IconSpeaker";
        }

        VolumeChanged?.Invoke(e.NewValue);
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e) => ToggleMute();

    private void PlaybackRatePersistenceTimer_Tick(object? sender, EventArgs e)
    {
        if (_isApplyingStartupSettings || _isExiting || PlaybackRateSlider == null) return;

        double sliderValue = Math.Round(Math.Clamp(PlaybackRateSlider.Value, 0.5, 2.0), 2);
        if (Math.Abs(sliderValue - _runtimePlaybackRate) <= 0.0001) return;

        // Watcher страхует случай, когда визуальный Slider изменился, но ValueChanged не
        // дошёл до setter из-за особенностей Popup/мыши. Источником состояния остаётся setter.
        SetPlaybackRate(sliderValue, persist: false);
        SettingsManager.Save(_settings);
    }

    private void PersistPlaybackSettingsAfterUserChange()
    {
        if (_isApplyingStartupSettings || _isOpeningPlaybackControlPopup || _isExiting) return;
        _settings.PlaybackSpeed = _runtimePlaybackRate;
        FireAndForget(SettingsManager.SaveAsync(_settings), "SavePlaybackSettingsAsync");
    }

    private const double PlaybackRateWheelStep = 0.05;
    private const double PlaybackPitchWheelStep = 1.0;

    private void ChangePlaybackRateBy(double delta)
    {
        SetPlaybackRate(_runtimePlaybackRate + delta, persist: true);
    }

    private void OpenPlaybackControlPopup()
    {
        if (PlaybackControlPopup.IsOpen) return;

        _isOpeningPlaybackControlPopup = true;
        PlaybackControlPopup.IsOpen = true;
    }

    private void PlaybackControlPopup_Closed(object? sender, EventArgs e)
    {
        if (_isExiting || _isApplyingStartupSettings) return;
        SetPlaybackRate(PlaybackRateSlider.Value, persist: true);
        _settings.PlaybackPitchSemitones = Math.Clamp(PlaybackPitchSlider.Value, -12.0, 12.0);
        SettingsManager.Save(_settings);
    }

    private void PlaybackControlPopup_Opened(object? sender, EventArgs e)
    {
        try
        {
            SetPlaybackRate(_runtimePlaybackRate, persist: false);
            PlaybackPitchSlider.Value = Math.Clamp(_settings.PlaybackPitchSemitones, -12.0, 12.0);
            PlaybackPitchValueText.Text = FormatPlaybackPitch(PlaybackPitchSlider.Value);
            ApplyPlaybackPitchLive(PlaybackPitchSlider.Value);
        }
        finally
        {
            _isOpeningPlaybackControlPopup = false;
            PlaybackRateSlider.Focus();
        }
    }

    private void PlaybackControlButton_Click(object sender, RoutedEventArgs e)
    {
        if (PlaybackControlPopup.IsOpen)
            PlaybackControlPopup.IsOpen = false;
        else
            OpenPlaybackControlPopup();
        e.Handled = true;
    }

    private void PlaybackControlButton_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        OpenPlaybackControlPopup();
        ChangePlaybackRateBy(Math.Sign(e.Delta) * PlaybackRateWheelStep);
        e.Handled = true;
    }

    private void PlaybackRateSlider_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        ChangePlaybackRateBy(Math.Sign(e.Delta) * PlaybackRateWheelStep);
        e.Handled = true;
    }

    private void PlaybackPitchSlider_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        PlaybackPitchSlider.Value = Math.Clamp(
            PlaybackPitchSlider.Value + Math.Sign(e.Delta) * PlaybackPitchWheelStep,
            PlaybackPitchSlider.Minimum,
            PlaybackPitchSlider.Maximum);
        e.Handled = true;
    }

    // Живо переприменяет только тему/акцент/подложку после импорта .lumi-профиля или сброса
    // настроек — остальное (хоткеи, эквалайзер, трей, мини-плеер) читается только при старте
    // соответствующих подсистем, поэтому SettingsWindow в обоих случаях дополнительно
    // предлагает перезапустить плеер.
    // Обновляет Rich Presence единым снимком аудиосостояния. Полная длительность и позиция
    // берутся только из AudioFileReader, поэтому Discord не зависит от текстовых полей UI.
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

    public void ApplyImportedSettingsLive()
    {
        ApplicationThemeManager.Apply(_settings.IsLightThemeResolved() ? ApplicationTheme.Light : ApplicationTheme.Dark);
        ApplyAccentColor();
        ApplyWindowBackdrop();
        _miniPlayerWindow?.ApplyArtworkProgressVisibility();
        _miniPlayerWindow?.ApplyArtworkProgressColor();
        _miniPlayerWindow?.ApplyArtworkStyle();
        ApplyDiscordRichPresenceSettingsLive();
        TrackContextMenuActions.Instance.Initialize(_settings.DisabledTrackContextMenuActions);
        ApplyPlaybackRateLive(_settings.PlaybackSpeed);
        ApplyPlaybackPitchLive(_settings.PlaybackPitchSemitones);
    }

    // Раньше вызывалась только из OnClosed — а поскольку MinimizeToTrayOnClose включён по
    // умолчанию, обычное закрытие крестиком просто прячет окно в трей, и до настоящего
    // "Выход" могло не доходить месяцами. Теперь дополнительно вызывается при сворачивании в
    // трей, на паузе и периодически во время игры.
    private void PersistPlaybackAndPlaylistState(bool asyncSave = false)
    {
        // До завершения RestoreSavedPlaylistAsync часть runtime-полей ещё содержит XAML-значения
        // (текущий трек, режим окна и т.п.). Не только плейлист, но и любые такие поля нельзя
        // записывать поверх settings.json после неудачного или прерванного старта.
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
        _settings.IsShuffleEnabled = _isShuffleEnabled;
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
        // OnClosed означает, что окно действительно закрывается насовсем (в отличие от
        // OnClosing, где закрытие ещё можно было заменить сворачиванием в трей) — на всякий
        // случай выставляем здесь и так, чтобы Closed-обработчик ShowChangelogWindow ниже
        // точно не попытался открыть окно настроек заново посреди выключения программы.
        // Timer должен быть остановлен до финального Save, чтобы он не начал новую запись
        // параллельно с закрытием окна.
        _playbackRatePersistenceTimer.Stop();
        _settingsCheckpointTimer.Stop();
        _playlistSearchDebounceTimer.Stop();
        StopHotkeyTrackRepeat();
        _playlistSearchCts?.Cancel();
        _settings.PlaybackSpeed = _runtimePlaybackRate;
        _isExiting = true;
        StopFolderWatchers();
        LocalizationService.LanguageChanged -= LocalizationService_LanguageChanged;
        _lifetimeCts.Cancel();
        _trackLoadCts?.Cancel();
        _replayGainCts?.Cancel();
        _waveformCts?.Cancel();
        CancelMainWindowLyricsLoad();

        // Сохраняем состояние ДО остановки — StopPlayback ниже обнуляет _audioFile, а
        // PersistPlaybackAndPlaylistState читает текущую позицию именно из него.
        FlushPlaybackClock();
        PersistPlaybackAndPlaylistState();
        StopPlayback(disposeOnly: true);

        // Настоящая, финальная утилизация устройства вывода — единственное место, где это
        // вообще происходит (см. подробный комментарий у поля _outputDevice в начале файла:
        // между треками StopPlayback теперь только останавливает его, не уничтожая).
        DisposeOutputDeviceSafely();
        _discordRichPresence.Dispose();

        _mediaHotKeys?.Dispose();
        _nowPlaying?.Dispose();
        _trayIconManager?.Dispose();
        _miniPlayerWindow?.Close();
        _settingsWindow?.Close();
        _statisticsWindow?.Close();
        _trackChangeToastController.Dispose();
        _changelogWindow?.Close();
        _coverArtWindow?.Close();
        _nowPlayingWindow?.Close();
        // Track-load, ReplayGain и waveform tasks владеют своими CTS и освобождают их
        // в собственных finally-блоках после отмены. Не Dispose здесь, пока task ещё может
        // обращаться к TokenSource.
        _lifetimeCts.Dispose();

        base.OnClosed(e);
    }
}
