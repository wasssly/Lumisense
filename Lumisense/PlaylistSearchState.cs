using System;
using System.ComponentModel;
using System.IO;

namespace AudioPlayer;

// Текущий поисковый запрос по плейлисту, bindable-объект по тому же принципу, что и
// FavoritesChangeNotifier: путь к файлу трека в Binding никогда не меняется, поэтому WPF
// сам не перевычислит видимость строки при смене запроса — MultiBinding в MainWindow.xaml
// держит второе плечо на Epoch, он и даёт повод перевызвать конвертер.
//
// Фильтрация чисто визуальная (Visibility ListViewItem, см. SearchableTrackListViewItemStyle),
// коллекции PlaylistFolder.Tracks не трогает. "Далее/Назад/Перемешать" видят все треки
// независимо от поиска — это осознанно, поиск не превращается в отдельный временный плейлист.
public sealed class PlaylistSearchState : INotifyPropertyChanged
{
    public static readonly PlaylistSearchState Instance = new();

    private PlaylistSearchState() { }

    private string _query = string.Empty;

    // Пустая строка — поиск не активен. Сравнение в Matches регистронезависимое.
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

    // Значение неважно, важен сам факт PropertyChanged (тот же приём, что и в FavoritesChangeNotifier)
    public int Epoch { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    // Сравниваем с именем файла без расширения и пути — тем, что видит пользователь в строке
    // трека. ID3-теги не читаем: они нигде не кэшируются заранее, а читать с диска на каждое
    // нажатие клавиши было бы заметно медленно на большой библиотеке.
    public bool Matches(string? filePath)
    {
        if (string.IsNullOrEmpty(_query)) return true;
        if (string.IsNullOrEmpty(filePath)) return false;

        string fileName = Path.GetFileNameWithoutExtension(filePath);
        return fileName.Contains(_query, StringComparison.OrdinalIgnoreCase);
    }
}
