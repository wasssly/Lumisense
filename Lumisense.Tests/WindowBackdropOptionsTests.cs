using Wpf.Ui.Controls;
using Xunit;

namespace Lumisense.Tests;

public sealed class WindowBackdropOptionsTests
{
    [Theory]
    [InlineData("Mica", "Mica")]
    [InlineData("MicaAlt", "MicaAlt")]
    [InlineData("Acrylic", "Acrylic")]
    [InlineData("Blur", "Blur")]
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
    public void Blur_IsBuiltOnAcrylicSoTheWindowBackgroundIsTransparent()
    {
        Assert.Equal(WindowBackdropType.Acrylic, WindowBackdropOptions.ToWpfUi("Blur", 22000));
        Assert.Equal(WindowBackdropType.Acrylic, WindowBackdropOptions.ToWpfUi("Blur", 22631));
        Assert.True(WindowBackdropOptions.IsBlur("Blur", 22631));
        Assert.False(WindowBackdropOptions.IsBlur("Acrylic", 22631));
        Assert.False(WindowBackdropOptions.IsBlur(null, 22631));
    }

    [Fact]
    public void AcrylicAndMica_AreAvailableOnWindows11()
    {
        Assert.Equal(WindowBackdropType.Acrylic, WindowBackdropOptions.ToWpfUi("Acrylic", 22000));
        Assert.Equal(WindowBackdropType.Mica, WindowBackdropOptions.ToWpfUi("Mica", 22000));
    }

    [Theory]
    [InlineData(19044, false)]
    [InlineData(19045, false)]
    [InlineData(21999, false)]
    [InlineData(22000, true)]
    [InlineData(22631, true)]
    public void BackdropSelection_IsAvailableOnlyOnWindows11(int build, bool expected) =>
        Assert.Equal(expected, WindowBackdropOptions.IsSelectionSupported(build));

    [Theory]
    [InlineData("Acrylic")]
    [InlineData("Blur")]
    [InlineData("MicaAlt")]
    [InlineData("Mica")]
    [InlineData(null)]
    public void OnWindows10_EveryChoiceBehavesAsMicaAndNeverAsBlur(string? value)
    {
        Assert.Equal("Mica", WindowBackdropOptions.Effective(value, 19045));
        Assert.Equal(WindowBackdropType.Mica, WindowBackdropOptions.ToWpfUi(value, 19045));
        Assert.False(WindowBackdropOptions.IsBlur(value, 19045));
    }
}
