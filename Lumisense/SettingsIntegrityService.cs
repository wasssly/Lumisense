using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AudioPlayer;

// Централизует загрузку старых settings.json, их нормализацию и резервные копии.
// Пользовательские данные никогда не должны пропадать из-за одной некорректной строки,
// устаревшего значения перечисления или незавершённой записи при закрытии приложения.
internal static class SettingsIntegrityService
{
    private const long MaxSettingsBytes = 8L * 1024 * 1024;
    private const int MaxRecoveryBackups = 5;
    private const int MaxCollectionItems = 10_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 16 };
    private static readonly Regex ColorRegex = new("^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$", RegexOptions.Compiled);

    public static bool TryLoad(string path, out AppSettings? settings, out string? failure)
    {
        settings = null;
        failure = null;

        try
        {
            if (!File.Exists(path))
            {
                failure = "Файл настроек не найден.";
                return false;
            }

            if (new FileInfo(path).Length > MaxSettingsBytes)
            {
                failure = "Файл настроек превышает допустимый размер.";
                return false;
            }

            string json = File.ReadAllText(path);
            return TryLoadJson(json, out settings, out failure);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            failure = ex.Message;
            return false;
        }
    }

    public static bool TryLoadLatestRecoveryBackup(string settingsPath, out AppSettings? settings)
    {
        settings = null;
        string directory = GetRecoveryDirectory(settingsPath);
        if (!Directory.Exists(directory)) return false;

        try
        {
            foreach (string path in Directory.EnumerateFiles(directory, "settings_*.json")
                         .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                if (TryLoad(path, out settings, out _))
                    return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Не удалось проверить резервные копии настроек: {ex.Message}");
        }

        settings = null;
        return false;
    }

    public static void CreateRecoveryBackups(string candidateJson, string settingsPath, string userDataBackupPath, string playlistBackupPath)
    {
        try
        {
            if (!HasUserData(candidateJson)) return;

            string settingsDirectory = Path.GetDirectoryName(settingsPath) ?? throw new InvalidOperationException("Не удалось определить папку настроек.");
            Directory.CreateDirectory(settingsDirectory);

            // Сохраняем прежние понятные пользователю имена для совместимости и ручной диагностики.
            WriteTextAtomically(userDataBackupPath, candidateJson);
            WriteTextAtomically(playlistBackupPath, candidateJson);

            string recoveryDirectory = GetRecoveryDirectory(settingsPath);
            Directory.CreateDirectory(recoveryDirectory);
            string name = $"settings_{DateTime.UtcNow:yyyyMMdd_HHmmss_fffffff}_{Guid.NewGuid():N}.json";
            WriteTextAtomically(Path.Combine(recoveryDirectory, name), candidateJson);
            PruneRecoveryBackups(recoveryDirectory);
        }
        catch (Exception ex)
        {
            // Резервная копия не должна блокировать основное сохранение, но ошибка остаётся в логе.
            Logger.Warn($"Не удалось обновить резервные копии пользовательских данных: {ex.Message}");
        }
    }

    private static bool TryLoadJson(string json, out AppSettings? settings, out string? failure)
    {
        settings = null;
        failure = null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                failure = "Корень settings.json должен быть объектом.";
                return false;
            }

            int sourceSchemaVersion = ReadSchemaVersion(document.RootElement);
            if (sourceSchemaVersion > AppSettings.CurrentSettingsSchemaVersion)
            {
                failure = "Файл настроек создан более новой версией Lumisense.";
                return false;
            }

            AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (loaded == null)
            {
                failure = "Не удалось прочитать объект настроек.";
                return false;
            }

            ApplyMigrations(loaded, sourceSchemaVersion, json);
            Normalize(loaded);
            settings = loaded;
            return true;
        }
        catch (JsonException ex)
        {
            failure = ex.Message;
            return false;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            failure = ex.Message;
            return false;
        }
    }

    private static int ReadSchemaVersion(JsonElement root)
    {
        if (!root.TryGetProperty(nameof(AppSettings.SettingsSchemaVersion), out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int schemaVersion))
            return 0;

        return Math.Max(0, schemaVersion);
    }

    private static void ApplyMigrations(AppSettings settings, int sourceSchemaVersion, string sourceJson)
    {
        if (sourceSchemaVersion < 1)
        {
            // До разделения режима Cover он одновременно окрашивал акцент и основу окна.
            if (!sourceJson.Contains("\"CoverBaseFromCover\"", StringComparison.Ordinal) &&
                string.Equals(settings.AccentColorMode, "Cover", StringComparison.OrdinalIgnoreCase))
            {
                settings.CoverBaseFromCover = true;
            }

            MigrateLegacyFlatPlaylist(settings);
            sourceSchemaVersion = 1;
        }

        if (sourceSchemaVersion < 2)
        {
            // В интерфейсе остались только None и Glow; прежние Scale/GlowScale безопасно
            // приводим к мягкому свечению, а не оставляем несуществующий эффект.
            if (!string.Equals(settings.SyncedLyricsHighlightEffect, "None", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(settings.SyncedLyricsHighlightEffect, "Glow", StringComparison.OrdinalIgnoreCase))
            {
                settings.SyncedLyricsHighlightEffect = "Glow";
            }

            sourceSchemaVersion = 2;
        }

        if (sourceSchemaVersion < 3)
        {
            // Новые поля имеют нейтральные defaults: стандартный масштаб и полный набор
            // анимаций, поэтому старые конфигурации сохраняют привычное поведение.
            settings.InterfaceScale = 1.0;
            settings.ReduceMotion = false;
            sourceSchemaVersion = 3;
        }

        if (sourceSchemaVersion < 4)
        {
            // До политики показа карточка появлялась при любой готовой смене трека.
            settings.TrackChangeToastPolicy = "EveryTrackChange";
            sourceSchemaVersion = 4;
        }

        if (sourceSchemaVersion < 5)
        {
            // Пустое имя — системный audio mapper Windows, то есть безопасное прежнее поведение.
            settings.OutputDeviceName = AudioOutputDeviceService.SystemDefaultDeviceName;
        }

        if (sourceSchemaVersion < 6)
        {
            settings.LyricsSearchPolicy = "AutoExact";
            sourceSchemaVersion = 6;
        }

        if (sourceSchemaVersion < 7)
        {
            MarkLegacyLooseFilesBucket(settings);
            sourceSchemaVersion = 7;
        }

        settings.SettingsSchemaVersion = AppSettings.CurrentSettingsSchemaVersion;
    }

    private static void Normalize(AppSettings settings)
    {
        var defaults = new AppSettings();

        settings.Theme = Allowed(settings.Theme, defaults.Theme, "Dark", "Light", "System");
        settings.Language = Allowed(settings.Language, defaults.Language, LocalizationService.Russian, LocalizationService.English);
        settings.AccentColorMode = Allowed(settings.AccentColorMode, defaults.AccentColorMode, "System", "Manual", "Cover");
        settings.AccentColorHex = ColorRegex.IsMatch(settings.AccentColorHex ?? string.Empty)
            ? settings.AccentColorHex ?? defaults.AccentColorHex
            : defaults.AccentColorHex;
        settings.WindowBackdropType = Allowed(settings.WindowBackdropType, defaults.WindowBackdropType, "Mica", "Acrylic");
        settings.ProgressBarStyle = Allowed(settings.ProgressBarStyle, defaults.ProgressBarStyle, "Slider", "Waveform");
        settings.SyncedLyricsHighlightEffect = Allowed(settings.SyncedLyricsHighlightEffect, defaults.SyncedLyricsHighlightEffect, "None", "Glow");
        settings.SyncedLyricsFontSize = ClampFinite(settings.SyncedLyricsFontSize, 11, 28, defaults.SyncedLyricsFontSize);
        settings.InterfaceScale = ClampFinite(settings.InterfaceScale, 0.85, 1.35, defaults.InterfaceScale);
        settings.SavedVolume = ClampFinite(settings.SavedVolume, 0, 1, defaults.SavedVolume);
        settings.PlaybackSpeed = ClampFinite(settings.PlaybackSpeed, 0.5, 2.0, defaults.PlaybackSpeed);
        settings.PlaybackPitchSemitones = ClampFinite(settings.PlaybackPitchSemitones, -12, 12, defaults.PlaybackPitchSemitones);
        settings.MiniPlayerOpacity = ClampFinite(settings.MiniPlayerOpacity, 0.2, 1.0, defaults.MiniPlayerOpacity);
        settings.TrackChangeToastWidth = ClampFinite(settings.TrackChangeToastWidth, 220, 560, defaults.TrackChangeToastWidth);
        settings.MiniPlayerArtworkProgressThickness = ClampFinite(
            settings.MiniPlayerArtworkProgressThickness, 1.0, 4.0, defaults.MiniPlayerArtworkProgressThickness);
        settings.TotalListenSeconds = Math.Max(0, double.IsFinite(settings.TotalListenSeconds) ? settings.TotalListenSeconds : 0);
        settings.LastPositionSeconds = Math.Max(0, double.IsFinite(settings.LastPositionSeconds) ? settings.LastPositionSeconds : 0);

        settings.PlayerViewMode = AllowedOrNull(settings.PlayerViewMode, "Square", "Rectangular", "Mini");
        settings.RepeatMode = Allowed(settings.RepeatMode, defaults.RepeatMode, "Off", "All", "One");
        settings.MiniPlayerArtworkStyle = Allowed(settings.MiniPlayerArtworkStyle, defaults.MiniPlayerArtworkStyle, "Default", "Vinyl");
        settings.MiniPlayerSecondaryButton = Allowed(settings.MiniPlayerSecondaryButton, defaults.MiniPlayerSecondaryButton, "Repeat", "Shuffle", "Favorite");
        settings.MiniPlayerInfoMode = Allowed(settings.MiniPlayerInfoMode, defaults.MiniPlayerInfoMode, "TitleArtist", "TitleOnly", "TitleRemaining");
        settings.MiniPlayerButtonsLayout = Allowed(settings.MiniPlayerButtonsLayout, defaults.MiniPlayerButtonsLayout, "Below", "Overlay");
        settings.MiniPlayerArtworkProgressColorMode = Allowed(settings.MiniPlayerArtworkProgressColorMode, defaults.MiniPlayerArtworkProgressColorMode, "Accent", "Fixed");
        settings.MiniPlayerArtworkProgressColorHex = ColorRegex.IsMatch(settings.MiniPlayerArtworkProgressColorHex ?? string.Empty)
            ? settings.MiniPlayerArtworkProgressColorHex ?? defaults.MiniPlayerArtworkProgressColorHex
            : defaults.MiniPlayerArtworkProgressColorHex;
        settings.OutputDeviceName = Trim(settings.OutputDeviceName, 128);
        settings.LyricsSearchPolicy = Allowed(settings.LyricsSearchPolicy, defaults.LyricsSearchPolicy,
            "LocalOnly", "AutoExact", "ManualOnly");
        settings.TrackChangeToastPosition = Allowed(settings.TrackChangeToastPosition, defaults.TrackChangeToastPosition,
            "BottomRight", "BottomLeft", "BottomCenter", "TopRight", "TopLeft", "TopCenter");
        settings.TrackChangeToastSize = Allowed(settings.TrackChangeToastSize, defaults.TrackChangeToastSize, "Small", "Medium", "Large");
        settings.TrackChangeToastPolicy = Allowed(settings.TrackChangeToastPolicy, defaults.TrackChangeToastPolicy,
            "EveryTrackChange", "PlaybackOnly", "ManualOnly");
        settings.UpdateDownloadSource = Allowed(settings.UpdateDownloadSource, defaults.UpdateDownloadSource,
            "GitHub", "GhProxy", "GhProxyV4", "GhProxyV6", "GhProxyCdn", "GhProxyCom", "GhFast");

        settings.SavedPlaylistFolders = NormalizeFolders(settings.SavedPlaylistFolders);
        // Legacy-поле заполняется только при чтении schema 0 и очищается сразу после переноса.
        // Не заменяем null пустым списком: это позволило бы снова записать в settings.json
        // уже неиспользуемое свойство SavedPlaylist.
        if (settings.SavedPlaylist != null)
            settings.SavedPlaylist = NormalizePaths(settings.SavedPlaylist);
        settings.FavoriteTracks = NormalizePaths(settings.FavoriteTracks);
        var favorites = new HashSet<string>(settings.FavoriteTracks, StringComparer.OrdinalIgnoreCase);
        settings.PinnedFavoriteTracks = NormalizePaths(settings.PinnedFavoriteTracks)
            .Where(favorites.Contains)
            .ToList();
        settings.ShuffleHistory = NormalizePaths(settings.ShuffleHistory);
        settings.ShuffleBag = NormalizePaths(settings.ShuffleBag);
        settings.SavedQueue = settings.SaveQueueBetweenRestarts
            ? NormalizePaths(settings.SavedQueue)
            : new List<string>();
        settings.PlayCounts ??= new Dictionary<string, int>();
        settings.PlayCounts = settings.PlayCounts
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value >= 0)
            .Take(MaxCollectionItems)
            .ToDictionary(pair => pair.Key.Trim(), pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        settings.EqualizerBandGainsDb = NormalizeGains(settings.EqualizerBandGainsDb);
        settings.EqualizerPresets = NormalizePresets(settings.EqualizerPresets);
        settings.DisabledTrackContextMenuActions = settings.DisabledTrackContextMenuActions?
            .Where(action => !string.IsNullOrWhiteSpace(action) && action.Length <= 64)
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToList() ?? new List<string>();
        settings.FileNameNormalizationTemplate = FileNameNormalizer.NormalizeTemplate(settings.FileNameNormalizationTemplate);

        NormalizeHotkeys(settings, defaults);
        settings.SettingsSchemaVersion = AppSettings.CurrentSettingsSchemaVersion;
    }

    private static void NormalizeHotkeys(AppSettings settings, AppSettings defaults)
    {
        settings.HotkeyPlayPause = NormalizeHotkey(settings.HotkeyPlayPause, defaults.HotkeyPlayPause);
        settings.HotkeyNext = NormalizeHotkey(settings.HotkeyNext, defaults.HotkeyNext);
        settings.HotkeyPrevious = NormalizeHotkey(settings.HotkeyPrevious, defaults.HotkeyPrevious);
        settings.HotkeyStop = NormalizeHotkey(settings.HotkeyStop, defaults.HotkeyStop);
        settings.HotkeyVolumeUp = NormalizeHotkey(settings.HotkeyVolumeUp, defaults.HotkeyVolumeUp);
        settings.HotkeyVolumeDown = NormalizeHotkey(settings.HotkeyVolumeDown, defaults.HotkeyVolumeDown);
        settings.HotkeyMute = NormalizeHotkey(settings.HotkeyMute, defaults.HotkeyMute);
        settings.HotkeyShuffle = NormalizeHotkey(settings.HotkeyShuffle, defaults.HotkeyShuffle);
        settings.HotkeyRepeat = NormalizeHotkey(settings.HotkeyRepeat, defaults.HotkeyRepeat);
        settings.HotkeySeekForward = NormalizeHotkey(settings.HotkeySeekForward, defaults.HotkeySeekForward);
        settings.HotkeySeekBackward = NormalizeHotkey(settings.HotkeySeekBackward, defaults.HotkeySeekBackward);
        settings.HotkeyDeleteTrack = NormalizeHotkey(settings.HotkeyDeleteTrack, defaults.HotkeyDeleteTrack);
    }

    private static HotkeyBinding NormalizeHotkey(HotkeyBinding? binding, HotkeyBinding fallback)
    {
        binding ??= fallback;
        binding.Key = binding.Key?.Trim() ?? string.Empty;
        if (binding.Key.Length > 64) binding.Key = string.Empty;
        return binding;
    }

    private static List<SavedPlaylistFolder> NormalizeFolders(IEnumerable<SavedPlaylistFolder>? folders)
    {
        if (folders == null) return new List<SavedPlaylistFolder>();

        return folders.Where(folder => folder != null).Take(512).Select(folder => new SavedPlaylistFolder
        {
            DisplayName = Trim(folder.DisplayName, 200),
            SourcePath = TrimOrNull(folder.SourcePath, 32_768),
            IsEnabled = folder.IsEnabled,
            IsExpanded = folder.IsExpanded,
            IsLooseFilesBucket = folder.IsLooseFilesBucket,
            Tracks = NormalizePaths(folder.Tracks)
        }).ToList();
    }

    private static List<string> NormalizePaths(IEnumerable<string>? paths) => paths?
        .Where(path => !string.IsNullOrWhiteSpace(path) && path.Length <= 32_768)
        .Select(path => path.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(MaxCollectionItems)
        .ToList() ?? new List<string>();

    private static double[] NormalizeGains(IEnumerable<double>? gains) => gains?
        .Select(gain => double.IsFinite(gain) ? Math.Clamp(gain, -24, 24) : 0)
        .Take(32)
        .ToArray() ?? new double[10];

    private static List<EqualizerPreset> NormalizePresets(IEnumerable<EqualizerPreset>? presets) => presets?
        .Where(preset => preset != null)
        .Take(100)
        .Select(preset => new EqualizerPreset
        {
            Name = Trim(preset.Name, 200),
            GainsDb = NormalizeGains(preset.GainsDb)
        })
        .ToList() ?? new List<EqualizerPreset>();

    private static void MigrateLegacyFlatPlaylist(AppSettings settings)
    {
        settings.SavedPlaylistFolders ??= new List<SavedPlaylistFolder>();
        if (settings.SavedPlaylistFolders.Count == 0 && settings.SavedPlaylist is { Count: > 0 })
        {
            settings.SavedPlaylistFolders.Add(new SavedPlaylistFolder
            {
                DisplayName = "Загруженные файлы",
                SourcePath = null,
                IsEnabled = true,
                IsLooseFilesBucket = true,
                Tracks = settings.SavedPlaylist.ToList()
            });
        }

        // Это свойство существовало лишь как временный источник данных миграции. Очищаем
        // его даже у пустого/дублирующего значения, чтобы следующее сохранение не оставляло
        // две независимые версии одного плейлиста в settings.json.
        settings.SavedPlaylist = null;
    }

    // Ранние версии миграции плоского плейлиста сохраняли эту группу без специального
    // флага. Исправляем только точное системное имя старого формата: вручную созданные
    // папки с произвольными именами не переименовываются и не меняют семантику.
    private static void MarkLegacyLooseFilesBucket(AppSettings settings)
    {
        foreach (SavedPlaylistFolder folder in settings.SavedPlaylistFolders ?? Enumerable.Empty<SavedPlaylistFolder>())
        {
            if (!folder.IsLooseFilesBucket && folder.SourcePath == null &&
                string.Equals(folder.DisplayName, "Загруженные файлы", StringComparison.Ordinal))
            {
                folder.IsLooseFilesBucket = true;
            }
        }
    }

    private static bool HasUserData(string candidateJson)
    {
        using var document = JsonDocument.Parse(candidateJson);
        JsonElement root = document.RootElement;
        bool hasFolderTracks = root.TryGetProperty("SavedPlaylistFolders", out var folders) &&
            folders.ValueKind == JsonValueKind.Array && folders.EnumerateArray().Any(folder =>
                folder.TryGetProperty("Tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array && tracks.GetArrayLength() > 0);
        bool hasLegacyTracks = root.TryGetProperty("SavedPlaylist", out var legacy) &&
            legacy.ValueKind == JsonValueKind.Array && legacy.GetArrayLength() > 0;
        bool hasFavorites = HasNonEmptyArray(root, "FavoriteTracks") || HasNonEmptyArray(root, "PinnedFavoriteTracks");
        bool hasPlayCounts = root.TryGetProperty("PlayCounts", out var playCounts) &&
            playCounts.ValueKind == JsonValueKind.Object && playCounts.EnumerateObject().Any();
        bool hasListenTime = root.TryGetProperty("TotalListenSeconds", out var listenSeconds) &&
            listenSeconds.ValueKind == JsonValueKind.Number && listenSeconds.GetDouble() > 0;
        bool hasResumeState = root.TryGetProperty("LastTrackPath", out var lastTrack) &&
            lastTrack.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(lastTrack.GetString());
        return hasFolderTracks || hasLegacyTracks || hasFavorites || hasPlayCounts || hasListenTime || hasResumeState;
    }

    private static void WriteTextAtomically(string destination, string contents)
    {
        string directory = Path.GetDirectoryName(destination) ?? throw new InvalidOperationException("Не удалось определить папку резервной копии.");
        Directory.CreateDirectory(directory);
        string temporaryPath = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, contents);
            File.Move(temporaryPath, destination, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch { /* best-effort cleanup */ }
        }
    }

    private static void PruneRecoveryBackups(string directory)
    {
        foreach (string path in Directory.EnumerateFiles(directory, "settings_*.json")
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Skip(MaxRecoveryBackups))
        {
            try { File.Delete(path); }
            catch { /* retry next save */ }
        }
    }

    private static string GetRecoveryDirectory(string settingsPath) => Path.Combine(
        Path.GetDirectoryName(settingsPath) ?? throw new InvalidOperationException("Не удалось определить папку настроек."),
        "settings-backups");

    private static bool HasNonEmptyArray(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0;

    private static double ClampFinite(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private static string Allowed(string? value, string fallback, params string[] allowed) =>
        value != null && allowed.Contains(value, StringComparer.OrdinalIgnoreCase) ? allowed.First(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase)) : fallback;

    private static string? AllowedOrNull(string? value, params string[] allowed) =>
        value != null && allowed.Contains(value, StringComparer.OrdinalIgnoreCase) ? allowed.First(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase)) : null;

    private static string Trim(string? value, int maxLength) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim()[..Math.Min(value.Trim().Length, maxLength)];

    private static string? TrimOrNull(string? value, int maxLength) => string.IsNullOrWhiteSpace(value) ? null : Trim(value, maxLength);
}
