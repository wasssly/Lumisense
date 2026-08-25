using System.Text.Json;
using AudioPlayer;
using Xunit;

namespace Lumisense.Tests;

public sealed class ReleaseAssetNameSelectionTests
{
    [Fact]
    public void VersionedAssets_AreSelectedForTheirExactReleaseVersion()
    {
        using JsonDocument release = CreateRelease(
            ("Lumisense-1.18.0-Setup.exe", "https://example.test/Lumisense-1.18.0-Setup.exe"),
            ("Lumisense-1.18.0-win-x64.msi", "https://example.test/Lumisense-1.18.0-win-x64.msi"),
            ("Lumisense-1.18.0-full.nupkg", "https://example.test/Lumisense-1.18.0-full.nupkg"),
            ("Lumisense_Setup.exe", "https://example.test/legacy-setup.exe"),
            ("Wasssly.Lumisense-win.msi", "https://example.test/legacy.msi"),
            ("Wasssly.Lumisense-1.18.0-full.nupkg", "https://example.test/legacy-full.nupkg"));

        var installer = UpdateChecker.FindInstallerAsset(release.RootElement, "1.18.0");
        var msi = UpdateChecker.FindMsiAsset(release.RootElement, "1.18.0");
        var fullPackage = UpdateChecker.FindVelopackFullPackageAsset(release.RootElement, "1.18.0");

        Assert.Equal("https://example.test/Lumisense-1.18.0-Setup.exe", installer.DownloadUrl);
        Assert.Equal("https://example.test/Lumisense-1.18.0-win-x64.msi", msi.DownloadUrl);
        Assert.Equal("https://example.test/Lumisense-1.18.0-full.nupkg", fullPackage.DownloadUrl);
    }

    [Fact]
    public void HistoricalReleaseAssets_UseOnlyExplicitLegacyFallbackNames()
    {
        using JsonDocument release = CreateRelease(
            ("Lumisense_Setup.exe", "https://example.test/legacy-setup.exe"),
            ("Wasssly.Lumisense-win.msi", "https://example.test/legacy.msi"),
            ("Wasssly.Lumisense-1.17.0-full.nupkg", "https://example.test/legacy-full.nupkg"));

        var installer = UpdateChecker.FindInstallerAsset(release.RootElement, "1.17.0");
        var msi = UpdateChecker.FindMsiAsset(release.RootElement, "1.17.0");
        var fullPackage = UpdateChecker.FindVelopackFullPackageAsset(release.RootElement, "1.17.0");

        Assert.Equal("https://example.test/legacy-setup.exe", installer.DownloadUrl);
        Assert.Equal("https://example.test/legacy.msi", msi.DownloadUrl);
        Assert.Equal("https://example.test/legacy-full.nupkg", fullPackage.DownloadUrl);
    }

    [Fact]
    public void Selector_DoesNotAcceptVersionedAssetsForAnotherRelease()
    {
        using JsonDocument release = CreateRelease(
            ("Lumisense-1.18.1-Setup.exe", "https://example.test/wrong-setup.exe"),
            ("Lumisense-1.18.1-win-x64.msi", "https://example.test/wrong.msi"),
            ("Lumisense-1.18.1-full.nupkg", "https://example.test/wrong-full.nupkg"));

        Assert.Null(UpdateChecker.FindInstallerAsset(release.RootElement, "1.18.0").DownloadUrl);
        Assert.Null(UpdateChecker.FindMsiAsset(release.RootElement, "1.18.0").DownloadUrl);
        Assert.Null(UpdateChecker.FindVelopackFullPackageAsset(release.RootElement, "1.18.0").DownloadUrl);
    }

    private static JsonDocument CreateRelease(params (string Name, string Url)[] assets)
    {
        string json = JsonSerializer.Serialize(new
        {
            assets = assets.Select(asset => new
            {
                name = asset.Name,
                browser_download_url = asset.Url,
                digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            })
        });
        return JsonDocument.Parse(json);
    }
}
