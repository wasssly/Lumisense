using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace AudioPlayer;

/// <summary>Показывает только имя файла без расширения и без пути к папке.</summary>
public class FileNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is string path ? Path.GetFileNameWithoutExtension(path) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Приглушает визуально выключенные группы плейлиста (IsEnabled = false).</summary>
public class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? 1.0 : 0.4;

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Обратный BooleanToVisibilityConverter: true → Collapsed, false → Visible.
/// Нужен там, где два блока переключаются по одному и тому же булеву свойству
/// (например, "свёрнутое" и "развёрнутое" содержимое карточки в списке изменений).</summary>
public class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>true, если позиция строки в списке (AlternationIndex ListView) нечётная —
/// используется для чередующейся подсветки строк плейлиста (zebra striping), см. Style
/// TargetType="ListViewItem" в App.xaml.</summary>
public class IsOddIndexConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is int index && index % 2 == 1;

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Порядковый номер трека в списке (1-based), вычисляется из AlternationIndex
/// строки ListView. Используется вместо старых линий-разделителей между треками: у каждой
/// строки плейлиста теперь есть свой номер слева, который при наведении мыши на строку
/// сменяется иконкой воспроизведения (см. DataTemplate.Triggers в MainWindow.xaml).
///
/// ВНИМАНИЕ: AlternationIndex ненадёжен при включённой UI-виртуализации ListView — при
/// пересоздании/переиспользовании (recycling) контейнеров строк WPF не всегда пересчитывает
/// это значение, из-за чего при повторном открытии/скролле плейлиста номера могли повторяться
/// или "залипать" на одном значении. Оставлен только для zebra striping (IsOddIndexConverter),
/// где неточность чисто косметическая. Для самого номера трека теперь используется
/// TrackNumberFromListConverter — он не зависит от виртуализации.</summary>
public class TrackNumberConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is int index ? (index + 1).ToString(culture) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Порядковый номер трека в списке (1-based), вычисляется напрямую из позиции
/// элемента в коллекции ItemsSource (values[1]) — а не из визуального AlternationIndex
/// ListViewItem. Из-за UI-виртуализации AlternationIndex может не обновляться корректно
/// при переиспользовании (recycling) контейнеров строк, что приводило к повторяющимся или
/// "залипшим" номерам после перезахода в плейлист/скролла. Этот конвертер всегда даёт
/// правильный номер независимо от виртуализации и переиспользования контейнеров.
/// values[0] — текущий трек (путь к файлу), values[1] — вся коллекция Tracks ListView'а.</summary>
public class TrackNumberFromListConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 2 && values[1] is System.Collections.IEnumerable items)
        {
            int i = 0;
            foreach (var item in items)
            {
                i++;
                if (Equals(item, values[0]))
                    return i.ToString(culture);
            }
        }
        return string.Empty;
    }

    public object?[] ConvertBack(object? value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>true, если трек (value — путь к файлу, DataContext строки ListView в плейлисте)
/// сейчас в избранном. Используется в DataTrigger сердечка строки трека (см. MainWindow.xaml) —
/// по умолчанию показан контур сердечка приглушённым цветом, а при true шаблон переключает его
/// на закрашенное красное сердечко.
///
/// Состояние берётся из FavoritesManager, а не из самого value, поэтому после переключения
/// избранного (см. MainWindow.FavoriteButton_Click) нужно заново перепривязать ItemsSource
/// (см. MainWindow.RefreshPlaylistView) — иначе WPF не узнает, что результат конвертера мог
/// измениться, и не перевызовет его сам по себе.</summary>
public class IsFavoriteConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is string path && FavoritesManager.IsFavorite(path);

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Шеврон для кнопки сворачивания/разворачивания списка треков группы.
/// Возвращает ключ нужной иконки ("IconChevronDown" / "IconChevronRight", см. папку Icons/) —
/// SvgPathIcon.Icon биндится сюда напрямую в MainWindow.xaml и сам подставляет нужную геометрию.</summary>
public class ExpandChevronConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "IconChevronDown" : "IconChevronRight";

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
