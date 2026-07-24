using System;
using System.ComponentModel;
using System.IO;

namespace AudioPlayer;

/// <summary>
/// Текущий поисковый запрос по плейлисту (см. PlaylistSearchBox в MainWindow.xaml) и лёгкий
/// bindable-объект вокруг него — по тому же принципу, что и FavoritesChangeNotifier (см.
/// Favorites.cs): сам путь к файлу трека, на который завязан основной Binding строки плейлиста,
/// никогда не меняется, поэтому обычный однозначный Binding никогда не перевычислился бы
/// заново сам по себе при изменении текста поиска. MultiBinding в
/// MainWindow.xaml (ItemContainerStyle вложенных ListView) держит второе плечо на Epoch —
/// он и даёт WPF повод перевызвать конвертер видимости заново для уже показанных строк, когда
/// запрос действительно поменялся.
///
/// Фильтрация — чисто визуальная: скрывает несовпавшие строки через Visibility их
/// ListViewItem-контейнеров (см. SearchableTrackListViewItemStyle), а не трогает сами
/// коллекции PlaylistFolder.Tracks. Список "Далее/Назад/Перемешать" и порядок треков при
/// воспроизведении по-прежнему видят ВСЕ треки независимо от того, что сейчас введено в поиск —
/// это осознанно: поиск помогает найти и кликнуть трек глазами, а не превращается в отдельный
/// временный плейлист.
/// </summary>
public sealed class PlaylistSearchState : INotifyPropertyChanged
{
    public static readonly PlaylistSearchState Instance = new();

    private PlaylistSearchState() { }

    private string _query = string.Empty;

    // Нормализованный (без пробелов по краям) поисковый запрос. Пустая строка — поиск не
    // активен, показаны все треки. Само сравнение с именем файла ниже (Matches) регистронезависимое.
    public string Query
    {
        get => _query;
        set
        {
            string normalized = (value ?? string.Empty).Trim();
            if (_query == normalized) return;
            _query = normalized;
            Epoch++;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Epoch)));
        }
    }

    // Значение само по себе смысла не несёт — важен сам факт PropertyChanged на нём (см.
    // комментарий у FavoritesChangeNotifier.Epoch — тот же приём).
    public int Epoch { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    // true, если трек нужно показывать при текущем запросе. Сравнивается с именем файла БЕЗ
    // расширения и без пути к папке — то есть ровно с тем текстом, который пользователь видит
    // в строке трека (см. FileNameConverter в Converters.cs и TextBlock в TrackItemTemplate).
    // Не читаем ID3-теги (исполнитель/название) — они нигде не кэшируются заранее для всего
    // плейлиста целиком, а читать их с диска на каждый трек при каждом нажатии клавиши в
    // поиске было бы заметно медленно на большой библиотеке.
    public bool Matches(string? filePath)
    {
        if (string.IsNullOrEmpty(_query)) return true;
        if (string.IsNullOrEmpty(filePath)) return false;

        string fileName = Path.GetFileNameWithoutExtension(filePath);
        return fileName.Contains(_query, StringComparison.OrdinalIgnoreCase);
    }
}
