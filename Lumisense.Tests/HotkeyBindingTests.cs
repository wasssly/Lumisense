using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class HotkeyBindingTests
{
    private static HotkeyBinding Binding(string key, bool ctrl = false, bool alt = false, bool shift = false, bool win = false) =>
        new() { Key = key, Ctrl = ctrl, Alt = alt, Shift = shift, Win = win };

    [Fact]
    public void SameCombinationAs_MatchesEqualModifiersAndKey_IgnoringKeyCase()
    {
        Assert.True(Binding("P", ctrl: true, alt: true).SameCombinationAs(Binding("p", ctrl: true, alt: true)));
    }

    [Theory]
    [InlineData("P", true, false, false, false, "P", false, false, false, false)]
    [InlineData("P", true, true, false, false, "P", true, false, false, false)]
    [InlineData("P", true, false, true, false, "P", true, false, false, false)]
    [InlineData("P", true, false, false, true, "P", true, false, false, false)]
    [InlineData("P", true, false, false, false, "N", true, false, false, false)]
    public void SameCombinationAs_IsFalse_WhenModifiersOrKeyDiffer(
        string keyA, bool ctrlA, bool altA, bool shiftA, bool winA,
        string keyB, bool ctrlB, bool altB, bool shiftB, bool winB)
    {
        Assert.False(Binding(keyA, ctrlA, altA, shiftA, winA).SameCombinationAs(Binding(keyB, ctrlB, altB, shiftB, winB)));
    }

    [Fact]
    public void SameCombinationAs_EmptyBindings_AreNeverAConflict()
    {
        // Две функции без назначенной комбинации — не дубликат.
        Assert.False(new HotkeyBinding().SameCombinationAs(new HotkeyBinding()));
        Assert.False(Binding("P", ctrl: true).SameCombinationAs(new HotkeyBinding()));
        Assert.False(Binding("P", ctrl: true).SameCombinationAs(null));
    }
}
