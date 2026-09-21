namespace Lumisense;

// Глобальный список избранного, общий для всего приложения. Живёт в памяти сессии,
// читается/пишется через AppSettings.FavoriteTracks.
public static class FavoritesManager
{
    // Порядок важен — свежедобавленные треки должны быть внизу списка "Избранное", а не
    // в произвольном порядке из одного HashSet. _lookup — для быстрой проверки IsFavorite.
    private static readonly List<string> _order = new();
    private static readonly HashSet<string> _lookup = new();

    // Закреплённые — подмножество _lookup (закреплять не-избранное бессмысленно, см. TogglePin/SetPinned).
    // Отдельное множество, а не флаг: трек здесь просто путь (string), места для состояния нет.
    private static readonly HashSet<string> _pinned = new();

    public static bool IsFavorite(string path) => _lookup.Contains(path);
    public static bool IsPinned(string path) => _pinned.Contains(path);

    public static int Count => _order.Count;

    // Вызывается при старте; пропускает дубликаты и пустые пути из settings.json. Закрепления треков,
    // которых нет в избранном (например, после ручной правки настроек), молча игнорируются.
    public static void Initialize(IEnumerable<string> savedPaths, IEnumerable<string>? pinnedPaths = null)
    {
        _order.Clear();
        _lookup.Clear();
        _pinned.Clear();

        foreach (var path in savedPaths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            if (_lookup.Add(path))
                _order.Add(path);
        }

        if (pinnedPaths == null) return;

        foreach (var path in pinnedPaths)
            if (_lookup.Contains(path))
                _pinned.Add(path);
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
            if (changed)
            {
                _order.Remove(path);
                // Убранный из избранного трек автоматически открепляется, чтобы не оставаться "хвостом" в _pinned.
                _pinned.Remove(path);
            }
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

    // Закреплённые треки идут первыми (см. GetAll); закрепить можно только то, что уже в избранном.
    public static bool TogglePin(string path)
    {
        if (!_lookup.Contains(path)) return false;

        bool newState = !_pinned.Contains(path);
        if (newState) _pinned.Add(path);
        else _pinned.Remove(path);

        FavoritesChangeNotifier.Instance.Bump();
        return newState;
    }

    // Копия, чтобы вызывающий код не портил внутреннее состояние; порядок не важен, нужно лишь множество.
    public static List<string> GetPinnedPaths() => new(_pinned);

    // Порядок добавления БЕЗ учёта закрепления — именно он пишется в AppSettings.FavoriteTracks; GetAll()
    // поднимает закреплённые наверх, а исходный порядок должен восстанавливаться при откреплении.
    public static List<string> GetOrder() => new(_order);

    public static void Reset()
    {
        _order.Clear();
        _lookup.Clear();
        _pinned.Clear();
        FavoritesChangeNotifier.Instance.Bump();
    }

    // Для показа в "Избранном": закреплённые первыми, затем остальные, оба в порядке добавления
    // (OrderByDescending стабилен, поэтому порядок внутри групп не меняется).
    public static List<string> GetAll() => _order
        .OrderByDescending(IsPinned)
        .ToList();
}

// Даёт сердечку трека повод перевычислиться при смене избранного: путь к файлу в Binding
// не меняется, без Epoch пришлось бы пересобирать весь ItemsSource на каждый клик.
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
