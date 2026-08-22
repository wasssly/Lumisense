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
    public const string UpdateVelopackFirstRunTitle = "update.velopack.firstRunTitle";
    public const string UpdateVelopackFirstRunMessage = "update.velopack.firstRunMessage";
    public const string UpdateLegacyCleanupTitle = "update.legacyCleanup.title";
    public const string UpdateLegacyCleanupMessage = "update.legacyCleanup.message";
    public const string UpdateLegacyCleanupStartingTitle = "update.legacyCleanup.startingTitle";
    public const string UpdateLegacyCleanupStartingMessage = "update.legacyCleanup.startingMessage";
    public const string UpdateLegacyCleanupFailed = "update.legacyCleanup.failed";
    public const string UpdateVelopackDownload = "update.velopack.download";
    public const string UpdateVelopackPreparing = "update.velopack.preparing";
    public const string UpdateVelopackApplying = "update.velopack.applying";
    public const string UpdateVelopackUnavailable = "update.velopack.unavailable";
    public const string UpdateMsiMigrationHint = "update.msiMigration.hint";
    public const string UpdateMsiMigrationButton = "update.msiMigration.button";
    public const string UpdateMsiMigrationConfirmTitle = "update.msiMigration.confirmTitle";
    public const string UpdateMsiMigrationConfirmMessage = "update.msiMigration.confirmMessage";
    public const string UpdateMsiMigrationLaunching = "update.msiMigration.launching";
    public const string UpdateMsiMigrationUnavailable = "update.msiMigration.unavailable";
    public const string UpdateMsiMigrationDownloadFailed = "update.msiMigration.downloadFailed";
    public const string UpdateDownloadCancel = "update.download.cancel";
    public const string UpdateDownloadCancelling = "update.download.cancelling";
    public const string UpdateDownloadPause = "update.download.pause";
    public const string UpdateDownloadResume = "update.download.resume";
    public const string UpdateDownloadPaused = "update.download.paused";
    public const string UpdateDownloadChangeSource = "update.download.changeSource";
    public const string UpdateDownloadSourceChanging = "update.download.sourceChanging";
    public const string UpdateDownloadSourceChangeConfirmTitle = "update.download.sourceChangeConfirmTitle";
    public const string UpdateDownloadSourceChangeConfirmMessage = "update.download.sourceChangeConfirmMessage";
    public const string UpdateVelopackDownloadPaused = "update.velopack.downloadPaused";
    public const string UpdateSourceProbeTitle = "update.sourceProbe.title";
    public const string UpdateSourceProbeSubtitle = "update.sourceProbe.subtitle";
    public const string UpdateSourceProbeRunning = "update.sourceProbe.running";
    public const string UpdateSourceProbeAvailable = "update.sourceProbe.available";
    public const string UpdateSourceProbeUnavailable = "update.sourceProbe.unavailable";
    public const string UpdateSourceProbeRecommended = "update.sourceProbe.recommended";
    public const string UpdateSourceProbeNoWorking = "update.sourceProbe.noWorking";
    public const string UpdateSourceProbeFailed = "update.sourceProbe.failed";
    public const string UpdateSourceProbeGood = "update.sourceProbe.good";
    public const string UpdateSourceProbeSlow = "update.sourceProbe.slow";
    public const string UpdateSourceProbeRow = "update.sourceProbe.row";
    public const string UpdateMsiMigrationAvailableTitle = "update.msiMigration.availableTitle";
    public const string UpdateMsiMigrationCurrentVersion = "update.msiMigration.currentVersion";
    public const string UpdateMsiMigrationManualSubtitle = "update.msiMigration.manualSubtitle";
    public const string UpdateMsiMigrationClose = "update.msiMigration.close";

    public const string ChangelogOpenReleaseOnGitHub = "changelog.openReleaseOnGitHub";
    public const string ChangelogNoDescription = "changelog.noDescription";
    public const string ChangelogChanges = "changelog.changes";

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
        [LocalizationKey.UpdateVelopackFirstRunTitle] = "Компактные обновления включены",
        [LocalizationKey.UpdateVelopackFirstRunMessage] = "Эта копия Lumisense использует новый механизм обновлений: последующие версии могут загружаться как небольшие delta-пакеты. Ваши настройки, плейлист, избранное и статистика хранятся отдельно от программы.",
        [LocalizationKey.UpdateLegacyCleanupTitle] = "Удалить старую EXE-копию?",
        [LocalizationKey.UpdateLegacyCleanupMessage] = "MSI-версия Lumisense успешно запущена. На компьютере также найдена старая EXE-установка Lumisense.\n\nУдалить старую EXE-копию сейчас?\n\nОткроется стандартный мастер удаления Windows с запросом прав администратора. Когда он спросит, удалить ли настройки и пользовательские данные Lumisense, выберите «Нет»: так сохранятся ваши настройки, плейлист, избранное и статистика.\n\nВы сможете удалить старую копию позже через «Установленные приложения» Windows.",
        [LocalizationKey.UpdateLegacyCleanupStartingTitle] = "Удаление старой EXE-копии",
        [LocalizationKey.UpdateLegacyCleanupStartingMessage] = "Откроется стандартный мастер удаления старой EXE-копии Lumisense. В его вопросе об удалении настроек и пользовательских данных выберите «Нет», чтобы сохранить данные для MSI-версии.",
        [LocalizationKey.UpdateLegacyCleanupFailed] = "Не удалось открыть мастер удаления старой EXE-копии. Старая программа не удалена; при необходимости удалите её позже через «Установленные приложения» Windows.",
        [LocalizationKey.UpdateVelopackDownload] = "Скачивание компактного обновления…",
        [LocalizationKey.UpdateVelopackPreparing] = "Проверка и подготовка обновления…",
        [LocalizationKey.UpdateVelopackApplying] = "Перезапуск для установки обновления…",
        [LocalizationKey.UpdateVelopackUnavailable] = "Компактное обновление недоступно для этой установки. Используйте полный установщик из GitHub Release.",
        [LocalizationKey.UpdateMsiMigrationHint] = "Доступен переход на MSI-версию. После него будущие обновления будут скачиваться компактными пакетами.",
        [LocalizationKey.UpdateMsiMigrationButton] = "Перейти на компактные обновления (MSI)",
        [LocalizationKey.UpdateMsiMigrationConfirmTitle] = "Перейти на MSI-версию?",
        [LocalizationKey.UpdateMsiMigrationConfirmMessage] = "Будет скачан проверенный MSI-установщик и запущена стандартная установка Windows. Система может запросить права администратора.\n\nСтарая EXE-копия Lumisense не будет удалена автоматически: сначала убедитесь, что новая копия запускается и работает корректно. Настройки, плейлист, избранное и статистика сохраняются. Продолжить?",
        [LocalizationKey.UpdateMsiMigrationLaunching] = "Запуск MSI-установщика…",
        [LocalizationKey.UpdateMsiMigrationUnavailable] = "В этом релизе нет проверенного MSI-пакета. Используйте обычный EXE-установщик или откройте релиз на GitHub.",
        [LocalizationKey.UpdateMsiMigrationDownloadFailed] = "Не удалось полностью скачать MSI из-за временного сетевого сбоя. Проверьте подключение и повторите попытку.",
        [LocalizationKey.UpdateDownloadCancel] = "Отменить загрузку",
        [LocalizationKey.UpdateDownloadCancelling] = "Отмена загрузки…",
        [LocalizationKey.UpdateDownloadPause] = "Приостановить",
        [LocalizationKey.UpdateDownloadResume] = "Продолжить",
        [LocalizationKey.UpdateDownloadPaused] = "Загрузка приостановлена.",
        [LocalizationKey.UpdateDownloadChangeSource] = "Источник: {0} — изменить",
        [LocalizationKey.UpdateDownloadSourceChanging] = "Источник изменён на {0}. Перезапускаем загрузку с начала…",
        [LocalizationKey.UpdateDownloadSourceChangeConfirmTitle] = "Сменить источник загрузки?",
        [LocalizationKey.UpdateDownloadSourceChangeConfirmMessage] = "Переключить загрузку с «{0}» на «{1}»?\n\nТекущая загрузка будет отменена, неполный файл удалён, а скачивание начнётся заново с нового источника. Уже загруженная часть не сохраняется.",
        [LocalizationKey.UpdateVelopackDownloadPaused] = "Компактное обновление остановлено. При продолжении Velopack повторно проверит и докачает доступные пакеты.",
        [LocalizationKey.UpdateSourceProbeTitle] = "Проверить зеркала",
        [LocalizationKey.UpdateSourceProbeSubtitle] = "Одновременно запросить у каждого источника только небольшой фрагмент release-файла (до 256 KiB)",
        [LocalizationKey.UpdateSourceProbeRunning] = "Проверяем источники обновлений…",
        [LocalizationKey.UpdateSourceProbeAvailable] = "{0} — {1} мс · {2}/с",
        [LocalizationKey.UpdateSourceProbeUnavailable] = "{0} — недоступен ({1})",
        [LocalizationKey.UpdateSourceProbeRecommended] = "Рекомендуемый источник для этой сети: {0}",
        [LocalizationKey.UpdateSourceProbeNoWorking] = "Ни один источник не ответил на тестовый запрос. Проверьте подключение или попробуйте позже.",
        [LocalizationKey.UpdateSourceProbeFailed] = "тест не пройден",
        [LocalizationKey.UpdateSourceProbeGood] = "доступен",
        [LocalizationKey.UpdateSourceProbeSlow] = "медленный",
        [LocalizationKey.UpdateSourceProbeRow] = "{0} · {1} мс · {2}/с",
        [LocalizationKey.UpdateMsiMigrationAvailableTitle] = "Доступен переход на MSI",
        [LocalizationKey.UpdateMsiMigrationCurrentVersion] = "Текущая EXE-версия: {0}",
        [LocalizationKey.UpdateMsiMigrationManualSubtitle] = "Доступен добровольный переход на MSI с компактными обновлениями",
        [LocalizationKey.UpdateMsiMigrationClose] = "Закрыть",

        [LocalizationKey.ChangelogOpenReleaseOnGitHub] = "Открыть релиз на GitHub",
        [LocalizationKey.ChangelogNoDescription] = "Нет описания",
        [LocalizationKey.ChangelogChanges + ".one"] = "{0} изменение",
        [LocalizationKey.ChangelogChanges + ".few"] = "{0} изменения",
        [LocalizationKey.ChangelogChanges + ".many"] = "{0} изменений",
        [LocalizationKey.ChangelogChanges + ".other"] = "{0} изменений",

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
        [LocalizationKey.UpdateVelopackFirstRunTitle] = "Compact updates are enabled",
        [LocalizationKey.UpdateVelopackFirstRunMessage] = "This copy of Lumisense uses the new update system: future versions may download as small delta packages. Your settings, playlist, favorites, and statistics are stored separately from the program.",
        [LocalizationKey.UpdateLegacyCleanupTitle] = "Remove the old EXE copy?",
        [LocalizationKey.UpdateLegacyCleanupMessage] = "The MSI version of Lumisense started successfully. An older EXE installation of Lumisense was also found on this computer.\n\nRemove the old EXE copy now?\n\nThe standard Windows uninstall wizard will open and request administrator permission. When it asks whether to remove Lumisense settings and user data, choose “No” to keep your settings, playlist, favorites, and statistics.\n\nYou can remove the old copy later from Windows Installed apps.",
        [LocalizationKey.UpdateLegacyCleanupStartingTitle] = "Removing the old EXE copy",
        [LocalizationKey.UpdateLegacyCleanupStartingMessage] = "The standard uninstall wizard for the old EXE copy of Lumisense will open. In its question about settings and user data, choose “No” to keep them for the MSI version.",
        [LocalizationKey.UpdateLegacyCleanupFailed] = "The uninstall wizard for the old EXE copy could not be opened. The old program was not removed; if needed, remove it later from Windows Installed apps.",
        [LocalizationKey.UpdateVelopackDownload] = "Downloading compact update…",
        [LocalizationKey.UpdateVelopackPreparing] = "Verifying and preparing update…",
        [LocalizationKey.UpdateVelopackApplying] = "Restarting to install update…",
        [LocalizationKey.UpdateVelopackUnavailable] = "A compact update is not available for this installation. Use the full installer from the GitHub Release.",
        [LocalizationKey.UpdateMsiMigrationHint] = "An MSI version is available. After moving to it, future updates will download as compact packages.",
        [LocalizationKey.UpdateMsiMigrationButton] = "Switch to compact updates (MSI)",
        [LocalizationKey.UpdateMsiMigrationConfirmTitle] = "Switch to the MSI version?",
        [LocalizationKey.UpdateMsiMigrationConfirmMessage] = "A verified MSI installer will be downloaded and Windows setup will start. Windows may ask for administrator permission.\n\nThe legacy EXE copy of Lumisense will not be removed automatically: first make sure the new copy starts and works correctly. Your settings, playlist, favorites, and statistics are preserved. Continue?",
        [LocalizationKey.UpdateMsiMigrationLaunching] = "Launching the MSI installer…",
        [LocalizationKey.UpdateMsiMigrationUnavailable] = "This release has no verified MSI package. Use the standard EXE installer or open the release on GitHub.",
        [LocalizationKey.UpdateMsiMigrationDownloadFailed] = "The MSI could not be downloaded completely because of a temporary network error. Check your connection and try again.",
        [LocalizationKey.UpdateDownloadCancel] = "Cancel download",
        [LocalizationKey.UpdateDownloadCancelling] = "Cancelling download…",
        [LocalizationKey.UpdateDownloadPause] = "Pause",
        [LocalizationKey.UpdateDownloadResume] = "Resume",
        [LocalizationKey.UpdateDownloadPaused] = "Download is paused.",
        [LocalizationKey.UpdateDownloadChangeSource] = "Source: {0} — change",
        [LocalizationKey.UpdateDownloadSourceChanging] = "Source changed to {0}. Restarting the download from the beginning…",
        [LocalizationKey.UpdateDownloadSourceChangeConfirmTitle] = "Change download source?",
        [LocalizationKey.UpdateDownloadSourceChangeConfirmMessage] = "Switch the download from “{0}” to “{1}”?\n\nThe current download will be cancelled, the incomplete file will be deleted, and the download will restart from the beginning using the new source. The already downloaded part will not be kept.",
        [LocalizationKey.UpdateVelopackDownloadPaused] = "The compact update is stopped. When continued, Velopack will verify and download the available packages again.",
        [LocalizationKey.UpdateSourceProbeTitle] = "Test mirrors",
        [LocalizationKey.UpdateSourceProbeSubtitle] = "Query each source concurrently for only a small release-file fragment (up to 256 KiB)",
        [LocalizationKey.UpdateSourceProbeRunning] = "Testing update sources…",
        [LocalizationKey.UpdateSourceProbeAvailable] = "{0} — {1} ms · {2}/s",
        [LocalizationKey.UpdateSourceProbeUnavailable] = "{0} — unavailable ({1})",
        [LocalizationKey.UpdateSourceProbeRecommended] = "Recommended source for this network: {0}",
        [LocalizationKey.UpdateSourceProbeNoWorking] = "No source answered the test request. Check your connection or try again later.",
        [LocalizationKey.UpdateSourceProbeFailed] = "test failed",
        [LocalizationKey.UpdateSourceProbeGood] = "available",
        [LocalizationKey.UpdateSourceProbeSlow] = "slow",
        [LocalizationKey.UpdateSourceProbeRow] = "{0} · {1} ms · {2}/s",
        [LocalizationKey.UpdateMsiMigrationAvailableTitle] = "MSI migration is available",
        [LocalizationKey.UpdateMsiMigrationCurrentVersion] = "Current EXE version: {0}",
        [LocalizationKey.UpdateMsiMigrationManualSubtitle] = "A voluntary move to MSI with compact updates is available",
        [LocalizationKey.UpdateMsiMigrationClose] = "Close",

        [LocalizationKey.ChangelogOpenReleaseOnGitHub] = "Open release on GitHub",
        [LocalizationKey.ChangelogNoDescription] = "No description",
        [LocalizationKey.ChangelogChanges + ".one"] = "{0} change",
        [LocalizationKey.ChangelogChanges + ".other"] = "{0} changes",

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
