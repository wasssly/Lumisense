using System.Text;
using System.Text.Json;
using Xunit;

namespace Lumisense.Tests;

public sealed class LyricsExtraSourcesTests
{
    [Fact]
    public void Kugou_ParseSongs_ReadsHashTitleArtistAndDuration()
    {
        const string json = """
            {"status":1,"data":{"info":[
              {"hash":"ABC123","songname":"Song","singername":"Singer","album_name":"Album","duration":215},
              {"hash":"","songname":"No hash","singername":"X","duration":100},
              {"hash":"DEF456","songname":"Other","singername":"Y","duration":"180"}
            ]}}
            """;

        var songs = KugouLyricsSource.ParseSongs(json);

        Assert.Equal(2, songs.Count);
        Assert.Equal("ABC123", songs[0].Hash);
        Assert.Equal("Album", songs[0].Album);
        Assert.Equal(215, songs[0].DurationSeconds);
        Assert.Equal(180, songs[1].DurationSeconds);
    }

    [Fact]
    public void Kugou_ParseSongs_WithoutData_ReturnsEmpty()
    {
        Assert.Empty(KugouLyricsSource.ParseSongs("{\"status\":0}"));
        Assert.Empty(KugouLyricsSource.ParseSongs("{\"data\":{\"info\":null}}"));
    }

    [Fact]
    public void Kugou_ParseCandidate_TakesFirstCandidateWithIdAndAccessKey()
    {
        const string json = """{"candidates":[{"id":"x"},{"id":12345,"accesskey":"KEY"}]}""";

        var candidate = KugouLyricsSource.ParseCandidate(json);

        Assert.NotNull(candidate);
        Assert.Equal("12345", candidate!.Value.Id);
        Assert.Equal("KEY", candidate.Value.AccessKey);
        Assert.Null(KugouLyricsSource.ParseCandidate("{\"candidates\":[]}"));
    }

    [Fact]
    public void Kugou_DecodeContent_DecodesBase64_StripsBomAndServiceTags()
    {
        string lrc = "\uFEFF[id:$00000000]\r\n[ar:Singer]\r\n[ti:Song]\r\n[offset:0]\r\n[00:01.00]First line\r\n[00:05.50]Second line\r\n";
        string json = JsonSerializer.Serialize(new { content = Convert.ToBase64String(Encoding.UTF8.GetBytes(lrc)) });

        string? text = KugouLyricsSource.DecodeContent(json);

        Assert.Equal("[offset:0]\n[00:01.00]First line\n[00:05.50]Second line", text);
    }

    [Fact]
    public void Kugou_DecodeContent_WithBrokenBase64_ReturnsNull()
    {
        Assert.Null(KugouLyricsSource.DecodeContent("{\"content\":\"***\"}"));
        Assert.Null(KugouLyricsSource.DecodeContent("{}"));
    }

    [Fact]
    public void Paxsenix_ParseItunesSongs_ReadsTrackIdAndDurationInSeconds()
    {
        const string json = """
            {"resultCount":2,"results":[
              {"trackId":1440857781,"trackName":"Song","artistName":"Singer","collectionName":"Album","trackTimeMillis":215000},
              {"artistName":"No id","trackName":"Broken"}
            ]}
            """;

        var songs = PaxsenixLyricsSource.ParseItunesSongs(json);

        Assert.Single(songs);
        Assert.Equal(1440857781L, songs[0].TrackId);
        Assert.Equal(215, songs[0].DurationSeconds);
        Assert.Equal("Album", songs[0].Album);
    }

    [Fact]
    public void Paxsenix_ParseAppleLyrics_PrefersReadyLrc()
    {
        const string json = """{"lrc":"[00:01.00]One\n[00:03.00]Two","plain":"One\nTwo","content":[]}""";

        var (synced, plain) = PaxsenixLyricsSource.ParseAppleLyrics(json);

        Assert.Equal("[00:01.00]One\n[00:03.00]Two", synced);
        Assert.Null(plain);
    }

    [Fact]
    public void Paxsenix_ParseAppleLyrics_BuildsLrcFromSyllableContent_SkippingBackgroundRows()
    {
        const string json = """
            {"content":[
              {"timestamp":1000,"text":[{"text":"Hel","part":true,"timestamp":1000},{"text":"lo","part":false,"timestamp":1200},{"text":"world","timestamp":1500}]},
              {"timestamp":2500,"background":true,"text":[{"text":"(ooh)","timestamp":2500}]},
              {"timestamp":65430,"text":[{"text":"Bye","timestamp":65430},{"text":"!","timestamp":65800}]}
            ]}
            """;

        var (synced, plain) = PaxsenixLyricsSource.ParseAppleLyrics(json);

        Assert.Equal("[00:01.00]Hello world\n[01:05.43]Bye!", synced);
        Assert.Null(plain);
    }

    [Fact]
    public void Paxsenix_ParseAppleLyrics_WithoutTiming_FallsBackToPlain()
    {
        const string json = """{"content":[{"timestamp":0,"text":[{"text":"Only"}]}],"plain":"Only line"}""";

        var (synced, plain) = PaxsenixLyricsSource.ParseAppleLyrics(json);

        Assert.Null(synced);
        Assert.Equal("Only line", plain);
    }

    [Fact]
    public void Paxsenix_ParseYoutubeVideos_AcceptsArrayAndWrappedObject()
    {
        const string bare = """[{"videoId":"vid1","title":"Song","author":"Singer","duration":"3:35"}]""";
        const string wrapped = """{"results":[{"id":"vid2","title":"Other","artist":"X","duration":"1:02:03"}]}""";

        var first = PaxsenixLyricsSource.ParseYoutubeVideos(bare);
        var second = PaxsenixLyricsSource.ParseYoutubeVideos(wrapped);

        Assert.Equal("vid1", Assert.Single(first).Id);
        Assert.Equal(215, first[0].DurationSeconds);
        Assert.Equal("vid2", Assert.Single(second).Id);
        Assert.Equal(3723, second[0].DurationSeconds);
    }

    [Theory]
    [InlineData("3:25", 205)]
    [InlineData("0:07", 7)]
    [InlineData("x:yy", 0)]
    [InlineData("", 0)]
    public void Paxsenix_ParseColonDuration(string value, double expected)
    {
        Assert.Equal(expected, PaxsenixLyricsSource.ParseColonDuration(value));
    }

    [Fact]
    public void Paxsenix_HasUsableTiming_NeedsAtLeastTwoDistinctTimestamps()
    {
        Assert.True(PaxsenixLyricsSource.HasUsableTiming("[00:01.00]a\n[00:02.00]b"));
        Assert.False(PaxsenixLyricsSource.HasUsableTiming("[00:00.00]a\n[00:00.00]b"));
        Assert.False(PaxsenixLyricsSource.HasUsableTiming("no timestamps"));
        Assert.False(PaxsenixLyricsSource.HasUsableTiming(null));
    }
}
