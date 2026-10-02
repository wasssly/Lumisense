using System;
using System.IO;
using System.Threading.Tasks;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

// Эти тесты работают только во временной папке: SettingsManager переключается на неё через UseStorageDirectoryForTests,
// а после теста возвращается к %AppData%. Проверяются только успешные пути, чтобы Logger ничего не писал в настоящий лог.
[Collection(StaticManagersCollection.Name)]
public sealed class SettingsManagerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumisense-settings-tests-" + Guid.NewGuid().ToString("N"));

    public SettingsManagerTests()
    {
        Directory.CreateDirectory(_directory);
        SettingsManager.UseStorageDirectoryForTests(_directory);
    }

    public void Dispose()
    {
        SettingsManager.UseStorageDirectoryForTests(null);
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* временная папка теста, остатки не критичны */ }
    }

    private string SettingsFile => Path.Combine(_directory, "settings.json");

    private static AppSettings WithUserData(string marker)
    {
        var settings = new AppSettings { AccentColorHex = "#FF8800" };
        settings.FavoriteTracks.Add(marker);
        return settings;
    }

    [Fact]
    public void Load_ReturnsDefaultsWhenThereIsNoFile()
    {
        Assert.False(SettingsManager.HasSavedSettingsFile);

        AppSettings loaded = SettingsManager.Load();

        Assert.Equal(new AppSettings().AccentColorHex, loaded.AccentColorHex);
        Assert.Empty(loaded.FavoriteTracks);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsSettings()
    {
        Assert.True(SettingsManager.Save(WithUserData("a.mp3")));

        Assert.True(SettingsManager.HasSavedSettingsFile);
        AppSettings loaded = SettingsManager.Load();
        Assert.Equal("#FF8800", loaded.AccentColorHex);
        Assert.Equal(new[] { "a.mp3" }, loaded.FavoriteTracks);
    }

    [Fact]
    public void Save_CreatesRecoveryBackupsNextToSettingsFile()
    {
        SettingsManager.Save(WithUserData("a.mp3"));

        Assert.True(File.Exists(Path.Combine(_directory, "settings.user-data-backup.json")));
        Assert.True(File.Exists(Path.Combine(_directory, "settings.playlist-backup.json")));
        Assert.Single(Directory.GetFiles(Path.Combine(_directory, "settings-backups"), "settings_*.json"));
    }

    [Fact]
    public void Save_WithoutUserDataDoesNotCreateRecoveryBackups()
    {
        SettingsManager.Save(new AppSettings());

        Assert.True(File.Exists(SettingsFile));
        Assert.False(Directory.Exists(Path.Combine(_directory, "settings-backups")));
    }

    [Fact]
    public void Save_LeavesNoTemporaryFilesBehind()
    {
        SettingsManager.Save(WithUserData("a.mp3"));
        SettingsManager.Save(WithUserData("b.mp3"));

        Assert.Empty(Directory.GetFiles(_directory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SaveAsync_WritesTheSameFileSaveWould()
    {
        await SettingsManager.SaveAsync(WithUserData("async.mp3"));

        AppSettings loaded = SettingsManager.Load();
        Assert.Equal(new[] { "async.mp3" }, loaded.FavoriteTracks);
    }

    [Fact]
    public async Task SaveIfChangedAsync_DoesNotWriteWhenNothingChangedSinceLoad()
    {
        AppSettings loaded = SettingsManager.Load();

        await SettingsManager.SaveIfChangedAsync(loaded);

        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public async Task SaveIfChangedAsync_WritesOnceAfterAChangeAndThenStaysQuiet()
    {
        AppSettings settings = SettingsManager.Load();
        settings.FavoriteTracks.Add("changed.mp3");

        await SettingsManager.SaveIfChangedAsync(settings);
        Assert.True(File.Exists(SettingsFile));
        DateTime firstWrite = File.GetLastWriteTimeUtc(SettingsFile);
        string firstContent = File.ReadAllText(SettingsFile);

        File.SetLastWriteTimeUtc(SettingsFile, firstWrite.AddMinutes(-5));
        await SettingsManager.SaveIfChangedAsync(settings);

        Assert.Equal(firstContent, File.ReadAllText(SettingsFile));
        Assert.Equal(firstWrite.AddMinutes(-5), File.GetLastWriteTimeUtc(SettingsFile));
    }

    [Fact]
    public void SaveLastObservedSnapshot_RewritesTheMostRecentlyObservedSettings()
    {
        SettingsManager.Save(WithUserData("last.mp3"));
        File.Delete(SettingsFile);

        SettingsManager.SaveLastObservedSnapshot();

        Assert.True(File.Exists(SettingsFile));
        Assert.Equal(new[] { "last.mp3" }, SettingsManager.Load().FavoriteTracks);
    }

    [Fact]
    public void SaveLastObservedSnapshot_DoesNothingBeforeAnythingWasObserved()
    {
        SettingsManager.SaveLastObservedSnapshot();

        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public void UseStorageDirectoryForTests_ResetsObservedStateBetweenTests()
    {
        SettingsManager.Save(WithUserData("first.mp3"));
        string other = Path.Combine(_directory, "other");
        Directory.CreateDirectory(other);

        SettingsManager.UseStorageDirectoryForTests(other);
        SettingsManager.SaveLastObservedSnapshot();

        Assert.False(File.Exists(Path.Combine(other, "settings.json")));
        Assert.False(SettingsManager.HasSavedSettingsFile);
    }
}
