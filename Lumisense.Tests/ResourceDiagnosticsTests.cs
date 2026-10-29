using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class ResourceDiagnosticsTests
{
    private static ResourceSample Sample(double minute, double privateMiB, int handles = 1000, int gdi = 100, int user = 50, int tracks = 0) =>
        new(minute, 5, privateMiB, privateMiB + 10, privateMiB / 2, handles, 40, gdi, user, (int)(minute / 5), tracks);

    private static List<ResourceSample> Series(int minutes, Func<int, ResourceSample> factory) =>
        Enumerable.Range(0, minutes).Select(factory).ToList();

    [Fact]
    public void SlopePerHour_IsExactForLinearData()
    {
        List<ResourceSample> samples = Series(60, minute => Sample(minute, 100 + minute * 0.5));

        Assert.Equal(30.0, ResourceTrend.SlopePerHour(samples, sample => sample.PrivateMiB), 6);
    }

    [Fact]
    public void SlopePerHour_NeedsAtLeastThreeSamples()
    {
        var samples = new List<ResourceSample> { Sample(0, 1), Sample(1, 100) };

        Assert.Equal(0.0, ResourceTrend.SlopePerHour(samples, sample => sample.PrivateMiB));
    }

    [Fact]
    public void FlatUsage_IsAssessedAsNormal()
    {
        List<ResourceSample> samples = Series(120, minute => Sample(minute, 150 + minute % 3, tracks: minute));

        ResourceAssessment assessment = ResourceTrend.Assess(ResourceTrend.Summarize(samples, 10));

        Assert.Equal(ResourceVerdict.Normal, assessment.Verdict);
        Assert.Empty(assessment.Reasons);
    }

    [Fact]
    public void SteadyGrowthOfMemoryAndHandles_IsReportedAsSuspectedLeak()
    {
        List<ResourceSample> samples = Series(120, minute => Sample(minute, 150 + minute, handles: 1000 + minute * 2));

        ResourceSummary summary = ResourceTrend.Summarize(samples, 10);
        ResourceAssessment assessment = ResourceTrend.Assess(summary);

        Assert.Equal(60.0, summary.PrivateSlopePerHour, 1);
        Assert.Equal(120.0, summary.HandlesSlopePerHour, 1);
        Assert.Equal(ResourceVerdict.SuspectedLeak, assessment.Verdict);
        Assert.Equal(2, assessment.Reasons.Count);
    }

    [Fact]
    public void WarmUpGrowthFollowedByAPlateau_IsNotALeak()
    {
        List<ResourceSample> samples = Series(120, minute => Sample(minute, minute < 10 ? 100 + minute * 8 : 180 + minute % 2));

        Assert.Equal(ResourceVerdict.Normal, ResourceTrend.Assess(ResourceTrend.Summarize(samples, 10)).Verdict);
    }

    [Fact]
    public void ShortRunsAndEmptyRuns_AreNotEnoughData()
    {
        List<ResourceSample> shortRun = Series(10, minute => Sample(minute, 150 + minute * 10));

        Assert.Equal(ResourceVerdict.NotEnoughData, ResourceTrend.Assess(ResourceTrend.Summarize(shortRun, 10)).Verdict);
        Assert.Equal(ResourceVerdict.NotEnoughData, ResourceTrend.Assess(ResourceTrend.Summarize(new List<ResourceSample>(), 10)).Verdict);
    }

    [Fact]
    public void Report_ContainsTheVersionAndCsvHasOneRowPerSample()
    {
        List<ResourceSample> samples = Series(30, minute => Sample(minute, 150));

        string report = ResourceReport.Build("Title", samples, 5, loadMode: false);
        string csv = ResourceReport.BuildCsv(samples);

        Assert.Contains(UpdateChecker.GetCurrentVersion(), report);
        Assert.StartsWith("Minute,", csv);
        Assert.Equal(samples.Count + 1, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void Sampler_ReadsTheCurrentProcess()
    {
        using var sampler = new ResourceSampler();

        ResourceSample first = sampler.Read(0);
        Thread.Sleep(100);
        ResourceSample second = sampler.Read(3);

        Assert.True(first.PrivateMiB > 10);
        Assert.True(second.Handles > 0);
        Assert.True(second.Threads > 0);
        Assert.Equal(3, second.TrackChanges);
        Assert.True(second.Minute > first.Minute);
    }

    [Fact]
    public async Task Session_CollectsSamplesAndSwitchesTracksOnSchedule()
    {
        var session = new ResourceSession
        {
            Title = "test",
            Duration = TimeSpan.FromSeconds(1.5),
            SampleInterval = TimeSpan.FromMilliseconds(250),
            NextTrackInterval = TimeSpan.FromMilliseconds(500)
        };
        int switches = 0;

        IReadOnlyList<ResourceSample> samples = await session.RunAsync(() => switches++, null, CancellationToken.None);

        Assert.InRange(samples.Count, 3, 8);
        Assert.InRange(switches, 2, 4);
        Assert.True(samples[^1].TrackChanges >= 1);
    }

    [Fact]
    public async Task Session_ReturnsThePartialResultWhenCancelled()
    {
        var session = new ResourceSession
        {
            Title = "test",
            Duration = TimeSpan.FromSeconds(30),
            SampleInterval = TimeSpan.FromMilliseconds(100)
        };
        using var cts = new CancellationTokenSource();

        Task<IReadOnlyList<ResourceSample>> run = session.RunAsync(null, null, cts.Token);
        await Task.Delay(600, TestContext.Current.CancellationToken);
        cts.Cancel();
        IReadOnlyList<ResourceSample> samples = await run;

        Assert.NotEmpty(samples);
        Assert.True(samples.Count < 20);
    }
}
