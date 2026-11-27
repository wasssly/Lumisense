using Lumisense;
using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Lumisense.Tests;

public sealed class TrackReaderFactoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumisense-reader-" + Guid.NewGuid().ToString("N"));

    public TrackReaderFactoryTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string WriteFile(string name, string header)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(header).Concat(new byte[40]).ToArray());
        return path;
    }

    [Theory]
    [InlineData("a.ogg")]
    [InlineData("a.OGA")]
    public void OggVorbisHeader_IsDetected(string name) =>
        Assert.True(TrackReaderFactory.IsOggVorbis(WriteFile(name, "OggS\0\u0002" + new string('\0', 20) + "\u0001vorbis")));

    [Fact]
    public void OpusInsideOgg_IsNotTreatedAsVorbis() =>
        Assert.False(TrackReaderFactory.IsOggVorbis(WriteFile("a.ogg", "OggS\0\u0002" + new string('\0', 20) + "OpusHead")));

    [Fact]
    public void OtherExtensions_AreLeftToMediaFoundation() =>
        Assert.False(TrackReaderFactory.IsOggVorbis(WriteFile("a.mp3", "OggS\0\u0002" + new string('\0', 20) + "\u0001vorbis")));

    [Fact]
    public void MissingFile_IsNotVorbis() =>
        Assert.False(TrackReaderFactory.IsOggVorbis(Path.Combine(_directory, "missing.ogg")));
}
