using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Lumisense.Tests;

// Подписи индекса поиска по настройкам заданы в коде, а результаты строятся после прохода локализации. Поэтому у каждой
// русской подписи должен быть перевод в словаре, иначе в английском интерфейсе результаты остаются русскими.
public sealed partial class SettingsSearchIndexLocalizationTests
{
    [GeneratedRegex("""^\s*Add\(\s*"((?:[^"\\]|\\.)*)"\s*,\s*"((?:[^"\\]|\\.)*)"\s*,\s*"[A-Za-z]+"\s*,""", RegexOptions.Multiline)]
    private static partial Regex SearchEntryRegex();

    private static string SourceFile([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "Lumisense", "SettingsWindow.xaml.cs"));

    private static bool HasCyrillic(string text) => text.Any(c => c is >= '\u0400' and <= '\u04FF');

    [Fact]
    public void EverySearchIndexLabelAndPageTitle_HasAnEnglishTranslation()
    {
        string path = SourceFile();
        if (!File.Exists(path)) return; // тесты запущены без исходников приложения

        MatchCollection entries = SearchEntryRegex().Matches(File.ReadAllText(path));
        Assert.True(entries.Count > 80, $"Нашлось слишком мало записей индекса ({entries.Count}): разбор исходника сломан.");

        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match entry in entries)
        {
            foreach (string text in new[] { entry.Groups[1].Value, entry.Groups[2].Value })
            {
                if (HasCyrillic(text) && !LocalizationService.HasEnglishTranslation(text)) missing.Add(text);
            }
        }

        Assert.True(missing.Count == 0, "Нет английского перевода у подписей поиска по настройкам:" + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void ToEnglish_ReturnsTheDictionaryTranslation_RegardlessOfInterfaceLanguage()
    {
        Assert.Equal("Discord privacy: timeline", LocalizationService.ToEnglish("Приватность Discord: таймлайн"));
        Assert.Equal("Нигде не записанная фраза", LocalizationService.ToEnglish("Нигде не записанная фраза"));
        Assert.Equal("", LocalizationService.ToEnglish(""));
    }
}
