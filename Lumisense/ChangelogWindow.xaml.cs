using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace AudioPlayer;

/// <summary>
/// Окно "Список изменений" — список версий слева (поиск + фильтр по типу + сортировка),
/// подробности выбранной версии справа. Правая панель верстается декларативно в XAML через
/// ElementName-биндинг на VersionsListBox.SelectedItem, так что этому коду остаётся только:
/// загрузить записи и пересчитывать видимый (отфильтрованный и отсортированный) список.
/// </summary>
public partial class ChangelogWindow : FluentWindow
{
    private readonly List<ChangelogEntryViewModel> _allEntries;
    private readonly ObservableCollection<ChangelogEntryViewModel> _visibleEntries = new();

    private bool _sortDescending = true;

    // RadioButton.IsChecked="True" в XAML (у SortByDateToggle) вызывает Checked ещё во время
    // InitializeComponent(), до того как _allEntries вообще загружен — этот флаг не даёт
    // обработчикам сортировки/фильтра дёрнуть RefreshVisible раньше времени.
    private readonly bool _isInitializing;

    public ChangelogWindow()
    {
        _isInitializing = true;
        InitializeComponent();
        _isInitializing = false;

        _allEntries = ChangelogLoader.Load()
            .Select(entry => new ChangelogEntryViewModel(entry))
            .ToList();

        VersionsListBox.ItemsSource = _visibleEntries;

        RefreshVisible(string.Empty);
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

        // Версия всегда идёт в том же порядке, что и дата (номер версии как раз и вычисляется
        // по хронологии дат в ChangelogLoader.AssignComputedFields) — сортировка "по версии"
        // была бы точной копией сортировки "по дате". Вместо дубликата — сортировка по
        // количеству изменений в версии, которая действительно может дать другой порядок.
        filtered = SortByCountToggle.IsChecked == true
            ? (_sortDescending ? filtered.OrderByDescending(e => e.Items.Count) : filtered.OrderBy(e => e.Items.Count))
            : (_sortDescending ? filtered.OrderByDescending(e => e.SortDate) : filtered.OrderBy(e => e.SortDate));

        var previouslySelected = VersionsListBox.SelectedItem as ChangelogEntryViewModel;

        _visibleEntries.Clear();
        foreach (var entry in filtered)
            _visibleEntries.Add(entry);

        bool isFiltering = query.Length > 0 || selectedTypes.Count > 0;
        EmptyState.Visibility = _visibleEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultsCountText.Text = isFiltering
            ? $"Найдено: {_visibleEntries.Count} из {_allEntries.Count}"
            : $"Версий в истории: {_allEntries.Count}";

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
}
