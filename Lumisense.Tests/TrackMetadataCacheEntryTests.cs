using Xunit;

namespace Lumisense.Tests;

public sealed class TrackMetadataCacheEntryTests
{
    [Theory]
    [InlineData("Artist", "Album Artist", "Artist")]
    [InlineData("", "Album Artist", "Album Artist")]
    [InlineData("   ", "Album Artist", "Album Artist")]
    [InlineData(null, "Album Artist", "Album Artist")]
    [InlineData(null, null, null)]
    public void DisplayArtist_FallsBackToAlbumArtist(string? artist, string? albumArtist, string? expected)
    {
        var entry = new TrackMetadataCache.Entry("Title", artist, albumArtist);

        Assert.Equal(expected, entry.DisplayArtist);
    }
}
