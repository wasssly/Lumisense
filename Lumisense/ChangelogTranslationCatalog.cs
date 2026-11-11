namespace Lumisense;

// Английский текст пунктов журнала: готовых переводов нет, берётся кэш ChangelogMachineTranslator, иначе показывается русский оригинал.
internal static class ChangelogTranslationCatalog
{
    public static string Translate(string source, bool isEnglish)
    {
        if (!isEnglish || string.IsNullOrEmpty(source)) return source;

        return ChangelogMachineTranslator.Shared.TryGetCached(source, out string machine) ? machine : source;
    }

    // Перевод пункта получен автоматически (для подсказки в окне журнала).
    public static bool IsMachineTranslated(string source, bool isEnglish) =>
        isEnglish && !string.IsNullOrEmpty(source) && ChangelogMachineTranslator.Shared.TryGetCached(source, out _);

    // Пункт ещё стоит перевести: он русский и в кэше его нет.
    public static bool NeedsMachineTranslation(string source) =>
        ChangelogMachineTranslator.IsTranslatable(source) && !ChangelogMachineTranslator.Shared.TryGetCached(source, out _);
}
