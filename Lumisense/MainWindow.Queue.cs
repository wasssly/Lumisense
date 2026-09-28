using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Lumisense;

// Popup очереди воспроизведения и меню «Ещё»: вынесено из MainWindow.xaml.cs
// только ради навигации, логика не менялась.
public partial class MainWindow
{
    private void MoreActionsButton_Click(object sender, RoutedEventArgs e)
    {
        MoreActionsPopup.IsOpen = !MoreActionsPopup.IsOpen;
        e.Handled = true;
    }

    // Очередь читаема в обычном и расширенном виде: при ширине владельца 524–664 DIP Popup растёт с окном, вне диапазона —
    // минимум/максимум; внешняя рамка шире на 28 DIP из-за Padding.
    private void UpdateQueuePopupWidth()
    {
        QueuePopupContent.Width = Math.Clamp(ActualWidth - 52, 472, 612);
    }

    private void QueueButton_Click(object sender, RoutedEventArgs e)
    {
        // Popup с StaysOpen=False закрывается в том же input-цикле, что и Click: открываем вторую панель в следующем проходе
        // Dispatcher, иначе WPF закрыл бы её вместе с меню «Ещё» или показал бы пустую кнопку.
        MoreActionsPopup.IsOpen = false;
        QueuePopup.IsOpen = false;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isExiting || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;

            QueuePopup.PlacementTarget = MoreActionsButton;
            UpdateQueuePopupWidth();
            QueuePopup.IsOpen = true;
        }), DispatcherPriority.Input);
        e.Handled = true;
    }

    private void QueueSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        RefreshQueueUi();
    }

    private void QueueSortCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;

        switch (QueueSortCombo.SelectedIndex)
        {
            case 0:
                _playbackQueue.RestoreInsertionOrder();
                break;
            case 1:
                _playbackQueue.SortByDisplayName(descending: false);
                break;
            case 2:
                _playbackQueue.SortByDisplayName(descending: true);
                break;
        }
    }

    private void ClearQueueButton_Click(object sender, RoutedEventArgs e) => _playbackQueue.Clear();

    private void RemoveFromQueueButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: QueueDisplayItem item }) return;
        _playbackQueue.Remove(item.FilePath);
    }

    // Popup-контент не биндится к MainWindow (см. _queueDisplayItems): список и пустое состояние обновляем вручную при
    // каждом изменении PlaybackQueue.
    private void RefreshQueueUi()
    {
        _queueDisplayItems.Clear();
        string query = QueueSearchBox?.Text?.Trim() ?? string.Empty;
        var visibleEntries = _playbackQueue.Items
            .Select((path, index) => new { Path = path, Position = index + 1 });
        if (!string.IsNullOrWhiteSpace(query))
        {
            visibleEntries = visibleEntries.Where(entry => Path.GetFileNameWithoutExtension(entry.Path)
                .Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        // Номер берётся из фактической очереди, а не только из фильтрованного списка, чтобы
        // поиск не создавал впечатление, будто совпадение стало следующим треком.
        foreach (var entry in visibleEntries)
            _queueDisplayItems.Add(new QueueDisplayItem(entry.Path, Path.GetFileNameWithoutExtension(entry.Path), entry.Position));

        bool hasQueueItems = _playbackQueue.Count > 0;
        bool hasVisibleItems = _queueDisplayItems.Count > 0;
        QueueItemsList.Visibility = hasVisibleItems ? Visibility.Visible : Visibility.Collapsed;
        QueueToolsPanel.Visibility = hasQueueItems ? Visibility.Visible : Visibility.Collapsed;
        QueueCountText.Text = string.Format(LocalizationService.Translate("В очереди: {0}"), _playbackQueue.Count);
        QueueEmptyText.Visibility = hasVisibleItems ? Visibility.Collapsed : Visibility.Visible;
        QueueEmptyText.Text = hasQueueItems
            ? LocalizationService.Translate("В очереди нет совпадений.")
            : LocalizationService.Translate("Пусто. Правый клик по треку → «Играть следующим» или «Добавить в очередь».");
        ClearQueueButton.Visibility = hasQueueItems ? Visibility.Visible : Visibility.Collapsed;
    }
}
