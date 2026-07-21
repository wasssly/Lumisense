using System.IO;
using System.Linq;
using System.Text.Json;

namespace AudioPlayer;

/// <summary>
/// Читает список версий из changelog.json — он встроен прямо в сборку как EmbeddedResource
/// (см. Lumisense.csproj), а не лежит рядом с .exe отдельным файлом: специально, чтобы его
/// нельзя было открыть и поправить прямо из папки установленной программы. Редактировать его
/// нужно в исходниках, в файле Changelog/changelog.json (при пересборке содержимое запекается
/// в сборку заново) — любым текстовым редактором. Формат самих записей описан ниже.
///
/// Номер версии в файле писать не нужно — он вычисляется автоматически (см. AssignComputedFields
/// и BumpForChanges ниже) по правилам SemVer (semver.org, "major.minor.patch") и зависит от
/// СМЫСЛА текста в списке изменений каждой записи, а не просто от формального типа
/// ("added"/"changed"/"fixed"/"removed") или от того, что запись вообще есть. Например,
/// "added: Добавлена кнопка" и "added: Добавлен полноэкранный режим" имеют один и тот же type,
/// но первое — мелкая правка (Patch), а второе — крупная новая возможность (Minor); классификация
/// конкретной формулировки — в ChangeLevelClassifier, а сам расчёт — в BumpForChanges:
///   - если хотя бы одно изменение по смыслу текста несовместимое (сменилась архитектура,
///     старый формат/данные больше не работают) → увеличивается major (X.0.0);
///   - иначе если хотя бы одно изменение — крупная новая или переработанная целиком возможность
///     (новый режим/окно/экран/система и т.п.) → увеличивается minor (X.Y.0);
///   - иначе (только мелкие правки/исправления, либо список пуст) → увеличивается patch (X.Y.Z).
/// Если крупных возможностей в записи сразу несколько — версия всё равно растёт только один раз.
/// Самая старая по дате запись — это база отсчёта, "1.0.0"; дальше версия каждой следующей
/// записи считается от версии предыдущей по этому правилу. Записи сортируются по полю "date"
/// от самой старой к самой новой — порядок в самом файле значения не имеет. "Текущая версия" —
/// та запись, у которой дата самая свежая (не обязательно последняя по счёту в файле).
///
/// Формат — простой JSON-массив, порядок записей в самом файле значения не имеет (сортировка
/// и определение текущей версии всё равно делаются по датам). У каждой строки изменения — свой
/// тип ("added" / "changed" / "fixed" / "removed", см. ChangeTypeCatalog); неизвестный или
/// отсутствующий type трактуется как "changed". И у версии, и у отдельного пункта изменения
/// есть необязательное поле "image" — либо прямая ссылка (http/https), либо имя файла (или
/// относительный путь) внутри этой же папки Changelog, рядом с changelog.json (см.
/// ChangelogImageResolver.Resolve):
/// [
///   {
///     "date": "12 июля 2026",
///     "image": "release-1.2.png",
///     "changes": [
///       { "type": "added", "text": "Что-то добавили", "image": "new-feature.png" },
///       { "type": "fixed", "text": "Что-то починили" }
///     ]
///   },
///   {
///     "date": "Первый релиз",
///     "changes": [ { "type": "added", "text": "..." } ]
///   }
/// ]
/// </summary>
public static class ChangelogLoader
{
    // Раньше changelog.json лежал рядом с .exe и читался через File.ReadAllText — теперь он
    // встроен в саму сборку (EmbeddedResource, см. Lumisense.csproj) именно для того, чтобы его
    // нельзя было открыть и подправить прямо из папки установки. Имя ресурса .NET SDK собирает
    // как "{RootNamespace}.{путь с \ заменёнными на .}" — здесь это "AudioPlayer.Changelog.
    // changelog.json", но чтобы не завязываться на точное совпадение (переименуют RootNamespace
    // — и точное имя разъедется), просто ищем среди всех ресурсов сборки тот, что заканчивается
    // на "changelog.json".
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

    /// <summary>Публичная точка входа для автоматического расчёта версий — берёт список
    /// changelog-записей (без заполненных Version/IsCurrent) и проставляет оба поля каждой
    /// записи по правилам, описанным в комментарии к классу выше. Список изменяется на месте
    /// (в тех же объектах ChangelogEntry) и возвращается тем же экземпляром — специально
    /// вынесена отдельной публичной функцией с таким сигнатурным видом, чтобы расчёт версий
    /// можно было гонять и не только из Load() (например, из тестов на произвольном наборе
    /// записей).</summary>
    public static List<ChangelogEntry> CalculateVersions(List<ChangelogEntry> changelogs)
    {
        AssignComputedFields(changelogs);
        return changelogs;
    }

    // Простановка вычисляемых полей — того, что раньше приходилось писать в JSON руками:
    //  - Version: посчитан по смыслу изменений (semver-стиль major.minor.patch), см.
    //    комментарий у BumpForChanges ниже;
    //  - IsCurrent: у записи с самой свежей датой (а не у первой в файле — записи в файле
    //    можно перечислять в любом порядке, "текущая версия" определяется исключительно
    //    по дате).
    // Записи с нераспознанной датой (ChangelogDateParser возвращает DateTime.MinValue —
    // например, "Первый релиз") в этой сортировке естественно оказываются самыми старыми.
    private static void AssignComputedFields(List<ChangelogEntry> entries)
    {
        var byDateAscending = entries
            .Select((entry, originalIndex) => (entry, originalIndex))
            .OrderBy(x => ChangelogDateParser.Parse(x.entry.Date))
            .ThenBy(x => x.originalIndex) // стабильный порядок при одинаковых/пустых датах
            .ToList();

        // Самая первая (самая старая) версия — это база отсчёта, "1.0.0". Дальше номер
        // каждой следующей версии считается от номера предыдущей и зависит от того, ЧТО
        // реально лежит в её списке изменений — а не просто от факта, что запись есть.
        int major = 1, minor = 0, patch = 0;

        for (int i = 0; i < byDateAscending.Count; i++)
        {
            var entry = byDateAscending[i].entry;

            if (i > 0)
                (major, minor, patch) = BumpForChanges(major, minor, patch, entry.Changes);

            entry.Version = $"{major}.{minor}.{patch}";
        }

        foreach (var entry in entries)
            entry.IsCurrent = false;

        byDateAscending[^1].entry.IsCurrent = true; // запись с самой свежей датой — последняя после сортировки по возрастанию
    }

    // Решает, какую часть номера версии увеличить — глядя не на ТИПЫ изменений внутри записи,
    // а на СМЫСЛ ТЕКСТА каждого из них (см. ChangeLevelClassifier): это то, что явно требует
    // ТЗ на автоматический расчёт версии — примитивное правило "есть added → minor" неверно,
    // потому что "Добавлена кнопка" и "Добавлен полноэкранный режим" оба имеют type "added", но
    // по масштабу это Patch и Minor соответственно.
    //
    // Берём максимальный уровень (Major > Minor > Patch) среди ВСЕХ изменений записи и бампаем
    // версию один раз согласно этому уровню:
    //  - Major (несовместимое изменение — сменилась архитектура, старый формат/данные больше
    //    не работают) → версия X.0.0, минор и патч сбрасываются в 0;
    //  - Minor (появилась хотя бы одна крупная новая возможность — новый режим/окно/экран/
    //    система и т.п.) → версия X.Y.0, патч сбрасывается в 0. Если крупных возможностей
    //    несколько сразу — версия всё равно растёт только один раз, а не по числу таких пунктов;
    //  - иначе (только мелкие правки/исправления — Patch) → версия X.Y.Z, увеличивается сама.
    // Пустой список изменений трактуется как Patch, чтобы версия не оставалась той же самой.
    //
    // "removed" само по себе НЕ поднимает Major — снятие функции ломает совместимость только
    // если это явно следует из текста (например "удалена старая система X"); обычное
    // "Убрана кнопка Y" по смыслу — такая же мелкая правка, как и любой другой Patch.
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
