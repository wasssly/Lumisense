namespace AudioPlayer;

// Глобальный счётчик прослушиваний по пути файла — общий для всего приложения, не привязан
// к конкретной группе плейлиста: один и тот же трек показывает одинаковый счётчик в любом
// плейлисте, где встречается. Живёт в памяти сессии, читается/пишется через
// AppSettings.PlayCounts в конструкторе и в PersistPlaybackAndPlaylistState MainWindow —
// тот же приём, что и у FavoritesManager (см. Favorites.cs).
public static class PlayCountManager
{
    private static readonly Dictionary<string, int> _counts = new();

    public static void Initialize(IReadOnlyDictionary<string, int> saved)
    {
        _counts.Clear();
        foreach (var (path, count) in saved)
        {
            if (string.IsNullOrWhiteSpace(path) || count <= 0) continue;
            _counts[path] = count;
        }
    }

    public static int GetCount(string path) => _counts.TryGetValue(path, out int count) ? count : 0;

    // Вызывается при каждом реальном старте воспроизведения трека (см. MainWindow.LoadAndPlay,
    // ветка autoPlay) — не при простом восстановлении последнего трека на паузе при запуске.
    public static void Increment(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        _counts[path] = GetCount(path) + 1;
        PlayCountChangeNotifier.Instance.Bump();
    }

    // Копия, а не сам словарь — чтобы вызывающий код не мог испортить внутреннее состояние
    public static Dictionary<string, int> GetAll() => new(_counts);
}

// Лёгкий bindable-объект по тому же принципу, что и FavoritesChangeNotifier: путь к файлу
// в основном Binding строки плейлиста не меняется, поэтому без Epoch WPF не узнал бы, что
// счётчик у конкретной строки нужно перечитать.
public sealed class PlayCountChangeNotifier : System.ComponentModel.INotifyPropertyChanged
{
    public static readonly PlayCountChangeNotifier Instance = new();

    private PlayCountChangeNotifier() { }

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
