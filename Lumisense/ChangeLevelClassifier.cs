namespace Lumisense;

// Определяет вес строки changelog (Patch/Minor/Major) по смыслу текста, а не по type — ломает
// старые данные → Major, новая крупная сущность → Minor, остальное → Patch. Правится списками ниже.
public static class ChangeLevelClassifier
{
    public enum Level
    {
        Patch = 0,
        Minor = 1,
        Major = 2,
    }

    // Major: несовместимые изменения — старые данные/настройки/поведение перестают работать.
    // Одиночное "removed" Major не даёт (см. ChangelogLoader.BumpForChanges).
    private static readonly string[] MajorMarkers =
    {
        "смена архитектур",
        "переход на новую архитектур",
        "несовместим",
        "потеря совместимост",
        "старый формат данных",
        "прежний формат данных",
        "формат данных изменил",
        "формат данных изменен",
        "формат данных изменён",
        "старые данные",
        "больше не работ",
        "перестанут работать",
        "перестал работать",
        "удалена старая систем",
        "удалена старая архитектур",
        "полностью удалена систем",
        "breaking change",
    };

    // Minor: крупная новая возможность.
    // Формулировки из ТЗ ("Добавлено окно настроек", "Добавлен мини-плеер") и родовые признаки.
    private static readonly string[] MinorFeatureMarkers =
    {
        "окно настроек",
        "мини-плеер",
        "миниплеер",
        "полноэкранн",
        "система патчноутов",
        "патчноут",
        "автосохранени",
        "система поиска",
        "новая архитектурная возможност",
        "архитектурная возможност",
        "раздел настроек",
        "функционал плейлист",
        "новую подсистем",
        "новая подсистем",
        "новое окно",
        "новый экран",
        "новый режим",
        "очередь «играть следующим»",
        "обработка недоступных файлов",
        "фактическое устройство",
        "новая систем",
        "новую систем",
        "нового режима работы",
        "режим работы",
    };

    // "поддержка" сама по себе слишком общая ("поддержка тем" может быть мелочью) — признак
    // крупного изменения только вместе с одним из этих слов рядом.
    private static readonly string[] TechnologySupportContext =
    {
        "smtc", "технологи", "формата", "протокол", "устройств", "windows",
    };

    // Родовые "крупные" существительные: вместе с NewWordMarkers ловят формулировки вроде
    // "Добавлена новая большая система статистики".
    private static readonly string[] BigEntityNouns =
    {
        "режим", "окно", "экран", "систем", "подсистем", "раздел настроек", "архитектур",
    };

    private static readonly string[] NewWordMarkers = { "новый", "новая", "новое", "новую", "новых" };

    // Вместе с BreakingRemovalNouns отличают удаление старой системы (Major) от "Убрана кнопка Y"
    // (Patch); само по себе "удал..." Major не даёт, см. ChangelogLoader.BumpForChanges.
    private static readonly string[] RemovalWordMarkers = { "удал", "убра", "снят" };

    // Сущности, снятие которых само по себе ломает совместимость (в отличие от BigEntityNouns).
    private static readonly string[] BreakingRemovalNouns = { "систем", "подсистем", "архитектур", "формат данных" };

    public static Level Classify(ChangeItem change)
    {
        var text = (change.Text ?? "").ToLowerInvariant();
        if (text.Length == 0) return Level.Patch;

        if (ContainsAny(text, MajorMarkers))
            return Level.Major;

        if (ContainsAny(text, RemovalWordMarkers) && ContainsAny(text, BreakingRemovalNouns))
            return Level.Major;

        if (ContainsAny(text, MinorFeatureMarkers))
            return Level.Minor;

        if (text.Contains("поддержк") && ContainsAny(text, TechnologySupportContext))
            return Level.Minor;

        if (ContainsAny(text, NewWordMarkers) && ContainsAny(text, BigEntityNouns))
            return Level.Minor;

        return Level.Patch;
    }

    private static bool ContainsAny(string text, string[] markers)
    {
        foreach (var marker in markers)
        {
            if (text.Contains(marker))
                return true;
        }
        return false;
    }
}
