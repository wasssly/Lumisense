using Xunit;

namespace Lumisense.Tests;

public sealed class AppIconsArtTests
{
    // Проверяется путь ресурса, а не Uri: схема pack:// в тестовом процессе без WPF-приложения не зарегистрирована.
    [Theory]
    [InlineData(AppIcons.Glass, "/Icons/app/lumisense-glass.png")]
    [InlineData(AppIcons.Spotlight, "/Icons/app/lumisense-spotlight.png")]
    [InlineData(AppIcons.Backlit, "/Icons/app/lumisense-backlit.png")]
    [InlineData(AppIcons.Stripes, "/Icons/app/lumisense-stripes.png")]
    [InlineData(AppIcons.Beam, "/Icons/app/lumisense-beam.png")]
    public void GetArtPath_PointsToThePngOfTheSelectedIcon(string icon, string expectedPath) =>
        Assert.Equal(expectedPath, AppIcons.GetArtPath(icon));

    [Fact]
    public void EveryKnownIconHasAnArtPath()
    {
        foreach (string icon in AppIcons.All)
            Assert.EndsWith(".png", AppIcons.GetArtPath(icon));
    }
}
