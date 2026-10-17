namespace Lumisense;

// Данные и алгоритм одной сессии шафла: включённость, история "назад/вперёд" и колода без повторов
// (ShuffleBagSelector). MainWindow решает, когда включать шафл и обновлять кнопку/иконку.
internal sealed class ShuffleSession
{
    private const int MaxPersistedHistory = 512;

    private readonly Random _random;
    private readonly List<string> _history = new();
    // Параллельно _history: true, если трек реально загружался (CommitShown). Остальные — шаги, пропущенные при быстром листании,
    // и соседи, спланированные для превью.
    private readonly List<bool> _shown = new();
    private int _historyIndex = -1;
    // Позиция в истории последнего реально загруженного трека: между ним и новым загруженным пропущенные шаги убираются.
    private int _committedIndex = -1;
    private List<string> _bag = new();
    private readonly HashSet<string> _playedInRound = new(StringComparer.OrdinalIgnoreCase);

    public ShuffleSession(Random random) => _random = random;

    // MainWindow подставляет живые источники: режим из настроек, число прослушиваний и признак избранного. Значения по умолчанию
    // дают прежнюю колоду без повторов.
    public Func<string> ModeProvider { get; set; } = () => ShuffleModes.Deck;
    public Func<string, int>? PlayCountProvider { get; set; }
    public Func<string, bool>? FavoriteProvider { get; set; }

    public bool IsEnabled { get; private set; }

    public void SetEnabled(bool enabled) => IsEnabled = enabled;

    // Вызывается из настроек при смене режима шаффла (AppSettings.ShuffleMode): колода старого/нового алгоритма после смены режима
    // бессмысленна, поэтому начинаем заново.
    public void Reset()
    {
        _history.Clear();
        _shown.Clear();
        _historyIndex = -1;
        _committedIndex = -1;
        _bag.Clear();
        _playedInRound.Clear();
    }

    // Загрузка трека дошла до конца: он действительно показан. Пропущенные при быстром листании шаги между прошлым показанным
    // треком и этим удаляем, иначе «Назад» вернул бы треки, которых пользователь не видел. Позиция навигации не меняется.
    public void CommitShown(string path)
    {
        if (!IsEnabled) return;

        int index = FindNearestIndex(path);
        if (index < 0) return;

        if (_committedIndex >= 0 && _committedIndex != index)
        {
            int low = Math.Min(_committedIndex, index);
            for (int k = Math.Max(_committedIndex, index) - 1; k > low; k--)
            {
                if (_shown[k]) continue;

                _history.RemoveAt(k);
                _shown.RemoveAt(k);
                if (k < _historyIndex) _historyIndex--;
                if (k < _committedIndex) _committedIndex--;
                if (k < index) index--;
            }
        }

        _shown[index] = true;
        _committedIndex = index;
    }

    private void AddToHistory(string path, bool shown)
    {
        _history.Add(path);
        _shown.Add(shown);
    }

    private void InsertAtHistoryStart(string path)
    {
        _history.Insert(0, path);
        _shown.Insert(0, false);
        if (_committedIndex >= 0) _committedIndex++;
    }

    // Единственная точка, где история начинается с текущего трека: он уже показан.
    private void StartHistoryWith(string path)
    {
        _history.Add(path);
        _shown.Add(true);
        _historyIndex = 0;
        _committedIndex = 0;
    }

    // Колода может быть уже раздана наперёд (превью соседей), поэтому «круг закончен» считаем по реально начатым трекам.
    public void MarkPlayed(string path) => _playedInRound.Add(path);

    public void ResetRound() => _playedInRound.Clear();

    public bool HasPlayedAll(List<string> activeTracks) =>
        IsEnabled && activeTracks.Count > 0 && activeTracks.All(_playedInRound.Contains);

    public string GetRandom(List<string> activeTracks, string? excludePath)
    {
        return ShuffleModeSelector.TakeNext(ModeProvider(), _bag, activeTracks, excludePath, _random, PlayCountProvider, FavoriteProvider)
            ?? throw new InvalidOperationException("Не удалось выбрать следующий трек из пустого плейлиста.");
    }

    public void StartStandardSession(string currentPath)
    {
        if (!IsEnabled) return;

        _history.Clear();
        _shown.Clear();
        StartHistoryWith(currentPath);
        _bag.Clear();
    }

    // Как выбирается следующий трек, определяет режим из настроек (ShuffleModes); во всех режимах трек не повторяется подряд.
    public string GetNext(List<string> activeTracks, string? excludePath) => GetRandom(activeTracks, excludePath);

    // Двигается по истории шафла на shift (-1 назад, +1 вперёд); null, если в эту сторону больше некуда. Треки, удалённые из
    // плейлиста, пропускаются вместе с «хвостом» истории после них.
    public string? GetHistoryTrack(int shift, List<string> activeTracks, string? currentPath)
    {
        if (_history.Count == 0 && currentPath != null)
        {
            // Первое переключение в шафле: заводим историю с текущего трека, чтобы было
            // куда возвращаться "назад" после первого же "вперёд".
            StartHistoryWith(currentPath);
        }

        int newIndex = _historyIndex + shift;
        if (newIndex < 0 || newIndex >= _history.Count) return null;

        var path = _history[newIndex];
        if (!activeTracks.Contains(path))
        {
            // Трек пропал из активного плейлиста — обрезаем историю на этом месте и
            // считаем, что дальше в эту сторону двигаться некуда.
            if (shift > 0)
            {
                _history.RemoveRange(newIndex, _history.Count - newIndex);
                _shown.RemoveRange(newIndex, _shown.Count - newIndex);
            }
            else
            {
                _history.RemoveRange(0, newIndex + 1);
                _shown.RemoveRange(0, newIndex + 1);
                _historyIndex -= newIndex + 1;
                _committedIndex -= newIndex + 1;
            }

            _historyIndex = Math.Clamp(_historyIndex, -1, _history.Count - 1);
            _committedIndex = Math.Clamp(_committedIndex, -1, _history.Count - 1);
            return null;
        }

        _historyIndex = newIndex;
        return path;
    }

    // Трек в истории на offset от path без изменения истории (для превью соседних обложек); null, если там ничего нет.
    public string? PeekNeighbor(string path, int offset)
    {
        int found = FindNearestIndex(path);
        int target = found + offset;
        return found < 0 || target < 0 || target >= _history.Count ? null : _history[target];
    }

    // Заранее выбирает до reach соседей path с каждой стороны, чтобы превью в карусели показывало именно те треки, которые затем
    // сыграют «Вперёд»/«Назад», и их обложки успели подгрузиться; позиция в истории не меняется.
    public void PlanNeighbors(List<string> activeTracks, string path, int reach)
    {
        if (activeTracks.Count < 2) return;

        if (_history.Count == 0)
            StartHistoryWith(path);

        int found = FindNearestIndex(path);
        if (found < 0) return;

        while (_history.Count - 1 - found < reach)
            AddToHistory(GetNext(activeTracks, _history[^1]), shown: false);

        while (found < reach)
        {
            InsertAtHistoryStart(GetNext(activeTracks, _history[0]));
            found++;
            if (_historyIndex >= 0) _historyIndex++;
        }
    }

    // Один трек может встречаться в истории несколько раз: берём вхождение, ближайшее к текущей позиции.
    private int FindNearestIndex(string path)
    {
        int found = -1;
        for (int i = 0; i < _history.Count; i++)
        {
            if (_history[i] == path && (found < 0 || Math.Abs(i - _historyIndex) < Math.Abs(found - _historyIndex)))
                found = i;
        }

        return found;
    }

    // Генерирует новый случайный трек и дописывает его в конец истории шафла — вызывается
    // только когда двигаться вперёд по уже существующей истории больше некуда.
    public string AppendNew(List<string> activeTracks, string? currentPath)
    {
        var next = GetNext(activeTracks, currentPath);

        if (_history.Count == 0 && currentPath != null)
            StartHistoryWith(currentPath);

        AddToHistory(next, shown: false);
        _historyIndex = _history.Count - 1;
        return next;
    }

    // Зеркальный аналог AppendNew для случая "назад" — вызывается только когда
    // в истории шафла ещё нет ничего раньше текущего трека.
    public string PrependNew(List<string> activeTracks, string? currentPath)
    {
        var prev = GetNext(activeTracks, currentPath);

        if (_history.Count == 0 && currentPath != null)
            StartHistoryWith(currentPath);

        InsertAtHistoryStart(prev);
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
        // В файле настроек не видно, какие записи реально играли, поэтому после восстановления считаем показанными все.
        _shown.AddRange(Enumerable.Repeat(true, _history.Count));

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

            _committedIndex = _historyIndex;
        }

        _bag = (settings.ShuffleBag ?? new List<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path) && activePaths.Contains(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
