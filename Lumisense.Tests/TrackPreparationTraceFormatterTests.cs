using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class TrackPreparationTraceFormatterTests
{
    [Fact]
    public void Format_ContainsOnlyNamedPreparationDurations()
    {
        string line = TrackPreparationTraceFormatter.Format(
            replayGainMilliseconds: 12,
            tagsMilliseconds: 34,
            embeddedArtworkMilliseconds: 56,
            audioFileReaderMilliseconds: 7,
            pipelineMilliseconds: 8);

        Assert.Equal(
            "TRACE track-prepare: replay-gain=12ms; tags=34ms; embedded-artwork=56ms; audio-file-reader=7ms; pipeline=8ms",
            line);
        Assert.DoesNotContain("C:\\", line);
        Assert.DoesNotContain("artist", line.ToLowerInvariant());
        Assert.DoesNotContain("title", line.ToLowerInvariant());
    }

    [Fact]
    public void Format_NormalizesNegativeDurationsToZero()
    {
        string line = TrackPreparationTraceFormatter.Format(-1, -2, -3, -4);

        Assert.Equal(
            "TRACE track-prepare: replay-gain=0ms; tags=0ms; embedded-artwork=0ms; audio-file-reader=0ms; pipeline=0ms",
            line);
    }
}
