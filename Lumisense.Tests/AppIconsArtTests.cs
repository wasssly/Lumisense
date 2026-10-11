using Xunit;

namespace Lumisense.Tests;

public sealed class AppIconsArtTests
{
    // Проверяется путь ресурса, а не Uri: схема pack:// в тестовом процессе без WPF-приложения не зарегистрирована.
    [Theory]
    [InlineData(AppIcons.Classic, "/Icons/app/lumisense.png")]
    [InlineData(AppIcons.Aurora, "/Icons/app/lumisense-aurora.png")]
    [InlineData(AppIcons.Lavender, "/Icons/app/lumisense-lavender.png")]
    public void GetArtPath_PointsToThePngOfTheSelectedIcon(string icon, string expectedPath) =>
        Assert.Equal(expectedPath, AppIcons.GetArtPath(icon));

    [Fact]
    public void EveryKnownIconHasAnArtPath()
    {
        foreach (string icon in AppIcons.All)
            Assert.EndsWith(".png", AppIcons.GetArtPath(icon));
    }
}
