using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace AudioPlayer;

// Только имя файла, без расширения и пути
public class FileNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is string path ? Path.GetFileNameWithoutExtension(path) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// Приглушает визуально выключенные группы плейлиста (IsEnabled = false)
public class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? 1.0 : 0.4;

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// Обратный BooleanToVisibilityConverter — для мест, где два блока переключаются одним и тем же
// булевым свойством (например, свёрнутое/развёрнутое содержимое карточки в списке изменений)
public class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// Число прослушиваний трека (values[0] — FilePath строки PlaylistTrackRow) в готовую для
// показа строку. values[1] — PlayCountChangeNotifier.Instance.Epoch, тот же приём, что и в
// IsFavoriteMultiConverter: путь к файлу сам по себе не меняется, Epoch даёт WPF повод
// перевызвать конвертер, когда счётчик где-то обновился. 0 прослушиваний не показываем —
// пустая строка вместо "0" не загромождает строки ещё не проигранных треков.
public class PlayCountMultiConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 0 || values[0] is not string path) return string.Empty;

        int count = PlayCountManager.GetCount(path);
        return count > 0 ? count.ToString(culture) : string.Empty;
    }

    public object?[] ConvertBack(object? value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// Нечётность позиции строки (AlternationIndex) — для чередующейся подсветки плейлиста (zebra striping)
public class IsOddIndexConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is int index && index % 2 == 1;

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// true, если трек (values[0] — FilePath строки PlaylistTrackRow) сейчас в избранном.
// values[1] — FavoritesChangeNotifier.Instance.Epoch, сам не используется, но даёт WPF повод
// перевызвать конвертер, когда избранное поменялось (путь к файлу сам по себе не меняется).
// Раньше при каждом клике по сердечку пересобирался весь ItemsSource плейлиста — тормозило
// на больших списках. Теперь обновляются только реально показанные строки.
public class IsFavoriteMultiConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length > 0 && values[0] is string path && FavoritesManager.IsFavorite(path);

    public object?[] ConvertBack(object? value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// То же самое, что и IsFavoriteMultiConverter выше, только про закрепление трека наверху
// "Избранного" (см. FavoritesManager.TogglePin, TrackPinIcon в MainWindow.xaml).
public class IsPinnedMultiConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length > 0 && values[0] is string path && FavoritesManager.IsPinned(path);

    public object?[] ConvertBack(object? value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// Видимость строки трека при поиске по плейлисту. values[0] — FilePath, values[1] —
// PlaylistSearchState.Instance.Epoch (тот же приём, что в IsFavoriteMultiConverter).
// Фильтрует через Visibility контейнера, а не ICollectionView.Filter на самой коллекции —
// так поиск не трогает данные плейлиста (ни порядок в "Далее/Назад", ни нумерацию треков).
public class TrackMatchesSearchMultiConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length > 0 && values[0] is string path && PlaylistSearchState.Instance.Matches(path)
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

    public object?[] ConvertBack(object? value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// Ключ иконки шеврона для кнопки сворачивания списка треков группы
public class ExpandChevronConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "IconChevronDown" : "IconChevronRight";

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// Собирает ширину/высоту в Rect(0,0,w,h) — нужен для скругления углов у Image (ChangelogWindow.xaml).
// Border с ClipToBounds клипует дочерний контент прямоугольником, игнорируя CornerRadius —
// приходится задавать Image собственный Clip (RectangleGeometry). Geometry не часть визуального
// дерева, RelativeSource внутри неё не работает, поэтому размер берём через ElementName на Image.
public class SizeToRectConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object parameter, CultureInfo culture)
    {
        double width = values.Length > 0 && values[0] is double w ? Math.Max(w, 0) : 0;
        double height = values.Length > 1 && values[1] is double h ? Math.Max(h, 0) : 0;
        return new System.Windows.Rect(0, 0, width, height);
    }

    public object?[] ConvertBack(object? value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
