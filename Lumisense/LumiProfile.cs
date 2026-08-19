using System.IO;
using System.Text.Json;

namespace AudioPlayer;

// Формат файла для переноса настроек плеера на другой компьютер (.lumi) — обычный JSON с
// другим расширением, тот же подход, что и у settings.json (см. SettingsManager.Load/Save).
public class LumiProfile
{
    public int FormatVersion { get; set; } = 1;
    public DateTime ExportedAtUtc { get; set; } = DateTime.UtcNow;

    // Только "предпочтения" — тема, акцент, эквалайзер, хоткеи, мини-плеер и т.п. Плейлист и
    // избранное сюда намеренно не входят (перенос профиля ограничен только настройками),
    // сессионные/локальные для конкретного компьютера поля (последний трек и позиция,
    // статистика прослушиваний, состояние окна на момент закрытия) — тоже не входят, см.
    // LumiProfileIO.CloneSettingsForExport.
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

    // JSON-рандтрип, а не ручное копирование полей — простой и надёжный способ полного клона
    // без риска случайно расшарить те же самые вложенные списки/объекты с живими настройками
    // приложения.
    private static AppSettings CloneSettingsForExport(AppSettings source)
    {
        var clone = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(source, JsonOptions), JsonOptions)!;

        clone.SavedPlaylistFolders = new List<SavedPlaylistFolder>();
        clone.SavedPlaylist = null;
        clone.FavoriteTracks = new List<string>();
        clone.LastTrackPath = null;
        clone.LastPositionSeconds = 0;
        clone.WasPlayingOnClose = false;
        clone.WasMiniPlayerOnClose = false;
        clone.PlayCounts = new Dictionary<string, int>();

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
        if ((settings.AccentColorHex?.Length ?? 0) > 32 || (settings.WindowBackdropType?.Length ?? 0) > 32 ||
            (settings.ProgressBarStyle?.Length ?? 0) > 32 || (settings.RepeatMode?.Length ?? 0) > 32 ||
            (settings.MiniPlayerSecondaryButton?.Length ?? 0) > 32 || (settings.MiniPlayerInfoMode?.Length ?? 0) > 32 ||
            (settings.MiniPlayerArtworkProgressColorMode?.Length ?? 0) > 32 ||
            (settings.MiniPlayerArtworkProgressColorHex?.Length ?? 0) > 32 ||
            (settings.FileNameNormalizationTemplate?.Length ?? 0) > 180)
            return false;

        if (!double.IsFinite(settings.PlaybackSpeed) || settings.PlaybackSpeed < 0.5 || settings.PlaybackSpeed > 2.0)
            return false;
        if (!double.IsFinite(settings.PlaybackPitchSemitones) || settings.PlaybackPitchSemitones < -12.0 || settings.PlaybackPitchSemitones > 12.0)
            return false;

        if ((settings.EqualizerPresets?.Count ?? int.MaxValue) > 100 ||
            (settings.SavedPlaylistFolders?.Count ?? int.MaxValue) > 100 ||
            (settings.DisabledTrackContextMenuActions?.Count ?? int.MaxValue) > 20 ||
            (settings.HotkeyPlayPause?.Key?.Length ?? 0) > 64 || (settings.HotkeyNext?.Key?.Length ?? 0) > 64 ||
            (settings.HotkeyPrevious?.Key?.Length ?? 0) > 64 || (settings.HotkeyStop?.Key?.Length ?? 0) > 64)
            return false;

        return settings.EqualizerPresets is not null && settings.DisabledTrackContextMenuActions is not null &&
               settings.DisabledTrackContextMenuActions.All(action => (action?.Length ?? int.MaxValue) <= 64) &&
               settings.EqualizerPresets.All(p =>
                   p is not null && (p.Name?.Length ?? int.MaxValue) <= 200 && p.GainsDb is not null &&
                   p.GainsDb.Length <= 32 && p.GainsDb.All(double.IsFinite) && p.GainsDb.All(g => g >= -100 && g <= 100));
    }

    // Явный allowlist намеренно не использует reflection: добавление нового свойства в
    // AppSettings не должно автоматически сделать его импортируемым или сбрасываемым.
    public static void Apply(AppSettings imported, AppSettings live)
    {
        CopyTransferableSettings(imported, live, preserveRuntimeData: true);
    }

    // Сбрасывает только настройки поведения и интерфейса, сохраняя пользовательские данные,
    // статистику, пресеты и состояние текущего сеанса.
    public static void ResetToDefaults(AppSettings live)
    {
        CopyTransferableSettings(new AppSettings(), live, preserveRuntimeData: true);
    }

    private static void CopyTransferableSettings(AppSettings source, AppSettings target, bool preserveRuntimeData)
    {
        target.Theme = source.Theme;
        target.AccentColorMode = source.AccentColorMode;
        target.AccentColorHex = source.AccentColorHex;
        target.CoverBaseFromCover = source.CoverBaseFromCover;
        target.WindowBackdropType = source.WindowBackdropType;
        target.ProgressBarStyle = source.ProgressBarStyle;
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
        target.MiniPlayerOpacity = source.MiniPlayerOpacity;
        target.MiniPlayerAlwaysOnTop = source.MiniPlayerAlwaysOnTop;
        target.MiniPlayerPinned = source.MiniPlayerPinned;
        target.MiniPlayerSnapToEdges = source.MiniPlayerSnapToEdges;
        target.MiniPlayerSecondaryButton = source.MiniPlayerSecondaryButton;
        target.MiniPlayerInfoMode = source.MiniPlayerInfoMode;
        target.ShowTrackChangeToast = source.ShowTrackChangeToast;
        target.TrackChangeToastPosition = source.TrackChangeToastPosition;
        target.TrackChangeToastMonitor = source.TrackChangeToastMonitor;
        target.TrackChangeToastSize = source.TrackChangeToastSize;
        target.TrackChangeToastWidth = source.TrackChangeToastWidth;
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
        target.FileNameNormalizationTemplate = FileNameNormalizer.NormalizeTemplate(source.FileNameNormalizationTemplate);
        target.DisabledTrackContextMenuActions = TrackContextMenuActions.NormalizeDisabledActions(source.DisabledTrackContextMenuActions);

        if (!preserveRuntimeData)
        {
            target.WasMiniPlayerOnClose = source.WasMiniPlayerOnClose;
            target.SkippedUpdateVersion = source.SkippedUpdateVersion;
        }
    }
}
