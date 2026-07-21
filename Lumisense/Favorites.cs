namespace AudioPlayer;

/// <summary>
/// Глобальный список избранных треков (сердечко на строке трека в плейлисте) — общий для всего
/// приложения, а не часть какой-то одной группы плейлиста (<see cref="PlaylistFolder"/>). Один
/// и тот же трек остаётся избранным независимо от того, в какой группе плейлиста он показан;
/// виртуальная группа "Избранное" (см. MainWindow._favoritesFolder) на лету собирается именно
/// из этого списка.
///
/// Хранится только в памяти на время сессии: заполняется из AppSettings.FavoriteTracks при
/// запуске (см. конструктор MainWindow) и записывается обратно туда же при закрытии окна
/// (см. MainWindow.OnClosed) — той же схемой, что и остальной плейлист (SavedPlaylistFolders).
/// </summary>
public static class FavoritesManager
{
    // Порядок важен для показа в виртуальном плейлисте "Избранное" — свежедобавленные треки
    // должны оказываться внизу списка (как при обычном добавлении файлов), а не в произвольном
    // порядке, который дал бы один только HashSet. _lookup нужен только для быстрой проверки
    // "избранное ли это" на каждой строке плейлиста, реальный порядок хранит _order.
    private static readonly List<string> _order = new();
    private static readonly HashSet<string> _lookup = new();

    public static bool IsFavorite(string path) => _lookup.Contains(path);

    public static int Count => _order.Count;

    // Вызывается один раз при старте приложения — заполняет список сохранёнными путями
    // (AppSettings.FavoriteTracks). Пропускает дубликаты и пути-пустышки на случай ручной
    // правки settings.json.
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

        // Уведомляем только когда состояние реально поменялось — а не на каждый вызов (Toggle
        // всегда меняет, но SetFavorite сама по себе могла быть вызвана и "впустую", например
        // повторным снятием сердечка с уже не избранного трека).
        if (changed) FavoritesChangeNotifier.Instance.Bump();
    }

    // Переключает состояние "избранное" для трека и возвращает новое состояние — удобно для
    // обработчика клика по сердечку, которому нужно и поменять состояние, и узнать, каким оно
    // стало (например, чтобы выбрать иконку без лишнего похода в IsFavorite сразу после).
    public static bool Toggle(string path)
    {
        bool newState = !_lookup.Contains(path);
        SetFavorite(path, newState);
        return newState;
    }

    // Копия текущего порядка избранных треков — используется и для построения виртуальной
    // группы "Избранное" в UI, и для сохранения в AppSettings.FavoriteTracks при закрытии.
    // Возвращает копию, а не сам список, чтобы вызывающий код не мог случайно испортить
    // внутреннее состояние менеджера.
    public static List<string> GetAll() => new(_order);
}

/// <summary>
/// Лёгкий bindable-объект, единственная задача которого — дать XAML-биндингам сердечка трека
/// (см. TrackItemTemplate в MainWindow.xaml, IsFavoriteMultiConverter в Converters.cs) повод
/// перевычислиться, когда где-то поменялось избранное. Сам путь к файлу трека, на который
/// завязан основной Binding, никогда не меняется — то есть без этого объекта WPF попросту не
/// узнал бы, что результат конвертера мог измениться, и единственным способом обновить
/// сердечки был бы полный пересбор ItemsSource всего плейлиста при КАЖДОМ переключении
/// избранного. На плейлистах с большим числом треков это было главной причиной подвисания
/// интерфейса при добавлении трека в избранное.
/// </summary>
public sealed class FavoritesChangeNotifier : System.ComponentModel.INotifyPropertyChanged
{
    public static readonly FavoritesChangeNotifier Instance = new();

    private FavoritesChangeNotifier() { }

    private int _epoch;

    // Значение само по себе смысла не несёт — важен сам факт PropertyChanged на нём.
    public int Epoch => _epoch;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public void Bump()
    {
        _epoch++;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Epoch)));
    }
}
