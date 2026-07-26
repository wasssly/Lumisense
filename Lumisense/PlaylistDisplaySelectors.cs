using System.Windows;
using System.Windows.Controls;

namespace AudioPlayer;

/// <summary>
/// Выбирает DataTemplate для элемента единого плоского PlaylistFoldersControl.ItemsSource (см.
/// MainWindow.RefreshPlaylistView) — там вперемешку, в одном списке, лежат PlaylistFolder
/// (заголовок папки) и PlaylistTrackRow (строка трека). Раньше папки и их треки были в
/// РАЗНЫХ ItemsControl/ListView (заголовок — в ItemsControl.ItemTemplate, треки — во вложенном
/// ListView.ItemTemplate папки), и каждая папка порождала свой собственный ListView, поэтому
/// виртуализация была невозможна (см. подробный комментарий в MainWindow.xaml у
/// PlaylistFoldersControl). Один плоский список с селектором шаблона — стандартный, надёжный
/// способ показать разнородные элементы в одном настоящем виртуализирующем ListView.
/// </summary>
public sealed class PlaylistDisplayItemTemplateSelector : DataTemplateSelector
{
    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
    {
        if (container is not FrameworkElement element) return base.SelectTemplate(item, container);

        return item switch
        {
            PlaylistFolder => (DataTemplate)element.FindResource("PlaylistFolderHeaderTemplate"),
            PlaylistTrackRow => (DataTemplate)element.FindResource("TrackItemTemplate"),
            _ => base.SelectTemplate(item, container)
        };
    }
}

/// <summary>
/// Тот же выбор, что и у PlaylistDisplayItemTemplateSelector выше, но для Style самого
/// ListViewItem-контейнера — заголовку папки не нужны ни ховер/выделение (это не выбираемая
/// строка списка), ни фильтрация по поиску (см. PlaylistFolderHeaderContainerStyle /
/// SearchableTrackListViewItemStyle в MainWindow.xaml).
/// </summary>
public sealed class PlaylistDisplayItemContainerStyleSelector : StyleSelector
{
    public override Style? SelectStyle(object? item, DependencyObject container)
    {
        if (container is not FrameworkElement element) return base.SelectStyle(item, container);

        return item switch
        {
            PlaylistFolder => (Style)element.FindResource("PlaylistFolderHeaderContainerStyle"),
            PlaylistTrackRow => (Style)element.FindResource("SearchableTrackListViewItemStyle"),
            _ => base.SelectStyle(item, container)
        };
    }
}
