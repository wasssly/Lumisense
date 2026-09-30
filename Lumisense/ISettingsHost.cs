using System.Windows;

namespace Lumisense;

// Всё, что SettingsWindow берёт у MainWindow: окно зависит от этого интерфейса, а не от всего MainWindow.
internal interface ISettingsHost
{
    // Оформление и окно
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
    bool IsAlbumArtTransitionEnabled { get; }
    void SetAlbumArtTransitionEnabled(bool enabled);
    void ApplyAccentColor();
    void ApplyAccessibilityPreferences();
    void ApplyCoverBaseTheme();
    void ApplyTrayTheme(bool isLight);
    void ApplyMiniPlayerThemeLive();
    void ApplyWindowBackdrop(bool forceReapply = false);
    void ApplySyncedLyricsAppearance();
    void ApplyProgressBarStyle();

    // Звук и устройство вывода
    void RefreshVolumeCurve();
    void RefreshReplayGain();
    void ApplyOutputDeviceSelection();
    AudioOutputRuntimeStatus GetOutputDeviceRuntimeStatus();
    string BuildAudioDiagnosticsReport();

    // Эквалайзер
    bool IsEqualizerEnabled { get; }
    bool IsEqualizerBypass { get; }
    IReadOnlyList<EqualizerPreset> EqualizerPresets { get; }
    double GetEqualizerBandGain(int band);
    void SetEqualizerEnabled(bool enabled);
    void SetEqualizerBypass(bool bypass);
    void SetEqualizerBandGain(int band, double gainDb);
    void ResetEqualizer();
    void SaveEqualizerPreset(string name);
    void ApplyEqualizerPreset(EqualizerPreset preset);
    void DeleteEqualizerPreset(EqualizerPreset preset);
    void ExportEqualizerPreset(EqualizerPreset preset, string filePath);
    EqualizerPreset? ImportEqualizerPresetFromFile(string filePath);

    // Мини-плеер и оверлей игр
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
    void ApplyMiniPlayerArtworkProgressVisibilityLive();
    void ApplyMiniPlayerArtworkProgressThicknessLive();
    void ApplyMiniPlayerArtworkProgressColorLive();
    void ApplyMiniPlayerInfoModeLive();

    // Контекстное меню треков, очередь и плейлист
    bool IsTrackContextMenuActionDisabled(string actionId);
    void SetTrackContextMenuActionDisabled(string actionId, bool disabled);
    void ResetShuffleState();
    void SetSaveQueueBetweenRestarts(bool enabled);
    Task<FileNameNormalizer.RenameResult?> NormalizePlaylistFileNamesAsync(Window dialogOwner);

    // Интеграции
    void ApplyDiscordRichPresenceSettingsLive();
    void ReapplyHotkeys();

    // Профиль, сброс и служебные окна
    void ApplyImportedSettingsLive();
    bool TryRestoreLastSettingsReset();
    void ResetAllUserData();
    void ShowSettingsWindow(string? section = null);
    void ShowChangelogWindow();
}
