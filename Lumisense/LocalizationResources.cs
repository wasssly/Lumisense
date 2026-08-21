using System;
using System.Collections.Generic;

namespace AudioPlayer;

// Стабильные ключи для новых и динамических строк. В отличие от Translate("русская фраза"),
// ключ не меняется при редактуре текста и поэтому не разрывает связь между RU/EN ресурсами.
// Полная миграция выполняется постепенно: старый словарь остаётся совместимым с существующим XAML.
public static class LocalizationKey
{
    public const string ProfileLyricsCacheInfo = "profile.lyricsCache.info";
    public const string ProfileLyricsCacheClearConfirm = "profile.lyricsCache.clearConfirm";
    public const string ProfileLyricsCacheClearTitle = "profile.lyricsCache.clearTitle";
    public const string ProfileLyricsCacheClearFailed = "profile.lyricsCache.clearFailed";
    public const string ProfileLyricsCacheClearErrorTitle = "profile.lyricsCache.clearErrorTitle";

    public const string ProfileResetSnapshotCreateFailedSettings = "profile.reset.snapshotCreateFailed.settings";
    public const string ProfileResetSnapshotCreateFailedFull = "profile.reset.snapshotCreateFailed.full";
    public const string ProfileResetCancelledTitle = "profile.reset.cancelledTitle";
    public const string ProfileResetFullConfirm = "profile.reset.fullConfirm";
    public const string ProfileRestoreConfirm = "profile.restore.confirm";
    public const string ProfileRestoreConfirmTitle = "profile.restore.confirmTitle";
    public const string ProfileRestoreUnavailable = "profile.restore.unavailable";
    public const string ProfileRestoreUnavailableTitle = "profile.restore.unavailableTitle";
    public const string ProfileRestoreCompleted = "profile.restore.completed";
    public const string ProfileRestoreCompletedTitle = "profile.restore.completedTitle";

    public const string StatisticsListens = "statistics.listens";

    public const string PlaylistLooseFiles = "playlist.looseFiles";
    public const string ApplicationVersion = "application.version";
    public const string UpdateImportant = "update.important";
    public const string UpdateFailureGeneric = "update.failure.generic";
    public const string UpdateFailureHttpStatus = "update.failure.httpStatus";
    public const string UpdateFailureInvalidResponse = "update.failure.invalidResponse";
    public const string UpdateFailureMissingInstallerChecksum = "update.failure.missingInstallerChecksum";
    public const string UpdateFailureNetwork = "update.failure.network";
    public const string UpdateFailureLoadVersionList = "update.failure.loadVersionList";

    public const string TrackStateNoTrack = "track.state.noTrack";
    public const string TrackStateNoTrackHint = "track.state.noTrackHint";
    public const string TrackStateLoading = "track.state.loading";
    public const string TrackStateLoadingHint = "track.state.loadingHint";
    public const string TrackStatePlaying = "track.state.playing";
    public const string TrackStatePlayingHint = "track.state.playingHint";
    public const string TrackStatePaused = "track.state.paused";
    public const string TrackStatePausedHint = "track.state.pausedHint";
    public const string TrackStateStopped = "track.state.stopped";
    public const string TrackStateStoppedHint = "track.state.stoppedHint";
    public const string TrackStateError = "track.state.error";
    public const string TrackStateErrorHint = "track.state.errorHint";
}

internal static class LocalizationResources
{
    private static readonly IReadOnlyDictionary<string, string> Russian = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [LocalizationKey.ProfileLyricsCacheInfo] = "Записей: {0} · {1}",
        [LocalizationKey.ProfileLyricsCacheClearConfirm] = "Удалить локально сохранённые вставленные тексты песен? Файлы .lrc/.txt рядом с музыкой и тексты в тегах не будут затронуты.",
        [LocalizationKey.ProfileLyricsCacheClearTitle] = "Очистить кэш текстов",
        [LocalizationKey.ProfileLyricsCacheClearFailed] = "Не удалось полностью очистить кэш текстов. Подробности сохранены в журнале.",
        [LocalizationKey.ProfileLyricsCacheClearErrorTitle] = "Ошибка очистки",

        [LocalizationKey.ProfileResetSnapshotCreateFailedSettings] = "Не удалось создать локальную точку восстановления. Сброс отменён, чтобы не потерять текущие настройки.",
        [LocalizationKey.ProfileResetSnapshotCreateFailedFull] = "Не удалось создать локальную точку восстановления. Полный сброс отменён, чтобы не потерять ваши данные.",
        [LocalizationKey.ProfileResetCancelledTitle] = "Сброс отменён",
        [LocalizationKey.ProfileResetFullConfirm] = "Перед очисткой будет создана локальная точка восстановления. Выполнить полный сброс сейчас?",
        [LocalizationKey.ProfileRestoreConfirm] = "Вернуть настройки, плейлист, избранное, статистику и пресеты из последней локальной точки, созданной перед сбросом? Текущие изменения будут заменены этим снимком.",
        [LocalizationKey.ProfileRestoreConfirmTitle] = "Вернуть состояние?",
        [LocalizationKey.ProfileRestoreUnavailable] = "Не удалось найти корректную точку восстановления. Она могла быть удалена или повреждена.",
        [LocalizationKey.ProfileRestoreUnavailableTitle] = "Восстановление недоступно",
        [LocalizationKey.ProfileRestoreCompleted] = "Состояние до сброса восстановлено. Для полного применения некоторых параметров перезапустите Lumisense.",
        [LocalizationKey.ProfileRestoreCompletedTitle] = "Восстановление завершено",

        [LocalizationKey.StatisticsListens + ".one"] = "{0} прослушивание",
        [LocalizationKey.StatisticsListens + ".few"] = "{0} прослушивания",
        [LocalizationKey.StatisticsListens + ".many"] = "{0} прослушиваний",
        [LocalizationKey.StatisticsListens + ".other"] = "{0} прослушиваний",

        [LocalizationKey.PlaylistLooseFiles] = "Отдельные файлы",
        [LocalizationKey.ApplicationVersion] = "Версия {0}",
        [LocalizationKey.UpdateImportant] = "Важно:",
        [LocalizationKey.UpdateFailureGeneric] = "Не удалось проверить обновления.",
        [LocalizationKey.UpdateFailureHttpStatus] = "GitHub вернул код {0}.",
        [LocalizationKey.UpdateFailureInvalidResponse] = "GitHub вернул неожиданный ответ.",
        [LocalizationKey.UpdateFailureMissingInstallerChecksum] = "В GitHub Release отсутствует контрольная сумма SHA-256 для установщика.",
        [LocalizationKey.UpdateFailureNetwork] = "Не удалось подключиться к GitHub. Проверьте подключение к интернету и повторите попытку.",
        [LocalizationKey.UpdateFailureLoadVersionList] = "Не удалось загрузить список версий: {0}",

        [LocalizationKey.TrackStateNoTrack] = "Трек не выбран",
        [LocalizationKey.TrackStateNoTrackHint] = "Выберите трек в плейлисте или нажмите воспроизведение.",
        [LocalizationKey.TrackStateLoading] = "Загрузка трека",
        [LocalizationKey.TrackStateLoadingHint] = "Открываем файл и подготавливаем воспроизведение.",
        [LocalizationKey.TrackStatePlaying] = "Воспроизводится",
        [LocalizationKey.TrackStatePlayingHint] = "Трек сейчас воспроизводится.",
        [LocalizationKey.TrackStatePaused] = "На паузе",
        [LocalizationKey.TrackStatePausedHint] = "Трек загружен и готов к продолжению.",
        [LocalizationKey.TrackStateStopped] = "Остановлено",
        [LocalizationKey.TrackStateStoppedHint] = "Воспроизведение остановлено. Нажмите кнопку воспроизведения, чтобы начать снова.",
        [LocalizationKey.TrackStateError] = "Не удалось открыть трек",
        [LocalizationKey.TrackStateErrorHint] = "Проверьте, доступен ли файл, и повторите попытку."
    };

    private static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [LocalizationKey.ProfileLyricsCacheInfo] = "Entries: {0} · {1}",
        [LocalizationKey.ProfileLyricsCacheClearConfirm] = "Delete locally stored pasted lyrics? .lrc/.txt files next to your music and lyrics in tags will not be affected.",
        [LocalizationKey.ProfileLyricsCacheClearTitle] = "Clear lyrics cache",
        [LocalizationKey.ProfileLyricsCacheClearFailed] = "The lyrics cache could not be fully cleared. Details were saved to the log.",
        [LocalizationKey.ProfileLyricsCacheClearErrorTitle] = "Clear error",

        [LocalizationKey.ProfileResetSnapshotCreateFailedSettings] = "A local recovery point could not be created. The reset was cancelled to avoid losing your current settings.",
        [LocalizationKey.ProfileResetSnapshotCreateFailedFull] = "A local recovery point could not be created. The full reset was cancelled to avoid losing your data.",
        [LocalizationKey.ProfileResetCancelledTitle] = "Reset cancelled",
        [LocalizationKey.ProfileResetFullConfirm] = "A local recovery point will be created before data is cleared. Perform the full reset now?",
        [LocalizationKey.ProfileRestoreConfirm] = "Restore settings, playlist, favorites, statistics, and presets from the latest local recovery point created before a reset? Your current changes will be replaced by this snapshot.",
        [LocalizationKey.ProfileRestoreConfirmTitle] = "Restore state?",
        [LocalizationKey.ProfileRestoreUnavailable] = "No valid recovery point was found. It may have been deleted or corrupted.",
        [LocalizationKey.ProfileRestoreUnavailableTitle] = "Restore unavailable",
        [LocalizationKey.ProfileRestoreCompleted] = "The state before the reset has been restored. Restart Lumisense to fully apply some settings.",
        [LocalizationKey.ProfileRestoreCompletedTitle] = "Restore complete",

        [LocalizationKey.StatisticsListens + ".one"] = "{0} listen",
        [LocalizationKey.StatisticsListens + ".other"] = "{0} listens",

        [LocalizationKey.PlaylistLooseFiles] = "Loose files",
        [LocalizationKey.ApplicationVersion] = "Version {0}",
        [LocalizationKey.UpdateImportant] = "Important:",
        [LocalizationKey.UpdateFailureGeneric] = "Could not check for updates.",
        [LocalizationKey.UpdateFailureHttpStatus] = "GitHub returned status {0}.",
        [LocalizationKey.UpdateFailureInvalidResponse] = "GitHub returned an unexpected response.",
        [LocalizationKey.UpdateFailureMissingInstallerChecksum] = "The GitHub Release does not contain an installer SHA-256 checksum.",
        [LocalizationKey.UpdateFailureNetwork] = "Could not connect to GitHub. Check your internet connection and try again.",
        [LocalizationKey.UpdateFailureLoadVersionList] = "Could not load the version list: {0}",

        [LocalizationKey.TrackStateNoTrack] = "No track selected",
        [LocalizationKey.TrackStateNoTrackHint] = "Select a track in the playlist or press Play.",
        [LocalizationKey.TrackStateLoading] = "Loading track",
        [LocalizationKey.TrackStateLoadingHint] = "Opening the file and preparing playback.",
        [LocalizationKey.TrackStatePlaying] = "Playing",
        [LocalizationKey.TrackStatePlayingHint] = "The track is currently playing.",
        [LocalizationKey.TrackStatePaused] = "Paused",
        [LocalizationKey.TrackStatePausedHint] = "The track is loaded and ready to continue.",
        [LocalizationKey.TrackStateStopped] = "Stopped",
        [LocalizationKey.TrackStateStoppedHint] = "Playback is stopped. Press Play to start again.",
        [LocalizationKey.TrackStateError] = "Could not open track",
        [LocalizationKey.TrackStateErrorHint] = "Check that the file is available and try again."
    };

    public static bool TryGet(string key, string language, out string value)
    {
        IReadOnlyDictionary<string, string> resources = string.Equals(language, LocalizationService.English,
            StringComparison.OrdinalIgnoreCase) ? English : Russian;
        if (resources.TryGetValue(key, out string? localized))
        {
            value = localized;
            return true;
        }

        value = string.Empty;
        return false;
    }

    public static IReadOnlyDictionary<string, string> GetForLanguage(string language) =>
        string.Equals(language, LocalizationService.English, StringComparison.OrdinalIgnoreCase) ? English : Russian;
}
