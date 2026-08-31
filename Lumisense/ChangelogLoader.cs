using System.IO;
using System.Linq;
using System.Text.Json;

namespace Lumisense;

// Читает список версий из changelog.json — встроен в сборку как EmbeddedResource
// (Lumisense.csproj), не лежит рядом с .exe отдельным файлом. Редактировать — в исходниках,
// Changelog/changelog.json.
//
// Номер версии не пишется в файле — считается автоматически по SemVer, по смыслу текста
// изменений, а не по формальному type (классификация — ChangeLevelClassifier, расчёт —
// BumpForChanges). База отсчёта — "1.0.0".
//
// Поле "date" необязательное — старые записи датированы, новые нет (changelog привязан к
// версии, а не к дате), поэтому сортировка "по хронологии" считается по номеру версии.
// Порядок: сначала записи с датой (от старой к новой), затем без даты (как в файле). "Текущая
// версия" — всегда последняя в этом порядке.
//
// Формат — JSON-массив, у каждого изменения свой type (added/changed/fixed/removed,
// неизвестный трактуется как changed), опциональное "image" у версии и у пункта:
// [
//   { "date": "12 июля 2026", "image": "release-1.2.png",
//     "changes": [ { "type": "added", "text": "Что-то добавили", "image": "new-feature.png" } ] },
//   { "date": "Первый релиз", "changes": [ { "type": "added", "text": "..." } ] }
// ]
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

    // Публичная точка входа для расчёта версий — проставляет Version/IsCurrent списку записей
    // на месте и возвращает тот же экземпляр; вынесена отдельно, чтобы гонять расчёт не только
    // из Load() (например, из тестов на произвольном наборе записей)
    public static List<ChangelogEntry> CalculateVersions(List<ChangelogEntry> changelogs)
    {
        AssignComputedFields(changelogs);
        return changelogs;
    }

    // Version считается по смыслу изменений (см. BumpForChanges), IsCurrent — у самой последней
    // записи в общем порядке (не обязательно последней по счёту в файле).
    //
    // Порядок роста версий — два куска подряд: сначала записи С датой (от старой к новой,
    // порядок в файле не важен), затем записи БЕЗ даты (в порядке из файла — раз даты нет,
    // это единственный способ понять, что было раньше). Датированные записи от этого не
    // меняют номер версии по сравнению с тем, что было раньше — недатированные просто
    // продолжают ту же последовательность дальше.
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

        // Самая первая (самая старая) версия — это база отсчёта, "1.0.0". Дальше номер
        // каждой следующей версии считается от номера предыдущей и зависит от того, ЧТО
        // реально лежит в её списке изменений — а не просто от факта, что запись есть.
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

    // Смотрим не на type изменений, а на смысл текста каждого (ChangeLevelClassifier) — "есть
    // added → minor" неверно, "Добавлена кнопка" и "Добавлен полноэкранный режим" оба added,
    // но по масштабу это Patch и Minor.
    //
    // Берём максимальный уровень среди всех изменений записи и бампаем версию один раз:
    // Major (несовместимое изменение) → X.0.0, Minor (крупная новая возможность) → X.Y.0
    // (несколько таких пунктов сразу всё равно поднимают версию один раз), иначе → X.Y.Z.
    // Пустой список — тоже Patch, чтобы версия не оставалась той же самой.
    //
    // "removed" само по себе Major не даёт — только если из текста явно следует поломка
    // совместимости ("удалена старая система X"), а не просто "Убрана кнопка Y".
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
