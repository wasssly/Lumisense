using AudioPlayer;
using Velopack;
using Xunit;

namespace Lumisense.Tests;

public sealed class VelopackBasePackagePlanTests
{
    [Fact]
    public void FindCurrentFullPackage_SelectsOnlyFullPackageForCurrentVersion()
    {
        Velopack.SemanticVersion current = Velopack.SemanticVersion.Parse("1.17.0");
        var previousFull = new VelopackAsset
        {
            FileName = "Wasssly.Lumisense-1.16.2-full.nupkg",
            Version = Velopack.SemanticVersion.Parse("1.16.2"),
            Type = VelopackAssetType.Full,
            Size = 81_423_373
        };
        var matchingDelta = new VelopackAsset
        {
            FileName = "Wasssly.Lumisense-1.17.0-delta.nupkg",
            Version = current,
            Type = VelopackAssetType.Delta,
            Size = 338_510
        };
        var matchingFull = new VelopackAsset
        {
            FileName = "Wasssly.Lumisense-1.17.0-full.nupkg",
            Version = current,
            Type = VelopackAssetType.Full,
            Size = 81_439_530
        };

        VelopackAsset? selected = VelopackBasePackagePlan.FindCurrentFullPackage(
            [previousFull, matchingDelta, matchingFull], current);

        Assert.Same(matchingFull, selected);
    }

    [Fact]
    public void FindCurrentFullPackage_ReturnsNullWhenOnlyDeltaMatchesCurrentVersion()
    {
        Velopack.SemanticVersion current = Velopack.SemanticVersion.Parse("1.17.0");
        var matchingDelta = new VelopackAsset
        {
            FileName = "Wasssly.Lumisense-1.17.0-delta.nupkg",
            Version = current,
            Type = VelopackAssetType.Delta,
            Size = 338_510
        };

        VelopackAsset? selected = VelopackBasePackagePlan.FindCurrentFullPackage([matchingDelta], current);

        Assert.Null(selected);
    }
}
