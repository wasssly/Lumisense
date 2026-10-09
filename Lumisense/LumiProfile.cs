using System.IO;
using System.Text.Json;

namespace Lumisense;

// Формат файла для переноса настроек плеера на другой компьютер (.lumi) — обычный JSON с
// другим расширением, тот же подход, что и у settings.json (см. SettingsManager.Load/Save).
public class LumiProfile
{
    public int FormatVersion { get; set; } = 1;
    public DateTime ExportedAtUtc { get; set; } = DateTime.UtcNow;

    // Только предпочтения (тема, акцент, эквалайзер, хоткеи, мини-плеер): плейлист, избранное и локальные
    // для компьютера поля (последний трек, статистика, состояние окна) не входят, см. CloneSettingsForExport.
    public AppSettings Settings { get; set; } = new();
}

public static class LumiProfileIO
{
    public const string FileExtension = ".lumi";
    public const string FileFilter = "Профиль Lumisense (*.lumi)|*.lumi";

    private const long MaxProfileBytes = 4L * 1024 * 1024;
    private const int MaxJsonDepth = 16;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        MaxDepth = MaxJsonDepth
    };

    public static void Export(string filePath, AppSettings liveSettings)
    {
        var profile = new LumiProfile { Settings = CloneSettingsForExport(liveSettings) };
        File.WriteAllText(filePath, JsonSerializer.Serialize(profile, JsonOptions));
    }

    // JSON-круг вместо ручного копирования: полный клон без риска расшарить вложенные списки с живыми настройками.
    private static AppSettings CloneSettingsForExport(AppSettings source)
    {
        var clone = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(source, JsonOptions), JsonOptions)!;

        clone.SavedPlaylistFolders = new List<SavedPlaylistFolder>();
        clone.SavedPlaylist = null;
        clone.FavoriteTracks = new List<string>();
        clone.PinnedFavoriteTracks = new List<string>();
        clone.LastTrackPath = null;
        clone.LastPositionSeconds = 0;
        clone.WasPlayingOnClose = false;
        clone.WasMiniPlayerOnClose = false;
        clone.ShuffleHistory = new List<string>();
        clone.ShuffleHistoryIndex = -1;
        clone.ShuffleBag = new List<string>();
        clone.SavedQueue = new List<string>();
        clone.PlayCounts = new Dictionary<string, int>();
        clone.TotalListenSeconds = 0;
        clone.StatsStartedAt = null;

        return clone;
    }

    // null — файл не читается (не .lumi, повреждён, не JSON и т.п.).
    public static LumiProfile? TryReadFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath) || new FileInfo(filePath).Length > MaxProfileBytes)
                return null;

            var json = File.ReadAllText(filePath);
            var profile = JsonSerializer.Deserialize<LumiProfile>(json, JsonOptions);
            return profile is not null && profile.FormatVersion == 1 && IsSafeSettings(profile.Settings)
                ? profile
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSafeSettings(AppSettings settings)
    {
        if ((settings.Language?.Length ?? 0) > 8 || (settings.AccentColorHex?.Length ?? 0) > 32 || (settings.WindowBackdropType?.Length ?? 0) > 32 ||
            (settings.ProgressBarStyle?.Length ?? 0) > 32 || (settings.SliderStyle?.Length ?? 0) > 32 || (settings.RepeatMode?.Length ?? 0) > 32 ||
            (settings.MiniPlayerSecondaryButton?.Length ?? 0) > 32 || (settings.MiniPlayerInfoMode?.Length ?? 0) > 32 ||
            (settings.MiniPlayerArtworkStyle?.Length ?? 0) > 32 ||
            (settings.MiniPlayerProgressStyle?.Length ?? 0) > 32 || (settings.MiniPlayerArtworkProgressStyle?.Length ?? 0) > 32 ||
            (settings.OutputDeviceName?.Length ?? 0) > 128 ||
            (settings.LyricsSearchPolicy?.Length ?? 0) > 32 ||
            (settings.TrackChangeToastPolicy?.Length ?? 0) > 32 ||
            (settings.MiniPlayerArtworkProgressColorMode?.Length ?? 0) > 32 ||
            (settings.MiniPlayerArtworkProgressColorHex?.Length ?? 0) > 32 ||
            (settings.FileNameNormalizationTemplate?.Length ?? 0) > 180 ||
            (settings.IconPack?.Length ?? 0) > 32 || (settings.AppIcon?.Length ?? 0) > 32 || (settings.WasapiMode?.Length ?? 0) > 32 ||
            (settings.TrackChangeToastArtSide?.Length ?? 0) > 32 || (settings.TrackChangeToastTextAlignment?.Length ?? 0) > 32)
            return false;

        if (settings.TrackChangeToastPolicy is not "EveryTrackChange" and not "PlaybackOnly" and not "ManualOnly")
            return false;
        if (settings.LyricsSearchPolicy is not "LocalOnly" and not "AutoExact" and not "ManualOnly")
            return false;
        if (!double.IsFinite(settings.InterfaceScale) || settings.InterfaceScale < AccessibilityPreferences.MinimumInterfaceScale ||
            settings.InterfaceScale > AccessibilityPreferences.MaximumInterfaceScale)
            return false;
        if (!double.IsFinite(settings.SyncedLyricsFontSize) || settings.SyncedLyricsFontSize < 11 || settings.SyncedLyricsFontSize > 28 ||
            (settings.SyncedLyricsHighlightEffect?.Length ?? 0) > 32 ||
            (settings.LyricsTextAlignment?.Length ?? 0) > 32 ||
            (settings.FavoriteHeartAnimation?.Length ?? 0) > 32 ||
            (settings.MiniPlayerSizePreset?.Length ?? 0) > 32 ||
            (settings.NowPlayingBackground?.Length ?? 0) > 32)
            return false;
        if (!double.IsFinite(settings.PlaybackSpeed) || settings.PlaybackSpeed < 0.5 || settings.PlaybackSpeed > 2.0)
            return false;
        if (!double.IsFinite(settings.PlaybackPitchSemitones) || settings.PlaybackPitchSemitones < -12.0 || settings.PlaybackPitchSemitones > 12.0)
            return false;

        if ((settings.EqualizerPresets?.Count ?? int.MaxValue) > 100 ||
            (settings.SavedPlaylistFolders?.Count ?? int.MaxValue) > 100 ||
            (settings.DisabledTrackContextMenuActions?.Count ?? int.MaxValue) > 20 ||
            (settings.DisabledMiniPlayerContextMenuActions?.Count ?? int.MaxValue) > 20 ||
            HasOverlongHotkey(settings))
            return false;

        return settings.EqualizerPresets is not null && settings.DisabledTrackContextMenuActions is not null &&
               settings.DisabledTrackContextMenuActions.All(action => (action?.Length ?? int.MaxValue) <= 64) &&
               settings.DisabledMiniPlayerContextMenuActions is not null &&
               settings.DisabledMiniPlayerContextMenuActions.All(action => (action?.Length ?? int.MaxValue) <= 64) &&
               settings.EqualizerPresets.All(p =>
                   p is not null && (p.Name?.Length ?? int.MaxValue) <= 200 && p.GainsDb is not null &&
                   p.GainsDb.Length <= 32 && p.GainsDb.All(double.IsFinite) && p.GainsDb.All(g => g >= -100 && g <= 100));
    }

    // Все горячие клавиши (HotkeyBinding) проверяются одним правилом, чтобы новая клавиша не осталась без проверки.
    private static bool HasOverlongHotkey(AppSettings settings) =>
        typeof(AppSettings).GetProperties()
            .Where(property => property.PropertyType == typeof(HotkeyBinding))
            .Any(property => ((HotkeyBinding?)property.GetValue(settings))?.Key?.Length > 64);

    // Явный allowlist намеренно не использует reflection: добавление нового свойства в
    // AppSettings не должно автоматически сделать его импортируемым или сбрасываемым.
    public static void Apply(AppSettings imported, AppSettings live)
    {
        CopyTransferableSettings(imported, live);
    }

    // Сбрасывает только настройки поведения и интерфейса, сохраняя пользовательские данные,
    // статистику, пресеты и состояние текущего сеанса.
    public static void ResetToDefaults(AppSettings live)
    {
        CopyTransferableSettings(new AppSettings(), live);
    }

    private static void CopyTransferableSettings(AppSettings source, AppSettings target)
    {
        target.Theme = source.Theme;
        target.Language = source.Language;
        target.AccentColorMode = source.AccentColorMode;
        target.AccentColorHex = source.AccentColorHex;
        target.InterfaceScale = source.InterfaceScale;
        target.ReduceMotion = source.ReduceMotion;
        target.SyncedLyricsFontSize = source.SyncedLyricsFontSize;
        target.SyncedLyricsHighlightEffect = source.SyncedLyricsHighlightEffect;
        target.CoverBaseFromCover = source.CoverBaseFromCover;
        target.WindowBackdropType = source.WindowBackdropType;
        target.ProgressBarStyle = source.ProgressBarStyle;
        target.SliderStyle = source.SliderStyle;
        target.AlwaysOnTop = source.AlwaysOnTop;
        target.RememberVolume = source.RememberVolume;
        target.SavedVolume = source.SavedVolume;
        target.UseLogarithmicVolume = source.UseLogarithmicVolume;
        target.ReplayGainEnabled = source.ReplayGainEnabled;
        target.DiscordRichPresenceEnabled = source.DiscordRichPresenceEnabled;
        target.DiscordRichPresenceShowTrackInfo = source.DiscordRichPresenceShowTrackInfo;
        target.DiscordRichPresenceShowTimeline = source.DiscordRichPresenceShowTimeline;
        target.PlaybackSpeed = source.PlaybackSpeed;
        target.PlaybackPitchSemitones = source.PlaybackPitchSemitones;
        target.MinimizeToTrayOnClose = source.MinimizeToTrayOnClose;
        target.StartHiddenInTray = source.StartHiddenInTray;
        target.NeverAutoPlayLastTrackOnStartup = source.NeverAutoPlayLastTrackOnStartup;
        target.IsPlaylistVisible = source.IsPlaylistVisible;
        target.PlayerViewMode = source.PlayerViewMode;
        target.IsShuffleEnabled = source.IsShuffleEnabled;
        target.RepeatMode = source.RepeatMode;
        target.AlbumArtTransitionEnabled = source.AlbumArtTransitionEnabled;
        target.AlbumArtGesturesEnabled = source.AlbumArtGesturesEnabled;
        target.MiniPlayerOpacity = source.MiniPlayerOpacity;
        target.MiniPlayerArtworkStyle = source.MiniPlayerArtworkStyle;
        target.MiniPlayerProgressStyle = source.MiniPlayerProgressStyle;
        target.MiniPlayerArtworkProgressStyle = source.MiniPlayerArtworkProgressStyle;
        target.MiniPlayerAlwaysOnTop = source.MiniPlayerAlwaysOnTop;
        target.MiniPlayerPinned = source.MiniPlayerPinned;
        target.MiniPlayerSnapToEdges = source.MiniPlayerSnapToEdges;
        target.MiniPlayerSecondaryButton = source.MiniPlayerSecondaryButton;
        target.MiniPlayerInfoMode = source.MiniPlayerInfoMode;
        target.MiniPlayerArtistMarquee = source.MiniPlayerArtistMarquee;
        target.OutputDeviceName = source.OutputDeviceName;
        target.LyricsSearchPolicy = source.LyricsSearchPolicy;
        target.ShowTrackChangeToast = source.ShowTrackChangeToast;
        target.TrackChangeToastPolicy = source.TrackChangeToastPolicy;
        target.TrackChangeToastPosition = source.TrackChangeToastPosition;
        target.TrackChangeToastMonitor = source.TrackChangeToastMonitor;
        target.TrackChangeToastSize = source.TrackChangeToastSize;
        target.TrackChangeToastWidth = source.TrackChangeToastWidth;
        target.TrackChangeToastOpacity = source.TrackChangeToastOpacity;
        target.TrackChangeToastArtNextToText = source.TrackChangeToastArtNextToText;
        target.MiniPlayerButtonsLayout = source.MiniPlayerButtonsLayout;
        target.MiniPlayerShowProgress = source.MiniPlayerShowProgress;
        target.MiniPlayerShowArtworkProgress = source.MiniPlayerShowArtworkProgress;
        target.MiniPlayerArtworkProgressColorMode = source.MiniPlayerArtworkProgressColorMode;
        target.MiniPlayerArtworkProgressColorHex = source.MiniPlayerArtworkProgressColorHex;
        target.MiniPlayerLeft = source.MiniPlayerLeft;
        target.MiniPlayerTop = source.MiniPlayerTop;
        target.SettingsWindowLeft = source.SettingsWindowLeft;
        target.SettingsWindowTop = source.SettingsWindowTop;
        target.HotkeyPlayPause = source.HotkeyPlayPause;
        target.HotkeyNext = source.HotkeyNext;
        target.HotkeyPrevious = source.HotkeyPrevious;
        target.HotkeyStop = source.HotkeyStop;
        target.HotkeyVolumeUp = source.HotkeyVolumeUp;
        target.HotkeyVolumeDown = source.HotkeyVolumeDown;
        target.HotkeyMute = source.HotkeyMute;
        target.HotkeyShuffle = source.HotkeyShuffle;
        target.HotkeyRepeat = source.HotkeyRepeat;
        target.HotkeySeekForward = source.HotkeySeekForward;
        target.HotkeySeekBackward = source.HotkeySeekBackward;
        target.HotkeyDeleteTrack = source.HotkeyDeleteTrack;
        target.UseImprovedShuffle = source.UseImprovedShuffle;
        target.HidePlaybackButtons = source.HidePlaybackButtons;
        target.UpdateDownloadSource = source.UpdateDownloadSource;
        target.EqualizerEnabled = source.EqualizerEnabled;
        target.EqualizerBypass = source.EqualizerBypass;
        target.EqualizerBandGainsDb = source.EqualizerBandGainsDb.ToArray();
        target.DiscordRichPresenceShowCoverArt = source.DiscordRichPresenceShowCoverArt;
        target.AutoRefreshPlaylistFolders = source.AutoRefreshPlaylistFolders;
        target.GameOverlayCompatibilityMode = source.GameOverlayCompatibilityMode;
        target.GameOverlayCompatibilityAutoDetect = source.GameOverlayCompatibilityAutoDetect;
        target.HotkeyToggleFavorite = source.HotkeyToggleFavorite;
        target.HotkeyToggleLyrics = source.HotkeyToggleLyrics;
        target.HotkeyToggleMiniPlayer = source.HotkeyToggleMiniPlayer;
        target.DisabledMiniPlayerContextMenuActions = MiniPlayerContextMenuActions.NormalizeDisabledActions(source.DisabledMiniPlayerContextMenuActions);

        // Строковые значения и число принимаем только допустимые (профиль мог прийти из другой версии), иначе остаётся текущее.
        if (IconPacks.IsKnown(source.IconPack))
            target.IconPack = source.IconPack;
        if (AppIcons.IsKnown(source.AppIcon))
            target.AppIcon = source.AppIcon;
        if (source.WasapiMode is "Shared" or "Exclusive")
            target.WasapiMode = source.WasapiMode;
        if (source.TrackChangeToastArtSide is "Left" or "Right")
            target.TrackChangeToastArtSide = source.TrackChangeToastArtSide;
        if (source.LyricsTextAlignment is "Left" or "Center" or "Right")
            target.LyricsTextAlignment = source.LyricsTextAlignment;
        if (HeartAnimation.IsKnown(source.FavoriteHeartAnimation))
            target.FavoriteHeartAnimation = source.FavoriteHeartAnimation;
        target.HideCoverInLyricsPanel = source.HideCoverInLyricsPanel;
        if (source.NowPlayingBackground is "Clouds" or "Orbs" or "Waves")
            target.NowPlayingBackground = source.NowPlayingBackground;
        if (MiniPlayerSizePreset.IsKnown(source.MiniPlayerSizePreset))
            target.MiniPlayerSizePreset = source.MiniPlayerSizePreset;
        if (source.TrackChangeToastTextAlignment is "Left" or "Center" or "Right")
            target.TrackChangeToastTextAlignment = source.TrackChangeToastTextAlignment;
        if (double.IsFinite(source.MiniPlayerArtworkProgressThickness))
            target.MiniPlayerArtworkProgressThickness = Math.Clamp(source.MiniPlayerArtworkProgressThickness, 2.0, 4.0);

        target.FileNameNormalizationTemplate = FileNameNormalizer.NormalizeTemplate(source.FileNameNormalizationTemplate);
        target.DisabledTrackContextMenuActions = TrackContextMenuActions.NormalizeDisabledActions(source.DisabledTrackContextMenuActions);
    }
}
