using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class SettingsRecoveryBackupsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumisense-backup-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _settingsPath;
    private readonly string _userDataBackupPath;
    private readonly string _playlistBackupPath;
    private readonly string _recoveryDirectory;

    public SettingsRecoveryBackupsTests()
    {
        Directory.CreateDirectory(_directory);
        _settingsPath = Path.Combine(_directory, "settings.json");
        _userDataBackupPath = Path.Combine(_directory, "settings.user-data-backup.json");
        _playlistBackupPath = Path.Combine(_directory, "settings.playlist-backup.json");
        _recoveryDirectory = Path.Combine(_directory, "settings-backups");
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* временная папка теста, остатки не критичны */ }
    }

    private static string Json(AppSettings settings) => JsonSerializer.Serialize(settings);

    private static string JsonWithFavorite(string marker) =>
        Json(new AppSettings { FavoriteTracks = new System.Collections.Generic.List<string> { marker } });

    private void CreateBackups(string json) =>
        SettingsIntegrityService.CreateRecoveryBackups(json, _settingsPath, _userDataBackupPath, _playlistBackupPath);

    private string[] Snapshots() =>
        Directory.Exists(_recoveryDirectory) ? Directory.GetFiles(_recoveryDirectory, "settings_*.json") : Array.Empty<string>();

    [Fact]
    public void CreateRecoveryBackups_SkipsSettingsWithoutUserData()
    {
        CreateBackups(Json(new AppSettings()));

        Assert.False(File.Exists(_userDataBackupPath));
        Assert.False(File.Exists(_playlistBackupPath));
        Assert.Empty(Snapshots());
    }

    [Fact]
    public void CreateRecoveryBackups_WritesAllCopiesWhenThereIsUserData()
    {
        string json = JsonWithFavorite("a.mp3");

        CreateBackups(json);

        Assert.Equal(json, File.ReadAllText(_userDataBackupPath));
        Assert.Equal(json, File.ReadAllText(_playlistBackupPath));
        string snapshot = Assert.Single(Snapshots());
        Assert.Equal(json, File.ReadAllText(snapshot));
    }

    [Theory]
    [InlineData("favorites", true)]
    [InlineData("pinned", true)]
    [InlineData("play-counts", true)]
    [InlineData("listen-time", true)]
    [InlineData("last-track", true)]
    [InlineData("folder-with-tracks", true)]
    [InlineData("legacy-playlist", true)]
    [InlineData("folder-without-tracks", false)]
    [InlineData("blank-last-track", false)]
    [InlineData("empty-legacy-playlist", false)]
    [InlineData("zero-listen-time", false)]
    public void CreateRecoveryBackups_DetectsWhichSettingsCountAsUserData(string kind, bool expectedBackup)
    {
        var settings = new AppSettings();
        switch (kind)
        {
            case "favorites": settings.FavoriteTracks.Add("a.mp3"); break;
            case "pinned": settings.PinnedFavoriteTracks.Add("a.mp3"); break;
            case "play-counts": settings.PlayCounts["a.mp3"] = 2; break;
            case "listen-time": settings.TotalListenSeconds = 12; break;
            case "last-track": settings.LastTrackPath = "a.mp3"; break;
            case "folder-with-tracks":
                settings.SavedPlaylistFolders.Add(new SavedPlaylistFolder { DisplayName = "Music", Tracks = { "a.mp3" } });
                break;
            case "legacy-playlist": settings.SavedPlaylist = new System.Collections.Generic.List<string> { "a.mp3" }; break;
            case "folder-without-tracks":
                settings.SavedPlaylistFolders.Add(new SavedPlaylistFolder { DisplayName = "Music" });
                break;
            case "blank-last-track": settings.LastTrackPath = "   "; break;
            case "empty-legacy-playlist": settings.SavedPlaylist = new System.Collections.Generic.List<string>(); break;
            case "zero-listen-time": settings.TotalListenSeconds = 0; break;
            default: throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        CreateBackups(Json(settings));

        Assert.Equal(expectedBackup, File.Exists(_userDataBackupPath));
        Assert.Equal(expectedBackup ? 1 : 0, Snapshots().Length);
    }

    [Fact]
    public void CreateRecoveryBackups_KeepsOnlyTheFiveNewestSnapshots()
    {
        for (int i = 0; i < 8; i++)
            CreateBackups(JsonWithFavorite($"track-{i}.mp3"));

        Assert.Equal(5, Snapshots().Length);
    }

    [Fact]
    public void TryLoadLatestRecoveryBackup_ReturnsFalseWhenThereIsNoBackupFolder()
    {
        bool loaded = SettingsIntegrityService.TryLoadLatestRecoveryBackup(_settingsPath, out AppSettings? settings);

        Assert.False(loaded);
        Assert.Null(settings);
    }

    [Fact]
    public void TryLoadLatestRecoveryBackup_LoadsTheBackupMadeBySave()
    {
        CreateBackups(JsonWithFavorite("saved.mp3"));

        bool loaded = SettingsIntegrityService.TryLoadLatestRecoveryBackup(_settingsPath, out AppSettings? settings);

        Assert.True(loaded);
        Assert.Equal(new[] { "saved.mp3" }, settings!.FavoriteTracks);
    }

    private string WriteSnapshot(string name, string contents, DateTime lastWriteUtc)
    {
        Directory.CreateDirectory(_recoveryDirectory);
        string path = Path.Combine(_recoveryDirectory, name);
        File.WriteAllText(path, contents);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    [Fact]
    public void TryLoadLatestRecoveryBackup_SkipsCorruptNewestSnapshotAndUsesNewestValidOne()
    {
        var now = DateTime.UtcNow;
        WriteSnapshot("settings_old.json", JsonWithFavorite("old.mp3"), now.AddMinutes(-30));
        WriteSnapshot("settings_middle.json", JsonWithFavorite("middle.mp3"), now.AddMinutes(-20));
        WriteSnapshot("settings_newest.json", "{ this is not json", now.AddMinutes(-10));

        bool loaded = SettingsIntegrityService.TryLoadLatestRecoveryBackup(_settingsPath, out AppSettings? settings);

        Assert.True(loaded);
        Assert.Equal(new[] { "middle.mp3" }, settings!.FavoriteTracks);
    }

    [Fact]
    public void TryLoadLatestRecoveryBackup_PrefersTheNewestValidSnapshot()
    {
        var now = DateTime.UtcNow;
        WriteSnapshot("settings_a.json", JsonWithFavorite("older.mp3"), now.AddMinutes(-20));
        WriteSnapshot("settings_b.json", JsonWithFavorite("newer.mp3"), now.AddMinutes(-5));

        SettingsIntegrityService.TryLoadLatestRecoveryBackup(_settingsPath, out AppSettings? settings);

        Assert.Equal(new[] { "newer.mp3" }, settings!.FavoriteTracks);
    }

    [Fact]
    public void TryLoadLatestRecoveryBackup_ReturnsFalseWhenEverySnapshotIsCorrupt()
    {
        var now = DateTime.UtcNow;
        WriteSnapshot("settings_a.json", "garbage", now.AddMinutes(-2));
        WriteSnapshot("settings_b.json", "[1, 2, 3]", now.AddMinutes(-1));

        bool loaded = SettingsIntegrityService.TryLoadLatestRecoveryBackup(_settingsPath, out AppSettings? settings);

        Assert.False(loaded);
        Assert.Null(settings);
    }

    [Fact]
    public void TryLoadLatestRecoveryBackup_IgnoresFilesThatAreNotSettingsSnapshots()
    {
        WriteSnapshot("notes.json", JsonWithFavorite("a.mp3"), DateTime.UtcNow);

        Assert.False(SettingsIntegrityService.TryLoadLatestRecoveryBackup(_settingsPath, out _));
    }
}
