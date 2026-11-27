using System.Net.Http;
using System.Text.Json;
using Xunit;

namespace Lumisense.Tests;

// Better Lyrics, Binimum, LyricsPlus, Deezer и LRCMUX: разбор их ответов без сети.
public sealed class LyricsNewSourcesTests
{
    private const string Ttml = """
        <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata">
          <body><div>
            <p begin="00:12.500" end="00:15.000"><span begin="00:12.500" end="00:13.000">Hel</span><span begin="00:13.000" end="00:14.000">lo</span> <span begin="00:14.000" end="00:15.000">world</span></p>
            <p begin="1:05.250" end="1:08.000"><span>Second</span> <span ttm:role="x-bg">(echo)</span><span ttm:role="x-translation">Второй</span> line</p>
          </div></body>
        </tt>
        """;

    [Fact]
    public void Ttml_BuildsLrcFromParagraphs_JoiningSyllablesAndSkippingBackgroundAndTranslation()
    {
        var (synced, plain) = TtmlLyricsParser.Parse(Ttml);

        Assert.Null(plain);
        Assert.Equal("[00:12.50]Hello world\n[01:05.25]Second line", synced);
    }

    [Fact]
    public void Ttml_WithoutDistinctTimes_FallsBackToPlainText()
    {
        const string ttml = "<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div><p>One</p><p>Two</p></div></body></tt>";

        var (synced, plain) = TtmlLyricsParser.Parse(ttml);

        Assert.Null(synced);
        Assert.Equal("One\nTwo", plain);
    }

    [Theory]
    [InlineData("<tt><body><div><p begin=\"0s\">a</p><p begin=\"0s\">b</p></div></body></tt>")]
    [InlineData("not xml at all")]
    [InlineData("<!DOCTYPE x [<!ENTITY a \"b\">]><tt><body><p begin=\"1s\">&a;</p></body></tt>")]
    [InlineData("")]
    public void Ttml_RejectsUnusableOrDangerousInput(string input)
    {
        var (synced, _) = TtmlLyricsParser.Parse(input);

        Assert.Null(synced);
    }

    [Theory]
    [InlineData("12.345s", 12.345)]
    [InlineData("1500ms", 1.5)]
    [InlineData("01:02.500", 62.5)]
    [InlineData("00:01:02.500", 62.5)]
    [InlineData("7", 7)]
    public void Ttml_ParseTime_UnderstandsCommonFormats(string value, double expected) =>
        Assert.Equal(expected, TtmlLyricsParser.ParseTime(value)!.Value, 3);

    [Fact]
    public void Ttml_ParseTime_RejectsGarbage()
    {
        Assert.Null(TtmlLyricsParser.ParseTime("abc"));
        Assert.Null(TtmlLyricsParser.ParseTime("1:2:3:4"));
        Assert.Null(TtmlLyricsParser.ParseTime(null));
    }

    [Fact]
    public void BetterLyrics_ParseResponse_ReadsTtmlFromJson()
    {
        string json = JsonSerializer.Serialize(new { ttml = Ttml });

        var (synced, _) = BetterLyricsSource.ParseResponse(json);

        Assert.StartsWith("[00:12.50]Hello world", synced);
    }

    [Fact]
    public void BetterLyrics_ParseResponse_AcceptsRawTtmlAndIgnoresErrors()
    {
        Assert.NotNull(BetterLyricsSource.ParseResponse(Ttml).Synced);
        Assert.Null(BetterLyricsSource.ParseResponse("{\"error\":\"API key required\"}").Synced);
        Assert.Null(BetterLyricsSource.ParseResponse("Song name or artist name not provided").Synced);
    }

    [Fact]
    public void BetterLyrics_BuildUrl_SendsEachParameterOnceAndEscapes()
    {
        Assert.Equal("https://api.betterlyrics.org/getLyrics?s=Song%20%26%20Dance&a=Some%20Band",
            BetterLyricsSource.BuildUrl("Song & Dance", "Some Band"));
        Assert.Equal("https://api.betterlyrics.org/getLyrics?s=Song", BetterLyricsSource.BuildUrl("Song", "—"));
    }

    [Fact]
    public void Binimum_ParseEntries_KeepsOnlyHttpsLinks_AndNormalizesDuration()
    {
        const string json = """
            {"results":[
              {"lyricsUrl":"https://cdn.example.org/a.ttml","track":"A","artist":"X","album":"Al","duration":215000},
              {"lyricsUrl":"http://cdn.example.org/b.ttml","track":"B"},
              {"lyricsUrl":"javascript:alert(1)"},
              {"track":"no link"},
              {"lyricsUrl":"https://cdn.example.org/c.ttml","duration":180}
            ]}
            """;

        var entries = BinimumLyricsSource.ParseEntries(json);

        Assert.Equal(2, entries.Count);
        Assert.Equal("A", entries[0].Title);
        Assert.Equal(215, entries[0].DurationSeconds);
        Assert.Equal(180, entries[1].DurationSeconds);
        Assert.Empty(BinimumLyricsSource.ParseEntries("{}"));
        Assert.Empty(BinimumLyricsSource.ParseEntries("garbage"));
    }

    [Fact]
    public void Binimum_BuildUrl_UsesTrackAndArtist() =>
        Assert.Equal("https://lyrics-api.binimum.org/?track=Song&artist=Band", BinimumLyricsSource.BuildUrl("Song", "Band"));

    [Fact]
    public void LyricsPlus_ParseResponse_BuildsLrcFromMillisecondTimes()
    {
        const string json = """
            {"type":"Word","metadata":{"title":"Real Title","artist":"Real Artist","album":"Alb"},
             "lyrics":[
               {"time":1000,"duration":2000,"text":"First line","syllabus":[{"text":"First ","time":1000,"duration":500}]},
               {"time":4500,"duration":1000,"text":"Second line"},
               {"time":9000,"text":""}
             ]}
            """;

        OnlineLyricsResult? result = LyricsPlusSource.ParseResponse(json, "Req", "ReqArtist");

        Assert.NotNull(result);
        Assert.Equal("[00:01.00]First line\n[00:04.50]Second line", result!.SyncedLyrics);
        Assert.Equal("Real Title", result.TrackName);
        Assert.Equal("Real Artist", result.ArtistName);
        Assert.Equal("LyricsPlus", result.Source);
    }

    [Fact]
    public void LyricsPlus_ParseResponse_FallsBackToRequestedNamesAndNestedData()
    {
        const string json = """{"data":{"lyrics":[{"time":0,"text":"a"},{"time":3000,"text":"b"}]}}""";

        OnlineLyricsResult? result = LyricsPlusSource.ParseResponse(json, "Req", "ReqArtist");

        Assert.NotNull(result);
        Assert.Equal("Req", result!.TrackName);
        Assert.Equal("ReqArtist", result.ArtistName);
        Assert.Equal("[00:00.00]a\n[00:03.00]b", result.SyncedLyrics);
    }

    [Fact]
    public void LyricsPlus_ParseResponse_WithoutTimes_ReturnsPlain_AndGarbageReturnsNull()
    {
        OnlineLyricsResult? plain = LyricsPlusSource.ParseResponse("""{"lyrics":[{"text":"one"},{"text":"two"}]}""", "T", "A");

        Assert.Null(plain!.SyncedLyrics);
        Assert.Equal("one\ntwo", plain.PlainLyrics);
        Assert.Null(LyricsPlusSource.ParseResponse("{}", "T", "A"));
        Assert.Null(LyricsPlusSource.ParseResponse("[]", "T", "A"));
        Assert.Null(LyricsPlusSource.ParseResponse("not json", "T", "A"));
    }

    [Fact]
    public void LyricsPlus_ReadSeconds_TreatsIntegersAsMillisecondsAndFractionsAsSeconds()
    {
        using JsonDocument document = JsonDocument.Parse("""{"a":1500,"b":1.5}""");

        Assert.Equal(1.5, LyricsPlusSource.ReadSeconds(document.RootElement, "a")!.Value, 3);
        Assert.Equal(1.5, LyricsPlusSource.ReadSeconds(document.RootElement, "b")!.Value, 3);
        Assert.Null(LyricsPlusSource.ReadSeconds(document.RootElement, "missing"));
    }

    [Fact]
    public async Task GetTextAsync_ReturnsNullForAResponseOverTheLimit_AndTheTextOtherwise()
    {
        using var small = new HttpClient(new FixedBodyHandler("hello"));
        using var large = new HttpClient(new FixedBodyHandler(new string('a', ExtraLyricsHttp.MaxResponseCharacters + 1)));

        Assert.Equal("hello", await ExtraLyricsHttp.GetTextAsync(small, "https://example.test/", "test", TestContext.Current.CancellationToken));
        Assert.Null(await ExtraLyricsHttp.GetTextAsync(large, "https://example.test/", "test", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Deezer_ParseToken_ReadsJwt()
    {
        Assert.Equal("abc.def", DeezerLyricsSource.ParseToken("{\"jwt\":\" abc.def \"}"));
        Assert.Null(DeezerLyricsSource.ParseToken("{}"));
        Assert.Null(DeezerLyricsSource.ParseToken("garbage"));
        Assert.Null(DeezerLyricsSource.ParseToken(null));
    }

    [Fact]
    public void Deezer_ParseSearch_ReadsTracksAndSkipsBrokenEntries()
    {
        const string json = """
            {"data":[
              {"id":3135556,"title":"Harder, Better","duration":224,"artist":{"name":"Daft Punk"},"album":{"title":"Discovery"}},
              {"title":"No id","duration":100},
              {"id":7,"title":"","duration":100}
            ]}
            """;

        var tracks = DeezerLyricsSource.ParseSearch(json);

        var track = Assert.Single(tracks);
        Assert.Equal(3135556, track.Id);
        Assert.Equal("Harder, Better", track.Title);
        Assert.Equal("Daft Punk", track.Artist);
        Assert.Equal("Discovery", track.Album);
        Assert.Equal(224, track.Duration);
        Assert.Empty(DeezerLyricsSource.ParseSearch("garbage"));
    }

    [Fact]
    public void Deezer_ParseLyrics_BuildsSyncedFromLinesAndKeepsPlainText()
    {
        const string json = """
            {"data":{"track":{"id":"1","lyrics":{"text":"Hello\nWorld",
              "synchronizedLines":[{"line":"World","milliseconds":5000},{"line":"Hello","milliseconds":1000},{"line":"","milliseconds":3000}]}}}}
            """;

        (string? plain, string? synced) = DeezerLyricsSource.ParseLyrics(json);

        Assert.Equal("Hello\nWorld", plain);
        Assert.Equal("[00:01.00]Hello\n[00:05.00]World", synced);
    }

    [Fact]
    public void Deezer_ParseLyrics_FallsBackToWordTimedLines()
    {
        const string json = """
            {"data":{"track":{"lyrics":{"text":null,"synchronizedLines":[],
              "synchronizedWordByWordLines":[
                {"start":1000,"end":2000,"words":[{"word":"Hi","start":1000,"end":1500},{"word":"there","start":1500,"end":2000}]},
                {"start":4000,"end":5000,"words":[{"word":"Bye","start":4000,"end":5000}]}]}}}}
            """;

        (string? plain, string? synced) = DeezerLyricsSource.ParseLyrics(json);

        Assert.Equal("Hi there\nBye", plain);
        Assert.Equal("[00:01.00]Hi there\n[00:04.00]Bye", synced);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("{}")]
    [InlineData("{\"data\":{\"track\":null}}")]
    [InlineData("{\"data\":{\"track\":{\"lyrics\":null}}}")]
    public void Deezer_ParseLyrics_ReturnsNothingWithoutLyrics(string json)
    {
        Assert.Equal((null, null), DeezerLyricsSource.ParseLyrics(json));
    }

    [Fact]
    public void Deezer_BuildRequestBody_CarriesTrackIdAsString()
    {
        string body = DeezerLyricsSource.BuildRequestBody(123);

        using JsonDocument document = JsonDocument.Parse(body);
        Assert.Equal("GetLyrics", document.RootElement.GetProperty("operationName").GetString());
        Assert.Equal("123", document.RootElement.GetProperty("variables").GetProperty("trackId").GetString());
        Assert.Contains("synchronizedLines", document.RootElement.GetProperty("query").GetString());
    }

    [Fact]
    public void Deezer_BuildSearchUrl_UsesTitleAndArtist() =>
        Assert.Equal("https://api.deezer.com/2.0/search?q=Song%20Band&limit=5", DeezerLyricsSource.BuildSearchUrl("Song", "Band"));

    [Fact]
    public void LrcMux_ParseResponse_ReadsLrcLibShapedObject()
    {
        const string json = """
            {"id":42,"trackName":"Song","artistName":"Band","albumName":"Album","duration":200.5,"instrumental":false,
             "plainLyrics":"Hello","syncedLyrics":"[00:01.00]Hello\n[00:03.00]World"}
            """;

        var result = Assert.Single(LrcMuxLyricsSource.ParseResponse(json));

        Assert.Equal("LRCMUX", result.Source);
        Assert.Equal("Song", result.TrackName);
        Assert.Equal("Band", result.ArtistName);
        Assert.Equal(200.5, result.Duration);
        Assert.True(result.HasSyncedLyrics);
    }

    [Fact]
    public void LrcMux_ParseResponse_ReadsArraysAndSkipsInstrumentalAndEmptyEntries()
    {
        const string json = """
            [
              {"id":1,"trackName":"Song","artistName":"Band","instrumental":true,"plainLyrics":"x"},
              {"id":2,"trackName":"Song","artistName":"Band","plainLyrics":"Words here"},
              {"id":3,"trackName":"Song","artistName":"Band","plainLyrics":"","syncedLyrics":null}
            ]
            """;

        var result = Assert.Single(LrcMuxLyricsSource.ParseResponse(json));

        Assert.False(result.HasSyncedLyrics);
        Assert.True(result.HasLyrics);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void LrcMux_ParseResponse_ReturnsNothingForUnusableBodies(string json)
    {
        Assert.Empty(LrcMuxLyricsSource.ParseResponse(json));
    }

    [Fact]
    public void LrcMux_BuildUrl_UsesTrackAndArtist() =>
        Assert.Equal("https://lrcmux.dev/api/get?track_name=Song&artist_name=Band", LrcMuxLyricsSource.BuildUrl("https://lrcmux.dev/api", "Song", "Band"));

    private sealed class FixedBodyHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
