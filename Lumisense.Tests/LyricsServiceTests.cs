using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class LyricsServiceTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), "LumisenseLyricsTests", Guid.NewGuid().ToString("N"));
    private string? _managedLrcPath;
    private string? _managedTextPath;
    private string? _managedSourcePath;

    [Fact]
    public async Task SaveOnlineResultAsync_SyncedLyrics_StoresLrcOutsideAudioDirectory()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string audioPath = Path.Combine(_temporaryDirectory, "track.mp3");
        await File.WriteAllBytesAsync(audioPath, Array.Empty<byte>(), TestContext.Current.CancellationToken);
        _managedLrcPath = LyricsService.GetManagedLrcPath(audioPath);

        var result = new OnlineLyricsResult(
            Id: 1,
            TrackName: "Track",
            ArtistName: "Artist",
            AlbumName: "Album",
            Duration: 180,
            PlainLyrics: null,
            SyncedLyrics: "[00:01.00]Первая строка");

        await LyricsService.SaveOnlineResultAsync(audioPath, result, CancellationToken.None);

        Assert.True(File.Exists(_managedLrcPath));
        Assert.False(File.Exists(Path.ChangeExtension(audioPath, ".lrc")));
        Assert.NotEqual(Path.GetDirectoryName(audioPath), Path.GetDirectoryName(_managedLrcPath));

        LyricsDocument document = await LyricsService.LoadAsync(audioPath, CancellationToken.None);
        Assert.Equal(LyricsKind.Synced, document.Kind);
        Assert.Single(document.Lines);
        Assert.Equal("Первая строка", document.Lines[0].Text);
    }

    [Fact]
    public async Task SaveOnlineResultAsync_PlainLyrics_StoresTextOutsideAudioDirectory()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string audioPath = Path.Combine(_temporaryDirectory, "plain-track.mp3");
        await File.WriteAllBytesAsync(audioPath, Array.Empty<byte>(), TestContext.Current.CancellationToken);
        _managedTextPath = LyricsService.GetManagedTextPath(audioPath);

        var result = new OnlineLyricsResult(
            Id: 2,
            TrackName: "Track",
            ArtistName: "Artist",
            AlbumName: "Album",
            Duration: 180,
            PlainLyrics: "Обычный текст",
            SyncedLyrics: null);

        await LyricsService.SaveOnlineResultAsync(audioPath, result, CancellationToken.None);

        Assert.True(File.Exists(_managedTextPath));
        Assert.False(File.Exists(Path.ChangeExtension(audioPath, ".txt")));
        Assert.NotEqual(Path.GetDirectoryName(audioPath), Path.GetDirectoryName(_managedTextPath));

        LyricsDocument document = await LyricsService.LoadAsync(audioPath, CancellationToken.None);
        Assert.Equal(LyricsKind.Plain, document.Kind);
        Assert.Equal("Обычный текст", document.PlainText);
    }

    [Fact]
    public async Task SaveOnlineResultAsync_SyncedLyrics_RemembersSourceInLabel()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string audioPath = Path.Combine(_temporaryDirectory, "source-track.mp3");
        await File.WriteAllBytesAsync(audioPath, Array.Empty<byte>(), TestContext.Current.CancellationToken);
        _managedLrcPath = LyricsService.GetManagedLrcPath(audioPath);
        _managedSourcePath = LyricsService.GetManagedSourcePath(audioPath);

        var result = new OnlineLyricsResult(
            Id: 3, TrackName: "Track", ArtistName: "Artist", AlbumName: "Album", Duration: 180,
            PlainLyrics: null, SyncedLyrics: "[00:01.00]Строка", Source: "NetEase");

        await LyricsService.SaveOnlineResultAsync(audioPath, result, CancellationToken.None);

        LyricsDocument document = await LyricsService.LoadAsync(audioPath, CancellationToken.None);
        Assert.Equal(LyricsKind.Synced, document.Kind);
        Assert.Equal("NetEase · синхронный текст", document.SourceLabel);
    }

    [Fact]
    public async Task SavePastedLyricsAsync_AfterOnlineResult_DropsPreviousSource()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string audioPath = Path.Combine(_temporaryDirectory, "pasted-after-online.mp3");
        await File.WriteAllBytesAsync(audioPath, Array.Empty<byte>(), TestContext.Current.CancellationToken);
        _managedTextPath = LyricsService.GetManagedTextPath(audioPath);
        _managedSourcePath = LyricsService.GetManagedSourcePath(audioPath);

        var result = new OnlineLyricsResult(
            Id: 4, TrackName: "Track", ArtistName: "Artist", AlbumName: "Album", Duration: 180,
            PlainLyrics: "Обычный текст", SyncedLyrics: null, Source: "QQ Music");
        await LyricsService.SaveOnlineResultAsync(audioPath, result, CancellationToken.None);

        LyricsDocument online = await LyricsService.LoadAsync(audioPath, CancellationToken.None);
        Assert.Equal("QQ Music · текст", online.SourceLabel);

        await LyricsService.SavePastedLyricsAsync(audioPath, "Вставленный вручную", CancellationToken.None);

        LyricsDocument pasted = await LyricsService.LoadAsync(audioPath, CancellationToken.None);
        Assert.Equal("Вставленный вручную", pasted.PlainText);
        Assert.Equal("Кэш вставленного текста", pasted.SourceLabel);
    }

    [Theory]
    [InlineData("NetEase", "NetEase")]
    [InlineData("  QQ Music \r\n", "QQ Music")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("Две\nстроки", null)]
    public void NormalizeStoredSource_AcceptsShortSingleLineNamesOnly(string raw, string? expected)
    {
        Assert.Equal(expected, LyricsService.NormalizeStoredSource(raw));
    }

    [Fact]
    public void NormalizeStoredSource_RejectsTooLongNames()
    {
        Assert.Null(LyricsService.NormalizeStoredSource(new string('x', 41)));
        Assert.Null(LyricsService.NormalizeStoredSource(null));
    }

    public void Dispose()
    {
        try
        {
            if (_managedLrcPath is not null && File.Exists(_managedLrcPath))
                File.Delete(_managedLrcPath);
            if (_managedTextPath is not null && File.Exists(_managedTextPath))
                File.Delete(_managedTextPath);
            if (_managedSourcePath is not null && File.Exists(_managedSourcePath))
                File.Delete(_managedSourcePath);
            if (Directory.Exists(_temporaryDirectory))
                Directory.Delete(_temporaryDirectory, recursive: true);
        }
        catch
        {
            // Очистка временных файлов не должна скрывать исходный результат теста.
        }
    }
}
