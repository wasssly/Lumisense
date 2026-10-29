using System;
using System.IO;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

[Collection(StaticManagersCollection.Name)]
public sealed class TrackMetadataCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumisense-metadata-tests-" + Guid.NewGuid().ToString("N"));

    public TrackMetadataCacheTests()
    {
        Directory.CreateDirectory(_directory);
        TrackMetadataCache.ClearForTests();
    }

    public void Dispose()
    {
        TrackMetadataCache.ClearForTests();
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* временная папка теста, остатки не критичны */ }
    }

    private string CreateFile(string name, string content = "audio")
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void RememberedTags_AreReturnedWhileTheFileIsUnchanged()
    {
        string path = CreateFile("a.mp3");

        TrackMetadataCache.Remember(path, "Title", "Artist", "Album Artist");

        Assert.True(TrackMetadataCache.TryGet(path, out TrackMetadataCache.Entry? entry));
        Assert.Equal("Title", entry!.Title);
        Assert.Equal("Artist", entry.Artist);
        Assert.Equal("Album Artist", entry.AlbumArtist);
    }

    [Fact]
    public void PathLookup_IgnoresCase()
    {
        string path = CreateFile("a.mp3");
        TrackMetadataCache.Remember(path, "Title", "Artist", null);

        Assert.True(TrackMetadataCache.TryGet(path.ToUpperInvariant(), out _));
    }

    [Fact]
    public void ChangedFile_InvalidatesTheEntry()
    {
        string path = CreateFile("a.mp3", "short");
        TrackMetadataCache.Remember(path, "Title", "Artist", null);

        File.WriteAllText(path, "a different and longer content");

        Assert.False(TrackMetadataCache.TryGet(path, out _));
    }

    [Fact]
    public void ChangedModificationTime_InvalidatesTheEntry()
    {
        string path = CreateFile("a.mp3");
        TrackMetadataCache.Remember(path, "Title", "Artist", null);

        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(5));

        Assert.False(TrackMetadataCache.TryGet(path, out _));
    }

    [Fact]
    public void MissingFile_IsNeverCachedOrReturned()
    {
        string path = Path.Combine(_directory, "missing.mp3");

        TrackMetadataCache.Remember(path, "Title", "Artist", null);

        Assert.False(TrackMetadataCache.TryGet(path, out _));
        Assert.Null(TrackMetadataCache.GetOrRead(path));
    }

    [Fact]
    public void GetOrRead_ReturnsTheCachedEntryWithoutReadingTheFileAgain()
    {
        // Файл не аудио: чтение тегов ничего бы не дало, поэтому результат может прийти только из кэша.
        string path = CreateFile("a.mp3");
        TrackMetadataCache.Remember(path, "Cached title", "Cached artist", null);

        TrackMetadataCache.Entry? entry = TrackMetadataCache.GetOrRead(path);

        Assert.Equal("Cached title", entry?.Title);
        Assert.Equal("Cached artist", entry?.Artist);
    }
}
