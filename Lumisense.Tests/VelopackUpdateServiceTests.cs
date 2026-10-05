using System.IO;
using System.Net.Http;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class VelopackUpdateServiceTests
{
    [Fact]
    public void PublicReleaseFeedUrl_UsesStableLatestDownloadDirectory()
    {
        Assert.Equal(
            "https://github.com/wasssly/Lumisense/releases/latest/download/",
            VelopackUpdateService.PublicReleaseFeedUrl);
        Assert.DoesNotContain("api.github.com", VelopackUpdateService.PublicReleaseFeedUrl);
    }

    [Fact]
    public void ClassifyFailure_TreatsTransportErrorsAsNetwork()
    {
        var wrapped = new InvalidOperationException("outer", new HttpRequestException("inner"));

        Assert.Equal(UpdateFailureKind.Network, VelopackUpdateService.ClassifyFailure(wrapped));
        Assert.Equal(UpdateFailureKind.Network, VelopackUpdateService.ClassifyFailure(new TimeoutException()));
    }

    [Fact]
    public void ClassifyFailure_TreatsParseErrorsAsInvalidResponse()
    {
        Assert.Equal(UpdateFailureKind.InvalidResponse,
            VelopackUpdateService.ClassifyFailure(new System.Text.Json.JsonException("bad feed")));
    }

    [Fact]
    public void ClassifyFailure_DoesNotBlameTheNetworkForOtherErrors()
    {
        Assert.Equal(UpdateFailureKind.Unknown,
            VelopackUpdateService.ClassifyFailure(new UnauthorizedAccessException()));
    }

    [Fact]
    public void HasSufficientDiskSpace_DoesNotBlockWhenFreeSpaceCannotBeDetermined()
    {
        Assert.True(VelopackUpdateService.HasSufficientDiskSpace(null, long.MaxValue));
        Assert.True(VelopackUpdateService.HasSufficientDiskSpace("  ", long.MaxValue));
    }

    [Fact]
    public void HasSufficientDiskSpace_ComparesAgainstFreeSpaceOfTheDrive()
    {
        string directory = Path.GetTempPath();

        Assert.True(VelopackUpdateService.HasSufficientDiskSpace(directory, 0));
        Assert.False(VelopackUpdateService.HasSufficientDiskSpace(directory, long.MaxValue));
    }
}
