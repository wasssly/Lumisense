using Color = System.Windows.Media.Color;
using Xunit;

namespace Lumisense.Tests;

public sealed class ToastColorCalculatorTests
{
    private static readonly Color Dark = Color.FromRgb(0x1C, 0x1C, 0x1E);

    [Fact]
    public void Blend_ReturnsTheBaseColorWithoutATint() =>
        Assert.Equal(Dark, ToastColorCalculator.Blend(Dark, null, 0.7));

    [Theory]
    [InlineData(0.0, 0x1C, 0x1C, 0x1E)]
    [InlineData(1.0, 0xFF, 0x00, 0x00)]
    [InlineData(0.5, 0x8E, 0x0E, 0x0F)]
    [InlineData(double.NaN, 0x1C, 0x1C, 0x1E)]
    [InlineData(5.0, 0xFF, 0x00, 0x00)]
    public void Blend_MixesTheTintIntoTheBaseByStrength(double strength, byte r, byte g, byte b) =>
        Assert.Equal(Color.FromRgb(r, g, b), ToastColorCalculator.Blend(Dark, Color.FromRgb(0xFF, 0x00, 0x00), strength));

    [Theory]
    [InlineData(0x1C, 0x1C, 0x1E, false)]
    [InlineData(0xF2, 0xF2, 0xF2, true)]
    [InlineData(0xFF, 0xE0, 0x40, true)]
    [InlineData(0x20, 0x40, 0xC0, false)]
    [InlineData(0x99, 0x99, 0x99, true)]
    public void IsLight_PicksTheTextColorWithTheBetterContrast(byte r, byte g, byte b, bool expected) =>
        Assert.Equal(expected, ToastColorCalculator.IsLight(Color.FromRgb(r, g, b)));

    [Theory]
    [InlineData("#0078D4", 0x00, 0x78, 0xD4)]
    [InlineData("ff8c00", 0xFF, 0x8C, 0x00)]
    public void TryParse_ReadsSixDigitHex(string hex, byte r, byte g, byte b) =>
        Assert.Equal(Color.FromRgb(r, g, b), ToastColorCalculator.TryParse(hex));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    public void TryParse_ReturnsNullForInvalidInput(string? hex) =>
        Assert.Null(ToastColorCalculator.TryParse(hex));
}
