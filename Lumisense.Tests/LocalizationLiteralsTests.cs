using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

// Находит в исходниках приложения русские строки, которые передаются в LocalizationService.Translate/Format,
// и проверяет, что у каждой есть английский перевод. Без перевода в английском интерфейсе осталась бы русская фраза.
public sealed class LocalizationLiteralsTests
{
    private static readonly Regex TranslateCall = new(
        @"(?:LocalizationService\.(?:Translate|Format)|(?<![\w.])Translate)\(\s*""((?:[^""\\]|\\.)*)""",
        RegexOptions.Compiled);

    private static readonly Regex Cyrillic = new(@"[\u0400-\u04FF]", RegexOptions.Compiled);

    // Атрибуты, которые LocalizationService.Apply переводит на месте.
    private static readonly Regex XamlText = new(
        @"\b(?:Text|Content|ToolTip|Header|Title|PlaceholderText)=""([^""]*)""", RegexOptions.Compiled);

    // Надписи окна обновлений в XAML — запасной текст: в коде они задаются через LocalizationKey из LocalizationResources.
    private static readonly HashSet<string> ResourceDrivenLiterals = new(StringComparer.Ordinal)
    {
        "Подготовить компактные обновления",
        "Добровольно скачайте полный пакет текущей MSI-версии один раз. Он станет локальной базой, и следующие совместимые обновления смогут использовать значительно меньшие delta-пакеты. Плеер не будет обновлён или перезапущен.",
        "Скачать базовый пакет",
        "Удалить старую EXE-копию",
        "Открыть мастер удаления",
        "Проверить зеркала",
        "Приостановить",
        "Отменить загрузку"
    };

    private static string ProjectDirectory([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "Lumisense"));

    private static bool IsSourceFile(string path)
    {
        string name = Path.GetFileName(path);
        if (name is "LocalizationService.cs" or "LocalizationResources.cs")
            return false;
        string normalized = path.Replace('\\', '/');
        return !normalized.Contains("/obj/", StringComparison.Ordinal) && !normalized.Contains("/bin/", StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRussianTranslateLiteral_HasAnEnglishTranslation()
    {
        string directory = ProjectDirectory();
        if (!Directory.Exists(directory))
            return; // тесты запущены без исходников приложения: сканировать нечего

        var missing = new SortedSet<string>(StringComparer.Ordinal);
        int checkedLiterals = 0;
        foreach (string file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories).Where(IsSourceFile))
        {
            string source = File.ReadAllText(file);
            foreach (Match match in TranslateCall.Matches(source))
            {
                string literal = Regex.Unescape(match.Groups[1].Value);
                if (!Cyrillic.IsMatch(literal))
                    continue;

                checkedLiterals++;
                if (!LocalizationService.HasEnglishTranslation(literal))
                    missing.Add(Path.GetFileName(file) + ": " + literal);
            }
        }

        Assert.True(checkedLiterals > 50, $"Нашлось слишком мало строк для проверки ({checkedLiterals}): регулярное выражение или путь к исходникам сломаны.");
        Assert.Equal(new List<string>(), missing.ToList());
    }

    [Fact]
    public void EveryRussianXamlLiteral_HasAnEnglishTranslation()
    {
        string directory = ProjectDirectory();
        if (!Directory.Exists(directory))
            return; // тесты запущены без исходников приложения: сканировать нечего

        var missing = new SortedSet<string>(StringComparer.Ordinal);
        int checkedLiterals = 0;
        foreach (string file in Directory.EnumerateFiles(directory, "*.xaml", SearchOption.AllDirectories).Where(IsSourceFile))
        {
            string source = File.ReadAllText(file);
            foreach (Match match in XamlText.Matches(source))
            {
                string literal = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
                if (!Cyrillic.IsMatch(literal) || literal.TrimStart().StartsWith('{') || ResourceDrivenLiterals.Contains(literal))
                    continue;

                checkedLiterals++;
                if (!LocalizationService.HasEnglishTranslation(literal))
                    missing.Add(Path.GetFileName(file) + ": " + literal);
            }
        }

        Assert.True(checkedLiterals > 100, $"Нашлось слишком мало строк для проверки ({checkedLiterals}): регулярное выражение или путь к исходникам сломаны.");
        Assert.Equal(new List<string>(), missing.ToList());
    }
}
