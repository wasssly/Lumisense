using System.Linq;
using System.Windows.Media;

namespace Lumisense;

// Обёртка над ChangelogEntry для окна списка изменений: сама ChangelogEntry — POCO под JSON,
// здесь — то, что нужно для UI (типизированные строки, поиск, ключи сортировки)
public sealed class ChangelogEntryViewModel
{
    public string Version { get; }
    public string Date { get; }
    public bool IsCurrent { get; }
    public string? GitHubReleaseUrl { get; }
    public bool HasGitHubRelease => !string.IsNullOrWhiteSpace(GitHubReleaseUrl);
    public string? GitHubReleaseToolTip => HasGitHubRelease
        ? LocalizationService.Get(LocalizationKey.ChangelogOpenReleaseOnGitHub)
        : null;
    public IReadOnlyList<ChangeItemViewModel> Items { get; }

    // у старых записей дата есть, у новых нет (changelog теперь привязан к версии, а не к дате)
    public bool HasDate => !string.IsNullOrWhiteSpace(Date);

    // не для отображения (в UI показывается исходная строка Date), раньше использовалась для
    // сортировки — теперь для этого ParsedVersion, SortDate оставлен про запас
    public DateTime SortDate { get; }

    // сортировка по версии вместо даты — у старых записей порядок совпадает (версия и
    // вычисляется по хронологии дат), у новых, не привязанных к дате, только версия и остаётся
    // источником правильного порядка. Null, если Version не разобрался — уходит в конец списка
    public System.Version? ParsedVersion { get; }

    // точки по одному цвету на каждый встречающийся тип изменений, порядок постоянный —
    // беглый обзор состава версии в списке слева без необходимости её открывать
    public IReadOnlyList<SolidColorBrush> PresentTypeBrushes { get; }

    // изменения, сгруппированные по типу — то, что показывается в панели деталей справа
    public IReadOnlyList<ChangeGroupViewModel> Groups { get; }

    // готово для Image.Source: либо http/https-ссылка, либо разрешённый локальный путь,
    // null если поле "image" пустое
    public string? ImageSource { get; }

    public bool HasImage => !string.IsNullOrWhiteSpace(ImageSource);

    // Подпись под версией в списке слева должна обновляться вместе с языком интерфейса.
    public string ChangesCountLabel => Items.Count == 0
        ? LocalizationService.Get(LocalizationKey.ChangelogNoDescription)
        : LocalizationService.FormatPlural(LocalizationKey.ChangelogChanges, Items.Count);

    public ChangelogEntryViewModel(ChangelogEntry source, string? gitHubReleaseUrl = null)
    {
        Version = source.Version;
        Date = source.Date;
        IsCurrent = source.IsCurrent;
        GitHubReleaseUrl = gitHubReleaseUrl;
        Items = source.Changes.Select(c => new ChangeItemViewModel(c)).ToList();
        ImageSource = ChangelogImageResolver.Resolve(source.Image);

        SortDate = ChangelogDateParser.Parse(source.Date);
        System.Version.TryParse(source.Version, out var parsedVersion);
        ParsedVersion = parsedVersion;

        var presentKeys = Items.Select(i => i.TypeKey).ToHashSet();
        PresentTypeBrushes = ChangeTypeCatalog.All
            .Where(info => presentKeys.Contains(info.Key))
            .Select(info =>
            {
                var brush = new SolidColorBrush(info.Color);
                brush.Freeze();
                return brush;
            })
            .ToList();

        Groups = ChangeTypeCatalog.All
            .Select(info => new ChangeGroupViewModel(info, Items.Where(i => i.TypeKey == info.Key).ToList()))
            .Where(group => group.Count > 0)
            .ToList();
    }

    // используется цветными фильтрами-чипами в окне списка изменений
    public bool HasType(string typeKey) => Items.Any(i => i.TypeKey == typeKey);

    // совпадает версия, дата, текст изменения или подпись типа с поисковым текстом;
    // пустой запрос совпадает всегда
    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;

        if (Version.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            Date.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var item in Items)
        {
            if (item.Text.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.TypeLabel.Contains(query, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

}
