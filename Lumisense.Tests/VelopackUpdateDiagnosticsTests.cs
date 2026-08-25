using System.Linq;
using AudioPlayer;
using Velopack;
using Xunit;

namespace Lumisense.Tests;

public sealed class VelopackUpdateDiagnosticsTests
{
    [Fact]
    public void CreatePlan_WithCandidateDeltas_PreservesAssetsAndTotalSize()
    {
        var full = new VelopackAsset
        {
            FileName = "Wasssly.Lumisense-1.16.2-full.nupkg",
            Size = 81_423_373
        };
        var deltaOne = new VelopackAsset
        {
            FileName = "Wasssly.Lumisense-1.16.1-delta.nupkg",
            Size = 100_000
        };
        var deltaTwo = new VelopackAsset
        {
            FileName = "Wasssly.Lumisense-1.16.2-delta.nupkg",
            Size = 223_915
        };
        var update = new UpdateInfo(full, isDowngrade: false, deltasToTarget: [deltaOne, deltaTwo]);

        VelopackUpdatePlan plan = VelopackUpdateDiagnostics.CreatePlan(update);

        Assert.True(plan.HasDeltaPlan);
        Assert.Equal(full.FileName, plan.FullPackage.FileName);
        Assert.Equal(323_915, plan.DeltaBytes);
        Assert.Equal(new[] { deltaOne.FileName, deltaTwo.FileName }, plan.DeltaPackages.Select(asset => asset.FileName));
    }

    [Fact]
    public void CreateReport_WithoutCandidateDeltas_ExplainsFullPackageExpectation()
    {
        var full = new VelopackAsset
        {
            FileName = "Wasssly.Lumisense-1.16.2-full.nupkg",
            Size = 81_423_373
        };
        var update = new UpdateInfo(full, isDowngrade: false);
        using var diagnostics = new VelopackUpdateDiagnostics("1.16.1", update);

        string report = diagnostics.CreateReport();

        Assert.False(diagnostics.Plan.HasDeltaPlan);
        Assert.Contains(full.FileName, report);
        Assert.Contains("a full package is expected", report);
        Assert.Contains("does not expose the actual transferred file", report);
    }
}
