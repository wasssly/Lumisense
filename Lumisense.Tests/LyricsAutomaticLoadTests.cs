using System.Threading;
using System.Threading.Tasks;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class LyricsAutomaticLoadTests
{
    [Theory]
    [InlineData("LocalOnly")]
    [InlineData("ManualOnly")]
    public async Task LoadWithAutomaticLookup_SkipsOnlineSearch_WhenPolicyIsNotAutoExact(string policy)
    {
        bool searching = false;

        var result = await LyricsService.LoadWithAutomaticLookupAsync(
            @"Z:\lumisense-tests\missing-track.mp3", "Title", "Artist", 180, policy, CancellationToken.None,
            onSearching: () => searching = true);

        Assert.NotNull(result);
        Assert.Null(result!.Lookup);
        Assert.False(searching);
        Assert.Equal(LyricsKind.None, result.Document.Kind);
    }

    [Fact]
    public async Task LoadWithAutomaticLookup_ReturnsNull_WhenRequestIsNoLongerCurrent()
    {
        var result = await LyricsService.LoadWithAutomaticLookupAsync(
            @"Z:\lumisense-tests\missing-track.mp3", "Title", "Artist", 180, "AutoExact", CancellationToken.None,
            isCurrent: () => false);

        Assert.Null(result);
    }
}
