using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace Lumisense;

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

// Число прослушиваний (values[0] — FilePath) в строку для показа. values[1] — Epoch, даёт WPF
// повод перевызвать конвертер. 0 показываем пустой строкой, чтобы не загромождать список.
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

// true, если трек (values[0] — FilePath) в избранном. values[1] — Epoch, не используется,
// но даёт WPF повод перевызвать конвертер, обновляя только показанные строки.
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

// Ключ иконки шеврона для кнопки сворачивания списка треков группы
public class ExpandChevronConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "IconChevronDown" : "IconChevronRight";

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// Ширина/высота в Rect(0,0,w,h) — для скругления углов у Image: Border с ClipToBounds
// игнорирует CornerRadius. RelativeSource в Geometry не работает, размер идёт по ElementName.
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
