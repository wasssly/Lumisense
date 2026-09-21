using System.IO;
using System.Linq;
using System.Text.Json;

namespace Lumisense;

// Читает версии из встроенного changelog.json (Changelog/changelog.json в исходниках). Номер
// версии не хранится в файле — считается по SemVer из текста изменений (ChangeLevelClassifier).
public static class ChangelogLoader
{
    // Ищем ресурс по суффиксу имени, а не по точному "Lumisense.Changelog.changelog.json" —
    // так переименование RootNamespace не сломает поиск
    public static List<ChangelogEntry> Load()
    {
        try
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("changelog.json", StringComparison.OrdinalIgnoreCase));

            if (resourceName != null)
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    using var reader = new StreamReader(stream);
                    var json = reader.ReadToEnd();
                    var entries = JsonSerializer.Deserialize<List<ChangelogEntry>>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (entries is { Count: > 0 })
                    {
                        CalculateVersions(entries);
                        return entries;
                    }
                }
            }
        }
        catch
        {
            // Ресурс отсутствует, повреждён или не читается — просто покажем встроенный список ниже
        }

        var fallback = DefaultEntries();
        CalculateVersions(fallback);
        return fallback;
    }

    // Проставляет Version/IsCurrent на месте и возвращает тот же список; отдельно от Load() — для тестов на произвольном наборе.
    public static List<ChangelogEntry> CalculateVersions(List<ChangelogEntry> changelogs)
    {
        AssignComputedFields(changelogs);
        return changelogs;
    }

    // Version считается по смыслу изменений (BumpForChanges), IsCurrent — у последней записи в общем порядке.
    // Порядок роста: сначала записи с датой (по возрастанию), затем без даты — в порядке файла.
    private static void AssignComputedFields(List<ChangelogEntry> entries)
    {
        var dated = entries
            .Select((entry, originalIndex) => (entry, originalIndex))
            .Where(x => !string.IsNullOrWhiteSpace(x.entry.Date))
            .OrderBy(x => ChangelogDateParser.Parse(x.entry.Date))
            .ThenBy(x => x.originalIndex) // стабильный порядок при одинаковых/нераспознанных датах
            .Select(x => x.entry);

        var undated = entries.Where(e => string.IsNullOrWhiteSpace(e.Date));

        var ordered = dated.Concat(undated).ToList();

        // Самая старая версия — база "1.0.0"; номер каждой следующей зависит от содержимого её списка изменений.
        int major = 1, minor = 0, patch = 0;

        for (int i = 0; i < ordered.Count; i++)
        {
            var entry = ordered[i];

            if (i > 0)
                (major, minor, patch) = BumpForChanges(major, minor, patch, entry.Changes);

            entry.Version = $"{major}.{minor}.{patch}";
        }

        foreach (var entry in entries)
            entry.IsCurrent = false;

        if (ordered.Count > 0)
            ordered[^1].IsCurrent = true; // последняя запись в общем порядке — датированные по возрастанию, затем недатированные по файлу
    }

    // Уровень — по смыслу текста (ChangeLevelClassifier), не по type: берём максимальный среди изменений записи,
    // бамп один раз: Major → X.0.0, Minor → X.Y.0, иначе X.Y.Z (пустой список — Patch); "removed" Major не даёт.
    private static (int major, int minor, int patch) BumpForChanges(
        int major, int minor, int patch, List<ChangeItem> changes)
    {
        var level = changes.Count == 0
            ? ChangeLevelClassifier.Level.Patch
            : changes.Max(ChangeLevelClassifier.Classify);

        return level switch
        {
            ChangeLevelClassifier.Level.Major => (major + 1, 0, 0),
            ChangeLevelClassifier.Level.Minor => (major, minor + 1, 0),
            _ => (major, minor, patch + 1),
        };
    }

    // Используется, если changelog.json не найден или не смог прочитаться —
    // чтобы окно списка изменений никогда не оказалось пустым
    private static List<ChangelogEntry> DefaultEntries() => new()
    {
        new ChangelogEntry
        {
            Date = "Первый релиз",
            Changes = new List<ChangeItem>
            {
                new() { Type = "added", Text = "Плейлист по папкам и отдельным файлам — каждую группу можно включать и выключать" },
                new() { Type = "added", Text = "Воспроизведение, пауза, стоп, переключение треков, перемешивание и повтор" },
                new() { Type = "added", Text = "Перемотка и регулировка громкости мышью по всей полосе, а не только по бегунку" },
                new() { Type = "added", Text = "Мини-плеер с обложкой, прогрессом и управлением поверх других окон" },
                new() { Type = "added", Text = "Глобальные горячие клавиши, которые работают из любого окна, даже когда плеер свёрнут" },
                new() { Type = "added", Text = "Интеграция с «Сейчас воспроизводится» в Windows 11 и сворачивание в трей" },
                new() { Type = "added", Text = "Светлая и тёмная тема, гибкая настройка окна и мини-плеера" },
            }
        }
    };
}
