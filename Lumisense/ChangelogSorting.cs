using System.Globalization;
using System.Linq;

namespace AudioPlayer;

// Парсит поле "date" из changelog.json (ISO или "12 июля 2026") для сортировки записей.
// Не дата, а произвольный текст вроде "Первый релиз" — считается DateTime.MinValue,
// такая запись просто оказывается самой старой при сортировке.
public static class ChangelogDateParser
{
    private static readonly Dictionary<string, int> RussianMonths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["января"] = 1, ["февраля"] = 2, ["марта"] = 3, ["апреля"] = 4,
        ["мая"] = 5, ["июня"] = 6, ["июля"] = 7, ["августа"] = 8,
        ["сентября"] = 9, ["октября"] = 10, ["ноября"] = 11, ["декабря"] = 12
    };

    // Явные форматы вместо общего DateTime.TryParse: под инвариантной культурой TryParse читает
    // "." как месяц.день, а не день.месяц — "05.07.26" превращалось в 7 мая вместо 5 июля молча,
    // а "13.07.26" вообще не парсилось и падало в MinValue. Ломало и сортировку, и нумерацию версий.
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
