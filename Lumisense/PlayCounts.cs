namespace Lumisense;

// Глобальный счётчик прослушиваний по пути файла, не привязан к группе плейлиста.
// Живёт в памяти сессии, читается/пишется через AppSettings.PlayCounts.
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

    // Вызывается, когда реально проиграна хотя бы половина трека (_halfPlayCounted в
    // MainWindow.ProgressTimer_Tick), а не при старте или коротком переключении.
    public static void Increment(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        _counts[path] = GetCount(path) + 1;
        PlayCountChangeNotifier.Instance.Bump();
    }

    // Копия, а не сам словарь — чтобы вызывающий код не мог испортить внутреннее состояние
    public static Dictionary<string, int> GetAll() => new(_counts);

    // Bump() сразу обновляет бейджики счётчика в строках плейлиста, не перестраивая список.
    public static void Reset()
    {
        _counts.Clear();
        PlayCountChangeNotifier.Instance.Bump();
    }
}

// По тому же принципу, что FavoritesChangeNotifier: путь к файлу в Binding не меняется,
// без Epoch WPF не узнал бы, что счётчик строки нужно перечитать.
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
