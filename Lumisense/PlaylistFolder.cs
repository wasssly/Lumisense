using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Lumisense;

// Группа треков в плейлисте — либо папка, добавленная целиком (рекурсивное сканирование),
// либо набор отдельных файлов ("Отдельные файлы"). Группу можно выключить целиком — треки
// остаются видны в списке, но пропускаются при "Далее/Назад/Перемешать" и автопереходе.
public class PlaylistFolder : INotifyPropertyChanged
{
    // Стабильный идентификатор — не зависит от порядка, используется только внутри сессии
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    // Путь к папке на диске, из которой были добавлены файлы. Null — для группы отдельных файлов
    // и для папок, созданных вручную ("Новую папку…" в меню добавления).
    public string? SourcePath { get; init; }

    private string _persistedDisplayName = "";

    // Имя системной группы отдельных файлов не является пользовательским: оно отображается
    // на текущем языке, а в settings.json сохраняется исходное значение только для обратной
    // совместимости. Имена папок, созданных пользователем, остаются неизменными.
    public string DisplayName
    {
        get => IsLooseFilesBucket ? LocalizationService.Get(LocalizationKey.PlaylistLooseFiles) : _persistedDisplayName;
        init => _persistedDisplayName = value;
    }

    public string PersistedDisplayName => _persistedDisplayName;

    // true только у единственной автосоздаваемой группы "Отдельные файлы" (см. AddLooseFiles
    // в MainWindow.xaml.cs) — отличает её от папок, созданных вручную через "Новую папку…",
    // у которых SourcePath тоже null, но это отдельные именованные группы.
    public bool IsLooseFilesBucket { get; init; }

    // true только у единственной виртуальной группы "Избранное" (см. MainWindow._favoritesFolder) —
    // её содержимое каждый раз пересобирается из FavoritesManager, а не хранится и не
    // редактируется как обычная группа: нельзя добавить в неё файлы напрямую, пересканировать
    // или удалить её саму — только снять сердечко с отдельного трека (см. RemoveTrackMenuItem_Click
    // и MainWindow.FavoriteButton_Click).
    public bool IsFavoritesGroup { get; init; }

    // Можно ли добавлять файлы прямо в эту группу кнопкой в её заголовке (см. AddFilesToFolderButton_Click)
    // — да для всего, что не привязано к папке на диске: и для "Отдельные файлы", и для ручных папок.
    // Виртуальная группа "Избранное" исключение: у неё SourcePath тоже null, но добавлять в неё
    // файлы напрямую нельзя — она собирается только из отмеченных сердечком треков.
    public bool CanAddFilesDirectly => SourcePath == null && !IsFavoritesGroup;

    // Можно ли проверить эту группу на новые треки, появившиеся на диске после добавления
    // (см. RescanFolderButton_Click / RescanFolderForNewTracks) — только для групп, реально
    // привязанных к папке на диске. У "Отдельные файлы" и ручных папок нет источника на диске,
    // который можно было бы пересканировать.
    public bool CanRescan => SourcePath != null;

    // ObservableCollection, а не List — SubtitleText ("N треков · путь") зависит от Tracks.Count
    // и должен обновляться сам при любом изменении списка. AddRange/RemoveAll добавлены ниже
    // как extension-методы, которых у ObservableCollection нет из коробки.
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

// Чтобы код, писавшийся под List<string>.AddRange/.RemoveAll, продолжил работать после
// перехода Tracks на ObservableCollection<string>. Каждый Add/Remove поднимает своё
// CollectionChanged — на нём держится реактивность SubtitleText.
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
