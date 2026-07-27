using System.Windows;
using System.Windows.Controls;

namespace AudioPlayer;

// PlaylistFoldersControl.ItemsSource — плоский список, где вперемешку лежат PlaylistFolder
// (заголовок папки) и PlaylistTrackRow (строка трека). Селектор выбирает шаблон по типу.
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

// То же самое, но для Style контейнера ListViewItem: заголовку папки не нужны ни ховер/выделение,
// ни фильтрация по поиску, в отличие от строки трека
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
