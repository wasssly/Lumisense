namespace Lumisense;

// Данные и алгоритм одной сессии шафла: включённость, история "назад/вперёд" и колода без повторов
// (ShuffleBagSelector). MainWindow решает, когда включать шафл и обновлять кнопку/иконку.
internal sealed class ShuffleSession
{
    private const int MaxPersistedHistory = 512;

    private readonly Random _random;
    private readonly List<string> _history = new();
    private int _historyIndex = -1;
    private List<string> _bag = new();

    public ShuffleSession(Random random) => _random = random;

    public bool IsEnabled { get; private set; }

    public void SetEnabled(bool enabled) => IsEnabled = enabled;

    // Вызывается из настроек при переключении "Шаффл без повторов": колода старого/нового алгоритма после смены режима
    // бессмысленна, поэтому начинаем заново.
    public void Reset()
    {
        _history.Clear();
        _historyIndex = -1;
        _bag.Clear();
    }

    public string GetRandom(List<string> activeTracks, string? excludePath)
    {
        return ShuffleBagSelector.TakeNext(_bag, activeTracks, excludePath, _random)
            ?? throw new InvalidOperationException("Не удалось выбрать следующий трек из пустого плейлиста.");
    }

    public void StartStandardSession(string currentPath, bool useImprovedShuffle)
    {
        if (!IsEnabled || useImprovedShuffle) return;

        _history.Clear();
        _history.Add(currentPath);
        _historyIndex = 0;
        _bag.Clear();
    }

    // Обычный shuffle и UseImprovedShuffle используют одну колоду: нет повторов до конца цикла и повтора текущего трека на границе колоды;
    // различия режимов — только в настройках и UI.
    public string GetNext(List<string> activeTracks, string? excludePath) => GetRandom(activeTracks, excludePath);

    // Двигается по истории шафла на shift (-1 назад, +1 вперёд); null, если в эту сторону больше некуда. Треки, удалённые из
    // плейлиста, пропускаются вместе с «хвостом» истории после них.
    public string? GetHistoryTrack(int shift, List<string> activeTracks, string? currentPath)
    {
        if (_history.Count == 0 && currentPath != null)
        {
            // Первое переключение в шафле: заводим историю с текущего трека, чтобы было
            // куда возвращаться "назад" после первого же "вперёд".
            _history.Add(currentPath);
            _historyIndex = 0;
        }

        int newIndex = _historyIndex + shift;
        if (newIndex < 0 || newIndex >= _history.Count) return null;

        var path = _history[newIndex];
        if (!activeTracks.Contains(path))
        {
            // Трек пропал из активного плейлиста — обрезаем историю на этом месте и
            // считаем, что дальше в эту сторону двигаться некуда.
            if (shift > 0)
                _history.RemoveRange(newIndex, _history.Count - newIndex);
            else
                _history.RemoveRange(0, newIndex + 1);
            _historyIndex = Math.Clamp(_historyIndex, -1, _history.Count - 1);
            return null;
        }

        _historyIndex = newIndex;
        return path;
    }

    // Трек в истории на offset от path без изменения истории (для превью соседних обложек); null, если там ничего нет.
    public string? PeekNeighbor(string path, int offset)
    {
        int found = -1;
        for (int i = 0; i < _history.Count; i++)
        {
            if (_history[i] == path && (found < 0 || Math.Abs(i - _historyIndex) < Math.Abs(found - _historyIndex)))
                found = i;
        }

        int target = found + offset;
        return found < 0 || target < 0 || target >= _history.Count ? null : _history[target];
    }

    // Генерирует новый случайный трек и дописывает его в конец истории шафла — вызывается
    // только когда двигаться вперёд по уже существующей истории больше некуда.
    public string AppendNew(List<string> activeTracks, string? currentPath)
    {
        var next = GetNext(activeTracks, currentPath);

        if (_history.Count == 0 && currentPath != null)
            _history.Add(currentPath);

        _history.Add(next);
        _historyIndex = _history.Count - 1;
        return next;
    }

    // Зеркальный аналог AppendNew для случая "назад" — вызывается только когда
    // в истории шафла ещё нет ничего раньше текущего трека.
    public string PrependNew(List<string> activeTracks, string? currentPath)
    {
        var prev = GetNext(activeTracks, currentPath);

        if (_history.Count == 0 && currentPath != null)
            _history.Add(currentPath);

        _history.Insert(0, prev);
        _historyIndex = 0;
        return prev;
    }

    // Вызывается после восстановления SavedPlaylistFolders: повреждённые, удалённые или выключенные пути не должны
    // делать «Назад» непредсказуемым, поэтому берём актуальные активные треки и ограничиваем сохранённый индекс.
    public void PersistTo(AppSettings settings)
    {
        if (!IsEnabled || _history.Count == 0)
        {
            settings.ShuffleHistory = new List<string>();
            settings.ShuffleHistoryIndex = -1;
            settings.ShuffleBag = new List<string>();
            return;
        }

        int firstPersistedIndex = Math.Max(0, _history.Count - MaxPersistedHistory);
        settings.ShuffleHistory = _history.Skip(firstPersistedIndex).ToList();
        settings.ShuffleHistoryIndex = Math.Clamp(
            _historyIndex - firstPersistedIndex, 0, settings.ShuffleHistory.Count - 1);
        settings.ShuffleBag = _bag.Take(MaxPersistedHistory).ToList();
    }

    public void RestoreFrom(AppSettings settings, HashSet<string> activePaths, string? lastTrackPath)
    {
        Reset();
        if (!settings.IsShuffleEnabled) return;
        if (activePaths.Count == 0) return;

        _history.AddRange((settings.ShuffleHistory ?? new List<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path) && activePaths.Contains(path))
            .TakeLast(MaxPersistedHistory));

        if (_history.Count > 0)
        {
            _historyIndex = Math.Clamp(settings.ShuffleHistoryIndex, 0, _history.Count - 1);
            if (lastTrackPath is { } trackPath)
            {
                int lastTrackIndex = _history.FindLastIndex(path =>
                    string.Equals(path, trackPath, StringComparison.OrdinalIgnoreCase));
                if (lastTrackIndex >= 0)
                    _historyIndex = lastTrackIndex;
            }
        }

        _bag = (settings.ShuffleBag ?? new List<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path) && activePaths.Contains(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
