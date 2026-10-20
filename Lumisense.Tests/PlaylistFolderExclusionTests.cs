using System.Collections.Generic;
using System.ComponentModel;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class PlaylistFolderExclusionTests
{
    [Fact]
    public void ExcludeTrack_RemembersPath_IgnoringCase_AndRaisesPropertyChanged()
    {
        var folder = new PlaylistFolder { SourcePath = @"C:\Music", DisplayName = "Music" };
        var raised = new List<string?>();
        folder.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        folder.ExcludeTrack(@"C:\Music\a.mp3");

        Assert.True(folder.IsExcluded(@"c:\music\A.MP3"));
        Assert.True(folder.HasExcludedTracks);
        Assert.Contains(nameof(PlaylistFolder.HasExcludedTracks), raised);
    }

    [Fact]
    public void ExcludeTrack_IsIgnored_ForFoldersWithoutSourcePath()
    {
        // У ручных папок и «Отдельных файлов» пересканирования нет, исключать там нечего.
        var folder = new PlaylistFolder { SourcePath = null, DisplayName = "Manual" };

        folder.ExcludeTrack("a.mp3");

        Assert.False(folder.HasExcludedTracks);
        Assert.False(folder.IsExcluded("a.mp3"));
    }

    [Fact]
    public void IncludeTrack_ForgetsOnlyThatPath()
    {
        var folder = new PlaylistFolder { SourcePath = @"C:\Music", DisplayName = "Music" };
        folder.ExcludeTrack("a.mp3");
        folder.ExcludeTrack("b.mp3");

        folder.IncludeTrack("A.MP3");

        Assert.False(folder.IsExcluded("a.mp3"));
        Assert.True(folder.IsExcluded("b.mp3"));
    }

    [Fact]
    public void RestoreAllExcluded_ClearsList_AndNotifiesOnlyWhenSomethingWasExcluded()
    {
        var folder = new PlaylistFolder { SourcePath = @"C:\Music", DisplayName = "Music" };
        int notifications = 0;
        folder.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistFolder.HasExcludedTracks)) notifications++;
        };

        folder.RestoreAllExcluded();
        Assert.Equal(0, notifications);

        folder.ExcludeTrack("a.mp3");
        folder.RestoreAllExcluded();

        Assert.False(folder.HasExcludedTracks);
        Assert.Equal(2, notifications);
    }

    [Fact]
    public void LoadExcluded_RestoresSavedPaths_WithoutNotifications()
    {
        var folder = new PlaylistFolder { SourcePath = @"C:\Music", DisplayName = "Music" };
        int notifications = 0;
        folder.PropertyChanged += (_, _) => notifications++;

        folder.LoadExcluded(new[] { "a.mp3", "b.mp3" });

        Assert.Equal(2, folder.ExcludedTracks.Count);
        Assert.True(folder.IsExcluded("B.mp3"));
        Assert.Equal(0, notifications);
    }
}
