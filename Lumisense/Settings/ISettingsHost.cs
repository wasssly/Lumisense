using System.Windows;
using System.Windows.Media;

namespace Lumisense;

// Всё, что SettingsWindow берёт у MainWindow, по темам. ISettingsHost объединяет их: окно настроек зависит от него,
// а не от всего MainWindow.
internal interface ISettingsHost : ISettingsAppearanceHost, ISettingsAudioHost, ISettingsEqualizerHost,
    ISettingsMiniPlayerHost, ISettingsPlaylistHost, ISettingsIntegrationsHost, ISettingsProfileHost,
    ISettingsDiagnosticsHost
{
}

// Встроенная диагностика производительности (окно DiagnosticsWindow) берёт у главного окна состояние плеера и переключение режимов.
internal interface ISettingsDiagnosticsHost
{
    IDiagnosticsHost GetDiagnosticsHost();
}

// Окно, тема и оформление: то, что страницы настроек применяют к MainWindow и мини-плееру на лету.
internal interface ISettingsAppearanceHost
{
    bool Topmost { get; set; }
    double Left { get; }
    double Top { get; }
    double Width { get; }
    double Height { get; }
    double ActualWidth { get; }
    double ActualHeight { get; }
    bool IsMiniMode { get; }
    string CurrentViewModeName { get; }
    void SetPlayerViewModeByName(string modeName);
    void ApplyMainWindowLayoutLive();
    bool IsAlbumArtTransitionEnabled { get; }
    void SetAlbumArtTransitionEnabled(bool enabled);
    void ApplyAccentColor();
    void ApplyAccessibilityPreferences();
    void ApplyCoverBaseTheme();
    void ApplyMiniPlayerThemeLive();
    void ApplyWindowBackdrop(bool forceReapply = false);
    void ApplySyncedLyricsAppearance();
    void ApplyProgressBarStyle();
    void ApplySliderStyle();
    void ApplyTrackStateBadgeMode();
    void RefreshLyricsDisplayMode();
    void ApplyProgressAppearance();
    float[]? GetCurrentWaveformPeaks();
    Color GetProgressBarColor(ProgressSurface surface);
    void PreviewTrackChangeToast();
    void PickToastPosition(Action<ToastPositionPickResult?> completed);
}

// Звук и устройство вывода.
internal interface ISettingsAudioHost
{
    void RefreshVolumeCurve();
    void RefreshReplayGain();
    void RefreshPlaybackRateMode();
    void ApplyLimiter();
    bool IsDeviceSoundProfilesEnabled { get; }
    void SetDeviceSoundProfilesEnabled(bool enabled);
    void ApplyChannelMode();
    void ApplyStereoImage();
    void ApplyLoudnessCompensation();
    void PreviewDeviceConnectOverlay();
    bool HasOutputDevice { get; }
    string GetOutputKindOverride();
    void SetOutputKindOverride(string tag);
    string GetOutputKindDetectionText();
    void ApplyOutputDeviceSelection();
    void ApplyPlaybackProcessPriority();
    AudioOutputRuntimeStatus GetOutputDeviceRuntimeStatus();
    string BuildAudioDiagnosticsReport();
}

// Эквалайзер и его пресеты.
internal interface ISettingsEqualizerHost
{
    bool IsEqualizerEnabled { get; }
    bool IsEqualizerAutoHeadroom { get; }
    IReadOnlyList<EqualizerPreset> EqualizerPresets { get; }
    double GetEqualizerBandGain(int band);
    void SetEqualizerEnabled(bool enabled);
    void SetEqualizerAutoHeadroom(bool enabled);
    void SetEqualizerBandGain(int band, double gainDb);
    void ResetEqualizer();
    void SaveEqualizerPreset(string name);
    void ApplyEqualizerPreset(EqualizerPreset preset);
    void DeleteEqualizerPreset(EqualizerPreset preset);
    void ExportEqualizerPreset(EqualizerPreset preset, string filePath);
    EqualizerPreset? ImportEqualizerPresetFromFile(string filePath);
}

// Настройки мини-плеера и совместимость с оверлеями игр.
internal interface ISettingsMiniPlayerHost
{
    bool EffectiveGameOverlayCompatibilityEnabled { get; }
    bool IsMiniPlayerContextMenuActionDisabled(string actionId);
    void SetMiniPlayerContextMenuActionDisabled(string actionId, bool disabled);
    void SetMiniPlayerPinned(bool pinned);
    void SetMiniPlayerSecondaryButtonMode(string? mode);
    void ApplyMiniPlayerOpacityLive(double opacity);
    void ApplyMiniPlayerTopmostLive(bool topmost);
    void ApplyMiniPlayerOverlayCompatibilityLive(bool enabled);
    void ApplyGameOverlayAutoDetectSettingLive();
    void ApplyMiniPlayerButtonsLayoutLive();
    void ApplyMiniPlayerArtworkStyleLive();
    void ApplyMiniPlayerProgressBarVisibilityLive();
    void ApplyMiniPlayerSizePresetLive();
    void ApplyMiniPlayerStyleLive();
    void ApplyIslandMonitorLive();
    void ApplyIslandSettingsLive();
    void ApplyAlbumArtTransitionStyleLive();
    void ApplyNowPlayingBackgroundLive();
    void ApplyMiniPlayerArtworkProgressVisibilityLive();
    void ApplyMiniPlayerArtworkProgressThicknessLive();
    void ApplyMiniPlayerArtworkProgressColorLive();
    void ApplyMiniPlayerInfoModeLive();
}

// Меню треков, очередь и плейлист.
internal interface ISettingsPlaylistHost
{
    bool IsTrackContextMenuActionDisabled(string actionId);
    void SetTrackContextMenuActionDisabled(string actionId, bool disabled);
    void ResetShuffleState();
    void SetSaveQueueBetweenRestarts(bool enabled);
    void SetAutoRefreshPlaylistFolders(bool enabled);
    Task<FileNameNormalizer.RenameResult?> NormalizePlaylistFileNamesAsync(Window dialogOwner);
}

// Интеграции (Discord, горячие клавиши).
internal interface ISettingsIntegrationsHost
{
    void ApplyDiscordRichPresenceSettingsLive();
    // Возвращает комбинации, которые не удалось зарегистрировать (заняты другим приложением).
    IReadOnlyList<HotkeyBinding> ReapplyHotkeys();
}

// Профиль: импорт, сброс данных, служебные окна.
internal interface ISettingsProfileHost
{
    void ApplyImportedSettingsLive();
    bool TryRestoreLastSettingsReset();
    void ResetAllUserData();
    void ShowSettingsWindow(string? section = null);
    void ShowChangelogWindow();
}
