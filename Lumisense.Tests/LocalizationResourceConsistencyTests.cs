using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class LocalizationResourceConsistencyTests
{
    private static readonly string[] PluralCategories = { "one", "few", "many", "other" };

    private static IReadOnlyDictionary<string, string> Russian => LocalizationResources.GetForLanguage(LocalizationService.Russian);
    private static IReadOnlyDictionary<string, string> English => LocalizationResources.GetForLanguage(LocalizationService.English);

    private static IEnumerable<string> DeclaredKeys() =>
        typeof(LocalizationKey).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);

    private static bool IsPluralCategoryKey(string key) =>
        PluralCategories.Any(category => key.EndsWith("." + category, StringComparison.Ordinal));

    private static string PluralBase(string key) => key[..key.LastIndexOf('.')];

    // Ключ-основа множественного числа (например statistics.listens) объявлен в LocalizationKey,
    // а тексты лежат в формах .one/.few/.many/.other.
    private static bool HasText(IReadOnlyDictionary<string, string> resources, string key) =>
        (resources.TryGetValue(key, out string? text) && !string.IsNullOrWhiteSpace(text))
        || (resources.TryGetValue(key + ".other", out string? other) && !string.IsNullOrWhiteSpace(other));

    private static SortedSet<int> Placeholders(string text) =>
        new(Regex.Matches(text, @"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}").Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)));

    [Fact]
    public void LocalizationKeyConstants_AreUnique()
    {
        var keys = DeclaredKeys().ToList();

        Assert.NotEmpty(keys);
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryDeclaredKey_HasTextInBothLanguages()
    {
        var missing = new List<string>();
        foreach (string key in DeclaredKeys())
        {
            if (!HasText(Russian, key))
                missing.Add("ru:" + key);
            if (!HasText(English, key))
                missing.Add("en:" + key);
        }

        Assert.Equal(new List<string>(), missing);
    }

    [Fact]
    public void EveryResourceKey_IsDeclaredInLocalizationKey()
    {
        var declared = new HashSet<string>(DeclaredKeys(), StringComparer.Ordinal);
        var undeclared = Russian.Keys.Concat(English.Keys)
            .Where(key => !declared.Contains(key) && !(IsPluralCategoryKey(key) && declared.Contains(PluralBase(key))))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(new List<string>(), undeclared);
    }

    [Fact]
    public void EveryKeyExistsInBothLanguages_ExceptRussianOnlyPluralForms()
    {
        var missingInEnglish = Russian.Keys
            .Where(key => !English.ContainsKey(key))
            .Where(key => !(key.EndsWith(".few", StringComparison.Ordinal) || key.EndsWith(".many", StringComparison.Ordinal)))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();
        var missingInRussian = English.Keys
            .Where(key => !Russian.ContainsKey(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(new List<string>(), missingInEnglish);
        Assert.Equal(new List<string>(), missingInRussian);
    }

    [Fact]
    public void EveryTextIsNotBlank()
    {
        var blank = Russian.Where(pair => string.IsNullOrWhiteSpace(pair.Value)).Select(pair => "ru:" + pair.Key)
            .Concat(English.Where(pair => string.IsNullOrWhiteSpace(pair.Value)).Select(pair => "en:" + pair.Key))
            .ToList();

        Assert.Equal(new List<string>(), blank);
    }

    [Fact]
    public void EveryTextIsAValidFormatString()
    {
        object[] arguments = Enumerable.Range(0, 8).Select(index => (object)index).ToArray();
        var broken = new List<string>();
        foreach (var (language, resources) in new[] { ("ru", Russian), ("en", English) })
        {
            foreach (var pair in resources)
            {
                try { _ = string.Format(CultureInfo.InvariantCulture, pair.Value, arguments); }
                catch (FormatException) { broken.Add(language + ":" + pair.Key); }
            }
        }

        Assert.Equal(new List<string>(), broken);
    }

    [Fact]
    public void SharedKeys_UseTheSamePlaceholdersInBothLanguages()
    {
        var mismatched = new List<string>();
        foreach (var pair in Russian)
        {
            if (!English.TryGetValue(pair.Key, out string? english))
                continue;
            if (!Placeholders(pair.Value).SetEquals(Placeholders(english)))
                mismatched.Add(pair.Key);
        }

        Assert.Equal(new List<string>(), mismatched.OrderBy(key => key, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void PluralGroups_CoverEveryCategoryTheLanguageUses()
    {
        var problems = new List<string>();
        foreach (string pluralBase in Russian.Keys.Concat(English.Keys).Where(IsPluralCategoryKey).Select(PluralBase).Distinct(StringComparer.Ordinal))
        {
            // FormatPlural берёт <base>.<категория>, а если её нет, то <base>.other.
            foreach (string category in new[] { "one", "few", "many" })
            {
                if (!Russian.ContainsKey(pluralBase + "." + category) && !Russian.ContainsKey(pluralBase + ".other"))
                    problems.Add("ru:" + pluralBase + "." + category);
            }
            foreach (string category in new[] { "one", "other" })
            {
                if (!English.ContainsKey(pluralBase + "." + category) && !English.ContainsKey(pluralBase + ".other"))
                    problems.Add("en:" + pluralBase + "." + category);
            }
        }

        Assert.Equal(new List<string>(), problems.OrderBy(problem => problem, StringComparer.Ordinal).ToList());
    }
}
