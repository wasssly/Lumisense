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

// Представление плейлиста: видимость панели, поиск, избранное и закрепление, полоса прокрутки, удаление треков.
// Вынесено из MainWindow.xaml.cs только ради навигации, логика не менялась.
public partial class MainWindow
{
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
}
