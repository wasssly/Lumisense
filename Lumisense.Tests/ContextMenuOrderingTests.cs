using System.Collections.Generic;
using System.Linq;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class ContextMenuOrderingTests
{
    [Fact]
    public void Arrange_MovesItemsWithIdsAndKeepsSeparatorsInPlace()
    {
        string?[] items = ["A", "B", null, "C", "D"];

        var result = ContextMenuOrdering.Arrange(items, id => id, new List<string> { "D", "C", "B", "A" });

        Assert.Equal(new string?[] { "D", "C", null, "B", "A" }, result);
    }

    [Fact]
    public void Arrange_PutsItemsMissingFromOrderAfterKnownOnesInOriginalOrder()
    {
        string?[] items = ["A", "B", "C"];

        var result = ContextMenuOrdering.Arrange(items, id => id, new List<string> { "C" });

        Assert.Equal(new string?[] { "C", "A", "B" }, result);
    }

    [Fact]
    public void Normalize_DropsUnknownMenusItemsAndDuplicates()
    {
        var saved = new Dictionary<string, List<string>>
        {
            [ContextMenuOrdering.Track] = ["CopyPath", "Unknown", "CopyPath", "PlayNext"],
            ["NoSuchMenu"] = ["PlayNext"],
            [ContextMenuOrdering.Mini] = [],
        };

        var result = ContextMenuOrdering.Normalize(saved);

        Assert.Single(result);
        Assert.Equal(new[] { "CopyPath", "PlayNext" }, result[ContextMenuOrdering.Track]);
    }

    [Fact]
    public void Effective_UsesDefaultOrderUntilSomethingIsSaved()
    {
        var settings = new AppSettings();

        Assert.Equal(ContextMenuOrdering.Defaults[ContextMenuOrdering.Island], ContextMenuOrdering.Effective(settings, ContextMenuOrdering.Island).ToArray());

        settings.ContextMenuOrder[ContextMenuOrdering.Island] = ["Settings", "PlayPause"];
        Assert.Equal(new[] { "Settings", "PlayPause" }, ContextMenuOrdering.Effective(settings, ContextMenuOrdering.Island).ToArray());
    }

    [Fact]
    public void AnimationAreas_NormalizeKeepsOnlyKnownDistinctAreas()
    {
        var result = AnimationAreas.Normalize(["Cover", "Nope", "Cover", AnimationAreas.Sliders]);

        Assert.Equal(new[] { "Cover", "Sliders" }, result);
    }

    [Fact]
    public void ShouldReduceMotion_DisablesOnlyTheChosenAreaUnlessEverythingIsReduced()
    {
        var settings = new AppSettings { DisabledAnimationAreas = [AnimationAreas.Island] };

        Assert.True(AccessibilityPreferences.ShouldReduceMotion(settings, AnimationAreas.Island));
        Assert.False(AccessibilityPreferences.ShouldReduceMotion(settings, AnimationAreas.Cover));

        settings.ReduceMotion = true;
        Assert.True(AccessibilityPreferences.ShouldReduceMotion(settings, AnimationAreas.Cover));
    }
}
