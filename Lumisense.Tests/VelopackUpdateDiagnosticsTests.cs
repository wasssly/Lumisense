using System.Linq;
using Lumisense;
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
    public void RuntimeState_TracksDownloadPauseResumeAndPreparation()
    {
        var full = new VelopackAsset
        {
            FileName = "Lumisense-1.19.0-full.nupkg",
            Size = 86_037_171
        };
        var delta = new VelopackAsset
        {
            FileName = "Lumisense-1.19.0-delta.nupkg",
            Size = 57_754_380
        };
        var update = new UpdateInfo(full, isDowngrade: false, deltasToTarget: [delta]);
        using var diagnostics = new VelopackUpdateDiagnostics("1.18.0", update);

        Assert.Equal(VelopackUpdateStage.Ready, diagnostics.Stage);
        Assert.Equal(0, diagnostics.ProgressPercentage);

        diagnostics.Start(resumed: false);
        diagnostics.Progress(37);
        Assert.Equal(VelopackUpdateStage.Downloading, diagnostics.Stage);
        Assert.Equal(37, diagnostics.ProgressPercentage);

        diagnostics.Pause();
        Assert.Equal(VelopackUpdateStage.Paused, diagnostics.Stage);
        Assert.Equal(37, diagnostics.ProgressPercentage);

        diagnostics.Start(resumed: true);
        Assert.Equal(VelopackUpdateStage.Downloading, diagnostics.Stage);
        Assert.Equal(0, diagnostics.ProgressPercentage);

        diagnostics.Prepared();
        string report = diagnostics.CreateReport();

        Assert.Equal(VelopackUpdateStage.PreparingRestart, diagnostics.Stage);
        Assert.Equal(100, diagnostics.ProgressPercentage);
        Assert.Contains("State: PreparingRestart", report);
        Assert.Contains("Progress reported by SDK: 100%", report);
        Assert.Contains("does not expose the actual transferred file", report);
        Assert.Contains("speed", report);
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
