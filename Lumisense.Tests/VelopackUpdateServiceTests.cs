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
}
