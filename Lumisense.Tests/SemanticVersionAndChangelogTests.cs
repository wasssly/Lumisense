using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("1.14.1", 1, 14, 1, null)]
    [InlineData(" v2.0.0-beta.3+build.7 ", 2, 0, 0, "beta.3")]
    public void TryParse_AcceptsValidVersions(string input, int major, int minor, int patch, string? prerelease)
    {
        bool parsed = SemanticVersion.TryParse(input, out var version);

        Assert.True(parsed);
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.Equal(prerelease, version.PreRelease);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1.0.0-01")]
    [InlineData("v01.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("")]
    public void TryParse_RejectsInvalidVersions(string input)
    {
        Assert.False(SemanticVersion.TryParse(input, out _));
    }

    [Theory]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("1.14.1", "1.15.0")]
    public void CompareTo_FollowsSemVerPrecedence(string lower, string higher)
    {
        Assert.True(SemanticVersion.TryParse(lower, out var left));
        Assert.True(SemanticVersion.TryParse(higher, out var right));

        Assert.True(left.CompareTo(right) < 0);
        Assert.True(right.CompareTo(left) > 0);
    }

    [Fact]
    public void ToString_ExcludesBuildMetadataAndKeepsPrerelease()
    {
        Assert.True(SemanticVersion.TryParse("1.2.3-rc.1+build.5", out var version));

        Assert.Equal("1.2.3-rc.1", version.ToString());
    }
}

public sealed class ChangeLevelClassifierTests
{
    [Theory]
    [InlineData("Добавлена очередь «Играть следующим» с поиском по названию, сортировкой по названию и восстановлением порядка добавления")]
    [InlineData("Добавлена обработка недоступных файлов в плейлисте: статус у трека, список проблемных записей, переход к строке, поиск замены и безопасная очистка только записей без удаления файлов с диска")]
    [InlineData("В настройках добавлена карточка «Фактическое устройство»: она показывает реально используемый аудиовывод и объясняет переход на системное устройство, если выбранное стало недоступно")]
    public void Classify_RecognizesRelease118FeaturesAsMinor(string text)
    {
        var change = new ChangeItem { Type = "added", Text = text };

        Assert.Equal(ChangeLevelClassifier.Level.Minor, ChangeLevelClassifier.Classify(change));
    }
}

public sealed class ChangelogTranslationCatalogTests
{
    [Fact]
    public void Translate_UsesLocalEnglishTranslationForKnownHistoricalEntry()
    {
        const string source = "Автопереход к следующему треку";

        Assert.Equal("Auto-advance to next track", ChangelogTranslationCatalog.Translate(source, isEnglish: true));
    }

    [Fact]
    public void Translate_PreservesRussianAndUnknownEntriesAsSafeFallback()
    {
        const string unknown = "Будущее изменение";

        Assert.Equal(unknown, ChangelogTranslationCatalog.Translate(unknown, isEnglish: true));
        Assert.Equal(unknown, ChangelogTranslationCatalog.Translate(unknown, isEnglish: false));
    }
}

public sealed class LocalizationResourcesTests
{
    [Fact]
    public void TryGet_ReturnsLanguageSpecificLooseFilesLabel()
    {
        Assert.True(LocalizationResources.TryGet(LocalizationKey.PlaylistLooseFiles, LocalizationService.Russian, out var russian));
        Assert.True(LocalizationResources.TryGet(LocalizationKey.PlaylistLooseFiles, LocalizationService.English, out var english));

        Assert.Equal("Отдельные файлы", russian);
        Assert.Equal("Loose files", english);
    }

    [Fact]
    public void TryGet_ReturnsFalseForUnknownStableKey()
    {
        Assert.False(LocalizationResources.TryGet("unknown.key", LocalizationService.English, out var value));
        Assert.Equal(string.Empty, value);
    }
}

public sealed class ToastPlacementCalculatorTests
{
    private static readonly System.Drawing.Rectangle WorkingArea = new(1920, 0, 2560, 1440);

    [Theory]
    [InlineData("TopLeft", 1940, 20)]
    [InlineData("TopCenter", 3050, 20)]
    [InlineData("TopRight", 4160, 20)]
    [InlineData("BottomLeft", 1940, 1348)]
    [InlineData("BottomCenter", 3050, 1348)]
    [InlineData("BottomRight", 4160, 1348)]
    public void Calculate_UsesTheSelectedMonitorWorkingArea(string position, int expectedX, int expectedY)
    {
        ToastPlacement placement = ToastPlacementCalculator.Calculate(WorkingArea, 300, 72, 1.0, position);

        Assert.Equal(expectedX, placement.X);
        Assert.Equal(expectedY, placement.Y);
        Assert.Equal(300, placement.Width);
        Assert.Equal(72, placement.Height);
    }

    [Fact]
    public void Calculate_UsesTheTargetMonitorDpiInsteadOfThePreviousWindowDpi()
    {
        ToastPlacement placement = ToastPlacementCalculator.Calculate(WorkingArea, 300, 72, 1.5, "BottomRight");

        Assert.Equal(4010, placement.X);
        Assert.Equal(1312, placement.Y);
        Assert.Equal(450, placement.Width);
        Assert.Equal(108, placement.Height);
    }

    [Fact]
    public void Calculate_FallsBackToOneForInvalidDpiScale()
    {
        ToastPlacement placement = ToastPlacementCalculator.Calculate(WorkingArea, 300, 72, 0, "TopLeft");

        Assert.Equal(300, placement.Width);
        Assert.Equal(72, placement.Height);
    }
}

public sealed class ChangeTypeCatalogTests
{
    [Theory]
    [InlineData("added", "added")]
    [InlineData("changed", "changed")]
    [InlineData("improved", "improved")]
    [InlineData("fixed", "fixed")]
    [InlineData("removed", "removed")]
    [InlineData("security", "security")]
    [InlineData("  Security ", "security")]
    public void Resolve_ReturnsKnownTypesCaseInsensitively(string input, string expectedKey)
    {
        Assert.Equal(expectedKey, ChangeTypeCatalog.Resolve(input).Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("performance")]
    [InlineData("unknown")]
    public void Resolve_FallsBackToChangedForUnknownTypes(string? input)
    {
        Assert.Same(ChangeTypeCatalog.Changed, ChangeTypeCatalog.Resolve(input));
    }

    [Fact]
    public void All_ContainsEveryTypeOnceWithUniqueKeys()
    {
        Assert.Equal(ChangeTypeCatalog.All.Count, ChangeTypeCatalog.All.Select(info => info.Key).Distinct().Count());
        Assert.Contains(ChangeTypeCatalog.Improved, ChangeTypeCatalog.All);
        Assert.Contains(ChangeTypeCatalog.Security, ChangeTypeCatalog.All);
    }
}
