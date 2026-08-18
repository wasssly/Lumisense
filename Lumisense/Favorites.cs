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

    // Закреплённые треки внутри избранного (см. TogglePin) — подмножество _lookup: закрепить
    // трек, который не в избранном, бессмысленно и не даёт эффекта (см. TogglePin/SetPinned).
    // Хранится отдельным множеством, а не флагом на самом треке — треки здесь всего лишь пути к
    // файлам (string), у них просто нет места для дополнительного состояния.
    private static readonly HashSet<string> _pinned = new();

    public static bool IsFavorite(string path) => _lookup.Contains(path);
    public static bool IsPinned(string path) => _pinned.Contains(path);

    public static int Count => _order.Count;

    // Вызывается один раз при старте, дубликаты и пустые пути пропускает (мало ли что
    // накорябали руками в settings.json). pinnedPaths — тоже из settings.json
    // (AppSettings.PinnedFavoriteTracks); закрепление того, чего нет в самом избранном,
    // молча игнорируется — например, если пользователь вручную отредактировал файл настроек
    // и убрал трек из FavoriteTracks, не тронув PinnedFavoriteTracks.
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
                // Закрепление вне избранного бессмысленно и нигде не показывается — раз трек
                // убрали из избранного, автоматически открепляем и его тоже, а не оставляем
                // висеть незаметным "хвостом" в _pinned до следующего добавления в избранное.
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

    // Закрепление трека внутри "Избранного" — закреплённые треки идут первыми (см. GetAll),
    // выше всех остальных избранных, независимо от того, когда их туда добавили. Закреплять
    // можно только то, что уже в избранном — трек вне списка избранного просто не показывается
    // ни в каком виде на странице "Избранное", закреплять там нечего.
    public static bool TogglePin(string path)
    {
        if (!_lookup.Contains(path)) return false;

        bool newState = !_pinned.Contains(path);
        if (newState) _pinned.Add(path);
        else _pinned.Remove(path);

        FavoritesChangeNotifier.Instance.Bump();
        return newState;
    }

    // Копия, а не сам список — чтобы вызывающий код не мог испортить внутреннее состояние.
    // Используется для персистентности (AppSettings.PinnedFavoriteTracks) — порядок здесь не
    // важен, важно только само множество путей.
    public static List<string> GetPinnedPaths() => new(_pinned);

    // Порядок добавления в избранное, БЕЗ учёта закрепления — то, что пишется в
    // AppSettings.FavoriteTracks (см. её комментарий: "порядок добавления в избранное"). Не то
    // же самое, что GetAll() ниже — GetAll() дополнительно поднимает закреплённые треки наверх
    // для показа в интерфейсе, а исходный порядок добавления при этом должен оставаться
    // неизменным и восстанавливаемым, если пользователь всё раскрепит обратно.
    public static List<string> GetOrder() => new(_order);

    public static void Reset()
    {
        _order.Clear();
        _lookup.Clear();
        _pinned.Clear();
        FavoritesChangeNotifier.Instance.Bump();
    }

    // Список для показа в виртуальном плейлисте "Избранное" — закреплённые треки первыми (в
    // своём собственном порядке добавления среди закреплённых), затем остальные избранные —
    // тоже в порядке добавления, как и раньше. OrderByDescending в LINQ — стабильная сортировка,
    // поэтому относительный порядок внутри каждой из двух групп не меняется.
    public static List<string> GetAll() => _order
        .OrderByDescending(IsPinned)
        .ToList();
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
