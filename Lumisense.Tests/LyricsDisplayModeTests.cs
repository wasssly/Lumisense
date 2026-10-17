using System;
using Xunit;

namespace Lumisense.Tests;

public sealed class LyricsDisplayModeTests
{
    private static LyricsDocument Synced() => new(
        LyricsKind.Synced,
        new[]
        {
            new LyricLine(TimeSpan.FromSeconds(1), "First line"),
            new LyricLine(TimeSpan.FromSeconds(5), "Second line")
        },
        string.Empty,
        "LRC");

    [Fact]
    public void ApplyDisplayMode_KeepsSyncedLyricsInSyncedMode()
    {
        LyricsDocument document = Synced();

        Assert.Same(document, LyricsService.ApplyDisplayMode(document, "Synced"));
        Assert.Same(document, LyricsService.ApplyDisplayMode(document, null));
    }

    [Fact]
    public void ApplyDisplayMode_TurnsSyncedLyricsIntoPlainTextInPlainMode()
    {
        LyricsDocument plain = LyricsService.ApplyDisplayMode(Synced(), "Plain");

        Assert.Equal(LyricsKind.Plain, plain.Kind);
        Assert.Empty(plain.Lines);
        Assert.Equal("First line" + Environment.NewLine + "Second line", plain.PlainText);
        Assert.Equal("LRC", plain.SourceLabel);
    }

    [Theory]
    [InlineData("Синхронный LRC", "Текст из LRC")]
    [InlineData("LRCLIB · синхронный текст", "LRCLIB · текст")]
    [InlineData("NetEase · синхронный текст", "NetEase · текст")]
    public void ApplyDisplayMode_PlainModeDoesNotCallTextSynced(string sourceLabel, string expected)
    {
        var document = new LyricsDocument(
            LyricsKind.Synced,
            new[] { new LyricLine(TimeSpan.FromSeconds(1), "First line") },
            string.Empty,
            sourceLabel);

        Assert.Equal(expected, LyricsService.ApplyDisplayMode(document, "Plain").SourceLabel);
    }

    [Fact]
    public void ApplyDisplayMode_LeavesPlainAndEmptyDocumentsAlone()
    {
        var plainSource = new LyricsDocument(LyricsKind.Plain, Array.Empty<LyricLine>(), "Text", "TXT");

        Assert.Same(plainSource, LyricsService.ApplyDisplayMode(plainSource, "Plain"));
        Assert.Same(LyricsDocument.Empty, LyricsService.ApplyDisplayMode(LyricsDocument.Empty, "Plain"));
    }
}
