using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace AudioPlayer;

// Отдельное окно на весь экран с боковой панелью (Библиотека / Сейчас играет) — см. подробный
// комментарий в начале FullScreenPlayerWindow.xaml. Полностью синхронизируется с MainWindow
// через её публичные события/методы, тем же способом, что уже отработан в MiniPlayerWindow —
// этот код-behind сознательно построен по образцу того файла (события подписываются в
// конструкторе с немедленной начальной синхронизацией, отписываются в Closed).
public partial class FullScreenPlayerWindow : FluentWindow
{
    private readonly MainWindow _owner;

    private bool _isDraggingProgress;
    private bool _isDraggingVolume;
    private bool _isSidebarCollapsed;

    // Кэш групп "Артисты"/"Альбомы" на время жизни этого окна — см. подробный комментарий у
    // NavArtists/NavAlbums в XAML и RebuildLibraryIndexAsync/EnsureLibraryIndexAsync ниже. Null, пока индекс ни
    // разу не строился (первый заход на вкладку) или после нажатия "Обновить".
    private List<TrackMetadata>? _libraryMetadataCache;
    private bool _isIndexingLibrary;

    public FullScreenPlayerWindow(MainWindow owner)
    {
        InitializeComponent();
        _owner = owner;

        // Живая привязка прямо к коллекции MainWindow — см. комментарий у PlaylistFolders
        LibraryFoldersList.ItemsSource = _owner.PlaylistFolders;

        SetSidebarCollapsed(_owner.Settings.FullScreenSidebarCollapsed, persist: false);

        _owner.TrackInfoChanged += OnTrackInfoChanged;
        _owner.ProgressChanged += OnProgressChanged;
        _owner.PlaybackStateChanged += OnPlaybackStateChanged;
        _owner.RepeatModeChanged += OnRepeatModeChanged;
        _owner.ShuffleStateChanged += OnShuffleStateChanged;
        _owner.VolumeChanged += OnVolumeChanged;

        // События выше стреляют только на ИЗМЕНЕНИЯ — при открытии окна их ещё не было ни
        // разу, поэтому текущее состояние сразу после конструктора синхронизируем вручную.
        OnTrackInfoChanged(_owner.CurrentTitle, _owner.CurrentArtist, _owner.CurrentArtBrush);
        OnPlaybackStateChanged(_owner.IsPlayingNow);
        OnProgressChanged(_owner.CurrentProgressSeconds, _owner.CurrentDurationSeconds);
        OnRepeatModeChanged(_owner.CurrentRepeatModeName);
        OnShuffleStateChanged(_owner.CurrentIsShuffleEnabled);
        OnVolumeChanged(_owner.CurrentVolume);

        Closed += (_, _) =>
        {
            _owner.TrackInfoChanged -= OnTrackInfoChanged;
            _owner.ProgressChanged -= OnProgressChanged;
            _owner.PlaybackStateChanged -= OnPlaybackStateChanged;
            _owner.RepeatModeChanged -= OnRepeatModeChanged;
            _owner.ShuffleStateChanged -= OnShuffleStateChanged;
            _owner.VolumeChanged -= OnVolumeChanged;
        };

        // Ширина заливки прогресса/громкости считается от ActualWidth дорожки (см.
        // SetProgressFillRatio/SetVolumeFillRatio) — на момент конструктора layout ещё не
        // посчитан (ActualWidth равен 0), поэтому досчитываем актуальные значения ещё раз
        // сразу после первого прохода раскладки.
        Loaded += (_, _) =>
        {
            OnProgressChanged(_owner.CurrentProgressSeconds, _owner.CurrentDurationSeconds);
            OnVolumeChanged(_owner.CurrentVolume);
        };
    }

    // ---------- Навигация по боковой панели ----------

    private void NavItem_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string key }) return;

        PageNowPlaying.Visibility = key == "NowPlaying" ? Visibility.Visible : Visibility.Collapsed;
        PageLibrary.Visibility = key == "Library" ? Visibility.Visible : Visibility.Collapsed;
        PageArtists.Visibility = key == "Artists" ? Visibility.Visible : Visibility.Collapsed;
        PageAlbums.Visibility = key == "Albums" ? Visibility.Visible : Visibility.Collapsed;

        // Индекс для "Артисты"/"Альбомы" строится лениво — только когда пользователь реально
        // на них заходит, а не сразу при открытии окна (незачем читать теги сотен файлов,
        // если человек всё это время слушает музыку со страницы "Сейчас играет").
        if (key is "Artists" or "Albums") _ = EnsureLibraryIndexAsync();
    }

    // ---------- Свёрнутая боковая панель ("рельса" из одних иконок) ----------

    private void SidebarToggleButton_Click(object sender, RoutedEventArgs e) =>
        SetSidebarCollapsed(!_isSidebarCollapsed, persist: true);

    // persist=false — только при открытии окна (см. конструктор), там значение и так уже из
    // настроек, повторно сохранять его же самому себе незачем.
    private void SetSidebarCollapsed(bool collapsed, bool persist)
    {
        _isSidebarCollapsed = collapsed;

        SidebarColumn.Width = new GridLength(collapsed ? 72 : 260);

        var labelVisibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarTitleText.Visibility = labelVisibility;
        NavNowPlayingText.Visibility = labelVisibility;
        NavLibraryText.Visibility = labelVisibility;
        NavArtistsText.Visibility = labelVisibility;
        NavAlbumsText.Visibility = labelVisibility;
        ExitFullScreenButtonText.Visibility = labelVisibility;

        // Схлопываем саму звёздочную колонку под заголовком до нуля (а не только прячем
        // TextBlock внутри неё) — звёздочные колонки в Grid делят место между собой независимо
        // от видимости содержимого, без этого лого осталось бы прижато к левому краю с пустым
        // "хвостом" вместо аккуратной узкой рельсы.
        SidebarTitleColumn.Width = collapsed ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        SidebarToggleButton.HorizontalAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Right;

        // Просто скрыть подписи (Visibility) недостаточно для аккуратного вида "рельсы":
        // RadioButton выровнен по левому краю (см. NavItemStyle, HorizontalContentAlignment
        // ="Left" — там это верно для полной ширины панели), без этой подстройки иконки при
        // сворачивании остались бы прижаты к левому краю узкой колонки вместо центра.
        var navAlign = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        NavNowPlaying.HorizontalContentAlignment = navAlign;
        NavLibrary.HorizontalContentAlignment = navAlign;
        NavArtists.HorizontalContentAlignment = navAlign;
        NavAlbums.HorizontalContentAlignment = navAlign;

        // У кнопки выхода схлопываем звёздочную колонку под текст до нуля — по той же причине,
        // что и у заголовка выше.
        ExitFullScreenButtonTextColumn.Width = collapsed ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ExitFullScreenButtonContent.HorizontalAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;

        // Разворачиваем стрелку в другую сторону — она всегда указывает в направлении, куда
        // приведёт следующий клик (вправо = "разворачивай", влево = "сворачивай").
        SidebarToggleIcon.RenderTransform = new RotateTransform(collapsed ? 0 : 180);
        SidebarToggleButton.ToolTip = collapsed ? "Развернуть панель" : "Свернуть панель";

        if (persist) _owner.Settings.FullScreenSidebarCollapsed = collapsed;
    }

    private void ExitFullScreenButton_Click(object sender, RoutedEventArgs e) => _owner.ExitFullScreenMode();

    // Escape и F11 оба выходят из полноэкранного режима — F11 как симметричный обратный ход
    // тому же входу (см. MainWindow_PreviewKeyDown/EnterFullScreenMode), Escape — привычная
    // клавиша выхода из полноэкранного режима в браузерах и других плеерах.
    private void FullScreenPlayerWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.F11)
        {
            e.Handled = true;
            _owner.ExitFullScreenMode();
        }
    }

    // ---------- Синхронизация с MainWindow ----------

    private void OnTrackInfoChanged(string title, string artist, Brush? art)
    {
        bool hasArtist = !string.IsNullOrWhiteSpace(artist) && artist != "—";

        NowPlayingTitleText.Text = title;
        NowPlayingArtistText.Text = artist;
        NowPlayingArtistText.Visibility = hasArtist ? Visibility.Visible : Visibility.Collapsed;

        BarTitleText.Text = title;
        BarArtistText.Text = hasArtist ? artist : "—";

        if (art != null)
        {
            NowPlayingArtBorder.Background = art;
            NowPlayingArtIcon.Visibility = Visibility.Collapsed;
            BarArtBorder.Background = art;
            BarArtIcon.Visibility = Visibility.Collapsed;
        }
        else
        {
            var placeholder = (Brush)FindResource("ControlFillColorSecondaryBrush");
            NowPlayingArtBorder.Background = placeholder;
            NowPlayingArtIcon.Visibility = Visibility.Visible;
            BarArtBorder.Background = placeholder;
            BarArtIcon.Visibility = Visibility.Visible;
        }
    }

    private void OnPlaybackStateChanged(bool isPlaying) =>
        BarPlayPauseButton.Icon = IconResources.MakeOnAccent(isPlaying ? "IconPause" : "IconPlay", 16);

    private void OnProgressChanged(double positionSeconds, double durationSeconds)
    {
        // Пока пользователь сам тащит ползунок — не перебиваем его собственным положением
        // трека, обновлённым уже ПОСЛЕ клика (тот же приём, что и в MiniPlayerWindow/MainWindow)
        if (_isDraggingProgress) return;

        BarCurrentTimeText.Text = System.TimeSpan.FromSeconds(System.Math.Max(positionSeconds, 0)).ToString(@"mm\:ss");
        BarTotalTimeText.Text = System.TimeSpan.FromSeconds(System.Math.Max(durationSeconds, 0)).ToString(@"mm\:ss");

        double ratio = durationSeconds > 0 ? System.Math.Clamp(positionSeconds / durationSeconds, 0, 1) : 0;
        SetProgressFillRatio(ratio);
    }

    private void OnRepeatModeChanged(string modeName)
    {
        BarRepeatButton.Icon = modeName switch
        {
            "All" => IconResources.MakeOnAccent("IconRepeatAll", 15),
            "One" => IconResources.MakeOnAccent("IconRepeatOne", 15),
            _ => IconResources.Make("IconRepeatAll", 15)
        };
        BarRepeatButton.ToolTip = modeName switch
        {
            "All" => "Повтор: весь плейлист",
            "One" => "Повтор: один трек",
            _ => "Повтор: выключен"
        };
        SetAccentButtonActive(BarRepeatButton, modeName != "Off");
    }

    private void OnShuffleStateChanged(bool enabled)
    {
        BarShuffleButton.Icon = enabled ? IconResources.MakeOnAccent("IconShuffle", 15) : IconResources.Make("IconShuffle", 15);
        BarShuffleButton.ToolTip = enabled ? "Перемешать: включено" : "Перемешать: выключено";
        SetAccentButtonActive(BarShuffleButton, enabled);
    }

    private void OnVolumeChanged(double volume)
    {
        BarMuteButton.Icon = IconResources.Make(volume <= 0 ? "IconSpeakerMute" : "IconSpeaker", 15);

        if (_isDraggingVolume) return;
        SetVolumeFillRatio(volume);
    }

    // Тот же приём "покрасить Background вручную", что и в MainWindow.SetAccentButtonActive и
    // MiniPlayerWindow — обходит нерабочее автообновление Appearance=Primary у WPF-UI при
    // смене акцента вживую (см. подробный комментарий в MainWindow.xaml.cs).
    private void SetAccentButtonActive(Wpf.Ui.Controls.Button button, bool active)
    {
        button.Appearance = ControlAppearance.Secondary;

        if (active)
            button.Background = new SolidColorBrush(_owner.GetResolvedAccentColor());
        else
            button.ClearValue(Control.BackgroundProperty);
    }

    // ---------- Транспорт ----------

    private void ShuffleButton_Click(object sender, RoutedEventArgs e) => _owner.ExternalToggleShuffle();
    private void RepeatButton_Click(object sender, RoutedEventArgs e) => _owner.ExternalToggleRepeat();
    private void PrevButton_Click(object sender, RoutedEventArgs e) => _owner.ExternalPrev();
    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => _owner.ExternalPlayPause();
    private void NextButton_Click(object sender, RoutedEventArgs e) => _owner.ExternalNext();
    private void MuteButton_Click(object sender, RoutedEventArgs e) => _owner.ExternalToggleMute();

    // ---------- Перемотка (тот же приём "заливка Border-ом + прозрачная накладка", что и в
    // MiniPlayerWindow.ProgressBar_MouseLeftButtonDown/MouseMove/Up) ----------

    private void SetProgressFillRatio(double ratio)
    {
        double trackWidth = BarProgressTrack.ActualWidth;
        BarProgressFill.Width = trackWidth > 0 ? trackWidth * System.Math.Clamp(ratio, 0, 1) : 0;
    }

    private void BarProgress_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingProgress = true;
        ((UIElement)sender).CaptureMouse();
        SetProgressFillRatio(e.GetPosition(BarProgressTrack).X / System.Math.Max(BarProgressTrack.ActualWidth, 1));
    }

    private void BarProgress_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingProgress) return;
        SetProgressFillRatio(e.GetPosition(BarProgressTrack).X / System.Math.Max(BarProgressTrack.ActualWidth, 1));
    }

    private void BarProgress_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingProgress) return;

        _isDraggingProgress = false;
        ((UIElement)sender).ReleaseMouseCapture();

        double ratio = BarProgressTrack.ActualWidth > 0 ? BarProgressFill.Width / BarProgressTrack.ActualWidth : 0;
        _owner.ExternalSeekRatio(ratio);
    }

    // ---------- Громкость (тот же приём, что и перемотка, но с абсолютной установкой
    // значения через MainWindow.ExternalSetVolume вместо ExternalSeekRatio) ----------

    private void SetVolumeFillRatio(double ratio)
    {
        double trackWidth = BarVolumeTrack.ActualWidth;
        BarVolumeFill.Width = trackWidth > 0 ? trackWidth * System.Math.Clamp(ratio, 0, 1) : 0;
    }

    private void BarVolume_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingVolume = true;
        ((UIElement)sender).CaptureMouse();
        UpdateVolumeFromMouse(e.GetPosition(BarVolumeTrack).X);
    }

    private void BarVolume_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingVolume) return;
        UpdateVolumeFromMouse(e.GetPosition(BarVolumeTrack).X);
    }

    private void BarVolume_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingVolume = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private void UpdateVolumeFromMouse(double x)
    {
        double width = BarVolumeTrack.ActualWidth;
        if (width <= 0) return;

        double ratio = System.Math.Clamp(x / width, 0, 1);
        SetVolumeFillRatio(ratio);
        _owner.ExternalSetVolume(ratio);
    }

    // ---------- Библиотека ----------

    // ClickCount >= 2 — запускаем только по двойному клику, как и в обычном плейлисте
    // главного окна (см. PlaylistTrackList_MouseDoubleClick), одиночный клик по строке ничего
    // не делает (кроме клика по самому сердечку — см. LibraryFavoriteButton_Click ниже,
    // у него собственный обработчик, а не проверка ClickCount здесь).
    private void LibraryTrack_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2) return;
        if (sender is FrameworkElement { Tag: string filePath })
            _owner.ExternalPlayTrack(filePath);
    }

    private void LibraryFavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string filePath })
            FavoritesManager.Toggle(filePath);
    }

    // ---------- Артисты / Альбомы ----------
    // В отличие от "Библиотеки" (прямая живая привязка к MainWindow.PlaylistFolders), для
    // группировки по исполнителю/альбому нужно прочитать ID3-теги каждого файла — это не
    // бесплатно на большой библиотеке, поэтому индекс строится один раз в фоне при первом
    // заходе на любую из двух вкладок (кэш общий для обеих — TrackMetadata содержит сразу и
    // артиста, и альбом), а не заново для каждой вкладки и не при каждом изменении плейлиста.
    // Кнопка "Обновить" на самих страницах — на случай, если плейлист поменяли, пока окно уже
    // было открыто на этой вкладке.

    private void ArtistsRefreshButton_Click(object sender, RoutedEventArgs e) => _ = RebuildLibraryIndexAsync();
    private void AlbumsRefreshButton_Click(object sender, RoutedEventArgs e) => _ = RebuildLibraryIndexAsync();

    private Task EnsureLibraryIndexAsync() => _libraryMetadataCache != null ? Task.CompletedTask : RebuildLibraryIndexAsync();

    private async Task RebuildLibraryIndexAsync()
    {
        if (_isIndexingLibrary) return;
        _isIndexingLibrary = true;

        var allPaths = _owner.PlaylistFolders.SelectMany(f => f.Tracks).ToList();

        ArtistsLoadingText.Visibility = Visibility.Visible;
        AlbumsLoadingText.Visibility = Visibility.Visible;
        ArtistsList.ItemsSource = null;
        AlbumsList.ItemsSource = null;

        try
        {
            // Чтение тегов — блокирующий файловый ввод-вывод, целиком в фоновом потоке, чтобы
            // не подвешивать интерфейс на сотнях-тысячах файлов; сюда же попадает и сама
            // группировка по артисту/альбому — она тоже не бесплатна на большом списке.
            var metadata = await Task.Run(() => allPaths.Select(ReadTrackMetadata).ToList());
            _libraryMetadataCache = metadata;

            ArtistsList.ItemsSource = GroupBy(metadata, m => m.Artist, "Неизвестный исполнитель");
            AlbumsList.ItemsSource = GroupBy(metadata, m => m.Album, "Без альбома");
        }
        finally
        {
            ArtistsLoadingText.Visibility = Visibility.Collapsed;
            AlbumsLoadingText.Visibility = Visibility.Collapsed;
            _isIndexingLibrary = false;
        }
    }

    private static List<LibraryGroup> GroupBy(List<TrackMetadata> metadata, System.Func<TrackMetadata, string> keySelector, string fallbackName) =>
        metadata
            .GroupBy(keySelector)
            .OrderBy(g => g.Key, System.StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new LibraryGroup(
                string.IsNullOrWhiteSpace(g.Key) ? fallbackName : g.Key,
                g.Select(m => m.FilePath).ToList()))
            .ToList();

    // Читает Title/Artist/Album из ID3-тегов файла — тот же TagLib, что и в
    // MainWindow.LoadAlbumArt, с тем же принципом "файл без тегов или с повреждёнными
    // метаданными не должен ронять всю индексацию" — просто откатываемся на разумные
    // значения по умолчанию.
    private static TrackMetadata ReadTrackMetadata(string filePath)
    {
        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            string artist = string.IsNullOrWhiteSpace(tagFile.Tag.FirstPerformer) ? "" : tagFile.Tag.FirstPerformer;
            string album = string.IsNullOrWhiteSpace(tagFile.Tag.Album) ? "" : tagFile.Tag.Album;
            return new TrackMetadata(filePath, artist, album);
        }
        catch
        {
            return new TrackMetadata(filePath, "", "");
        }
    }
}

// Артист/альбом трека — используется только для группировки в FullScreenPlayerWindow
// (Артисты/Альбомы), заголовок трека здесь не нужен: сама строка трека в списке берёт его из
// имени файла тем же FileNameConverter, что и "Библиотека" (см. LibraryTrackRowTemplate).
internal sealed record TrackMetadata(string FilePath, string Artist, string Album);

// Одна группа в "Артистах"/"Альбомах" — тот же по сути DataContext, что и PlaylistFolder у
// "Библиотеки" (Name/SubtitleText/список путей), но собранная не из папок плейлиста, а из
// TrackMetadata (см. FullScreenPlayerWindow.GroupBy). SubtitleText — та же формулировка
// склонения, что и у PlaylistFolder.SubtitleText, ради единообразия карточек в обеих вкладках.
internal sealed class LibraryGroup
{
    public string Name { get; }
    public List<string> TrackPaths { get; }

    public LibraryGroup(string name, List<string> trackPaths)
    {
        Name = name;
        TrackPaths = trackPaths;
    }

    public string SubtitleText => $"{TrackPaths.Count} {PluralizeTracks(TrackPaths.Count)}";

    // Тот же алгоритм русского склонения, что и в PlaylistFolder.SubtitleText — сознательно
    // продублирован, а не вынесен в общий хелпер: PlaylistFolder специально не знает о
    // FullScreenPlayerWindow (модель плейлиста не должна зависеть от конкретного окна,
    // которое её показывает), а тащить ради трёх строк отдельный статический класс с одним
    // методом смысла не было.
    private static string PluralizeTracks(int count)
    {
        int n = System.Math.Abs(count) % 100;
        int n1 = n % 10;
        if (n is > 10 and < 20) return "треков";
        if (n1 == 1) return "трек";
        if (n1 is > 1 and < 5) return "трека";
        return "треков";
    }
}
