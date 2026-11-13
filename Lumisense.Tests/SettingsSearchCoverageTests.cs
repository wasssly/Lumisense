using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Lumisense.Tests;

// Каждая настройка должна находиться поиском: либо её элемент (или карточка с ним) указан в индексе, либо слова её
// подписи есть в подписи и ключевых словах одной из записей. Тест читает XAML и BuildSearchIndex из исходников.
public sealed partial class SettingsSearchCoverageTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] Pages =
    [
        "PageAppearance", "PageLyrics", "PageCover", "PageWindow", "PagePlayback", "PageProgress", "PageLibrary", "PageIntegrations",
        "PageNotifications", "PageEqualizer", "PageMiniPlayer", "PageIsland", "PageHotkeys", "PageProfile", "PageUpdates", "PageAbout",
    ];

    private static readonly HashSet<string> ControlKinds = ["CheckBox", "RadioButton", "ComboBox", "Slider", "ToggleSwitch", "Button", "TextBox"];

    // Кнопки без собственного смысла вне карточки: их ищут по названию карточки.
    private static readonly HashSet<string> GenericLabels =
    [
        "Удалить", "Применить", "Выбрать цвет…", "Порядок по умолчанию", "Очистить", "Повторить", "Свой цвет через палитру",
        "Показать пример", "Сохранить как…",
    ];

    [GeneratedRegex("""^\s*Add\(\s*"((?:[^"\\]|\\.)*)"\s*,\s*"(?:[^"\\]|\\.)*"\s*,\s*"[A-Za-z]+"\s*,\s*([A-Za-z0-9_]+)\s*,\s*"((?:[^"\\]|\\.)*)"\s*\);""", RegexOptions.Multiline)]
    private static partial Regex IndexEntryRegex();

    [GeneratedRegex("[а-яёa-z]{4,}")]
    private static partial Regex WordRegex();

    private static string AppFile(string name, [CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "Lumisense", name));

    [Fact]
    public void EverySettingControl_IsFindableThroughTheSearchIndex()
    {
        string xamlPath = AppFile("SettingsWindow.xaml"), codePath = AppFile("SettingsWindow.xaml.cs");
        if (!File.Exists(xamlPath) || !File.Exists(codePath)) return; // тесты запущены без исходников приложения

        var entries = IndexEntryRegex().Matches(File.ReadAllText(codePath))
            .Select(match => (Label: match.Groups[1].Value, Target: match.Groups[2].Value, Keywords: match.Groups[3].Value))
            .ToList();
        Assert.True(entries.Count > 200, $"Найдено мало записей индекса ({entries.Count}): разбор BuildSearchIndex сломан.");

        var targets = entries.Select(entry => entry.Target).ToHashSet(StringComparer.Ordinal);
        string[] haystacks = entries
            .Select(entry => $"{entry.Label} {LocalizationService.ToEnglish(entry.Label)} {entry.Keywords}".ToLowerInvariant())
            .ToArray();

        XDocument document = XDocument.Load(xamlPath);
        var unreachable = new SortedSet<string>(StringComparer.Ordinal);
        int checkedControls = 0;

        foreach (XElement page in document.Descendants().Where(element => Pages.Contains((string?)element.Attribute(Xaml + "Name"))))
        {
            foreach (XElement control in page.Descendants().Where(element => ControlKinds.Contains(element.Name.LocalName)))
            {
                string? name = (string?)control.Attribute(Xaml + "Name");
                if (name is null) continue;

                string label = (string?)control.Attribute("Content") ?? (string?)control.Attribute("AutomationProperties.Name") ?? "";
                if (label.Length == 0 || label.StartsWith('{') || GenericLabels.Contains(label)) continue;

                string[] words = WordRegex().Matches(label.ToLowerInvariant()).Select(match => match.Value).ToArray();
                if (words.Length == 0) continue;

                checkedControls++;
                bool isTargetOrInside = control.AncestorsAndSelf()
                    .Any(ancestor => (string?)ancestor.Attribute(Xaml + "Name") is { } ancestorName && targets.Contains(ancestorName));
                if (!isTargetOrInside && !haystacks.Any(haystack => words.All(haystack.Contains)))
                    unreachable.Add($"{name}: {label}");
            }
        }

        Assert.True(checkedControls > 150, $"Проверено мало элементов ({checkedControls}): разбор XAML сломан.");
        Assert.True(unreachable.Count == 0,
            "Эти настройки не находятся поиском. Добавьте запись в BuildSearchIndex или слова подписи в ключевые слова группы:"
            + Environment.NewLine + string.Join(Environment.NewLine, unreachable));
    }
}
