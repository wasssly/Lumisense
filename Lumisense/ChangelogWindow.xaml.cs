using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Wpf.Ui.Controls;

namespace Lumisense;

// Окно "Список изменений": список версий слева (поиск + фильтр + сортировка), детали
// справа — верстаются декларативно через ElementName-биндинг на VersionsListBox.SelectedItem,
// так что этому коду остаётся только загрузить записи и пересчитывать видимый список.
public partial class ChangelogWindow : FluentWindow
{
    private List<ChangelogEntryViewModel> _allEntries = new();
    private readonly ObservableCollection<ChangelogEntryViewModel> _visibleEntries = new();
    private IReadOnlyDictionary<string, string> _githubReleaseUrls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private System.Threading.CancellationTokenSource? _releaseAvailabilityCts;
    private bool _isClosed;

    private bool _sortDescending = true;
    private System.Windows.Threading.DispatcherTimer? _detailsScrollAnimationTimer;
    private double _detailsScrollAnimationStart;
    private double _detailsScrollAnimationTarget;
    private DateTime _detailsScrollAnimationStartedUtc;
    private const double DetailsScrollAnimationDurationMs = 360;

    // RadioButton.IsChecked="True" в XAML (у SortByVersionToggle) вызывает Checked ещё во время
    // InitializeComponent(), до того как _allEntries вообще загружен — этот флаг не даёт
    // обработчикам сортировки/фильтра дёрнуть RefreshVisible раньше времени.
    private readonly bool _isInitializing;
    private readonly AppSettings _settings;

    public ChangelogWindow(AppSettings settings)
    {
        _isInitializing = true;
        _settings = settings;
        InitializeComponent();
        AccessibilityPreferences.ApplyToWindow(this, settings);
        _isInitializing = false;

        VersionsListBox.ItemsSource = _visibleEntries;
        LocalizationService.LanguageChanged += LocalizationService_LanguageChanged;
        ReloadEntries(string.Empty);
        Loaded += ChangelogWindow_Loaded;
    }

    private void ReloadEntries(string? query)
    {
        _allEntries = ChangelogLoader.Load()
            .Select(entry => new ChangelogEntryViewModel(
                entry,
                _githubReleaseUrls.TryGetValue(entry.Version, out string? releaseUrl) ? releaseUrl : null))
            .ToList();
        RefreshVisible(query);
    }

    private async void ChangelogWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= ChangelogWindow_Loaded;
        _releaseAvailabilityCts = new System.Threading.CancellationTokenSource();

        ReleaseListResult releaseResult = await UpdateChecker.GetAllReleasesAsync(_releaseAvailabilityCts.Token);
        if (_isClosed || _releaseAvailabilityCts.IsCancellationRequested)
            return;

        _githubReleaseUrls = releaseResult.Releases
            .Where(release =>
                !string.IsNullOrWhiteSpace(release.Version) &&
                !string.IsNullOrWhiteSpace(release.ReleaseNotesUrl) &&
                UpdateChecker.IsTrustedReleaseNotesUrl(release.ReleaseNotesUrl))
            .GroupBy(release => release.Version, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().ReleaseNotesUrl!, StringComparer.OrdinalIgnoreCase);

        string? selectedVersion = (VersionsListBox.SelectedItem as ChangelogEntryViewModel)?.Version;
        ReloadEntries(SearchBox.Text);
        if (selectedVersion is not null)
            VersionsListBox.SelectedItem = _visibleEntries.FirstOrDefault(entry => entry.Version == selectedVersion);
    }

    private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
    {
        // Текст поиска мог быть введён на предыдущем языке и после перевода карточек
        // перестать совпадать. Очищаем его, но сохраняем выбранную пользователем версию.
        string? selectedVersion = (VersionsListBox.SelectedItem as ChangelogEntryViewModel)?.Version;
        SearchBox.Text = string.Empty;
        ReloadEntries(string.Empty);

        if (selectedVersion is not null)
            VersionsListBox.SelectedItem = _visibleEntries.FirstOrDefault(entry => entry.Version == selectedVersion);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshVisible(SearchBox.Text);

    private void TypeFilterToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        RefreshVisible(SearchBox.Text);
    }

    private void SortOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        RefreshVisible(SearchBox.Text);
    }

    private void SortDirectionButton_Click(object sender, RoutedEventArgs e)
    {
        _sortDescending = !_sortDescending;

        SortDirectionIcon.RenderTransformOrigin = new Point(0.5, 0.5);
        SortDirectionIcon.RenderTransform = new RotateTransform(_sortDescending ? 0 : 180);

        RefreshVisible(SearchBox.Text);
    }

    private void RefreshVisible(string? query)
    {
        query = query?.Trim() ?? string.Empty;

        var selectedTypes = new List<string>();
        if (FilterAddedToggle.IsChecked == true) selectedTypes.Add(ChangeTypeCatalog.Added.Key);
        if (FilterChangedToggle.IsChecked == true) selectedTypes.Add(ChangeTypeCatalog.Changed.Key);
        if (FilterFixedToggle.IsChecked == true) selectedTypes.Add(ChangeTypeCatalog.Fixed.Key);
        if (FilterRemovedToggle.IsChecked == true) selectedTypes.Add(ChangeTypeCatalog.Removed.Key);

        IEnumerable<ChangelogEntryViewModel> filtered = _allEntries.Where(entry =>
            entry.Matches(query) &&
            (selectedTypes.Count == 0 || selectedTypes.Any(entry.HasType)));

        // Версия всегда идёт в том же порядке, что и дата у уже выпущенных (датированных)
        // записей — номер версии как раз и вычисляется по хронологии дат в
        // ChangelogLoader.AssignComputedFields. Но у НОВЫХ записей даты вообще нет (changelog
        // теперь привязан к версии, а не к дате, см. ChangelogLoader) — поэтому сортируем по
        // ParsedVersion, а не по SortDate: для старых записей результат тот же самый, а для
        // новых, у которых date пустая, только version и даёт правильный порядок (пустая дата
        // сортировалась бы как "самая старая", отправляя свежедобавленные записи в самый конец
        // вместо начала). Отдельная сортировка "по версии" была бы дублем — вместо неё сортировка
        // по количеству изменений в версии, которая действительно может дать другой порядок.
        filtered = SortByCountToggle.IsChecked == true
            ? (_sortDescending ? filtered.OrderByDescending(e => e.Items.Count) : filtered.OrderBy(e => e.Items.Count))
            : (_sortDescending ? filtered.OrderByDescending(e => e.ParsedVersion) : filtered.OrderBy(e => e.ParsedVersion));

        var previouslySelected = VersionsListBox.SelectedItem as ChangelogEntryViewModel;

        _visibleEntries.Clear();
        foreach (var entry in filtered)
            _visibleEntries.Add(entry);

        bool isFiltering = query.Length > 0 || selectedTypes.Count > 0;
        EmptyState.Visibility = _visibleEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultsCountText.Text = isFiltering
            ? LocalizationService.Format("Найдено: {0} из {1}", _visibleEntries.Count, _allEntries.Count)
            : LocalizationService.Format("Версий в истории: {0}", _allEntries.Count);

        // Если версия, выбранная до этого, всё ещё видна — оставляем её выбранной, чтобы
        // деталей на правой панели не "прыгали" без необходимости; иначе выбираем первую
        // подходящую, а если совпадений нет вовсе — снимаем выбор.
        if (previouslySelected != null && _visibleEntries.Contains(previouslySelected))
            VersionsListBox.SelectedItem = previouslySelected;
        else
            VersionsListBox.SelectedIndex = _visibleEntries.Count > 0 ? 0 : -1;
    }

    private void VersionsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool hasSelection = VersionsListBox.SelectedItem != null;
        DetailsScroll.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        NoSelectionState.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;

        // При переключении версии правая панель раньше сохраняла позицию прокрутки
        // от предыдущей выбранной версии (например, "внизу"), из-за чего новая версия
        // открывалась не с начала. Сбрасываем скролл наверх при каждой смене выбора.
        if (hasSelection)
            DetailsScroll.ScrollToHome();
    }

    private void VersionRelease_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChangelogEntryViewModel entry } ||
            !entry.HasGitHubRelease ||
            !UpdateChecker.IsTrustedReleaseNotesUrl(entry.GitHubReleaseUrl!))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(entry.GitHubReleaseUrl!) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось открыть GitHub Release {entry.GitHubReleaseUrl}: {ex.Message}");
        }

        e.Handled = true;
    }

    private void SummaryGroup_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChangeGroupViewModel group })
        {
            e.Handled = true;
            ScrollToGroup(group);
        }
    }

    private void ScrollToGroup(ChangeGroupViewModel group)
    {
        if (GroupSectionsItemsControl.ItemContainerGenerator.ContainerFromItem(group) is not FrameworkElement container)
        {
            Dispatcher.BeginInvoke(new Action(() => ScrollToGroup(group)),
                System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }

        var point = container.TransformToAncestor(DetailsScroll).Transform(new Point(0, 0));
        StartDetailsSmoothScroll(Math.Max(0, DetailsScroll.VerticalOffset + point.Y - 12));
    }

    private void ChangeCard_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border card || card.Tag is true) return;
        card.Tag = true;

        int index = 0;
        if (FindVisualParent<ContentPresenter>(card) is ContentPresenter presenter &&
            FindVisualParent<ItemsControl>(card) is ItemsControl itemsControl)
        {
            index = Math.Max(0, itemsControl.ItemContainerGenerator.IndexFromContainer(presenter));
        }

        var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240))
        {
            BeginTime = TimeSpan.FromMilliseconds(Math.Min(index * 35, 280)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        card.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) return match;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private void DetailsScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        DetailsScrollTopButton.Visibility = DetailsScroll.VerticalOffset > 80
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void DetailsScrollTopButton_Click(object sender, RoutedEventArgs e)
    {
        StartDetailsSmoothScroll(0);
    }

    private void StartDetailsSmoothScroll(double targetOffset)
    {
        _detailsScrollAnimationTimer?.Stop();
        _detailsScrollAnimationStart = DetailsScroll.VerticalOffset;
        _detailsScrollAnimationTarget = Math.Clamp(targetOffset, 0, DetailsScroll.ScrollableHeight);
        _detailsScrollAnimationStartedUtc = DateTime.UtcNow;
        _detailsScrollAnimationTimer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(16), System.Windows.Threading.DispatcherPriority.Render, (_, _) =>
            {
                double elapsed = (DateTime.UtcNow - _detailsScrollAnimationStartedUtc).TotalMilliseconds;
                double t = Math.Clamp(elapsed / DetailsScrollAnimationDurationMs, 0, 1);
                double eased = 1 - Math.Pow(1 - t, 3);
                DetailsScroll.ScrollToVerticalOffset(_detailsScrollAnimationStart +
                    (_detailsScrollAnimationTarget - _detailsScrollAnimationStart) * eased);
                if (t >= 1)
                {
                    _detailsScrollAnimationTimer?.Stop();
                    _detailsScrollAnimationTimer = null;
                }
            }, Dispatcher);
        _detailsScrollAnimationTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        _releaseAvailabilityCts?.Cancel();
        _releaseAvailabilityCts?.Dispose();
        _releaseAvailabilityCts = null;
        _detailsScrollAnimationTimer?.Stop();
        _detailsScrollAnimationTimer = null;
        LocalizationService.LanguageChanged -= LocalizationService_LanguageChanged;
        base.OnClosed(e);
    }

    // ---------- Свой скроллбар списка версий (с нуля, без ScrollBar/Track) ----------
    // Тот же приём, что и у плейлиста в главном окне (см. MainWindow.xaml.cs): ScrollViewer
    // со скрытым системным скроллбаром + отдельная дорожка (VersionsScrollTrack) и ползунок
    // (VersionsScrollThumb) в своей собственной колонке, которую скроллбар WPF-UI никогда не
    // перекрывает, потому что физически в ней и находится, а не рисуется поверх содержимого.
    private bool _isDraggingVersionsThumb;
    private double _versionsThumbDragStartMouseY;
    private double _versionsThumbDragStartOffset;

    // Мышиное колесо по умолчанию гоняло список слишком далеко и рывками — та же причина,
    // что и раньше была у плейлиста (см. PlaylistTrackList_PreviewMouseWheel): переводим
    // e.Delta (~120 за одно деление) в небольшой фиксированный шаг в пикселях вручную, чтобы
    // прокрутка ощущалась мягкой, а не скачками через карточку.
    private void VersionsScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        e.Handled = true;

        const double pixelsPerNotch = 40.0;
        double offsetDelta = e.Delta / 120.0 * pixelsPerNotch;
        VersionsScrollViewer.ScrollToVerticalOffset(VersionsScrollViewer.VerticalOffset - offsetDelta);
    }

    private void VersionsScrollViewer_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        UpdateVersionsScrollThumb();
    }

    private void VersionsScrollTrack_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateVersionsScrollThumb();
    }

    private void UpdateVersionsScrollThumb()
    {
        double trackHeight = VersionsScrollTrack.ActualHeight;
        double extent = VersionsScrollViewer.ExtentHeight;
        double viewport = VersionsScrollViewer.ViewportHeight;
        double offset = VersionsScrollViewer.VerticalOffset;

        // Весь список помещается на экран — прятать ползунок, скроллить нечего
        if (trackHeight <= 0 || extent <= viewport || extent <= 0)
        {
            VersionsScrollThumb.Visibility = Visibility.Collapsed;
            return;
        }

        VersionsScrollThumb.Visibility = Visibility.Visible;

        double thumbHeight = Math.Max(24, trackHeight * (viewport / extent));
        double maxThumbTop = Math.Max(0, trackHeight - thumbHeight);
        double maxOffset = Math.Max(0, extent - viewport);
        double thumbTop = maxOffset <= 0 ? 0 : offset / maxOffset * maxThumbTop;

        VersionsScrollThumb.Height = thumbHeight;
        VersionsScrollThumb.Margin = new Thickness(0, thumbTop, 0, 0);
    }

    // Клик по дорожке (не по самому ползунку) — мгновенный прыжок к месту клика,
    // ползунок центрируется под курсором.
    private void VersionsScrollTrack_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsDescendantOf(source, VersionsScrollThumb)) return;
        if (VersionsScrollThumb.Visibility != Visibility.Visible) return;

        double trackHeight = VersionsScrollTrack.ActualHeight;
        double extent = VersionsScrollViewer.ExtentHeight;
        double viewport = VersionsScrollViewer.ViewportHeight;
        double thumbHeight = VersionsScrollThumb.ActualHeight;
        double maxThumbTop = Math.Max(0, trackHeight - thumbHeight);
        double maxOffset = Math.Max(0, extent - viewport);
        if (maxThumbTop <= 0 || maxOffset <= 0) return;

        double clickY = e.GetPosition(VersionsScrollTrack).Y;
        double targetThumbTop = Math.Clamp(clickY - thumbHeight / 2, 0, maxThumbTop);
        double newOffset = targetThumbTop / maxThumbTop * maxOffset;

        VersionsScrollViewer.ScrollToVerticalOffset(newOffset);
    }

    private void VersionsScrollThumb_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isDraggingVersionsThumb = true;
        _versionsThumbDragStartMouseY = e.GetPosition(VersionsScrollTrack).Y;
        _versionsThumbDragStartOffset = VersionsScrollViewer.VerticalOffset;
        VersionsScrollThumb.CaptureMouse();
        e.Handled = true;
    }

    private void VersionsScrollThumb_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDraggingVersionsThumb) return;

        double trackHeight = VersionsScrollTrack.ActualHeight;
        double extent = VersionsScrollViewer.ExtentHeight;
        double viewport = VersionsScrollViewer.ViewportHeight;
        double thumbHeight = VersionsScrollThumb.ActualHeight;
        double maxThumbTop = Math.Max(0, trackHeight - thumbHeight);
        double maxOffset = Math.Max(0, extent - viewport);
        if (maxThumbTop <= 0 || maxOffset <= 0) return;

        double currentY = e.GetPosition(VersionsScrollTrack).Y;
        double deltaOffset = (currentY - _versionsThumbDragStartMouseY) / maxThumbTop * maxOffset;

        VersionsScrollViewer.ScrollToVerticalOffset(Math.Clamp(_versionsThumbDragStartOffset + deltaOffset, 0, maxOffset));
    }

    private void VersionsScrollThumb_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isDraggingVersionsThumb = false;
        VersionsScrollThumb.ReleaseMouseCapture();
    }

    private static bool IsDescendantOf(DependencyObject element, DependencyObject ancestor)
    {
        var current = element;
        while (current != null)
        {
            if (ReferenceEquals(current, ancestor)) return true;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    // ---------- Просмотр картинки версии/изменения крупно ----------
    //
    // То же самое окно (CoverArtWindow), что открывается по клику на обложку трека в главном
    // окне: приближение по клику, панорамирование зажатой левой кнопкой, сброс правой кнопкой.
    // Единственная разница с MainWindow.AlbumArtBorder_MouseLeftButtonDown — там источник уже
    // хранится готовым BitmapImage-полем, а здесь Image.Source достаём прямо из элемента,
    // который кликнули: WPF сам, через встроенный конвертер типов, превратил строковый путь
    // (ChangelogEntryViewModel.ImageSource) в BitmapSource при биндинге — доставать и
    // перезагружать картинку заново не нужно.
    private CoverArtWindow? _coverArtWindow;

    private void ChangelogImage_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // WPF конвертирует строку в Image.Source через ImageSourceConverter, который отдаёт
        // BitmapFrame, а не BitmapImage — сравнение строго на BitmapImage здесь никогда не
        // срабатывало, поэтому клик по картинке ничего не делал. BitmapSource — общий
        // базовый класс для обоих, CoverArtWindow принимает именно его.
        if (sender is not System.Windows.Controls.Image { Source: System.Windows.Media.Imaging.BitmapSource bitmap }) return;

        if (_coverArtWindow == null)
        {
            _coverArtWindow = new CoverArtWindow(bitmap, Title, _settings) { Owner = this };

            // Та же причина, что и в MainWindow.AlbumArtBorder_MouseLeftButtonDown: явные
            // координаты под рабочую область монитора вместо WindowState.Maximized — у окон
            // с Mica-фоном и ExtendsContentIntoTitleBar нативный Maximize нередко даёт лишние
            // отступы по краям.
            var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
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
}
