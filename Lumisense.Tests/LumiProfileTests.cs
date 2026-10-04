using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class LumiProfileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumisense-lumi-tests-" + Guid.NewGuid().ToString("N"));

    public LumiProfileTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* временная папка теста, остатки не критичны */ }
    }

    private string PathFor(string name) => Path.Combine(_directory, name);

    private string WriteProfile(AppSettings settings, int formatVersion = 1)
    {
        string path = PathFor(Guid.NewGuid().ToString("N") + LumiProfileIO.FileExtension);
        File.WriteAllText(path, JsonSerializer.Serialize(new LumiProfile { FormatVersion = formatVersion, Settings = settings }));
        return path;
    }

    private static AppSettings WithRuntimeData(AppSettings settings)
    {
        settings.SavedPlaylistFolders = new List<SavedPlaylistFolder>
        {
            new() { DisplayName = "Music", SourcePath = @"C:\Music", Tracks = new List<string> { @"C:\Music\a.mp3" } }
        };
        settings.SavedPlaylist = new List<string> { @"C:\Music\a.mp3" };
        settings.FavoriteTracks = new List<string> { @"C:\Music\a.mp3" };
        settings.PinnedFavoriteTracks = new List<string> { @"C:\Music\a.mp3" };
        settings.LastTrackPath = @"C:\Music\a.mp3";
        settings.LastPositionSeconds = 42;
        settings.WasPlayingOnClose = true;
        settings.WasMiniPlayerOnClose = true;
        settings.ShuffleHistory = new List<string> { @"C:\Music\a.mp3" };
        settings.ShuffleHistoryIndex = 3;
        settings.ShuffleBag = new List<string> { @"C:\Music\b.mp3" };
        settings.SavedQueue = new List<string> { @"C:\Music\c.mp3" };
        settings.PlayCounts = new Dictionary<string, int> { [@"C:\Music\a.mp3"] = 5 };
        settings.TotalListenSeconds = 3600;
        settings.StatsStartedAt = "2026-01-01T00:00:00.0000000Z";
        return settings;
    }

    [Fact]
    public void Export_KeepsPreferencesAndDropsPersonalAndRuntimeData()
    {
        var live = WithRuntimeData(new AppSettings
        {
            Theme = "Light",
            Language = "en",
            AccentColorHex = "#FF8800",
            PlaybackSpeed = 1.25
        });
        string path = PathFor("profile" + LumiProfileIO.FileExtension);

        LumiProfileIO.Export(path, live);
        LumiProfile? profile = LumiProfileIO.TryReadFile(path);

        Assert.NotNull(profile);
        AppSettings exported = profile!.Settings;
        Assert.Equal("Light", exported.Theme);
        Assert.Equal("en", exported.Language);
        Assert.Equal("#FF8800", exported.AccentColorHex);
        Assert.Equal(1.25, exported.PlaybackSpeed);

        Assert.Empty(exported.SavedPlaylistFolders);
        Assert.Null(exported.SavedPlaylist);
        Assert.Empty(exported.FavoriteTracks);
        Assert.Empty(exported.PinnedFavoriteTracks);
        Assert.Null(exported.LastTrackPath);
        Assert.Equal(0, exported.LastPositionSeconds);
        Assert.False(exported.WasPlayingOnClose);
        Assert.False(exported.WasMiniPlayerOnClose);
        Assert.Empty(exported.ShuffleHistory);
        Assert.Equal(-1, exported.ShuffleHistoryIndex);
        Assert.Empty(exported.ShuffleBag);
        Assert.Empty(exported.SavedQueue);
        Assert.Empty(exported.PlayCounts);
        Assert.Equal(0, exported.TotalListenSeconds);
        Assert.Null(exported.StatsStartedAt);
    }

    [Fact]
    public void Export_DoesNotChangeLiveSettings()
    {
        var live = WithRuntimeData(new AppSettings());

        LumiProfileIO.Export(PathFor("profile" + LumiProfileIO.FileExtension), live);

        Assert.Single(live.SavedPlaylistFolders);
        Assert.Single(live.FavoriteTracks);
        Assert.Equal(@"C:\Music\a.mp3", live.LastTrackPath);
        Assert.Equal(42, live.LastPositionSeconds);
        Assert.Single(live.SavedQueue);
        Assert.Equal(5, live.PlayCounts[@"C:\Music\a.mp3"]);
        Assert.Equal(3600, live.TotalListenSeconds);
    }

    [Fact]
    public void TryReadFile_AcceptsDefaultSettingsRoundTrip()
    {
        string path = PathFor("default" + LumiProfileIO.FileExtension);

        LumiProfileIO.Export(path, new AppSettings());

        LumiProfile? profile = LumiProfileIO.TryReadFile(path);
        Assert.NotNull(profile);
        Assert.Equal(1, profile!.FormatVersion);
    }

    [Fact]
    public void TryReadFile_ReturnsNullForMissingFile()
    {
        Assert.Null(LumiProfileIO.TryReadFile(PathFor("missing" + LumiProfileIO.FileExtension)));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("null")]
    [InlineData("")]
    public void TryReadFile_ReturnsNullForUnreadableContent(string content)
    {
        string path = PathFor("broken" + LumiProfileIO.FileExtension);
        File.WriteAllText(path, content);

        Assert.Null(LumiProfileIO.TryReadFile(path));
    }

    [Fact]
    public void TryReadFile_ReturnsNullForFilesLargerThanFourMegabytes()
    {
        string path = PathFor("huge" + LumiProfileIO.FileExtension);
        File.WriteAllBytes(path, new byte[4 * 1024 * 1024 + 1]);

        Assert.Null(LumiProfileIO.TryReadFile(path));
    }

    [Fact]
    public void TryReadFile_ReturnsNullForUnsupportedFormatVersion()
    {
        Assert.Null(LumiProfileIO.TryReadFile(WriteProfile(new AppSettings(), formatVersion: 2)));
        Assert.Null(LumiProfileIO.TryReadFile(WriteProfile(new AppSettings(), formatVersion: 0)));
    }

    [Theory]
    [InlineData("language")]
    [InlineData("accent")]
    [InlineData("toast-policy")]
    [InlineData("lyrics-policy")]
    [InlineData("scale-high")]
    [InlineData("scale-low")]
    [InlineData("lyrics-font-small")]
    [InlineData("lyrics-font-large")]
    [InlineData("speed-high")]
    [InlineData("speed-low")]
    [InlineData("pitch-high")]
    [InlineData("template-long")]
    [InlineData("hotkey-long")]
    [InlineData("preset-gain")]
    [InlineData("preset-count")]
    [InlineData("menu-actions-count")]
    public void TryReadFile_RejectsUnsafeSettings(string problem)
    {
        var settings = new AppSettings();
        switch (problem)
        {
            case "language": settings.Language = new string('x', 9); break;
            case "accent": settings.AccentColorHex = new string('#', 33); break;
            case "toast-policy": settings.TrackChangeToastPolicy = "Whenever"; break;
            case "lyrics-policy": settings.LyricsSearchPolicy = "Whenever"; break;
            case "scale-high": settings.InterfaceScale = 5.0; break;
            case "scale-low": settings.InterfaceScale = 0.1; break;
            case "lyrics-font-small": settings.SyncedLyricsFontSize = 10; break;
            case "lyrics-font-large": settings.SyncedLyricsFontSize = 29; break;
            case "speed-high": settings.PlaybackSpeed = 3.0; break;
            case "speed-low": settings.PlaybackSpeed = 0.4; break;
            case "pitch-high": settings.PlaybackPitchSemitones = 13; break;
            case "template-long": settings.FileNameNormalizationTemplate = new string('t', 181); break;
            case "hotkey-long": settings.HotkeyPlayPause = new HotkeyBinding { Key = new string('k', 65) }; break;
            case "preset-gain":
                settings.EqualizerPresets = new List<EqualizerPreset> { new() { Name = "Loud", GainsDb = new[] { 101.0 } } };
                break;
            case "preset-count":
                for (int i = 0; i < 101; i++)
                    settings.EqualizerPresets.Add(new EqualizerPreset { Name = "p" + i });
                break;
            case "menu-actions-count":
                for (int i = 0; i < 21; i++)
                    settings.DisabledTrackContextMenuActions.Add("action" + i);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(problem), problem, null);
        }

        Assert.Null(LumiProfileIO.TryReadFile(WriteProfile(settings)));
    }

    [Fact]
    public void TryReadFile_AcceptsValuesExactlyOnTheLimits()
    {
        var settings = new AppSettings
        {
            Language = new string('x', 8),
            InterfaceScale = AccessibilityPreferences.MinimumInterfaceScale,
            SyncedLyricsFontSize = 11,
            PlaybackSpeed = 0.5,
            PlaybackPitchSemitones = -12,
            TrackChangeToastPolicy = "ManualOnly",
            LyricsSearchPolicy = "LocalOnly"
        };
        Assert.NotNull(LumiProfileIO.TryReadFile(WriteProfile(settings)));

        settings.InterfaceScale = AccessibilityPreferences.MaximumInterfaceScale;
        settings.SyncedLyricsFontSize = 28;
        settings.PlaybackSpeed = 2.0;
        settings.PlaybackPitchSemitones = 12;
        Assert.NotNull(LumiProfileIO.TryReadFile(WriteProfile(settings)));
    }

    [Fact]
    public void Apply_CopiesPreferencesAndKeepsLiveRuntimeData()
    {
        var live = WithRuntimeData(new AppSettings());
        var imported = new AppSettings
        {
            Theme = "Light",
            AccentColorHex = "#FF8800",
            PlaybackSpeed = 1.5,
            MiniPlayerOpacity = 0.6,
            EqualizerEnabled = true,
            EqualizerBandGainsDb = new[] { 1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0 }
        };

        LumiProfileIO.Apply(imported, live);

        Assert.Equal("Light", live.Theme);
        Assert.Equal("#FF8800", live.AccentColorHex);
        Assert.Equal(1.5, live.PlaybackSpeed);
        Assert.Equal(0.6, live.MiniPlayerOpacity);
        Assert.True(live.EqualizerEnabled);
        Assert.Equal(imported.EqualizerBandGainsDb, live.EqualizerBandGainsDb);
        Assert.NotSame(imported.EqualizerBandGainsDb, live.EqualizerBandGainsDb);

        Assert.Single(live.SavedPlaylistFolders);
        Assert.Single(live.FavoriteTracks);
        Assert.Equal(@"C:\Music\a.mp3", live.LastTrackPath);
        Assert.Equal(42, live.LastPositionSeconds);
        Assert.Single(live.SavedQueue);
        Assert.Equal(5, live.PlayCounts[@"C:\Music\a.mp3"]);
    }

    [Fact]
    public void Apply_NormalizesFileNameTemplateAndDisabledMenuActions()
    {
        var live = new AppSettings();
        var imported = new AppSettings
        {
            FileNameNormalizationTemplate = "   ",
            DisabledTrackContextMenuActions = new List<string>
            {
                "favorite", "Bogus", TrackContextMenuActions.CopyPath, TrackContextMenuActions.Favorite
            }
        };

        LumiProfileIO.Apply(imported, live);

        Assert.Equal(FileNameNormalizer.DefaultTemplate, live.FileNameNormalizationTemplate);
        Assert.Equal(new[] { TrackContextMenuActions.CopyPath, TrackContextMenuActions.Favorite }, live.DisabledTrackContextMenuActions);
    }

    [Fact]
    public void ResetToDefaults_RestoresPreferencesButKeepsUserDataAndQueueSetting()
    {
        var live = WithRuntimeData(new AppSettings
        {
            Theme = "Light",
            AccentColorHex = "#FF8800",
            PlaybackSpeed = 1.5,
            FileNameNormalizationTemplate = "{Title}{Extension}",
            SaveQueueBetweenRestarts = true
        });

        LumiProfileIO.ResetToDefaults(live);

        var defaults = new AppSettings();
        Assert.Equal(defaults.Theme, live.Theme);
        Assert.Equal(defaults.AccentColorHex, live.AccentColorHex);
        Assert.Equal(defaults.PlaybackSpeed, live.PlaybackSpeed);
        Assert.Equal(defaults.FileNameNormalizationTemplate, live.FileNameNormalizationTemplate);

        // Пользовательские данные чистит ResetAllUserData, а настройка запоминания очереди остаётся предпочтением.
        Assert.True(live.SaveQueueBetweenRestarts);
        Assert.Single(live.FavoriteTracks);
        Assert.Equal(@"C:\Music\a.mp3", live.LastTrackPath);
        Assert.Single(live.SavedQueue);
    }
}
