using System.Linq;
using System.Windows.Media;

namespace AudioPlayer;

/// <summary>
/// Обёртка над ChangelogEntry для окна "Список изменений". Сама ChangelogEntry — простой
/// POCO под JSON (см. ChangelogLoader) и не занимается отображением; здесь — то, что нужно
/// для UI: типизированные строки изменений (см. ChangeItemViewModel), поиск (включая поиск
/// по названию типа) и ключи для сортировки по дате/версии.
/// </summary>
public sealed class ChangelogEntryViewModel
{
    public string Version { get; }
    public string Date { get; }
    public bool IsCurrent { get; }
    public IReadOnlyList<ChangeItemViewModel> Items { get; }

    /// <summary>Разобранная дата — для сортировки "по дате" (см. ChangelogDateParser).
    /// Не для отображения — в UI показывается исходная строка Date как есть.</summary>
    public DateTime SortDate { get; }

    /// <summary>Цветные точки по одной на каждый встречающийся в версии тип изменений, в
    /// постоянном порядке (Добавлено → Изменено → Исправлено → Удалено) — беглый обзор состава
    /// версии прямо в списке слева, без необходимости её открывать.</summary>
    public IReadOnlyList<SolidColorBrush> PresentTypeBrushes { get; }

    /// <summary>Изменения, сгруппированные по типу (в порядке Добавлено → Изменено →
    /// Исправлено → Удалено) — то, что реально показывается в панели деталей справа: там
    /// они уже не одним плоским списком, а разбиты на подписанные секции по типу.</summary>
    public IReadOnlyList<ChangeGroupViewModel> Groups { get; }

    /// <summary>Картинка версии, готовая к показу в Image.Source (WPF сам сконвертирует
    /// строку через ImageSourceConverter) — либо прямая http/https-ссылка, либо разрешённый
    /// локальный путь. Null, если поле "image" не заполнено — см. ChangelogImageResolver.Resolve.</summary>
    public string? ImageSource { get; }

    public bool HasImage => !string.IsNullOrWhiteSpace(ImageSource);

    /// <summary>Короткая подпись количества изменений с правильным русским склонением —
    /// показывается под версией в списке слева, например "3 изменения".</summary>
    public string ChangesCountLabel => Items.Count == 0
        ? "Нет описания"
        : $"{Items.Count} {Pluralize(Items.Count)}";

    public ChangelogEntryViewModel(ChangelogEntry source)
    {
        Version = source.Version;
        Date = source.Date;
        IsCurrent = source.IsCurrent;
        Items = source.Changes.Select(c => new ChangeItemViewModel(c)).ToList();
        ImageSource = ChangelogImageResolver.Resolve(source.Image);

        SortDate = ChangelogDateParser.Parse(source.Date);

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

    // Поле "image" в changelog.json можно заполнить двумя способами: прямой ссылкой или
    // именем файла внутри папки Changelog — см. ChangelogImageResolver.Resolve, общий и для
    // картинки версии, и для картинки отдельного пункта (ChangeItemViewModel).

    /// <summary>Есть ли среди изменений версии хотя бы одно указанного типа — используется
    /// цветными фильтрами-чипами в окне списка изменений.</summary>
    public bool HasType(string typeKey) => Items.Any(i => i.TypeKey == typeKey);

    /// <summary>Совпадает ли версия, дата, текст изменения или подпись его типа (например,
    /// "исправлено") с поисковым текстом. Пустой запрос совпадает всегда.</summary>
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

    private static string Pluralize(int count)
    {
        int mod100 = count % 100;
        if (mod100 is >= 11 and <= 14) return "изменений";

        return (count % 10) switch
        {
            1 => "изменение",
            2 or 3 or 4 => "изменения",
            _ => "изменений"
        };
    }
}
