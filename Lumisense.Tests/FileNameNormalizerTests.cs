using System;
using System.IO;
using System.Linq;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class FileNameNormalizerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumisense-rename-tests-" + Guid.NewGuid().ToString("N"));

    public FileNameNormalizerTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* временная папка теста, остатки не критичны */ }
    }

    // Файлы без тегов: имя берётся из имени файла, поэтому содержимое не важно.
    private string CreateFile(string name, string contents = "audio")
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, contents);
        return path;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeTemplate_FallsBackToDefaultForBlankTemplates(string? template)
    {
        Assert.Equal(FileNameNormalizer.DefaultTemplate, FileNameNormalizer.NormalizeTemplate(template));
    }

    [Fact]
    public void NormalizeTemplate_TrimsWhitespaceAndLimitsLengthTo180()
    {
        Assert.Equal("{Title}{Extension}", FileNameNormalizer.NormalizeTemplate("  {Title}{Extension}  "));
        Assert.Equal(180, FileNameNormalizer.NormalizeTemplate(new string('t', 300)).Length);
    }

    [Fact]
    public void ResolveArtistAndTitle_PrefersTagsAndTrimsThem()
    {
        var (artist, title) = FileNameNormalizer.ResolveArtistAndTitle("Other - Name.mp3", " Daft Punk ", " One More Time ", "Unknown");

        Assert.Equal("Daft Punk", artist);
        Assert.Equal("One More Time", title);
    }

    [Theory]
    [InlineData("Artist - Title.mp3")]
    [InlineData("01 - Artist - Title.mp3")]
    [InlineData("07. Artist - Title.mp3")]
    [InlineData("  12_Artist - Title.mp3")]
    public void ResolveArtistAndTitle_SplitsFileNameAndDropsTrackNumberPrefix(string fileName)
    {
        var (artist, title) = FileNameNormalizer.ResolveArtistAndTitle(fileName, null, null, "Unknown");

        Assert.Equal("Artist", artist);
        Assert.Equal("Title", title);
    }

    [Fact]
    public void ResolveArtistAndTitle_UsesFallbackArtistAndFileNameWhenNothingCanBeSplit()
    {
        var (artist, title) = FileNameNormalizer.ResolveArtistAndTitle("Just a title.mp3", null, null, "Unknown");

        Assert.Equal("Unknown", artist);
        Assert.Equal("Just a title", title);
    }

    [Fact]
    public void ResolveArtistAndTitle_KeepsNumberOnlyFileNameAsTitle()
    {
        var (artist, title) = FileNameNormalizer.ResolveArtistAndTitle("123.mp3", null, null, "Unknown");

        Assert.Equal("Unknown", artist);
        Assert.Equal("123", title);
    }

    [Fact]
    public void ResolveArtistAndTitle_SplitsTitleTagWhenArtistTagIsMissing()
    {
        var (artist, title) = FileNameNormalizer.ResolveArtistAndTitle("file.mp3", null, "Artist - Title", "Unknown");

        Assert.Equal("Artist", artist);
        Assert.Equal("Title", title);
    }

    [Fact]
    public void ResolveArtistAndTitle_TakesMissingTitleFromFileNameWhenArtistIsTagged()
    {
        var (artist, title) = FileNameNormalizer.ResolveArtistAndTitle("Other - Name.mp3", "Tagged", null, "Unknown");

        Assert.Equal("Tagged", artist);
        Assert.Equal("Name", title);
    }

    [Fact]
    public void BuildPreview_ProposesArtistDashTitleForNumberedFile()
    {
        string source = CreateFile("01 - Artist - Title.mp3");

        var preview = Assert.Single(FileNameNormalizer.BuildPreview(new[] { source }, null));

        Assert.True(preview.CanRename);
        Assert.Null(preview.SkipReason);
        Assert.Equal("Artist - Title.mp3", preview.TargetFileName);
        Assert.Equal(_directory, Path.GetDirectoryName(preview.TargetPath));
    }

    [Fact]
    public void BuildPreview_SkipsFilesThatAlreadyHaveTheTargetName()
    {
        string source = CreateFile("Artist - Title.mp3");

        var preview = Assert.Single(FileNameNormalizer.BuildPreview(new[] { source }, null));

        Assert.False(preview.CanRename);
        Assert.NotNull(preview.SkipReason);
    }

    [Fact]
    public void BuildPreview_SkipsWhenTargetFileAlreadyExists()
    {
        string source = CreateFile("01 - Artist - Title.mp3");
        CreateFile("Artist - Title.mp3");

        var preview = Assert.Single(FileNameNormalizer.BuildPreview(new[] { source }, null));

        Assert.False(preview.CanRename);
        Assert.NotNull(preview.SkipReason);
    }

    [Fact]
    public void BuildPreview_SkipsMissingAndProtectedFiles()
    {
        string missing = Path.Combine(_directory, "missing.mp3");
        string protectedFile = CreateFile("01 - Playing - Now.mp3");

        var previews = FileNameNormalizer.BuildPreview(new[] { missing, protectedFile }, null, new[] { protectedFile });

        Assert.Equal(2, previews.Count);
        Assert.All(previews, p => Assert.False(p.CanRename));
        Assert.All(previews, p => Assert.NotNull(p.SkipReason));
        Assert.True(File.Exists(protectedFile));
    }

    [Fact]
    public void BuildPreview_SkipsBothFilesThatWouldGetTheSameName()
    {
        string first = CreateFile("01 - Artist - Title.mp3");
        string second = CreateFile("02 - Artist - Title.mp3");
        string other = CreateFile("03 - Artist - Other.mp3");

        var previews = FileNameNormalizer.BuildPreview(new[] { first, second, other }, null);

        Assert.False(previews.Single(p => p.SourcePath == first).CanRename);
        Assert.False(previews.Single(p => p.SourcePath == second).CanRename);
        Assert.True(previews.Single(p => p.SourcePath == other).CanRename);
    }

    [Fact]
    public void BuildPreview_ListsTheSamePathOnlyOnceAndIgnoresBlankPaths()
    {
        string source = CreateFile("01 - Artist - Title.mp3");

        var previews = FileNameNormalizer.BuildPreview(new[] { source, source, "", "   " }, null);

        Assert.Single(previews);
    }

    [Fact]
    public void BuildPreview_AppendsOriginalExtensionWhenTemplateHasNone()
    {
        string source = CreateFile("Artist - Title.mp3");

        var preview = Assert.Single(FileNameNormalizer.BuildPreview(new[] { source }, "{Title}"));

        Assert.True(preview.CanRename);
        Assert.Equal("Title.mp3", preview.TargetFileName);
    }

    [Fact]
    public void BuildPreview_ExpandsTokensCaseInsensitively()
    {
        string source = CreateFile("Artist - Title.mp3");

        var preview = Assert.Single(FileNameNormalizer.BuildPreview(new[] { source }, "{title} ({ARTIST}){Extension}"));

        Assert.Equal("Title (Artist).mp3", preview.TargetFileName);
    }

    [Fact]
    public void BuildPreview_SkipsTemplatesWithUnknownTokens()
    {
        string source = CreateFile("Artist - Title.mp3");

        var preview = Assert.Single(FileNameNormalizer.BuildPreview(new[] { source }, "{Title} {Bitrate}{Extension}"));

        Assert.False(preview.CanRename);
        Assert.NotNull(preview.SkipReason);
    }

    [Fact]
    public void BuildPreview_ReplacesPathSeparatorsSoTheFileStaysInItsFolder()
    {
        string source = CreateFile("Artist - Title.mp3");

        var preview = Assert.Single(FileNameNormalizer.BuildPreview(new[] { source }, "{Title}/{Artist}{Extension}"));

        Assert.True(preview.CanRename);
        Assert.Equal("Title_Artist.mp3", preview.TargetFileName);
        Assert.Equal(_directory, Path.GetDirectoryName(preview.TargetPath));
    }

    [Fact]
    public void BuildPreview_SkipsReservedWindowsNames()
    {
        string source = CreateFile("Artist - CON.mp3");

        var preview = Assert.Single(FileNameNormalizer.BuildPreview(new[] { source }, "{Title}"));

        Assert.False(preview.CanRename);
        Assert.NotNull(preview.SkipReason);
    }

    [Fact]
    public void Execute_RenamesFilesAndReportsThem()
    {
        string first = CreateFile("01 - Artist - One.mp3", "first");
        string second = CreateFile("02 - Artist - Two.mp3", "second");
        var preview = FileNameNormalizer.BuildPreview(new[] { first, second }, null);

        var result = FileNameNormalizer.Execute(preview);

        Assert.Equal(2, result.RenamedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Empty(result.Errors);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.Equal("first", File.ReadAllText(Path.Combine(_directory, "Artist - One.mp3")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(_directory, "Artist - Two.mp3")));
        Assert.Equal(Path.Combine(_directory, "Artist - One.mp3"), result.RenamedPaths[first]);
    }

    [Fact]
    public void Execute_CountsNonRenamablePreviewsAsSkipped()
    {
        string renamable = CreateFile("01 - Artist - One.mp3");
        string alreadyDone = CreateFile("Artist - Two.mp3");
        var preview = FileNameNormalizer.BuildPreview(new[] { renamable, alreadyDone }, null);

        var result = FileNameNormalizer.Execute(preview);

        Assert.Equal(1, result.RenamedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.True(File.Exists(alreadyDone));
    }

    [Fact]
    public void Execute_DoesNotOverwriteATargetThatAppearedAfterThePreview()
    {
        string source = CreateFile("01 - Artist - Title.mp3", "source");
        var preview = FileNameNormalizer.BuildPreview(new[] { source }, null);
        string target = CreateFile("Artist - Title.mp3", "someone else");

        var result = FileNameNormalizer.Execute(preview);

        Assert.Equal(0, result.RenamedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Single(result.Errors);
        Assert.Equal("source", File.ReadAllText(source));
        Assert.Equal("someone else", File.ReadAllText(target));
    }

    [Fact]
    public void Execute_FailsGracefullyWhenTheSourceDisappeared()
    {
        string source = CreateFile("01 - Artist - Title.mp3");
        var preview = FileNameNormalizer.BuildPreview(new[] { source }, null);
        File.Delete(source);

        var result = FileNameNormalizer.Execute(preview);

        Assert.Equal(0, result.RenamedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.False(File.Exists(Path.Combine(_directory, "Artist - Title.mp3")));
    }
}
