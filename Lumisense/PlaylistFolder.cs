using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AudioPlayer;

/// <summary>
/// Группа треков в плейлисте — либо папка, добавленная целиком (с рекурсивным сканированием),
/// либо набор отдельных файлов ("Отдельные файлы"). Группу можно выключить целиком —
/// тогда её треки остаются видны в списке, но пропускаются при "Далее/Назад/Перемешать"
/// и при автопереходе к следующему треку.
/// </summary>
public class PlaylistFolder : INotifyPropertyChanged
{
    // Стабильный идентификатор — не зависит от порядка, используется только внутри сессии
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    // Путь к папке на диске, из которой были добавлены файлы. Null — для группы отдельных файлов
    // и для папок, созданных вручную ("Новую папку…" в меню добавления).
    public string? SourcePath { get; init; }

    public string DisplayName { get; init; } = "";

    // true только у единственной автосоздаваемой группы "Отдельные файлы" (см. AddLooseFiles
    // в MainWindow.xaml.cs) — отличает её от папок, созданных вручную через "Новую папку…",
    // у которых SourcePath тоже null, но это отдельные именованные группы.
    public bool IsLooseFilesBucket { get; init; }

    // Можно ли добавлять файлы прямо в эту группу кнопкой в её заголовке (см. AddFilesToFolderButton_Click)
    // — да для всего, что не привязано к папке на диске: и для "Отдельные файлы", и для ручных папок.
    public bool CanAddFilesDirectly => SourcePath == null;

    // Можно ли проверить эту группу на новые треки, появившиеся на диске после добавления
    // (см. RescanFolderButton_Click / RescanFolderForNewTracks) — только для групп, реально
    // привязанных к папке на диске. У "Отдельные файлы" и ручных папок нет источника на диске,
    // который можно было бы пересканировать.
    public bool CanRescan => SourcePath != null;

    // Полные пути к файлам, в порядке добавления
    public List<string> Tracks { get; } = new();

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

    private static string TrackWord(int count)
    {
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
