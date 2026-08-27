using System.Collections.Generic;
using AudioPlayer;
using Xunit;

namespace Lumisense.Tests;

public sealed class TrackLoadPerformanceMeasurementTests
{
    [Fact]
    public void Complete_WhenTraceEnabled_EmitsEverySuccessfulLoad()
    {
        var lines = new List<string>();
        var measurement = new TrackLoadPerformanceMeasurement(traceAllLoads: true, lines.Add);

        measurement.MarkStage("open-audio-file");
        measurement.MarkStage("initialize-output");
        measurement.Complete(succeeded: true);

        string line = Assert.Single(lines);
        Assert.StartsWith("TRACE track-load completed:", line);
        Assert.Contains("open-audio-file=", line);
        Assert.Contains("initialize-output=", line);
    }

    [Fact]
    public void Complete_WhenTraceEnabled_EmitsOverlappedPreparationWaitStage()
    {
        var lines = new List<string>();
        var measurement = new TrackLoadPerformanceMeasurement(traceAllLoads: true, lines.Add);

        measurement.MarkStage("fade-out");
        measurement.MarkStage("wait-prepared-audio-and-metadata");
        measurement.Complete(succeeded: true);

        string line = Assert.Single(lines);
        Assert.Contains("fade-out=", line);
        Assert.Contains("wait-prepared-audio-and-metadata=", line);
    }

    [Fact]
    public void Complete_WhenTraceDisabledAndLoadIsFast_DoesNotEmitLine()
    {
        var lines = new List<string>();
        var measurement = new TrackLoadPerformanceMeasurement(traceAllLoads: false, lines.Add);

        measurement.MarkStage("open-audio-file");
        measurement.Complete(succeeded: true);

        Assert.Empty(lines);
    }
}
