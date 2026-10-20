using Xunit;

namespace Lumisense.Tests;

public class GeniusSoundCloudCoverTests
{
    private const string SearchJson = """
        {"meta":{"status":200},"response":{"sections":[
          {"type":"top_hit","hits":[]},
          {"type":"song","hits":[
            {"type":"song","result":{"title":"Night & Day","artist_names":"Some Artist",
              "song_art_image_url":"https://images.genius.com/abc.1000x1000x1.jpg",
              "song_art_image_thumbnail_url":"https://images.genius.com/abc.300x300x1.jpg"}},
            {"type":"song","result":{"title":"Foreign","artist_names":"X",
              "song_art_image_url":"https://evil.example.com/a.jpg"}},
            {"type":"song","result":{"title":"Dup","artist_names":"Some Artist",
              "song_art_image_url":"https://images.genius.com/abc.1000x1000x1.jpg"}},
            {"type":"song","result":{"title":"Placeholder","artist_names":"Y",
              "song_art_image_url":"https://assets.genius.com/images/default_cover_image.png?1"}}
          ]}]}}
        """;

    [Fact]
    public void GeniusSearch_ParsesHits_SkipsForeignHostsDuplicatesAndPlaceholders()
    {
        var item = Assert.Single(CoverArtProviders.ParseGeniusSearchResults(SearchJson));

        Assert.Equal("https://images.genius.com/abc.1000x1000x1.jpg", item.FullUrl);
        Assert.Equal("https://images.genius.com/abc.300x300x1.jpg", item.ThumbUrl);
        Assert.Equal("Some Artist — Night & Day", item.Label);
        Assert.Equal("Genius", item.Source);
    }

    [Fact]
    public void GeniusSearch_AcceptsOfficialApiShape()
    {
        const string json = """{"response":{"hits":[{"result":{"title":"T","artist_names":"A","song_art_image_url":"https://images.genius.com/z.jpg"}}]}}""";

        var item = Assert.Single(CoverArtProviders.ParseGeniusSearchResults(json));
        Assert.Equal(item.FullUrl, item.ThumbUrl);
    }

    [Fact]
    public void GeniusSearch_ReturnsEmptyForUnknownShape()
    {
        Assert.Empty(CoverArtProviders.ParseGeniusSearchResults("""{"response":{}}"""));
        Assert.Empty(CoverArtProviders.ParseGeniusSearchResults("""{"foo":1}"""));
    }

    [Fact]
    public void GeniusPage_UsesOgImage()
    {
        const string html = """
            <meta property="og:title" content="Artist &amp; Co – Song Lyrics | Genius Lyrics">
            <meta property="og:image" content="https://images.genius.com/page.1000x1000x1.png">
            """;

        var item = Assert.Single(CoverArtProviders.ParseGeniusPage(html));
        Assert.Equal("https://images.genius.com/page.1000x1000x1.png", item.FullUrl);
        Assert.StartsWith("Artist & Co", item.Label);
    }

    [Fact]
    public void GeniusPage_RejectsForeignImageHost()
    {
        Assert.Empty(CoverArtProviders.ParseGeniusPage("""<meta property="og:image" content="https://evil.example.com/a.jpg">"""));
    }

    [Fact]
    public void SoundCloudOEmbed_UpgradesThumbnailSize()
    {
        const string json = """{"title":"Song by Artist","thumbnail_url":"https://i1.sndcdn.com/artworks-abc-def-large.jpg"}""";

        var item = Assert.Single(CoverArtProviders.ParseSoundCloudOEmbed(json));
        Assert.Equal("https://i1.sndcdn.com/artworks-abc-def-t500x500.jpg", item.FullUrl);
        Assert.Equal("Song by Artist", item.Label);
    }

    [Fact]
    public void SoundCloudOEmbed_RejectsForeignThumbnailHost()
    {
        const string json = """{"title":"x","thumbnail_url":"https://evil.example.com/a-large.jpg"}""";

        Assert.Empty(CoverArtProviders.ParseSoundCloudOEmbed(json));
    }

    [Theory]
    [InlineData("https://soundcloud.com/a/b", "soundcloud.com", true)]
    [InlineData("https://images.genius.com/x.jpg", "genius.com", true)]
    [InlineData("http://soundcloud.com/a/b", "soundcloud.com", false)]
    [InlineData("https://notsoundcloud.com/a", "soundcloud.com", false)]
    [InlineData("artist title", "genius.com", false)]
    public void IsHostedLink_RequiresHttpsAndDomain(string text, string domain, bool expected)
    {
        Assert.Equal(expected, CoverArtProviders.IsHostedLink(text, domain));
    }
}
