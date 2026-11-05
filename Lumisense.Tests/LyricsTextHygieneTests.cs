using System;
using System.Linq;
using Xunit;

namespace Lumisense.Tests;

public sealed class LyricsTextHygieneTests
{
    private static OnlineLyricsResult Online(string? plain, string? synced, string source = "NetEase") =>
        new(1, "Title", "Artist", string.Empty, 200, plain, synced, source);

    [Theory]
    [InlineData("作曲 : Mallu Magalhães", true)]
    [InlineData("作词：Someone", true)]
    [InlineData("Composer: Someone", true)]
    [InlineData("Produced by : Someone", true)]
    [InlineData("{\"t\":0,\"c\":[{\"tx\":\"作曲: \"}]}", true)]
    [InlineData("I will write you a song", false)]
    [InlineData("Words fail me", false)]
    [InlineData("", false)]
    public void IsCreditLine_RecognizesServiceLines(string text, bool expected) =>
        Assert.Equal(expected, LyricsTextHygiene.IsCreditLine(text));

    [Fact]
    public void HasRealLyrics_FalseForCreditsOnlyLrc()
    {
        const string lrc = "[00:00.00-1] 作曲 : Mallu Magalhães\n[00:01.00-1] 作词 : Mallu Magalhães";

        Assert.False(LyricsTextHygiene.HasRealLyrics(lrc, null));
        Assert.False(Online(null, lrc).HasLyrics);
    }

    [Fact]
    public void CreateDocument_DropsCreditsAndKeepsSuffixedTimestamps()
    {
        const string lrc = "[00:00.00-1] 作曲 : Mallu Magalhães\n[00:12.50-1]First line\n[00:15.00,300]Second line";

        LyricsDocument document = LyricsService.CreateDocumentFromOnlineResult(Online(null, lrc));

        Assert.Equal(LyricsKind.Synced, document.Kind);
        Assert.Equal(new[] { "First line", "Second line" }, document.Lines.Select(line => line.Text).ToArray());
    }

    [Fact]
    public void CreateDocument_CleansRawLrcInPlainText()
    {
        const string plain = "[00:00.00-1] 作曲 : Someone\n[00:10.00-1]Hello\n\n[00:20.00-1]World";

        LyricsDocument document = LyricsService.CreateDocumentFromOnlineResult(Online(plain, null, "Kugou"));

        Assert.Equal(LyricsKind.Plain, document.Kind);
        Assert.DoesNotContain("[", document.PlainText);
        Assert.Contains("Hello", document.PlainText);
        Assert.DoesNotContain("作曲", document.PlainText);
    }

    [Fact]
    public void CreateDocument_CreditsOnlyResultIsEmpty() =>
        Assert.Equal(LyricsKind.None, LyricsService.CreateDocumentFromOnlineResult(Online("作曲 : A\n作词 : B", null)).Kind);
}
