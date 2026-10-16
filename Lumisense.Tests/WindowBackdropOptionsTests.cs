using Wpf.Ui.Controls;
using Xunit;

namespace Lumisense.Tests;

public sealed class WindowBackdropOptionsTests
{
    [Theory]
    [InlineData("Mica", "Mica")]
    [InlineData("MicaAlt", "MicaAlt")]
    [InlineData("Acrylic", "Acrylic")]
    [InlineData("Unknown", "Mica")]
    [InlineData(null, "Mica")]
    public void Normalize_KeepsKnownValuesAndFallsBackToMica(string? value, string expected) =>
        Assert.Equal(expected, WindowBackdropOptions.Normalize(value));

    [Fact]
    public void MicaAlt_MapsToTabledBackdropOnWindows11_22H2() =>
        Assert.Equal(WindowBackdropType.Tabbed, WindowBackdropOptions.ToWpfUi("MicaAlt", 22621));

    [Fact]
    public void MicaAlt_FallsBackToMicaOnOlderBuilds() =>
        Assert.Equal(WindowBackdropType.Mica, WindowBackdropOptions.ToWpfUi("MicaAlt", 22000));

    [Fact]
    public void AcrylicAndMica_DoNotDependOnBuild()
    {
        Assert.Equal(WindowBackdropType.Acrylic, WindowBackdropOptions.ToWpfUi("Acrylic", 19045));
        Assert.Equal(WindowBackdropType.Mica, WindowBackdropOptions.ToWpfUi("Mica", 19045));
    }
}
