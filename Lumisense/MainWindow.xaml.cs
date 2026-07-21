using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.Wave;
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

    // Поддерживаемые расширения — используются при сканировании папок
    private static readonly string[] SupportedExtensions = { ".mp3", ".wav", ".wma", ".flac", ".m4a", ".aac", ".ogg" };

    private const string LooseFilesDisplayName = "Отдельные файлы";

    // AudioFileReader умеет читать mp3/wav/wma и сразу даёт регулировку громкости
    private AudioFileReader? _audioFile;
    private WaveOutEvent? _outputDevice;

    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    // Плейлист теперь хранится как список групп (папка целиком или отдельные файлы).
    // Каждую группу можно включать/выключать — выключенные пропускаются при воспроизведении.
    private readonly List<PlaylistFolder> _folders = new();

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
    private bool _isUserInteractingWithProgress;
    private bool _isSyncingProgressFromPlayback;
    private bool _isPlaying;
    private bool _isShuffleEnabled;

    // История треков, сыгранных в режиме шафла: "Вперёд" на новом месте генерирует
    // случайный трек и дописывает его в конец, а "Назад" не генерирует ничего нового,
    // а просто возвращается на шаг назад по этому списку (как в браузере) — иначе
    // "назад" в шафле оказывалось таким же случайным выбором, как и "вперёд", и не давало
    // вернуться к реально предыдущему треку.
    private readonly List<string> _shuffleHistory = new();
    private int _shuffleHistoryIndex = -1;
    private bool _isMiniMode;
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
    private MiniPlayerWindow? _miniPlayerWindow;
    private SettingsWindow? _settingsWindow;
    private CoverArtWindow? _coverArtWindow;
    private bool _isExiting;

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

    // Тоже только для мини-плеера — у него теперь своя кнопка повтора (см.
    // MiniPlayerWindow.RepeatButton_Click), и её вид должен оставаться в синхроне с основным
    // окном, чем бы режим ни переключили: этой кнопкой, кнопкой в основном окне или хоткеем.
    public event Action<string>? RepeatModeChanged;

    // Отдельно от VolumeSlider_ValueChanged (который дёргается и при загрузке сохранённой
    // громкости на старте) — только для мини-плеера, который показывает всплывающий
    // индикатор процентов при изменении громкости хоткеями/скроллом. Аргумент — итоговая
    // громкость 0..1, как в VolumeSlider.Value.
    public event Action<double>? VolumeChanged;

    public bool IsMiniMode => _isMiniMode;
    public AppSettings Settings => _settings;
    public string CurrentTitle => TrackTitleText.Text;
    public string CurrentArtist => TrackArtistText.Text;
    public Brush? CurrentArtBrush => AlbumArtIcon.Visibility == Visibility.Visible ? null : AlbumArtBorder.Background;
    public bool IsPlayingNow => _isPlaying;

    // Для мини-плеера — узнать текущий режим повтора сразу при открытии, до первого события
    // RepeatModeChanged (тем же способом, каким мини-плеер узнаёт текущий трек/состояние
    // воспроизведения при своём создании — см. конструктор MiniPlayerWindow).
    public string CurrentRepeatModeName => _repeatMode.ToString();

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

    public MainWindow()
    {
        InitializeComponent();
        IconResources.SetOnAccent(PlayPauseIcon, true);
        FavoritesManager.Initialize(_settings.FavoriteTracks);
        _progressTimer.Tick += ProgressTimer_Tick;
        ApplySettingsOnStartup();

        // Не await — намеренно "запустили и забыли": файловая проверка треков и загрузка
        // последнего трека идут в фоне, окно тем временем показывается сразу, без ожидания
        // (см. подробный комментарий над RestoreSavedPlaylistAsync).
        _ = StartupRestoreAndRescanAsync();

        StateChanged += MainWindow_StateChanged;
        SizeChanged += MainWindow_SizeChanged;

        // Подстраховка для завершения сеанса Windows (выключение/перезагрузка/выход из
        // системы) — в этот момент OnClosing/OnClosed могут не успеть отработать штатно, а
        // сворачивание в трей само по себе новых сохранений после первого раза не вызывает.
        // Без этого позиция трека, начатого прямо перед выключением компьютера, терялась бы
        // до следующего периодического автосохранения (см. ProgressTimer_Tick).
        System.Windows.Application.Current.SessionEnding += (_, _) => PersistPlaybackAndPlaylistState();
    }

    // ---------- Полноэкранный режим ----------
    // Срабатывает при разворачивании окна кнопкой "Развернуть" в заголовке (или двойным
    // кликом по заголовку/системными средствами) — в обоих случаях WindowState становится
    // Maximized, и это единственное, что нам нужно отследить.
    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        var fullscreen = WindowState == WindowState.Maximized;
        if (fullscreen == _isFullscreenLayout) return;

        _isFullscreenLayout = fullscreen;
        ApplyFullscreenLayout(fullscreen);
    }

    // Монитор пользователя может быть любого размера — пересчитываем ширину ContentHost и
    // при изменении размеров развёрнутого окна (например, при переносе на другой экран), а
    // также при ручном изменении размера окна в квадратном виде — иначе после растягивания
    // такого окна мышью контент снова остался бы прежней узкой ширины с пустыми полями
    // по бокам.
    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isFullscreenLayout || _viewMode == PlayerViewMode.Square) UpdateContentMaxWidth();
    }

    // Подстраивает интерфейс под полноэкранный режим: крупнее обложка, шрифты и кнопки
    // управления, шире (но по-прежнему по центру, а не "простынёй" на весь монитор) рабочая
    // область — вместо прежней узкой колонки шириной с обычное окно 440px. При возврате в
    // обычный режим (WindowState.Normal) все размеры возвращаются как было — если только
    // в этот момент не активен квадратный вид плеера, у которого тот же крупный стиль
    // (см. ApplyContentScale и PlayerViewMode.Square в SetPlayerViewMode).
    private void ApplyFullscreenLayout(bool fullscreen)
    {
        UpdateContentMaxWidth();
        ApplyContentScale(fullscreen || _viewMode == PlayerViewMode.Square);
    }

    // Крупный ("as fullscreen") или обычный размер элементов интерфейса — обложка, шрифты,
    // кнопки управления. Вынесено из ApplyFullscreenLayout в отдельный метод, потому что тот
    // же крупный стиль используется и квадратным видом плеера (PlayerViewMode.Square,
    // см. SetPlayerViewMode) — оба варианта выглядят одинаково просторно, разница только в
    // том, что полноэкранный занимает весь монитор, а квадратный — увеличенное окно.
    private void ApplyContentScale(bool big)
    {
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

    // ContentHost.MaxWidth считается от реальной ширины развёрнутого окна (а значит и
    // монитора), а не жёстко зашитым числом — на широком мониторе интерфейс станет шире,
    // на небольшом ноутбучном экране не станет неоправданно огромным. В квадратном виде
    // плеера (не полноэкранном) окно само по себе шире обычного (см. SquareMinHeightWithPlaylist
    // в SetPlayerViewMode) — раньше здесь стоял фиксированный SquareContentMaxWidth (560),
    // из-за чего по бокам оставались пустые поля шире самого плейлиста. Теперь предел
    // считается от фактической ширины окна, а SquareContentMaxWidth остался лишь нижней
    // границей на случай, если окно квадратного вида по какой-то причине окажется уже неё.
    private void UpdateContentMaxWidth()
    {
        ContentHost.MaxWidth = _isFullscreenLayout
            ? Math.Clamp(ActualWidth * 0.55, NormalContentMaxWidth, 760)
            : _viewMode == PlayerViewMode.Square
                ? Math.Clamp(Width - 40, SquareContentMaxWidth, 900)
                : NormalContentMaxWidth;
    }

    // Единственная точка входа для первого показа окна — вызывается один раз из
    // App.OnStartup вместо автоматического Show() через StartupUri (см. комментарий там же).
    //
    // 1) EnsureHandle() создаёт нативный HWND без показа окна пользователю — от него зависят
    //    глобальные хоткеи, интеграция с Now Playing и иконка в трее (настраиваются в
    //    OnSourceInitialized, который EnsureHandle и вызывает), а получить их можно и не
    //    показывая окно.
    // 2) RestorePlayerViewMode() решает, с какого вида запускаться, и подгоняет размеры/режим
    //    (в т.ч. может целиком свернуть в мини-плеер и создать MiniPlayerWindow).
    // 3) Show() на самом MainWindow вызывается ТОЛЬКО если по итогу не мини-режим — если же
    //    последним был мини-плеер, Show() здесь не вызывается вообще, и окно ни на мгновение
    //    не появляется на экране.
    public void StartupPresent()
    {
        new WindowInteropHelper(this).EnsureHandle();

        RestorePlayerViewMode();

        if (!_isMiniMode) Show();

        _ = CheckForUpdatesOnStartupAsync();
    }

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
            await System.Threading.Tasks.Task.Delay(System.TimeSpan.FromSeconds(3));

            var result = await UpdateChecker.CheckAsync();
            if (result.Status != UpdateCheckStatus.UpdateAvailable) return;
            if (result.LatestVersion != null && result.LatestVersion == _settings.SkippedUpdateVersion) return;

            var dialog = new UpdateAvailableWindow(result, _settings);
            // Owner можно ставить только на уже показанное окно (актуально, если старт был в
            // мини-режиме — см. StartupPresent, тогда IsVisible всё ещё false).
            if (IsVisible) dialog.Owner = this;
            dialog.ShowDialog();
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
            // Вид плеера ещё не сохранялся, но settings.json уже существует — это версия
            // плеера до появления этой настройки. Раньше "скрытый плейлист" не означал
            // увеличенный квадратный стиль (эта настройка появилась только сейчас), поэтому
            // не подменяем его новым квадратным видом, а просто открываем прямоугольный вид
            // и восстанавливаем видимость плейлиста отдельно, как и раньше.
            startupMode = _settings.WasMiniPlayerOnClose ? PlayerViewMode.Mini : PlayerViewMode.Rectangular;
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
    private List<string> FlattenAll() =>
        _isFavoritesView ? FavoritesManager.GetAll() : _folders.SelectMany(f => f.Tracks).ToList();

    private List<string> FlattenActive() =>
        _isFavoritesView ? FavoritesManager.GetAll() : _folders.Where(f => f.IsEnabled).SelectMany(f => f.Tracks).ToList();

    private string? GetCurrentTrackPath() => _currentTrackPath;

    // Восстанавливает плейлист, сохранённый при прошлом закрытии приложения, а также
    // последний проигранный трек и позицию в нём (без автозапуска воспроизведения).
    // Файлы, которые с тех пор были удалены/перемещены, просто пропускаются.
    // Раньше это делалось СИНХРОННО прямо в конструкторе, то есть ДО того, как окно вообще
    // успевало появиться на экране — а File.Exists по каждому треку сохранённого плейлиста
    // (это реальное обращение к диску на каждый файл) при плейлисте в сотни-тысячи треков,
    // особенно на HDD или сетевом пути, вполне может суммарно занять несколько секунд. Плюс
    // следом ещё и LoadAndPlay последнего трека — открытие аудиофайла, чтение тегов/обложки.
    // Именно это и было основной причиной долгого "чёрного экрана" при запуске плеера.
    //
    // Теперь сканирование File.Exists уходит в пул потоков через Task.Run, а окно тем временем
    // уже показано и отзывчиво — плейлист и последний трек просто появляются в нём чуть позже,
    // как только фоновая проверка закончится (обычно доли секунды, редко больше).
    private async System.Threading.Tasks.Task RestoreSavedPlaylistAsync()
    {
        if (_settings.SavedPlaylistFolders.Count == 0) return;

        var restoredFolders = await System.Threading.Tasks.Task.Run(() =>
        {
            var result = new List<PlaylistFolder>();

            foreach (var saved in _settings.SavedPlaylistFolders)
            {
                var existingTracks = saved.Tracks.Where(File.Exists).ToList();

                // Пропускаем группу, только если в ней ДЕЙСТВИТЕЛЬНО были файлы, а теперь их не
                // осталось (все удалены/перемещены) — а не любую группу с нулём треков. Иначе
                // ручная папка, созданная вручную и ещё не наполненная файлами до закрытия
                // приложения, терялась бы при следующем запуске.
                if (saved.Tracks.Count > 0 && existingTracks.Count == 0) continue;

                var folder = new PlaylistFolder
                {
                    SourcePath = saved.SourcePath,
                    DisplayName = saved.DisplayName,
                    IsEnabled = saved.IsEnabled,
                    IsLooseFilesBucket = saved.IsLooseFilesBucket,
                    IsExpanded = saved.IsExpanded
                };
                folder.Tracks.AddRange(existingTracks);
                result.Add(folder);
            }

            return result;
        });

        // Дальше — снова в UI-потоке (обычное поведение await в WPF): трогаем _folders,
        // элементы окна и т.п.
        foreach (var folder in restoredFolders) _folders.Add(folder);

        if (_folders.Count == 0) return;
        RefreshPlaylistView();

        if (string.IsNullOrEmpty(_settings.LastTrackPath)) return;

        var all = FlattenAll();
        if (!all.Contains(_settings.LastTrackPath)) return;

        LoadAndPlay(_settings.LastTrackPath, autoPlay: false,
            startPosition: TimeSpan.FromSeconds(Math.Max(_settings.LastPositionSeconds, 0)));
    }

    // Оборачивает восстановление плейлиста и последующую тихую проверку папок на новые треки
    // в одну задачу — именно её (а не RestoreSavedPlaylistAsync напрямую) запускает конструктор.
    // Порядок важен: сканировать папки на новые файлы имеет смысл только после того, как сам
    // список папок восстановлен из настроек.
    private async System.Threading.Tasks.Task StartupRestoreAndRescanAsync()
    {
        await RestoreSavedPlaylistAsync();
        await RescanAllFoldersForNewTracksAsync();
    }

    // Тихая фоновая проверка всех папок плейлиста, реально привязанных к папке на диске (то
    // есть у которых SourcePath не null — не "Отдельные файлы" и не созданные вручную "Новую
    // папку…"), на новые треки, появившиеся там уже ПОСЛЕ того, как папка была добавлена в
    // плейлист (докинули в неё файлов, пока плеер был закрыт, и т.п.).
    //
    // Запускается один раз при старте, сразу следом за восстановлением плейлиста, а также по
    // кнопке "Проверить папку на новые треки" в заголовке конкретной группы (см.
    // RescanFolderButton_Click — там переиспользуется тот же самый сканер через AddFolderPath,
    // просто для одной папки и синхронно, раз это явное разовое действие пользователя, а не
    // фоновая проверка при старте).
    //
    // Каждая папка сканируется в фоновом потоке (Task.Run) — диск, особенно сетевой или
    // большая библиотека, может отвечать не мгновенно, а UI-поток трогать этим не хочется
    // (см. ровно то же обоснование у RestoreSavedPlaylistAsync). Никаких диалогов/уведомлений
    // при старте не показываем — новые треки просто тихо появляются в списке, если найдутся.
    private async System.Threading.Tasks.Task RescanAllFoldersForNewTracksAsync()
    {
        // Снимок на момент запуска: если пользователь успеет что-то поменять в плейлисте прямо
        // во время фоновой проверки (удалить папку и т.п.), не идём по уже несуществующим.
        var foldersToCheck = _folders.Where(f => f.SourcePath != null).ToList();

        foreach (var folder in foldersToCheck)
        {
            if (!_folders.Contains(folder)) continue; // могли успеть удалить, пока проверяли предыдущую

            string sourcePath = folder.SourcePath!;
            List<string>? newTracks;

            try
            {
                newTracks = await System.Threading.Tasks.Task.Run(() =>
                    Directory.EnumerateFiles(sourcePath, "*.*", SearchOption.AllDirectories)
                        .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                        .Where(f => !folder.Tracks.Contains(f))
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                        .ToList());
            }
            catch
            {
                // Папка стала недоступна (отключили флешку/сетевой диск, удалили с диска и
                // т.п.) — это не ошибка уровня "нужно показать пользователю", просто пропускаем
                continue;
            }

            if (newTracks.Count == 0 || !_folders.Contains(folder)) continue;

            folder.Tracks.AddRange(newTracks);
            RefreshPlaylistView();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Глобальные медиаклавиши (Play/Pause, Next, Prev, Stop) — работают даже без фокуса на окне
        _mediaHotKeys = new GlobalMediaHotKeys(this);
        _mediaHotKeys.PlayPausePressed += () => Dispatcher.Invoke(() => PlayPauseButton_Click(this, new RoutedEventArgs()));
        _mediaHotKeys.NextPressed += () => Dispatcher.Invoke(PlayNextTrack);
        _mediaHotKeys.PreviousPressed += () => Dispatcher.Invoke(() => PrevButton_Click(this, new RoutedEventArgs()));
        _mediaHotKeys.StopPressed += () => Dispatcher.Invoke(() => StopButton_Click(this, new RoutedEventArgs()));
        _mediaHotKeys.VolumeUpPressed += () => Dispatcher.Invoke(() => ChangeVolumeBy(0.02));
        _mediaHotKeys.VolumeDownPressed += () => Dispatcher.Invoke(() => ChangeVolumeBy(-0.02));
        _mediaHotKeys.MutePressed += () => Dispatcher.Invoke(ToggleMute);
        _mediaHotKeys.ShufflePressed += () => Dispatcher.Invoke(() => ShuffleButton_Click(this, new RoutedEventArgs()));
        _mediaHotKeys.RepeatPressed += () => Dispatcher.Invoke(() => RepeatButton_Click(this, new RoutedEventArgs()));
        _mediaHotKeys.DeleteTrackPressed += () => Dispatcher.Invoke(DeleteCurrentTrackFromDiskHotkey);
        _mediaHotKeys.ApplyCustomHotkeys(_settings);

        // Интеграция с Now Playing Windows 11 (панель задач, блокировка экрана, наушники с кнопками)
        try
        {
            _nowPlaying = new NowPlayingIntegration(this);
            _nowPlaying.PlayRequested += () => Dispatcher.Invoke(() =>
            {
                if (!_isPlaying) PlayPauseButton_Click(this, new RoutedEventArgs());
            });
            _nowPlaying.PauseRequested += () => Dispatcher.Invoke(() =>
            {
                if (_isPlaying) PlayPauseButton_Click(this, new RoutedEventArgs());
            });
            _nowPlaying.NextRequested += () => Dispatcher.Invoke(PlayNextTrack);
            _nowPlaying.PreviousRequested += () => Dispatcher.Invoke(() => PrevButton_Click(this, new RoutedEventArgs()));
            _nowPlaying.StopRequested += () => Dispatcher.Invoke(() => StopButton_Click(this, new RoutedEventArgs()));
        }
        catch
        {
            // SMTC недоступен в некоторых окружениях (например, без нужного Windows SDK
            // на машине сборки) — в этом случае просто отключаем интеграцию, плеер работает дальше
            _nowPlaying = null;
        }

        // Системный трей
        _trayIconManager = new TrayIconManager();
        _trayIconManager.OpenRequested += RestoreFromTray;
        _trayIconManager.ExitRequested += ExitApplicationCompletely;
        _trayIconManager.PlayPauseRequested += () => Dispatcher.Invoke(() => PlayPauseButton_Click(this, new RoutedEventArgs()));
        _trayIconManager.NextRequested += () => Dispatcher.Invoke(PlayNextTrack);
        _trayIconManager.PreviousRequested += () => Dispatcher.Invoke(() => PrevButton_Click(this, new RoutedEventArgs()));
        PlaybackStateChanged += isPlaying => _trayIconManager?.SetPlayingState(isPlaying);
        TrackInfoChanged += (title, artist, _) => _trayIconManager?.SetNowPlayingText(title, artist);
        _trayIconManager.SetPlayingState(_isPlaying);
        _trayIconManager.ApplyTheme(isLight: _settings.Theme == "Light");
    }

    /// <summary>Перекрашивает меню трея под текущую тему — вызывается из SettingsWindow при
    /// переключении темы плеера. WinForms-меню трея живёт в отдельном UI-стеке и не подхватывает
    /// Fluent-тему WPF-UI автоматически, поэтому без явного вызова осталось бы в прежней палитре
    /// после переключения темы в настройках.</summary>
    public void ApplyTrayTheme(bool isLight) => _trayIconManager?.ApplyTheme(isLight);

    private void RestoreFromTray()
    {
        Dispatcher.Invoke(() =>
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
            Activate();
            _trayIconManager?.Hide();
        });
    }

    private void ExitApplicationCompletely()
    {
        Dispatcher.Invoke(() =>
        {
            _isExiting = true;
            Close();
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

            // Сворачивание в трей раньше вообще ничего не сохраняло — settings.json обновлялся
            // только в OnClosed, то есть только при настоящем "Выход" из трея. А поскольку
            // MinimizeToTrayOnClose включён по умолчанию, обычное закрытие крестиком почти
            // всегда идёт именно сюда: пользователь месяцами мог ни разу не заходить в трей за
            // "Выход" (просто выключал компьютер, пока плеер тихо играл в фоне) — и плеер при
            // следующем запуске каждый раз открывал один и тот же трек с той самой первой
            // позиции, что сохранилась при самом первом настоящем закрытии. См. подробности у
            // PersistPlaybackAndPlaylistState.
            PersistPlaybackAndPlaylistState();
            return;
        }

        base.OnClosing(e);
    }

    private void ApplySettingsOnStartup()
    {
        ApplicationThemeManager.Apply(_settings.Theme == "Light" ? ApplicationTheme.Light : ApplicationTheme.Dark);

        if (_settings.AlwaysOnTop)
            Topmost = true;

        if (_settings.RememberVolume)
            VolumeSlider.Value = Math.Clamp(_settings.SavedVolume, 0.0, 1.0);

        SetShuffleEnabled(_settings.IsShuffleEnabled);
        SetRepeatMode(Enum.TryParse<RepeatMode>(_settings.RepeatMode, out var savedRepeatMode)
            ? savedRepeatMode
            : RepeatMode.Off);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettingsWindow();

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
            _changelogWindow = new ChangelogWindow { Owner = this };
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

    // ---------- Просмотр обложки ----------

    private void AlbumArtBorder_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // У трека может не быть обложки (показан плейсхолдер-иконка) — тогда открывать нечего
        if (_currentAlbumArt is null) return;

        if (_coverArtWindow == null)
        {
            _coverArtWindow = new CoverArtWindow(_currentAlbumArt, TrackTitleText.Text)
            {
                Owner = this
            };

            // WindowState.Maximized здесь намеренно не используется: у окон с Mica-фоном и
            // ExtendsContentIntoTitleBar (как у всех FluentWindow этого проекта) нативный
            // Maximize через WindowChrome нередко даёт лишние отступы по краям — окно выглядит
            // "почти" на весь экран, но не встык с его границами. Вместо этого явно выставляем
            // координаты и размер под рабочую область (без учёта панели задач) того монитора,
            // на котором сейчас находится главное окно — получается гарантированно ровно
            // на весь экран, независимо от особенностей рендеринга Mica-хрома.
            var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
            var workArea = screen.WorkingArea;

            _coverArtWindow.WindowStartupLocation = WindowStartupLocation.Manual;
            _coverArtWindow.Left = workArea.Left;
            _coverArtWindow.Top = workArea.Top;
            _coverArtWindow.Width = workArea.Width;
            _coverArtWindow.Height = workArea.Height;

            _coverArtWindow.Closed += (_, _) => _coverArtWindow = null;
            _coverArtWindow.Show();
        }
        else
        {
            _coverArtWindow.Activate();
        }
    }

    // Обложки может не быть (плейсхолдер-иконка) — тогда контекстное меню показывать не о
    // чем, все три пункта всё равно ничего бы не сделали.
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
            System.Windows.MessageBox.Show(this, $"Не удалось сохранить изображение:\n{ex.Message}", "Ошибка",
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
            System.Windows.MessageBox.Show(this, $"Не удалось скопировать изображение:\n{ex.Message}", "Ошибка",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void AlbumArtPropertiesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_currentAlbumArt is null || _currentAlbumArtBytes is null) return;

        var propsWindow = new CoverArtPropertiesWindow(
            _currentAlbumArt, _currentAlbumArtBytes, _currentAlbumArtMimeType, _currentAlbumArtPictureType,
            TrackTitleText.Text, TrackArtistText.Text, _currentTrackPath)
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

            // Раньше высота без плейлиста была захардкожена одним числом — оно оказалось
            // меньше, чем реально нужно для остального контента (обложка, прогресс,
            // кнопки, громкость), и всё это просто обрезалось по нижнему краю окна,
            // выглядя как "окно свернулось", а не как аккуратно скрытый плейлист.
            // Вместо гадания даём WPF самому измерить, сколько места нужно оставшимся
            // строкам грида, и подгоняем окно ровно под них.
            MinHeight = 0;
            SizeToContent = SizeToContent.Height;
            UpdateLayout();
            double collapsedHeight = ActualHeight;
            SizeToContent = SizeToContent.Manual;

            MinHeight = collapsedHeight;
            Height = collapsedHeight;
        }

        TogglePlaylistButton.Icon = IconResources.Make(_isPlaylistVisible ? "IconChevronDown" : "IconChevronRight");
        TogglePlaylistButton.ToolTip = _isPlaylistVisible ? "Скрыть плейлист" : "Показать плейлист";
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

    private void UpdateViewModeMenuChecks()
    {
        SquareViewMenuItem.IsChecked = _viewMode == PlayerViewMode.Square;
        RectangularViewMenuItem.IsChecked = _viewMode == PlayerViewMode.Rectangular;
        MiniViewMenuItem.IsChecked = _viewMode == PlayerViewMode.Mini;
    }

    // ---------- Добавление файлов и папок ----------

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
        var dialog = new TextInputDialog("Новая папка", "Название папки:") { Owner = this };
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

    // ---------- Добавление папки (рекурсивно), в том числе нескольких сразу ----------

    private void AddFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите папку с музыкой",
            Multiselect = true
        };

        if (dialog.ShowDialog() != true) return;

        // Каждая выбранная папка становится отдельной группой плейлиста, которую
        // потом можно независимо включать/выключать
        bool foundAnything = dialog.FolderNames.Aggregate(false, (found, folderPath) => AddFolderPath(folderPath) || found);

        if (!foundAnything)
        {
            System.Windows.MessageBox.Show("В выбранной папке не найдено поддерживаемых аудиофайлов.",
                "Ничего не найдено", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
    }

    // Сканирует папку рекурсивно и добавляет её как отдельную группу плейлиста.
    // Возвращает false, если в ней не нашлось ни одного поддерживаемого аудиофайла
    // (например, нет доступа или папка пустая) — используется, чтобы решить, показывать
    // ли предупреждение "ничего не найдено".
    private bool AddFolderPath(string folderPath)
    {
        List<string> filesInFolder;
        try
        {
            filesInFolder = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (UnauthorizedAccessException)
        {
            // Нет доступа (например, системная папка) — считаем как "ничего не нашли"
            return false;
        }

        if (filesInFolder.Count == 0) return false;

        AddFolderGroup(folderPath, filesInFolder);
        return true;
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
        }

        RefreshPlaylistView();

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
            looseFolder = new PlaylistFolder { SourcePath = null, DisplayName = LooseFilesDisplayName, IsLooseFilesBucket = true };
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

    // Кнопка "Проверить папку на новые треки" в заголовке группы — разовая ручная проверка по
    // требованию пользователя (в отличие от тихой фоновой RescanAllFoldersForNewTracksAsync при
    // старте). Переиспользует AddFolderPath: он и так умеет, если папка с таким SourcePath уже
    // есть в плейлисте, добавить в неё только те файлы, которых там ещё нет — то есть делает
    // ровно то же самое, что нужно здесь, просто в ответ на клик. Выполняется синхронно на
    // UI-потоке (это единичное явное действие пользователя над одной папкой, а не фоновая
    // проверка всего плейлиста при старте) — как и у обычного "Добавить папку" из меню.
    private void RescanFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PlaylistFolder folder }) return;
        if (folder.SourcePath == null) return;

        int before = folder.Tracks.Count;
        bool foundAnything = AddFolderPath(folder.SourcePath);
        int addedCount = folder.Tracks.Count - before;

        if (!foundAnything || addedCount <= 0)
        {
            System.Windows.MessageBox.Show("Новых треков в этой папке не найдено.",
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
    }

    // В отличие от удаления одной группы (см. выше), очистка плейлиста целиком не оставляет
    // вообще ничего, на что можно было бы переключиться дальше — поэтому, если что-то играло,
    // останавливаем воспроизведение и возвращаем плеер к пустому состоянию ("Файл не выбран"),
    // а не оставляем текущий трек тихо доигрывать сам по себе.
    private void ClearPlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        if (_folders.Count == 0) return;

        var confirm = System.Windows.MessageBox.Show(
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

        TrackTitleText.Text = "Файл не выбран";
        TrackArtistText.Text = "—";
        TotalTimeText.Text = "00:00";
        ResetAlbumArtPlaceholder();

        TrackInfoChanged?.Invoke(TrackTitleText.Text, TrackArtistText.Text, CurrentArtBrush);
    }

    // Пока открыт виртуальный плейлист "Избранное", здесь показывается не _folders, а один
    // _favoritesFolder, каждый раз пересобираемый заново из FavoritesManager — так его список
    // треков (и, как следствие, сердечки/навигация "Далее-Назад") всегда актуален, даже если
    // избранное поменяли где-то ещё (например, сняли сердечко с трека в обычном плейлисте, пока
    // сам виртуальный плейлист был открыт в другом окне — мини-плеере это не грозит, но принцип
    // тот же, что и у остального кода: не хранить то, что можно на лету вычислить).
    private void RefreshPlaylistView()
    {
        if (_isFavoritesView)
        {
            _favoritesFolder.Tracks.Clear();
            _favoritesFolder.Tracks.AddRange(FavoritesManager.GetAll());

            PlaylistFoldersControl.ItemsSource = null;
            PlaylistFoldersControl.ItemsSource = new[] { _favoritesFolder };
            return;
        }

        PlaylistFoldersControl.ItemsSource = null;
        PlaylistFoldersControl.ItemsSource = _folders;
    }

    // ---------- Избранное ----------

    private void FavoritesButton_Click(object sender, RoutedEventArgs e) => SetFavoritesViewActive(!_isFavoritesView);

    // Переключает панель плейлиста между обычным видом (группы _folders) и виртуальным
    // плейлистом "Избранное". Кнопки "Добавить"/"Очистить" в этом режиме скрыты — в виртуальную
    // группу нельзя добавлять файлы напрямую и нечего "очищать" (это не настоящая группа, а
    // производная от сердечек на треках, см. PlaylistFolder.IsFavoritesGroup).
    private void SetFavoritesViewActive(bool active)
    {
        _isFavoritesView = active;

        PlaylistHeaderText.Text = active ? "Избранное" : "Плейлист";
        FavoritesButton.Appearance = active ? ControlAppearance.Primary : ControlAppearance.Secondary;
        FavoritesButtonIcon.Icon = active ? "IconHeartFilled" : "IconHeart";
        IconResources.SetOnAccent(FavoritesButtonIcon, active);

        AddButton.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        ClearPlaylistButton.Visibility = active ? Visibility.Collapsed : Visibility.Visible;

        RefreshPlaylistView();
    }

    // Сердечко на строке трека (см. TrackFavoriteButton в MainWindow.xaml) и одноимённый пункт
    // контекстного меню приводят сюда же — оба просто переключают избранное для того же трека,
    // единственная разница в том, откуда берётся путь к файлу (DataContext кнопки против
    // DataContext пункта меню).
    private void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: string filePath }) return;
        FavoritesManager.Toggle(filePath);
        RefreshPlaylistView();
    }

    private void FavoriteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: string filePath }) return;
        FavoritesManager.Toggle(filePath);
        RefreshPlaylistView();
    }

    private void ToggleFolderExpand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PlaylistFolder folder }) return;
        folder.IsExpanded = !folder.IsExpanded;
    }

    // Внутренние ListView треков имеют свой ScrollViewer, который перехватывает колесо мыши
    // и не даёт событию дойти до общего скролла плейлиста. Перехватываем его тут (пока оно
    // ещё не обработано самим ListView — PreviewMouseWheel идёт "сверху вниз") и вручную
    // прокручиваем общий ScrollViewer, чтобы колесо мыши работало над списком треков так же,
    // как и над остальной частью плейлиста.
    private void PlaylistTrackList_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        e.Handled = true;

        // e.Delta приходит ~120 за одно деление колеса — если использовать его как есть,
        // список плейлиста скачет резкими рывками по ~120px за раз. Обычный ScrollViewer
        // в такой ситуации сам конвертирует его в несколько строк плавной прокрутки;
        // повторяем то же самое здесь вручную, чтобы прокрутка ощущалась так же плавно,
        // как перетаскивание слайдера громкости, а не резкими скачками.
        const double pixelsPerNotch = 48.0;
        double offsetDelta = e.Delta / 120.0 * pixelsPerNotch;
        PlaylistScrollViewer.ScrollToVerticalOffset(PlaylistScrollViewer.VerticalOffset - offsetDelta);
    }

    // ---------- Свой скроллбар плейлиста (с нуля, без ScrollBar/Track) ----------
    //
    // Никаких встроенных ScrollBar/Track — только PlaylistScrollTrack (дорожка, Grid)
    // и PlaylistScrollThumb (ползунок, Border) из XAML. Вся логика — здесь:
    //  - PlaylistScrollViewer_ScrollChanged / PlaylistScrollTrack_SizeChanged
    //    пересчитывают высоту и позицию ползунка при любом изменении контента/офсета/размера;
    //  - клик по дорожке мимо ползунка мгновенно прыгает туда, куда кликнули;
    //  - перетаскивание ползунка двигает прокрутку один в один за мышью (ручной MouseCapture).
    private bool _isDraggingPlaylistThumb;
    private double _playlistThumbDragStartMouseY;
    private double _playlistThumbDragStartOffset;

    private void PlaylistScrollViewer_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        UpdatePlaylistScrollThumb();
    }

    private void PlaylistScrollTrack_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePlaylistScrollThumb();
    }

    private void UpdatePlaylistScrollThumb()
    {
        double trackHeight = PlaylistScrollTrack.ActualHeight;
        double extent = PlaylistScrollViewer.ExtentHeight;
        double viewport = PlaylistScrollViewer.ViewportHeight;
        double offset = PlaylistScrollViewer.VerticalOffset;

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

        double trackHeight = PlaylistScrollTrack.ActualHeight;
        double extent = PlaylistScrollViewer.ExtentHeight;
        double viewport = PlaylistScrollViewer.ViewportHeight;
        double thumbHeight = PlaylistScrollThumb.ActualHeight;
        double maxThumbTop = Math.Max(0, trackHeight - thumbHeight);
        double maxOffset = Math.Max(0, extent - viewport);
        if (maxThumbTop <= 0 || maxOffset <= 0) return;

        double clickY = e.GetPosition(PlaylistScrollTrack).Y;
        double targetThumbTop = Math.Clamp(clickY - thumbHeight / 2, 0, maxThumbTop);
        double newOffset = targetThumbTop / maxThumbTop * maxOffset;

        PlaylistScrollViewer.ScrollToVerticalOffset(newOffset);
    }

    private void PlaylistScrollThumb_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isDraggingPlaylistThumb = true;
        _playlistThumbDragStartMouseY = e.GetPosition(PlaylistScrollTrack).Y;
        _playlistThumbDragStartOffset = PlaylistScrollViewer.VerticalOffset;
        PlaylistScrollThumb.CaptureMouse();
        e.Handled = true;
    }

    private void PlaylistScrollThumb_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDraggingPlaylistThumb) return;

        double trackHeight = PlaylistScrollTrack.ActualHeight;
        double extent = PlaylistScrollViewer.ExtentHeight;
        double viewport = PlaylistScrollViewer.ViewportHeight;
        double thumbHeight = PlaylistScrollThumb.ActualHeight;
        double maxThumbTop = Math.Max(0, trackHeight - thumbHeight);
        double maxOffset = Math.Max(0, extent - viewport);
        if (maxThumbTop <= 0 || maxOffset <= 0) return;

        double currentY = e.GetPosition(PlaylistScrollTrack).Y;
        double deltaOffset = (currentY - _playlistThumbDragStartMouseY) / maxThumbTop * maxOffset;

        PlaylistScrollViewer.ScrollToVerticalOffset(Math.Clamp(_playlistThumbDragStartOffset + deltaOffset, 0, maxOffset));
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
        if (sender is not System.Windows.Controls.ListView { DataContext: PlaylistFolder folder } listView) return;
        if (listView.SelectedIndex < 0 || listView.SelectedIndex >= folder.Tracks.Count) return;

        var filePath = folder.Tracks[listView.SelectedIndex];
        LoadAndPlay(filePath);
    }

    // ---------- Контекстное меню трека (правый клик по строке в плейлисте) ----------
    //
    // У каждого пункта меню: DataContext унаследован от ContextMenu.PlacementTarget
    // (StackPanel — корень ItemTemplate) и равен пути к файлу трека; CommandParameter
    // привязан к PlacementTarget.Tag, который, в свою очередь, привязан в XAML к
    // DataContext охватывающего ListView — то есть к PlaylistFolder, которому
    // принадлежит трек. Так получаем оба нужных значения без лишнего кода.

    private void PlayTrackMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: string filePath }) return;
        LoadAndPlay(filePath);
    }

    private void ShowInExplorerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: string filePath }) return;
        if (!File.Exists(filePath)) return;

        // /select, выделяет сам файл в открывшемся окне проводника, а не просто открывает папку
        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\"");
    }

    private void CopyPathMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: string filePath }) return;
        System.Windows.Clipboard.SetText(filePath);
    }

    private void CopyFileMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: string filePath }) return;
        if (!File.Exists(filePath)) return;

        // Кладём в буфер обмена сам файл (а не просто его путь текстом), чтобы можно было
        // вставить (Ctrl+V) прямо в проводник или другую папку — как при обычном Ctrl+C по файлу.
        var files = new System.Collections.Specialized.StringCollection();
        files.Add(filePath);
        System.Windows.Clipboard.SetFileDropList(files);
    }

    // Раньше здесь запускался системный shell-диалог "Свойства" через ShellExecute (verb
    // "properties") — но для многих типов аудиофайлов Windows не регистрирует обработчик
    // этого verb-а, и вызов просто молча ничего не делал. Вместо системного — своё окно
    // в стиле самого плеера (см. TrackPropertiesWindow), которое всегда доступно и не зависит
    // от того, что там зарегистрировано в реестре для .mp3/.flac/.wav у конкретного пользователя.
    private void TrackPropertiesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: string filePath }) return;
        if (!File.Exists(filePath)) return;

        new TrackPropertiesWindow(filePath) { Owner = this }.ShowDialog();
    }

    // Отдельное окно редактирования тегов (название/исполнитель/альбом/год/трек/жанр/
    // комментарий) — пишет прямо в файл через TagLib#. Если отредактированный файл — это
    // как раз сейчас играющий трек, обновляем название/исполнителя/обложку в самом плеере
    // сразу же, не дожидаясь следующего переключения трека.
    private void EditTagsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { DataContext: string filePath }) return;
        if (!File.Exists(filePath)) return;

        var tagsWindow = new TrackTagsWindow(filePath) { Owner = this };
        tagsWindow.ShowDialog();

        if (tagsWindow.Saved && filePath == _currentTrackPath)
        {
            LoadAlbumArt(filePath);
            _nowPlaying?.UpdateTrackInfo(TrackTitleText.Text, TrackArtistText.Text);
            TrackInfoChanged?.Invoke(TrackTitleText.Text, TrackArtistText.Text, CurrentArtBrush);
        }
    }

    private void RemoveTrackMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem menuItem) return;
        if (menuItem.DataContext is not string filePath) return;
        if (menuItem.CommandParameter is not PlaylistFolder folder) return;

        // В виртуальной группе "Избранное" своего списка треков по сути нет — она каждый раз
        // пересобирается из FavoritesManager (см. RefreshPlaylistView), поэтому "убрать из
        // плейлиста" здесь означает "снять сердечко", а не удаление из folder.Tracks — иначе
        // трек тут же вернулся бы в список при следующем обновлении.
        if (folder.IsFavoritesGroup)
        {
            FavoritesManager.SetFavorite(filePath, false);
            RefreshPlaylistView();
            return;
        }

        // Если убираемый трек сейчас играет — не прерываем воспроизведение (он уже
        // загружен в память и от списка не зависит), просто убираем строку из плейлиста.
        folder.Tracks.Remove(filePath);
        RefreshPlaylistView();
    }

    // Безвозвратно удаляет файл трека с диска (не просто убирает из плейлиста). В отличие от
    // "Убрать из плейлиста", это затрагивает реальный файл — поэтому сначала спрашиваем
    // подтверждение и удаляем через корзину (Microsoft.VisualBasic.FileIO), а не File.Delete,
    // чтобы у пользователя оставался шанс восстановить файл в случае ошибки.
    private void DeleteTrackFromDiskMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem menuItem) return;
        if (menuItem.DataContext is not string filePath) return;

        DeleteTrackFromDisk(filePath);
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

        var confirm = System.Windows.MessageBox.Show(
            $"Удалить файл «{trackName}» с диска?\n\nФайл будет перемещён в корзину, а трек — убран из всех плейлистов.",
            "Удаление трека с диска",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);

        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        // Если трек сейчас играет (или просто загружен, на паузе) — файл у него открыт
        // NAudio-потоком, поэтому удаление ниже упадёт с "файл занят другим процессом",
        // пока мы явно не остановим воспроизведение и не освободим хендл.
        if (filePath == _currentTrackPath)
            StopPlayback();

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
            System.Windows.MessageBox.Show($"Не удалось удалить файл:\n{filePath}\n\n{ex.Message}",
                "Ошибка удаления", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return;
        }

        // Файла больше нет — убираем эту дорожку из ВСЕХ плейлистов, где она встречается,
        // а не только из того, где был вызван правый клик (иначе в других группах осталась
        // бы "битая" ссылка на несуществующий файл). Избранное — туда же, по той же причине.
        foreach (var folder in _folders)
            folder.Tracks.RemoveAll(t => t == filePath);
        FavoritesManager.SetFavorite(filePath, false);

        RefreshPlaylistView();
    }

    // ---------- Загрузка и воспроизведение ----------

    private void LoadAndPlay(string filePath, bool autoPlay = true, TimeSpan? startPosition = null)
    {
        StopPlayback(disposeOnly: true);

        try
        {
            _audioFile = new AudioFileReader(filePath) { Volume = ToOutputVolume(VolumeSlider.Value) };
            _outputDevice = new WaveOutEvent();
            _outputDevice.Init(_audioFile);
            _outputDevice.PlaybackStopped += OutputDevice_PlaybackStopped;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Не удалось открыть файл:\n{filePath}\n\n{ex.Message}",
                "Ошибка воспроизведения", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        SetTrackInfoText(Path.GetFileNameWithoutExtension(filePath),
            Path.GetDirectoryName(filePath) is { } dir ? Path.GetFileName(dir) : "—");
        TotalTimeText.Text = _audioFile.TotalTime.ToString(@"mm\:ss");
        ProgressSlider.Maximum = Math.Max(_audioFile.TotalTime.TotalSeconds, 0.01);

        _currentTrackPath = filePath;

        LoadAlbumArt(filePath);

        // Позиция, с которой стартуем: либо восстановленная (сохранённая между запусками),
        // либо начало трека. Раньше при переключении трека на паузе слайдер и текст времени
        // просто не трогались и продолжали показывать позицию ПРЕЖНЕГО трека — сбрасываем
        // их явно на каждую загрузку, а не только когда есть startPosition.
        var position = startPosition.HasValue && startPosition.Value < _audioFile.TotalTime
            ? startPosition.Value
            : TimeSpan.Zero;

        _audioFile.CurrentTime = position;
        ProgressSlider.Value = position.TotalSeconds;
        CurrentTimeText.Text = position.ToString(@"mm\:ss");

        _nowPlaying?.UpdateTrackInfo(TrackTitleText.Text, TrackArtistText.Text);
        TrackInfoChanged?.Invoke(TrackTitleText.Text, TrackArtistText.Text, CurrentArtBrush);

        // Мини-плеер узнаёт о прогрессе только через это событие — без него его полоса
        // прогресса осталась бы показывать позицию предыдущего трека до первого тика таймера
        // (а на паузе таймер вообще не запускается).
        ProgressChanged?.Invoke(position.TotalSeconds, _audioFile.TotalTime.TotalSeconds);

        if (autoPlay)
        {
            _outputDevice.Play();
            _isPlaying = true;
            PlayPauseButton.Icon = IconResources.MakeOnAccent("IconPause", 15);
            _progressTimer.Start();
            _nowPlaying?.SetPlaybackStatus(Windows.Media.MediaPlaybackStatus.Playing);
            PlaybackStateChanged?.Invoke(true);
        }
        else
        {
            // Восстановление состояния без автозапуска: трек загружен и готов, но на паузе
            _isPlaying = false;
            PlayPauseButton.Icon = IconResources.MakeOnAccent("IconPlay", 15);
            _nowPlaying?.SetPlaybackStatus(Windows.Media.MediaPlaybackStatus.Paused);
            PlaybackStateChanged?.Invoke(false);
        }

        ScrollPlaylistToCurrentTrack();
    }

    // ---------- Подсветка и автопрокрутка плейлиста к текущему треку ----------
    //
    // Подсветка текущего трека — это ровно то же самое выделение строки (ListViewItem.
    // IsSelected), что появляется при обычном клике мышью по треку в плейлисте (см. общий
    // Style TargetType="ListViewItem" в App.xaml). Никакой отдельной "подсветки играющего
    // трека" нет — вместо этого при каждой смене трека (см. вызов в конце LoadAndPlay) просто
    // выставляем SelectedItem нужной строки в её ListView, а во всех остальных группах
    // выделение снимаем, чтобы подсвеченной оставалась только строка реально играющего сейчас
    // трека. Заодно разворачиваем содержащую его группу, если она была свёрнута, и
    // прокручиваем общий ScrollViewer плейлиста так, чтобы эта строка была видна — так же, как
    // если бы пользователь сам туда докликал. Раньше это работало только при запуске трека
    // кликом по строке; теперь работает при любом переключении — кнопками "Далее"/"Назад",
    // шафлом, по окончании трека и т.д.

    private void ScrollPlaylistToCurrentTrack()
    {
        var path = _currentTrackPath;
        var folder = string.IsNullOrEmpty(path)
            ? null
            : _isFavoritesView
                ? (_favoritesFolder.Tracks.Contains(path) ? _favoritesFolder : null)
                : _folders.FirstOrDefault(f => f.Tracks.Contains(path));

        if (folder != null && !folder.IsExpanded)
            folder.IsExpanded = true;

        // Разворачивание группы (если она была свёрнута) применяется к раскладке не сразу —
        // ждём завершения текущего цикла раскладки/рендера, прежде чем искать визуальный
        // контейнер строки трека, иначе ListView внутри группы ещё не успеет её создать.
        Dispatcher.BeginInvoke(new Action(() => HighlightAndScrollToTrack(folder, path)),
            DispatcherPriority.Loaded);
    }

    private void HighlightAndScrollToTrack(PlaylistFolder? folder, string? trackPath)
    {
        System.Windows.Controls.ListView? activeListView = null;

        if (folder != null && trackPath != null &&
            PlaylistFoldersControl.ItemContainerGenerator.ContainerFromItem(folder) is FrameworkElement folderContainer &&
            FindVisualChild<System.Windows.Controls.ListView>(folderContainer) is { } listView)
        {
            listView.UpdateLayout();
            listView.SelectedItem = trackPath;
            activeListView = listView;
        }

        // Снимаем выделение во всех остальных группах — иначе там осталась бы висеть подсветка
        // от предыдущего трека или от случайного клика пользователя по другой группе.
        IEnumerable<PlaylistFolder> shownFolders = _isFavoritesView ? new[] { _favoritesFolder } : _folders;
        foreach (var otherFolder in shownFolders)
        {
            if (PlaylistFoldersControl.ItemContainerGenerator.ContainerFromItem(otherFolder) is not FrameworkElement otherContainer)
                continue;
            if (FindVisualChild<System.Windows.Controls.ListView>(otherContainer) is not { } otherListView)
                continue;
            if (!ReferenceEquals(otherListView, activeListView))
                otherListView.SelectedIndex = -1;
        }

        if (activeListView == null || trackPath == null) return;
        if (PlaylistScrollViewer.ActualHeight <= 0) return;

        if (activeListView.ItemContainerGenerator.ContainerFromItem(trackPath) is not FrameworkElement trackContainer)
            return;

        // Координаты строки трека в системе координат общего ScrollViewer плейлиста, с учётом
        // уже накопленного вертикального смещения — чтобы получить абсолютную позицию внутри
        // всего прокручиваемого содержимого, а не только видимой сейчас его части.
        var topLeft = trackContainer.TransformToVisual(PlaylistScrollViewer).Transform(new Point(0, 0));
        double top = topLeft.Y + PlaylistScrollViewer.VerticalOffset;
        double bottom = top + trackContainer.ActualHeight;

        double viewTop = PlaylistScrollViewer.VerticalOffset;
        double viewBottom = viewTop + PlaylistScrollViewer.ViewportHeight;

        const double edgePadding = 8;

        if (top < viewTop)
            PlaylistScrollViewer.ScrollToVerticalOffset(Math.Max(0, top - edgePadding));
        else if (bottom > viewBottom)
            PlaylistScrollViewer.ScrollToVerticalOffset(bottom - PlaylistScrollViewer.ViewportHeight + edgePadding);
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

    private void LoadAlbumArt(string filePath)
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

                ApplyAlbumArtBrush(new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill });
                _currentAlbumArt = bitmap;
                _currentAlbumArtBytes = bytes;
                _currentAlbumArtMimeType = string.IsNullOrWhiteSpace(pictures[0].MimeType) ? "image/jpeg" : pictures[0].MimeType;
                _currentAlbumArtPictureType = pictures[0].Type;
            }
            else
            {
                ResetAlbumArtPlaceholder();
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
            ResetAlbumArtPlaceholder();
        }
    }

    private void ApplyAlbumArtBrush(Brush brush)
    {
        AlbumArtBorder.Background = brush;
        AlbumArtIcon.Visibility = Visibility.Collapsed;
    }

    private void ResetAlbumArtPlaceholder()
    {
        AlbumArtBorder.Background = (Brush)FindResource("ControlFillColorSecondaryBrush");
        AlbumArtIcon.Visibility = Visibility.Visible;
        _currentAlbumArt = null;
        _currentAlbumArtBytes = null;
        _currentAlbumArtMimeType = null;
        _currentAlbumArtPictureType = null;
    }

    private void OutputDevice_PlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // Срабатывает и когда трек доигран до конца, и когда мы сами останавливаем поток —
        // но StopPlayback() заранее отписывается от этого события перед любой "ручной"
        // остановкой (кнопка "Стоп", переключение трека, пауза — см. её комментарий), поэтому
        // если обработчик всё-таки вызвался, это ВСЕГДА естественное завершение потока, а не
        // наше собственное действие.
        //
        // Раньше здесь была ещё проверка "_audioFile.Position >= _audioFile.Length - 1" —
        // сравнение в БАЙТАХ с запасом всего в 1 байт. Из-за выравнивания по блокам сэмплов
        // (например, 4 байта на сэмпл у 16-бит стерео) реальная позиция в момент естественной
        // остановки почти всегда на несколько байт МЕНЬШЕ Length, поэтому условие часто было
        // ложным — трек доигрывался, но HandleTrackFinishedNaturally() не вызывался, и плеер
        // просто молча замолкал вместо перехода к следующему треку. Особенно заметно это было
        // при включённом "Повторе всего плейлиста": вместо бесконечного зацикливания плеер
        // останавливался на каждом (или почти каждом) треке. Сравнение по времени с секундным
        // запасом устойчиво к этому округлению.
        Dispatcher.Invoke(() =>
        {
            if (_audioFile != null && _audioFile.TotalTime - _audioFile.CurrentTime <= TimeSpan.FromMilliseconds(750))
            {
                HandleTrackFinishedNaturally();
            }
        });
    }

    private void HandleTrackFinishedNaturally()
    {
        string? currentPath = GetCurrentTrackPath();
        if (currentPath == null) return;

        switch (_repeatMode)
        {
            case RepeatMode.One:
                // Повторяем тот же самый трек с начала
                LoadAndPlay(currentPath);
                break;

            case RepeatMode.All:
                PlayNextTrack();
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
                    PlayNextTrack();
                break;
        }
    }

    // ---------- Кнопки управления ----------

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_outputDevice == null)
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
            _outputDevice.Pause();
            PlayPauseButton.Icon = IconResources.MakeOnAccent("IconPlay", 15);
            _progressTimer.Stop();
            _nowPlaying?.SetPlaybackStatus(Windows.Media.MediaPlaybackStatus.Paused);
            PlaybackStateChanged?.Invoke(false);

            // На паузе часто и надолго оставляют трек, не закрывая плеер вовсе — сохраняем
            // позицию сразу же, а не ждём следующего реального закрытия (см. PersistPlaybackAndPlaylistState).
            PersistPlaybackAndPlaylistState();
        }
        else
        {
            _outputDevice.Play();
            PlayPauseButton.Icon = IconResources.MakeOnAccent("IconPause", 15);
            _progressTimer.Start();
            _nowPlaying?.SetPlaybackStatus(Windows.Media.MediaPlaybackStatus.Playing);
            PlaybackStateChanged?.Invoke(true);
        }
        _isPlaying = !_isPlaying;
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) => StopPlayback();

    private void StopPlayback(bool disposeOnly = false)
    {
        _progressTimer.Stop();

        // Stop()/Dispose() ниже сами поднимают событие PlaybackStopped — это особенность
        // NAudio: оно срабатывает и при естественном окончании трека, и при любой ручной
        // остановке потока. Если не отписаться заранее, при автопереключении трека прямо из
        // обработчика естественного завершения (OutputDevice_PlaybackStopped →
        // HandleTrackFinishedNaturally → PlayNextTrack → LoadAndPlay → сюда) это же событие
        // срабатывало бы повторно, ещё для СТАРОГО _audioFile, чья Position всё ещё "в конце
        // трека" — HandleTrackFinishedNaturally запускался бы второй раз подряд поверх первого
        // и путал, какой трек включать следующим. Из-за этой гонки автопереход срабатывал
        // через раз, а не на каждом треке.
        if (_outputDevice != null)
            _outputDevice.PlaybackStopped -= OutputDevice_PlaybackStopped;

        _outputDevice?.Stop();
        _outputDevice?.Dispose();
        _audioFile?.Dispose();
        _outputDevice = null;
        _audioFile = null;
        _isPlaying = false;

        if (!disposeOnly)
        {
            ProgressSlider.Value = 0;
            CurrentTimeText.Text = "00:00";
            PlayPauseButton.Icon = IconResources.MakeOnAccent("IconPlay", 15);
            _nowPlaying?.SetPlaybackStatus(Windows.Media.MediaPlaybackStatus.Stopped);
            PlaybackStateChanged?.Invoke(false);
        }
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e)
    {
        var active = FlattenActive();
        if (active.Count == 0) return;

        string? currentPath = GetCurrentTrackPath();
        string prevPath;

        if (_isShuffleEnabled)
        {
            // Не генерируем новый случайный трек, а идём на шаг назад по уже пройденной
            // истории шафла — и только если двигаться назад больше некуда (это самый первый
            // "назад", раньше которого история не заходит), подбираем случайный трек и
            // дописываем его в начало истории, чтобы дальнейшие "вперёд"/"назад" оставались
            // последовательными.
            prevPath = GetShuffleHistoryTrack(-1, active, currentPath) ?? PrependNewShuffleTrack(active, currentPath);
        }
        else
        {
            int posInActive = currentPath != null ? active.IndexOf(currentPath) : -1;
            int prevPos = posInActive <= 0 ? active.Count - 1 : posInActive - 1;
            prevPath = active[prevPos];
        }

        LoadAndPlay(prevPath, autoPlay: _isPlaying);
    }

    private void NextButton_Click(object sender, RoutedEventArgs e) => PlayNextTrack();

    private void PlayNextTrack()
    {
        var active = FlattenActive();
        if (active.Count == 0) return;

        string? currentPath = GetCurrentTrackPath();
        string nextPath;

        if (_isShuffleEnabled)
        {
            // Если перед этим переключались назад по истории шафла, "вперёд" сначала
            // возвращает туда, откуда уходили назад, а не сразу к новому случайному треку —
            // и только когда история исчерпана, генерируем новый случайный трек и дописываем
            // его в конец.
            nextPath = GetShuffleHistoryTrack(+1, active, currentPath)
                       ?? AppendNewShuffleTrack(active, currentPath);
        }
        else
        {
            int posInActive = currentPath != null ? active.IndexOf(currentPath) : -1;
            int nextPos = posInActive < 0 ? 0 : (posInActive + 1) % active.Count;
            nextPath = active[nextPos];
        }

        LoadAndPlay(nextPath, autoPlay: _isPlaying);
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
        var next = GetRandomTrack(activeTracks, currentPath);

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
        var prev = GetRandomTrack(activeTracks, currentPath);

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
    private void SetShuffleEnabled(bool enabled)
    {
        _isShuffleEnabled = enabled;
        ShuffleButton.Appearance = _isShuffleEnabled ? ControlAppearance.Primary : ControlAppearance.Secondary;
        IconResources.SetOnAccent(ShuffleIcon, _isShuffleEnabled);

        // Старая история шафла относится к предыдущему "заезду" по плейлисту — начинаем
        // её заново, чтобы "назад" не утаскивал в состояние из совсем другого включения.
        _shuffleHistory.Clear();
        _shuffleHistoryIndex = -1;
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
                RepeatButton.Appearance = ControlAppearance.Secondary;
                RepeatButton.ToolTip = "Повтор: выключен";
                break;
            case RepeatMode.All:
                RepeatButton.Icon = IconResources.MakeOnAccent("IconRepeatAll");
                RepeatButton.Appearance = ControlAppearance.Primary;
                RepeatButton.ToolTip = "Повтор: весь плейлист";
                break;
            case RepeatMode.One:
                RepeatButton.Icon = IconResources.MakeOnAccent("IconRepeatOne");
                RepeatButton.Appearance = ControlAppearance.Primary;
                RepeatButton.ToolTip = "Повтор: один трек";
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
        // На этот момент _viewMode ещё хранит вид ДО перехода в мини-режим (SetPlayerViewMode
        // присваивает новое значение уже после вызова этого метода) — запоминаем его, чтобы
        // при "развернуть" в ExitMiniMode вернуться именно туда, откуда ушли.
        _preMiniViewMode = _viewMode;

        _miniPlayerWindow = new MiniPlayerWindow(this)
        {
            Opacity = _settings.MiniPlayerOpacity,
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

        _isMiniMode = true;
        Hide();

        // У мини-плеера ShowInTaskbar="False" (см. MiniPlayerWindow.xaml) — в мини-режиме у
        // приложения вообще нет никакого присутствия ни в панели задач, ни в трее, кроме самого
        // окошка мини-плеера. Показываем иконку в трее и здесь, а не только при закрытии
        // основного окна в трей (см. OnClosing) — иначе, свернув плеер в мини-режим, до него
        // потом никак не добраться, кроме как найти и кликнуть само окошко мини-плеера.
        _trayIconManager?.Show($"Lumisense — {TrackTitleText.Text}");
    }

    // Вызывается из MiniPlayerWindow при нажатии кнопки "развернуть"
    public void ExitMiniMode()
    {
        _isMiniMode = false;

        _miniPlayerWindow?.Close();
        _miniPlayerWindow = null;

        Show();
        WindowState = WindowState.Normal;
        Activate();
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
        if (_miniPlayerWindow != null) _miniPlayerWindow.Opacity = opacity;
    }

    public void ApplyMiniPlayerTopmostLive(bool topmost)
    {
        if (_miniPlayerWindow != null) _miniPlayerWindow.Topmost = topmost;
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

    // Вызывается из окна настроек сразу после того, как пользователь записал новую
    // комбинацию клавиш (или очистил старую) — применяет её без перезапуска приложения
    public void ReapplyHotkeys() => _mediaHotKeys?.ApplyCustomHotkeys(_settings);

    // ---------- Управление плеером извне (из MiniPlayerWindow) ----------

    public void ExternalPlayPause() => PlayPauseButton_Click(this, new RoutedEventArgs());
    public void ExternalNext() => PlayNextTrack();
    public void ExternalPrev() => PrevButton_Click(this, new RoutedEventArgs());
    public void ExternalToggleRepeat() => RepeatButton_Click(this, new RoutedEventArgs());

    // Используется и колесом мыши над ползунком громкости в главном окне (см.
    // VolumeOverlay_MouseWheel), и колесом мыши над мини-плеером целиком
    public void ExternalChangeVolume(double delta) => ChangeVolumeBy(delta);

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

    private void ProgressTimer_Tick(object? sender, EventArgs e)
    {
        // Пока пользователь держит ползунок нажатым (клик или перетаскивание) — не трогаем его
        // значение автоматически, иначе неточность перемотки в mp3/aac будет сбивать позицию
        // прямо во время движения, и ползунок будет "дёргаться".
        if (_audioFile == null || _isUserInteractingWithProgress) return;

        _isSyncingProgressFromPlayback = true;
        ProgressSlider.Value = _audioFile.CurrentTime.TotalSeconds;
        _isSyncingProgressFromPlayback = false;

        CurrentTimeText.Text = _audioFile.CurrentTime.ToString(@"mm\:ss");
        ProgressChanged?.Invoke(_audioFile.CurrentTime.TotalSeconds, _audioFile.TotalTime.TotalSeconds);

        if (++_ticksSinceLastAutoSave >= AutoSaveEveryNTicks)
        {
            _ticksSinceLastAutoSave = 0;
            PersistPlaybackAndPlaylistState();
        }
    }

    private void ProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        CurrentTimeText.Text = TimeSpan.FromSeconds(e.NewValue).ToString(@"mm\:ss");

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

    // Переводит положение ползунка (0..1, линейное — как двигает его пользователь) в
    // множитель амплитуды, который реально подаётся на выходное устройство.
    //
    // При выключенной настройке — как и раньше, множитель совпадает с положением ползунка
    // один в один (линейная шкала).
    //
    // При включённой — ползунок сначала переводится в децибелы в диапазоне [MinDb, 0] и
    // только потом — в множитель амплитуды (10^(dB/20)). Так каждое движение ползунка на
    // одинаковое расстояние воспринимается на слух как одинаковое изменение громкости — в
    // отличие от линейной шкалы, где почти весь заметный на слух диапазон сжат в нижние
    // 10-20% ползунка, а верхняя половина хода почти не меняет громкость субъективно.
    private const double MinVolumeDb = -40.0; // тише практически не слышно — дальше просто тишина

    private float ToOutputVolume(double sliderValue)
    {
        sliderValue = Math.Clamp(sliderValue, 0.0, 1.0);

        if (!_settings.UseLogarithmicVolume)
            return (float)sliderValue;

        if (sliderValue <= 0.0) return 0f;

        double db = MinVolumeDb * (1.0 - sliderValue);
        double raw = Math.Pow(10.0, db / 20.0);

        // Без этой поправки сама формула 10^(dB/20) при sliderValue → 0 стремится не к 0, а к
        // "полу" в 10^(MinVolumeDb/20) (около 1% амплитуды при -40 дБ) — ноль получался только
        // отдельным жёстким условием выше. Из-за этого самый нижний, последний отрезок хода
        // ползунка перед нулём давал куда более резкий скачок громкости к полной тишине, чем
        // любой другой равный по длине участок шкалы — на слух это ощущалось как обрыв, а не
        // плавное затухание. Линейно перенормируем кривую (вычитаем и делим на этот же "пол"),
        // чтобы она сама, гладко, доходила ровно до 0 к sliderValue = 0, без отдельного излома.
        double floor = Math.Pow(10.0, MinVolumeDb / 20.0);
        return (float)((raw - floor) / (1.0 - floor));
    }

    // Вызывается из окна настроек сразу при переключении чекбокса "Логарифмическая
    // регулировка громкости", чтобы новая кривая применилась к уже играющему треку сразу,
    // не дожидаясь, пока пользователь потрогает ползунок громкости.
    public void RefreshVolumeCurve()
    {
        if (_audioFile != null)
            _audioFile.Volume = ToOutputVolume(VolumeSlider.Value);
    }

    // Прокрутка колесом мыши над строкой громкости (ползунок, кнопка без звука, подпись
    // процентов — вся строка целиком) крутит громкость так же, как хоткеи громкости:
    // одно деление колеса = 5%. e.Delta положителен при прокрутке "от себя" (вверх).
    private void VolumeRow_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        ChangeVolumeBy(Math.Sign(e.Delta) * 0.02);
        e.Handled = true;
    }

    // Выключить/включить звук: если громкость сейчас не нулевая — запоминаем её и обнуляем;
    // если уже 0 (неважно, из-за клика по этой же кнопке, хоткея или ручного перетаскивания
    // ползунка в 0) — возвращаем последнее ненулевое значение.
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

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_audioFile != null)
            _audioFile.Volume = ToOutputVolume(e.NewValue);

        if (VolumeValueText != null)
            VolumeValueText.Text = $"{(int)Math.Round(e.NewValue * 100)}%";

        if (e.NewValue > 0)
            _lastNonZeroVolume = e.NewValue;

        // Иконка динамика отражает фактическую громкость: 0 — значок "без звука",
        // независимо от того, как звук оказался выключен (кнопкой, хоткеем или
        // просто утащен ползунком до нуля вручную).
        if (SpeakerIcon != null)
        {
            SpeakerIcon.Icon = e.NewValue <= 0.0 ? "IconSpeakerMute" : "IconSpeaker";
        }

        VolumeChanged?.Invoke(e.NewValue);
    }

    // Клик по значку динамика возле ползунка громкости: выключает звук, а при повторном
    // нажатии возвращает ровно то значение громкости, что было до выключения.
    private void MuteButton_Click(object sender, RoutedEventArgs e) => ToggleMute();

    // Единая точка сохранения всего, что должно переживать перезапуск — плейлист, громкость,
    // последний трек и позиция в нём, вид плеера, шафл/повтор, избранное. Раньше это было
    // только внутри OnClosed, то есть срабатывало исключительно при настоящем "Выход" из трея —
    // а поскольку MinimizeToTrayOnClose включён по умолчанию, обычное закрытие крестиком почти
    // всегда просто прячет окно в трей (см. OnClosing) и до "Выход" дело может вообще не
    // доходить месяцами: пользователь просто выключает компьютер, пока плеер тихо играет в
    // фоне. Из-за этого settings.json так и оставался с треком/позицией от самого первого
    // настоящего закрытия — плеер каждый раз при запуске открывал один и тот же трек, будто
    // вообще не запоминал последний. Теперь этот же метод дополнительно вызывается при
    // сворачивании в трей, на паузе и периодически во время игры (см. вызовы ниже) — так
    // актуальное состояние почти всегда уже на диске к моменту следующего запуска, даже если
    // процесс завершится не штатно.
    private void PersistPlaybackAndPlaylistState()
    {
        if (_settings.RememberVolume)
            _settings.SavedVolume = VolumeSlider.Value;

        _settings.SavedPlaylistFolders = _folders.Select(f => new SavedPlaylistFolder
        {
            DisplayName = f.DisplayName,
            SourcePath = f.SourcePath,
            IsEnabled = f.IsEnabled,
            IsExpanded = f.IsExpanded,
            Tracks = f.Tracks.ToList(),
            IsLooseFilesBucket = f.IsLooseFilesBucket
        }).ToList();

        _settings.LastTrackPath = GetCurrentTrackPath();
        _settings.LastPositionSeconds = _audioFile?.CurrentTime.TotalSeconds ?? _settings.LastPositionSeconds;
        _settings.WasMiniPlayerOnClose = _isMiniMode;
        _settings.IsPlaylistVisible = _isPlaylistVisible;
        _settings.PlayerViewMode = _viewMode.ToString();
        _settings.IsShuffleEnabled = _isShuffleEnabled;
        _settings.RepeatMode = _repeatMode.ToString();
        _settings.FavoriteTracks = FavoritesManager.GetAll();

        SettingsManager.Save(_settings);
    }

    protected override void OnClosed(EventArgs e)
    {
        // OnClosed означает, что окно действительно закрывается насовсем (в отличие от
        // OnClosing, где закрытие ещё можно было заменить сворачиванием в трей) — на всякий
        // случай выставляем здесь и так, чтобы Closed-обработчик ShowChangelogWindow ниже
        // точно не попытался открыть окно настроек заново посреди выключения программы.
        _isExiting = true;

        // Сохраняем состояние ДО остановки — StopPlayback ниже обнуляет _audioFile, а
        // PersistPlaybackAndPlaylistState читает текущую позицию именно из него.
        PersistPlaybackAndPlaylistState();
        StopPlayback(disposeOnly: true);

        _mediaHotKeys?.Dispose();
        _trayIconManager?.Dispose();
        _miniPlayerWindow?.Close();
        _settingsWindow?.Close();
        _changelogWindow?.Close();
        _coverArtWindow?.Close();

        base.OnClosed(e);
    }
}
