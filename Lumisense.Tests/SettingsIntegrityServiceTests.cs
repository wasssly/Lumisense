using System;
using System.IO;
using AudioPlayer;
using Xunit;

namespace Lumisense.Tests;

// Все тесты пишут settings.json во временный файл (Path.GetTempFileName) и удаляют его в
// finally — SettingsIntegrityService.TryLoad принимает путь параметром, поэтому реальный
// %AppData%\Lumisense\settings.json пользователя/CI-раннера здесь никогда не затрагивается.
public sealed class SettingsIntegrityServiceTests : IDisposable
{
    private readonly string _tempPath = Path.GetTempFileName();

    public void Dispose()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }

    private bool TryLoad(string json, out AppSettings? settings, out string? failure)
    {
        File.WriteAllText(_tempPath, json);
        return SettingsIntegrityService.TryLoad(_tempPath, out settings, out failure);
    }

    [Fact]
    public void TryLoad_MissingFile_ReturnsFalse()
    {
        File.Delete(_tempPath);

        bool result = SettingsIntegrityService.TryLoad(_tempPath, out AppSettings? settings, out string? failure);

        Assert.False(result);
        Assert.Null(settings);
        Assert.NotNull(failure);
    }

    [Fact]
    public void TryLoad_MalformedJson_ReturnsFalse()
    {
        bool result = TryLoad("{ not valid json", out AppSettings? settings, out string? failure);

        Assert.False(result);
        Assert.Null(settings);
        Assert.NotNull(failure);
    }

    [Fact]
    public void TryLoad_JsonArrayRoot_ReturnsFalse()
    {
        bool result = TryLoad("[1, 2, 3]", out AppSettings? settings, out _);

        Assert.False(result);
        Assert.Null(settings);
    }

    [Fact]
    public void TryLoad_NewerSchemaVersionThanCurrent_ReturnsFalse()
    {
        string json = $"{{\"SettingsSchemaVersion\": {AppSettings.CurrentSettingsSchemaVersion + 1}}}";

        bool result = TryLoad(json, out AppSettings? settings, out string? failure);

        Assert.False(result);
        Assert.Null(settings);
        Assert.NotNull(failure);
    }

    [Fact]
    public void TryLoad_EmptyObject_SucceedsWithDefaults()
    {
        bool result = TryLoad("{}", out AppSettings? settings, out string? failure);

        Assert.True(result);
        Assert.NotNull(settings);
        Assert.Null(failure);
        Assert.Equal("Dark", settings!.Theme);
        Assert.Equal(AppSettings.CurrentSettingsSchemaVersion, settings.SettingsSchemaVersion);
    }

    [Fact]
    public void TryLoad_InvalidEnumLikeValue_FallsBackToDefault()
    {
        bool result = TryLoad("{\"Theme\": \"Neon\"}", out AppSettings? settings, out _);

        Assert.True(result);
        Assert.Equal("Dark", settings!.Theme);
    }

    [Fact]
    public void TryLoad_ValidEnumLikeValue_IsPreserved()
    {
        bool result = TryLoad("{\"Theme\": \"Light\"}", out AppSettings? settings, out _);

        Assert.True(result);
        Assert.Equal("Light", settings!.Theme);
    }

    [Fact]
    public void TryLoad_InvalidAccentColorHex_FallsBackToDefault()
    {
        bool result = TryLoad("{\"AccentColorHex\": \"not-a-color\"}", out AppSettings? settings, out _);

        Assert.True(result);
        Assert.Equal("#0078D4", settings!.AccentColorHex);
    }

    [Fact]
    public void TryLoad_ValidAccentColorHex_IsPreserved()
    {
        bool result = TryLoad("{\"AccentColorHex\": \"#FF00AA\"}", out AppSettings? settings, out _);

        Assert.True(result);
        Assert.Equal("#FF00AA", settings!.AccentColorHex);
    }

    [Theory]
    [InlineData(0.5, 0.85)] // ниже минимума — прижимается к 0.85
    [InlineData(2.0, 1.35)] // выше максимума — прижимается к 1.35
    [InlineData(1.1, 1.1)]  // в допустимом диапазоне — не меняется
    public void TryLoad_InterfaceScale_IsClampedToAllowedRange(double input, double expected)
    {
        bool result = TryLoad($"{{\"InterfaceScale\": {input}}}", out AppSettings? settings, out _);

        Assert.True(result);
        Assert.Equal(expected, settings!.InterfaceScale, precision: 5);
    }

    [Fact]
    public void TryLoad_VolumeAboveMax_IsClampedToOne()
    {
        bool result = TryLoad("{\"SavedVolume\": 5.5}", out AppSettings? settings, out _);

        Assert.True(result);
        Assert.Equal(1.0, settings!.SavedVolume, precision: 5); // 5.5 выше максимума 1.0 — прижимается
    }

    [Fact]
    public void TryLoad_PinnedTrackNotInFavorites_IsDropped()
    {
        string json = """
        {
            "FavoriteTracks": ["C:\\Music\\a.mp3"],
            "PinnedFavoriteTracks": ["C:\\Music\\a.mp3", "C:\\Music\\b.mp3"]
        }
        """;

        bool result = TryLoad(json, out AppSettings? settings, out _);

        Assert.True(result);
        Assert.Single(settings!.PinnedFavoriteTracks);
        Assert.Equal("C:\\Music\\a.mp3", settings.PinnedFavoriteTracks[0]);
    }

    [Fact]
    public void TryLoad_UnknownUpdateDownloadSource_FallsBackToGitHub()
    {
        bool result = TryLoad("{\"UpdateDownloadSource\": \"SomeRandomMirror\"}", out AppSettings? settings, out _);

        Assert.True(result);
        Assert.Equal("GitHub", settings!.UpdateDownloadSource);
    }
}
