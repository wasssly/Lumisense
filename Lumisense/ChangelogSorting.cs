using System.Globalization;
using System.Linq;

namespace AudioPlayer;

/// <summary>Разбирает поле "date" записи changelog.json в настоящую DateTime — для сортировки
/// по дате в окне списка изменений. Понимает ISO ("2026-07-12") и русский формат с названием
/// месяца ("12 июля 2026"). Если это не дата, а произвольный текст вроде "Первый релиз" —
/// возвращает DateTime.MinValue, так что при сортировке по дате такая запись естественно
/// оказывается самой старой (что обычно и есть правда для первого релиза).</summary>
public static class ChangelogDateParser
{
    private static readonly Dictionary<string, int> RussianMonths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["января"] = 1, ["февраля"] = 2, ["марта"] = 3, ["апреля"] = 4,
        ["мая"] = 5, ["июня"] = 6, ["июля"] = 7, ["августа"] = 8,
        ["сентября"] = 9, ["октября"] = 10, ["ноября"] = 11, ["декабря"] = 12
    };

    // Числовые форматы даты — в первую очередь "дд.ММ.гггг" / "дд.ММ.гг", то, что реально
    // пишут в поле "date" вручную (см. changelog.json). Разбираем их ЯВНЫМИ шаблонами, а не
    // общим DateTime.TryParse: под инвариантной культурой TryParse трактует "." в порядке
    // месяц.день, а не день.месяц — из-за этого "05.07.26" читалось как 7 мая вместо 5 июля
    // (тихая ошибка, дата валидна, но неверна), а "13.07.26" (день больше 12 — невозможный
    // номер месяца) не распознавалось вовсе и проваливалось в DateTime.MinValue (то есть
    // считалось "самой старой" датой). Оба случая ломали не только сортировку по дате, но и
    // номера версий — ChangelogLoader.AssignComputedFields нумерует версии как раз по
    // хронологическому порядку этих дат.
    private static readonly string[] ExactDateFormats =
    {
        "dd.MM.yyyy", "dd.MM.yy", "d.M.yyyy", "d.M.yy",
        "yyyy-MM-dd", "yyyy.MM.dd"
    };

    public static DateTime Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return DateTime.MinValue;

        var trimmed = text.Trim();

        if (DateTime.TryParseExact(trimmed, ExactDateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsedExact))
            return parsedExact;

        if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedIso))
            return parsedIso;

        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 &&
            int.TryParse(parts[0], out var day) &&
            RussianMonths.TryGetValue(parts[1], out var month) &&
            int.TryParse(parts[2], out var year))
        {
            try { return new DateTime(year, month, day); }
            catch (ArgumentOutOfRangeException) { return DateTime.MinValue; }
        }

        return DateTime.MinValue;
    }
}
