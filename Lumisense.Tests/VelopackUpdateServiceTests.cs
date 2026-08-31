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
}
