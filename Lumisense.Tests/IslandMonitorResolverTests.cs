using Xunit;

namespace Lumisense.Tests;

public class IslandMonitorResolverTests
{
    private sealed record Screen(string Name, bool Primary);

    private static readonly Screen[] Screens =
    {
        new("\\\\.\\DISPLAY1", false),
        new("\\\\.\\DISPLAY2", true),
        new("\\\\.\\DISPLAY3", false)
    };

    private static Screen Pick(string? saved) =>
        IslandMonitorResolver.Pick(Screens, saved, s => s.Name, s => s.Primary);

    [Fact]
    public void SavedMonitor_IsUsedWhenConnected() => Assert.Equal("\\\\.\\DISPLAY3", Pick("\\\\.\\DISPLAY3").Name);

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("\\\\.\\DISPLAY9")]
    public void MissingOrEmptyMonitor_FallsBackToPrimary(string? saved) => Assert.Equal("\\\\.\\DISPLAY2", Pick(saved).Name);

    [Fact]
    public void WithoutPrimary_FirstMonitorIsUsed()
    {
        var screens = new[] { new Screen("A", false), new Screen("B", false) };

        Assert.Equal("A", IslandMonitorResolver.Pick(screens, null, s => s.Name, s => s.Primary).Name);
    }

    [Fact]
    public void NewSettings_DefaultToClassicStyleOnPrimaryMonitor()
    {
        var settings = new AppSettings();

        Assert.Equal("Classic", settings.MiniPlayerStyle);
        Assert.Equal("", settings.IslandMonitor);
    }
}
