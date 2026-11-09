using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class DiscordCoverArtistMatchTests
{
    [Theory]
    [InlineData("Daft Punk", "Daft Punk — Discovery", true)]
    [InlineData("Daft Punk", "Daft Punk & Pharrell Williams — Random Access Memories", true)]
    [InlineData("Daft Punk & Pharrell", "Daft Punk — Get Lucky", true)]
    [InlineData("Daft Punk", "Justice — Cross", false)]
    [InlineData("", "Anyone — Anything", true)]
    [InlineData("Daft Punk", "", false)]
    public void ArtistMatches_ComparesArtistPartOfLabel(string requested, string label, bool expected)
    {
        Assert.Equal(expected, DiscordCoverArtLookupService.ArtistMatches(requested, label));
    }
}
