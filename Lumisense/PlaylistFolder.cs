using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Lumisense;

// Группа треков — папка целиком или набор отдельных файлов. Выключенная группа остаётся
// видна, но пропускается при "Далее/Назад/Перемешать" и автопереходе.
public class PlaylistFolder : INotifyPropertyChanged
{
    // Стабильный идентификатор — не зависит от порядка, используется только внутри сессии
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    // Путь к папке на диске, из которой были добавлены файлы. Null — для группы отдельных файлов
    // и для папок, созданных вручную ("Новую папку…" в меню добавления).
    public string? SourcePath { get; init; }

    private string _persistedDisplayName = "";

    // Имя системной группы показывается на текущем языке, а в settings.json остаётся исходным (обратная
    // совместимость); имена пользовательских папок не меняются.
    public string DisplayName
    {
        get => IsLooseFilesBucket ? LocalizationService.Get(LocalizationKey.PlaylistLooseFiles) : _persistedDisplayName;
        init => _persistedDisplayName = value;
    }

    public string PersistedDisplayName => _persistedDisplayName;

    // true только у автосоздаваемой группы "Отдельные файлы" (см. AddLooseFiles): у ручных папок SourcePath
    // тоже null, но это отдельные именованные группы.
    public bool IsLooseFilesBucket { get; init; }

    // true только у виртуальной группы "Избранное": она пересобирается из FavoritesManager и не редактируется
    // как обычная (без добавления, пересканирования и удаления — только снять сердечко с трека).
    public bool IsFavoritesGroup { get; init; }

    // Можно ли добавлять файлы кнопкой в заголовке: для всего без папки на диске, кроме "Избранного"
    // (оно собирается только из треков с сердечком).
    public bool CanAddFilesDirectly => SourcePath == null && !IsFavoritesGroup;

    // Только для групп с папкой на диске: у "Отдельные файлы" и ручных папок пересканировать нечего.
    public bool CanRescan => SourcePath != null;

    // ObservableCollection, чтобы SubtitleText ("N треков · путь") обновлялся при любом изменении списка;
    // AddRange/RemoveAll ниже — extension-методы, которых у него нет.
    public ObservableCollection<string> Tracks { get; } = new();

    public PlaylistFolder()
    {
        Tracks.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SubtitleText));
    }

    private bool _isEnabled = true;

    // Включена ли группа в проигрывание. Привязано двусторонним биндингом к чекбоксу в UI.
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            OnPropertyChanged();
        }
    }

    private bool _isExpanded = true;

    // Показан ли список треков этой группы в UI (сворачивание/разворачивание по клику на заголовок)
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
        }
    }

    public string SubtitleText
    {
        get
        {
            var word = TrackWord(Tracks.Count);
            return SourcePath != null
                ? $"{Tracks.Count} {word} · {SourcePath}"
                : $"{Tracks.Count} {word}";
        }
    }

    public void RefreshLocalizedSubtitle()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(SubtitleText));
    }

    private static string TrackWord(int count)
    {
        if (LocalizationService.IsEnglish)
            return count == 1 ? "track" : "tracks";

        int hundredsRemainder = count % 100;
        if (hundredsRemainder is >= 11 and <= 14) return "треков";

        return (count % 10) switch
        {
            1 => "трек",
            2 or 3 or 4 => "трека",
            _ => "треков"
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

// Чтобы код под List<string>.AddRange/.RemoveAll работал после перехода на
// ObservableCollection. Каждый Add/Remove поднимает CollectionChanged — на нём SubtitleText.
public static class ObservableCollectionExtensions
{
    public static void AddRange<T>(this ObservableCollection<T> collection, IEnumerable<T> items)
    {
        foreach (var item in items)
            collection.Add(item);
    }

    public static void RemoveAll<T>(this ObservableCollection<T> collection, Func<T, bool> predicate)
    {
        // Сначала собираем, что удалять, а не удаляем прямо во время перечисления — менять
        // коллекцию, по которой в этот момент идёт foreach/enumerator, нельзя.
        var toRemove = collection.Where(predicate).ToList();
        foreach (var item in toRemove)
            collection.Remove(item);
    }
}
