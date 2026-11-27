using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using static Lumisense.BackgroundTask;

namespace Lumisense;

public partial class SettingsWindow
{
    private void RememberVolumeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.RememberVolume = RememberVolumeCheckBox.IsChecked == true;
    }

    private void LogarithmicVolumeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.UseLogarithmicVolume = LogarithmicVolumeCheckBox.IsChecked == true;
        _owner.RefreshVolumeCurve();
    }

    private void NeverAutoPlayLastTrackOnStartupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.NeverAutoPlayLastTrackOnStartup = NeverAutoPlayLastTrackOnStartupCheckBox.IsChecked == true;
    }

    private void TrackLoadTraceCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.TrackLoadTraceEnabled = TrackLoadTraceCheckBox.IsChecked == true;
        AudioDiagnosticsCopyStatusText.Text = _settings.TrackLoadTraceEnabled
            ? LocalizationService.Translate("Trace подготовки трека включён")
            : LocalizationService.Translate("Trace подготовки трека выключен");
    }

    private void ClearArtworkCacheButton_Click(object sender, RoutedEventArgs e)
        => BackgroundTask.FireAndForget(ClearArtworkCacheButton_ClickAsync(sender, e), nameof(ClearArtworkCacheButton_Click));

    private async Task ClearArtworkCacheButton_ClickAsync(object sender, RoutedEventArgs e)
    {
        var confirmation = LocalizedMessageBox.Show(this,
            "Удалить локально сохранённые интернет-обложки?\n\nПри следующем поиске нужные изображения будут скачаны заново.",
            "Очистить кэш обложек?", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (confirmation != System.Windows.MessageBoxResult.Yes) return;

        ClearArtworkCacheButton.IsEnabled = false;
        ArtworkCacheClearResultText.Visibility = Visibility.Collapsed;
        try
        {
            var result = await System.Threading.Tasks.Task.Run(CoverArtSearchWindow.ClearArtworkCache);
            ArtworkCacheClearResultText.Text = result.DeletedFiles == 0
                ? "Кэш обложек уже пуст."
                : $"Удалено файлов: {result.DeletedFiles}; освобождено: {FormatArtworkCacheSize(result.FreedBytes)}."
                  + (result.FailedFiles > 0 ? $" Не удалось удалить файлов: {result.FailedFiles}." : string.Empty);
            ArtworkCacheClearResultText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ArtworkCacheClearResultText.Text = LocalizationService.Translate($"Не удалось очистить кэш: {ex.Message}");
            ArtworkCacheClearResultText.Visibility = Visibility.Visible;
        }
        finally
        {
            ClearArtworkCacheButton.IsEnabled = true;
        }
    }

    private static string FormatArtworkCacheSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} Б";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} КБ";
        return $"{bytes / (1024.0 * 1024.0):0.#} МБ";
    }

    // Подсчёт обходит все файлы кэша и заодно чистит устаревшие: делаем в фоне, чтобы не задерживать открытие окна.
    private int _lyricsCacheInfoVersion;

    private void RefreshLyricsCacheInfo() =>
        FireAndForget(RefreshLyricsCacheInfoAsync(++_lyricsCacheInfoVersion), "RefreshLyricsCacheInfoAsync");

    private async Task RefreshLyricsCacheInfoAsync(int version)
    {
        LyricsCacheInfo info = await Task.Run(LyricsService.GetPastedLyricsCacheInfo);
        if (_isWindowClosed || version != _lyricsCacheInfoVersion) return;

        LyricsCacheInfoText.Text = LocalizationService.FormatKey(LocalizationKey.ProfileLyricsCacheInfo,
            info.EntryCount, FormatLyricsCacheSize(info.TotalBytes));
        ClearLyricsCacheButton.IsEnabled = !info.IsEmpty;
    }

    private static string FormatLyricsCacheSize(long bytes)
    {
        string bytesUnit = LocalizationService.IsEnglish ? "B" : "Б";
        string kilobytesUnit = LocalizationService.IsEnglish ? "KB" : "КБ";
        string megabytesUnit = LocalizationService.IsEnglish ? "MB" : "МБ";
        if (bytes < 1024) return $"{bytes} {bytesUnit}";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} {kilobytesUnit}";
        return $"{bytes / (1024.0 * 1024.0):0.#} {megabytesUnit}";
    }

    private void ClearLyricsCacheButton_Click(object sender, RoutedEventArgs e)
    {
        if (LyricsService.GetPastedLyricsCacheInfo().IsEmpty)
        {
            RefreshLyricsCacheInfo();
            return;
        }

        var confirm = LocalizedMessageBox.Show(this,
            LocalizationService.Get(LocalizationKey.ProfileLyricsCacheClearConfirm),
            LocalizationService.Get(LocalizationKey.ProfileLyricsCacheClearTitle), System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        if (!LyricsService.ClearPastedLyricsCache())
        {
            LocalizedMessageBox.Show(this, LocalizationService.Get(LocalizationKey.ProfileLyricsCacheClearFailed),
                LocalizationService.Get(LocalizationKey.ProfileLyricsCacheClearErrorTitle), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }

        RefreshLyricsCacheInfo();
    }

    private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
    {
        // Показанные результаты поиска хранят уже выведенные подписи: пересобираем их на новом языке.
        if (SettingsSearchBox.Text.Trim().Length > 0) RefreshSearchResults();

        if (_lazyPages.TryGetValue("About", out ILazySettingsPage? aboutPage)) ((AboutSettingsPage)aboutPage).RefreshAppVersionText();
        InitializeOutputDeviceCombo();
        if (_lazyPages.TryGetValue("Notifications", out ILazySettingsPage? toastPage)) ((NotificationsSettingsPage)toastPage).RefreshMonitorCombo();
        InitializeIslandMonitorCombo();
        if (_lazyPages.TryGetValue("Integrations", out ILazySettingsPage? discordPage)) ((IntegrationsSettingsPage)discordPage).UpdateDiscordRichPresenceConnectionStatus();

        if (_lazyPages.TryGetValue("Equalizer", out ILazySettingsPage? eqPage)) ((EqualizerSettingsPage)eqPage).RefreshBandTexts();

        if (_lazyPages.TryGetValue("Updates", out ILazySettingsPage? updatesPage)) ((UpdatesSettingsPage)updatesPage).RefreshLocalization();

        RefreshLyricsCacheInfo();
        RefreshMiniArtworkProgressThicknessPresentation();
    }
}
