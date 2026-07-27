namespace AudioPlayer;

// Глобальный список избранного (сердечко у трека), общий для всего приложения, а не для
// какой-то одной группы PlaylistFolder. Виртуальная группа "Избранное" (MainWindow._favoritesFolder)
// собирается на лету из этого списка. Живёт только в памяти сессии, читается/пишется
// через AppSettings.FavoriteTracks в конструкторе и в OnClosed MainWindow.
public static class FavoritesManager
{
    // Порядок важен — свежедобавленные треки должны быть внизу списка "Избранное", а не
    // в произвольном порядке из одного HashSet. _lookup — для быстрой проверки IsFavorite.
    private static readonly List<string> _order = new();
    private static readonly HashSet<string> _lookup = new();

    public static bool IsFavorite(string path) => _lookup.Contains(path);

    public static int Count => _order.Count;

    // Вызывается один раз при старте, дубликаты и пустые пути пропускает (мало ли что
    // накорябали руками в settings.json)
    public static void Initialize(IEnumerable<string> savedPaths)
    {
        _order.Clear();
        _lookup.Clear();

        foreach (var path in savedPaths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            if (_lookup.Add(path))
                _order.Add(path);
        }
    }

    public static void SetFavorite(string path, bool isFavorite)
    {
        bool changed;

        if (isFavorite)
        {
            changed = _lookup.Add(path);
            if (changed) _order.Add(path);
        }
        else
        {
            changed = _lookup.Remove(path);
            if (changed) _order.Remove(path);
        }

        // уведомляем, только если состояние реально поменялось, а не на каждый вызов
        if (changed) FavoritesChangeNotifier.Instance.Bump();
    }

    // Меняет состояние и сразу возвращает новое — удобно для клика по сердечку
    public static bool Toggle(string path)
    {
        bool newState = !_lookup.Contains(path);
        SetFavorite(path, newState);
        return newState;
    }

    // Копия, а не сам список — чтобы вызывающий код не мог испортить внутреннее состояние
    public static List<string> GetAll() => new(_order);
}

// Лёгкий bindable-объект, единственная задача которого — дать сердечку трека (TrackItemTemplate,
// IsFavoriteMultiConverter) повод перевычислиться, когда где-то поменялось избранное. Путь к файлу
// в основном Binding не меняется, так что без Epoch WPF не узнал бы, что конвертер надо перевызвать,
// и единственным способом обновить сердечки был бы пересбор всего ItemsSource на каждый клик.
public sealed class FavoritesChangeNotifier : System.ComponentModel.INotifyPropertyChanged
{
    public static readonly FavoritesChangeNotifier Instance = new();

    private FavoritesChangeNotifier() { }

    private int _epoch;

    // значение неважно, важен сам факт PropertyChanged
    public int Epoch => _epoch;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public void Bump()
    {
        _epoch++;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Epoch)));
    }
}
