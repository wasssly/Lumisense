using System;
using System.Collections.Generic;
using System.Linq;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class ShuffleSessionTests
{
    private static ShuffleSession CreateEnabled(int seed = 42)
    {
        var session = new ShuffleSession(new Random(seed));
        session.SetEnabled(true);
        return session;
    }

    // Состояние истории и мешка наружу не видно, поэтому читаем его через PersistTo.
    private static AppSettings Persist(ShuffleSession session)
    {
        var settings = new AppSettings();
        session.PersistTo(settings);
        return settings;
    }

    private static ShuffleSession RestoreWithHistory(string[] history, int index, params string[] active)
    {
        var session = CreateEnabled();
        var settings = new AppSettings
        {
            IsShuffleEnabled = true,
            ShuffleHistory = history.ToList(),
            ShuffleHistoryIndex = index
        };
        session.RestoreFrom(settings, active.ToHashSet(), lastTrackPath: null);
        return session;
    }

    [Fact]
    public void SetEnabled_TogglesIsEnabled()
    {
        var session = new ShuffleSession(new Random(1));
        Assert.False(session.IsEnabled);

        session.SetEnabled(true);
        Assert.True(session.IsEnabled);

        session.SetEnabled(false);
        Assert.False(session.IsEnabled);
    }

    [Fact]
    public void GetRandom_ThrowsWhenThereAreNoActiveTracks()
    {
        var session = CreateEnabled();

        Assert.Throws<InvalidOperationException>(() => session.GetRandom(new List<string>(), null));
    }

    [Fact]
    public void GetRandom_ReturnsTheOnlyActiveTrack()
    {
        var session = CreateEnabled();

        Assert.Equal("a.mp3", session.GetRandom(new List<string> { "a.mp3" }, "a.mp3"));
    }

    [Fact]
    public void StartStandardSession_SeedsHistoryWithCurrentTrackAndClearsBag()
    {
        var session = RestoreWithHistory(new[] { "a.mp3" }, 0, "a.mp3", "b.mp3", "c.mp3");
        var withBag = new AppSettings { IsShuffleEnabled = true, ShuffleBag = new List<string> { "b.mp3", "c.mp3" } };
        session.RestoreFrom(withBag, new HashSet<string> { "a.mp3", "b.mp3", "c.mp3" }, null);

        session.StartStandardSession("c.mp3", useImprovedShuffle: false);
        AppSettings settings = Persist(session);

        Assert.Equal(new[] { "c.mp3" }, settings.ShuffleHistory);
        Assert.Equal(0, settings.ShuffleHistoryIndex);
        Assert.Empty(settings.ShuffleBag);
    }

    [Fact]
    public void StartStandardSession_DoesNothingWhenShuffleIsDisabledOrImproved()
    {
        var disabled = new ShuffleSession(new Random(1));
        disabled.StartStandardSession("a.mp3", useImprovedShuffle: false);
        // Выключенный shuffle PersistTo всё равно очищает, поэтому включаем и смотрим, что истории нет.
        disabled.SetEnabled(true);
        Assert.Empty(Persist(disabled).ShuffleHistory);

        var improved = CreateEnabled();
        improved.StartStandardSession("a.mp3", useImprovedShuffle: true);
        Assert.Empty(Persist(improved).ShuffleHistory);
    }

    [Fact]
    public void GetHistoryTrack_SeedsEmptyHistoryWithCurrentTrackAndHasNowhereToGo()
    {
        var session = CreateEnabled();
        var active = new List<string> { "a.mp3", "b.mp3" };

        Assert.Null(session.GetHistoryTrack(-1, active, "a.mp3"));
        Assert.Null(session.GetHistoryTrack(1, active, "a.mp3"));

        AppSettings settings = Persist(session);
        Assert.Equal(new[] { "a.mp3" }, settings.ShuffleHistory);
        Assert.Equal(0, settings.ShuffleHistoryIndex);
    }

    [Fact]
    public void GetHistoryTrack_WithoutCurrentTrackAndHistoryReturnsNull()
    {
        var session = CreateEnabled();

        Assert.Null(session.GetHistoryTrack(-1, new List<string> { "a.mp3" }, null));
        Assert.Empty(Persist(session).ShuffleHistory);
    }

    [Fact]
    public void AppendNew_DoesNotRepeatCurrentTrackAndRecordsTheStep()
    {
        for (int seed = 0; seed < 20; seed++)
        {
            var session = CreateEnabled(seed);
            var active = new List<string> { "a.mp3", "b.mp3", "c.mp3" };

            string next = session.AppendNew(active, "a.mp3");

            Assert.NotEqual("a.mp3", next, StringComparer.OrdinalIgnoreCase);
            AppSettings settings = Persist(session);
            Assert.Equal(new[] { "a.mp3", next }, settings.ShuffleHistory);
            Assert.Equal(1, settings.ShuffleHistoryIndex);
        }
    }

    [Fact]
    public void PrependNew_InsertsBeforeCurrentTrackAndPointsAtIt()
    {
        for (int seed = 0; seed < 20; seed++)
        {
            var session = CreateEnabled(seed);
            var active = new List<string> { "a.mp3", "b.mp3", "c.mp3" };

            string previous = session.PrependNew(active, "a.mp3");

            Assert.NotEqual("a.mp3", previous, StringComparer.OrdinalIgnoreCase);
            AppSettings settings = Persist(session);
            Assert.Equal(new[] { previous, "a.mp3" }, settings.ShuffleHistory);
            Assert.Equal(0, settings.ShuffleHistoryIndex);
        }
    }

    [Fact]
    public void GetHistoryTrack_WalksBackAndForwardThroughAppendedTracks()
    {
        var session = CreateEnabled();
        var active = new List<string> { "a.mp3", "b.mp3", "c.mp3" };
        string first = session.AppendNew(active, "a.mp3");
        string second = session.AppendNew(active, first);

        Assert.Equal(first, session.GetHistoryTrack(-1, active, second));
        Assert.Equal("a.mp3", session.GetHistoryTrack(-1, active, first));
        Assert.Null(session.GetHistoryTrack(-1, active, "a.mp3"));
        Assert.Equal(first, session.GetHistoryTrack(1, active, "a.mp3"));
        Assert.Equal(second, session.GetHistoryTrack(1, active, first));
        Assert.Null(session.GetHistoryTrack(1, active, second));
    }

    [Fact]
    public void GetHistoryTrack_BackwardStepOntoRemovedTrackDropsItAndEverythingBeforeIt()
    {
        var session = RestoreWithHistory(new[] { "a.mp3", "b.mp3", "c.mp3" }, 2, "a.mp3", "b.mp3", "c.mp3");
        var activeWithoutB = new List<string> { "a.mp3", "c.mp3" };

        Assert.Null(session.GetHistoryTrack(-1, activeWithoutB, "c.mp3"));

        AppSettings settings = Persist(session);
        Assert.Equal(new[] { "c.mp3" }, settings.ShuffleHistory);
        Assert.Equal(0, settings.ShuffleHistoryIndex);
    }

    [Fact]
    public void GetHistoryTrack_ForwardStepOntoRemovedTrackDropsItAndEverythingAfterIt()
    {
        var session = RestoreWithHistory(new[] { "a.mp3", "b.mp3", "c.mp3" }, 0, "a.mp3", "b.mp3", "c.mp3");
        var activeWithoutB = new List<string> { "a.mp3", "c.mp3" };

        Assert.Null(session.GetHistoryTrack(1, activeWithoutB, "a.mp3"));

        AppSettings settings = Persist(session);
        Assert.Equal(new[] { "a.mp3" }, settings.ShuffleHistory);
        Assert.Equal(0, settings.ShuffleHistoryIndex);
    }

    [Fact]
    public void Reset_ClearsHistoryAndBagButKeepsShuffleEnabled()
    {
        var session = RestoreWithHistory(new[] { "a.mp3", "b.mp3" }, 1, "a.mp3", "b.mp3");

        session.Reset();

        Assert.True(session.IsEnabled);
        AppSettings settings = Persist(session);
        Assert.Empty(settings.ShuffleHistory);
        Assert.Equal(-1, settings.ShuffleHistoryIndex);
        Assert.Empty(settings.ShuffleBag);
    }

    [Fact]
    public void PersistTo_WritesEmptyStateWhenShuffleIsDisabled()
    {
        var session = RestoreWithHistory(new[] { "a.mp3", "b.mp3" }, 1, "a.mp3", "b.mp3");
        session.SetEnabled(false);
        var settings = new AppSettings
        {
            ShuffleHistory = new List<string> { "stale.mp3" },
            ShuffleHistoryIndex = 5,
            ShuffleBag = new List<string> { "stale.mp3" }
        };

        session.PersistTo(settings);

        Assert.Empty(settings.ShuffleHistory);
        Assert.Equal(-1, settings.ShuffleHistoryIndex);
        Assert.Empty(settings.ShuffleBag);
    }

    [Fact]
    public void PersistTo_KeepsOnlyTheLast512HistoryEntriesAndShiftsTheIndex()
    {
        var session = CreateEnabled();
        var active = Enumerable.Range(0, 600).Select(i => $"t{i}.mp3").ToList();
        string current = active[0];
        string last = current;
        for (int i = 0; i < 600; i++)
            last = session.AppendNew(active, last);

        AppSettings settings = Persist(session);

        Assert.Equal(512, settings.ShuffleHistory.Count);
        Assert.Equal(last, settings.ShuffleHistory[^1]);
        Assert.Equal(511, settings.ShuffleHistoryIndex);
        Assert.True(settings.ShuffleBag.Count <= 512);
    }

    [Fact]
    public void RestoreFrom_RestoresNothingWhenShuffleWasDisabledInSettings()
    {
        var session = CreateEnabled();
        var settings = new AppSettings
        {
            IsShuffleEnabled = false,
            ShuffleHistory = new List<string> { "a.mp3" },
            ShuffleHistoryIndex = 0,
            ShuffleBag = new List<string> { "a.mp3" }
        };

        session.RestoreFrom(settings, new HashSet<string> { "a.mp3" }, null);

        Assert.Empty(Persist(session).ShuffleHistory);
    }

    [Fact]
    public void RestoreFrom_RestoresNothingWhenThereAreNoActiveTracks()
    {
        var session = CreateEnabled();
        var settings = new AppSettings
        {
            IsShuffleEnabled = true,
            ShuffleHistory = new List<string> { "a.mp3" },
            ShuffleHistoryIndex = 0
        };

        session.RestoreFrom(settings, new HashSet<string>(), null);

        Assert.Empty(Persist(session).ShuffleHistory);
    }

    [Fact]
    public void RestoreFrom_DropsBlankAndInactivePathsFromHistoryAndBag()
    {
        var session = CreateEnabled();
        var settings = new AppSettings
        {
            IsShuffleEnabled = true,
            ShuffleHistory = new List<string> { "a.mp3", "", "gone.mp3", "  ", "b.mp3" },
            ShuffleHistoryIndex = 4,
            ShuffleBag = new List<string> { "gone.mp3", "c.mp3", "", "b.mp3" }
        };

        session.RestoreFrom(settings, new HashSet<string> { "a.mp3", "b.mp3", "c.mp3" }, null);

        AppSettings restored = Persist(session);
        Assert.Equal(new[] { "a.mp3", "b.mp3" }, restored.ShuffleHistory);
        Assert.Equal(new[] { "c.mp3", "b.mp3" }, restored.ShuffleBag);
    }

    [Theory]
    [InlineData(99, 2)]
    [InlineData(-1, 0)]
    [InlineData(1, 1)]
    public void RestoreFrom_ClampsSavedHistoryIndex(int savedIndex, int expectedIndex)
    {
        var session = RestoreWithHistory(new[] { "a.mp3", "b.mp3", "c.mp3" }, savedIndex, "a.mp3", "b.mp3", "c.mp3");

        Assert.Equal(expectedIndex, Persist(session).ShuffleHistoryIndex);
    }

    [Fact]
    public void RestoreFrom_PointsAtTheLastOccurrenceOfTheLastPlayedTrackIgnoringCase()
    {
        var session = CreateEnabled();
        var settings = new AppSettings
        {
            IsShuffleEnabled = true,
            ShuffleHistory = new List<string> { "a.mp3", "b.mp3", "a.mp3", "c.mp3" },
            ShuffleHistoryIndex = 3
        };

        session.RestoreFrom(settings, new HashSet<string> { "a.mp3", "b.mp3", "c.mp3" }, "A.MP3");

        Assert.Equal(2, Persist(session).ShuffleHistoryIndex);
    }

    [Fact]
    public void RestoreFrom_RemovesCaseInsensitiveDuplicatesFromBag()
    {
        var session = CreateEnabled();
        var settings = new AppSettings
        {
            IsShuffleEnabled = true,
            ShuffleHistory = new List<string> { "a.mp3" },
            ShuffleBag = new List<string> { "b.mp3", "B.mp3", "c.mp3" }
        };

        session.RestoreFrom(settings, new HashSet<string> { "a.mp3", "b.mp3", "B.mp3", "c.mp3" }, null);

        Assert.Equal(new[] { "b.mp3", "c.mp3" }, Persist(session).ShuffleBag);
    }

    [Fact]
    public void RestoreFrom_DiscardsPreviousSessionState()
    {
        var session = RestoreWithHistory(new[] { "old1.mp3", "old2.mp3" }, 1, "old1.mp3", "old2.mp3");
        var settings = new AppSettings { IsShuffleEnabled = true, ShuffleHistory = new List<string> { "new.mp3" }, ShuffleHistoryIndex = 0 };

        session.RestoreFrom(settings, new HashSet<string> { "new.mp3" }, null);

        Assert.Equal(new[] { "new.mp3" }, Persist(session).ShuffleHistory);
    }
}
