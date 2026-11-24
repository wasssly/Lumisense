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
    private void TrackContextMenuActionCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        if (sender is not System.Windows.Controls.CheckBox { Tag: string actionId } checkBox) return;

        _owner.SetTrackContextMenuActionDisabled(actionId, checkBox.IsChecked != true);
    }

    private void FileNameNormalizationTemplateTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_isInitializing) return;

        // Пустой шаблон безопасно возвращается к дефолтному в момент запуска операции. Не
        // переписываем TextBox во время набора, чтобы не ломать редактирование пользователю.
        _settings.FileNameNormalizationTemplate = string.IsNullOrWhiteSpace(FileNameNormalizationTemplateTextBox.Text)
            ? FileNameNormalizer.DefaultTemplate
            : FileNameNormalizationTemplateTextBox.Text.Trim();
        FileNameNormalizationResultText.Visibility = Visibility.Collapsed;
    }

    private void NormalizePlaylistFileNamesButton_Click(object sender, RoutedEventArgs e)
        => BackgroundTask.FireAndForget(NormalizePlaylistFileNamesButton_ClickAsync(sender, e), nameof(NormalizePlaylistFileNamesButton_Click));

    private async Task NormalizePlaylistFileNamesButton_ClickAsync(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.FileNameNormalizationTemplate = string.IsNullOrWhiteSpace(FileNameNormalizationTemplateTextBox.Text)
            ? FileNameNormalizer.DefaultTemplate
            : FileNameNormalizationTemplateTextBox.Text.Trim();

        NormalizePlaylistFileNamesButton.IsEnabled = false;
        FileNameNormalizationResultText.Text = LocalizationService.Translate("Подготавливается предпросмотр файлов…");
        FileNameNormalizationResultText.Visibility = Visibility.Visible;
        FileNameNormalizationLoadingIndicator.IsAnimationEnabled = !AccessibilityPreferences.ShouldReduceMotion(_settings, AnimationAreas.Loading);
        FileNameNormalizationLoadingIndicator.Visibility = Visibility.Visible;

        try
        {
            FileNameNormalizer.RenameResult? result = await _owner.NormalizePlaylistFileNamesAsync(this);
            if (result is null)
            {
                FileNameNormalizationResultText.Text = LocalizationService.Translate("Нормализация отменена.");
                return;
            }

            string errors = result.Errors.Count > 0 ? $" Ошибок: {result.Errors.Count}." : string.Empty;
            FileNameNormalizationResultText.Text =
                LocalizationService.Translate($"Готово. Переименовано: {result.RenamedCount}; пропущено: {result.SkippedCount}.{errors}");
        }
        catch (Exception ex)
        {
            FileNameNormalizationResultText.Text = LocalizationService.Translate($"Не удалось нормализовать имена: {ex.Message}");
        }
        finally
        {
            FileNameNormalizationLoadingIndicator.Visibility = Visibility.Collapsed;
            NormalizePlaylistFileNamesButton.IsEnabled = true;
        }
    }

    private void LoadShuffleModeControls()
    {
        string mode = ShuffleModes.Normalize(_settings.ShuffleMode);
        ShuffleModeDeckRadio.IsChecked = mode == ShuffleModes.Deck;
        ShuffleModeRandomRadio.IsChecked = mode == ShuffleModes.Random;
        ShuffleModeFavoritesRadio.IsChecked = mode == ShuffleModes.Favorites;
        ShuffleModeRareRadio.IsChecked = mode == ShuffleModes.Rare;
        ShuffleModeFoldersRadio.IsChecked = mode == ShuffleModes.Folders;
        ShuffleModeDescriptionText.Text = LocalizationService.Translate(ShuffleModeDescription(mode));
    }

    private static string ShuffleModeDescription(string mode) => mode switch
    {
        ShuffleModes.Random => "Каждый следующий трек выбирается случайно из всего плейлиста, повторы возможны, но не два раза подряд.",
        ShuffleModes.Favorites => "Случайный выбор, при котором треки из избранного выпадают втрое чаще остальных.",
        ShuffleModes.Rare => "Случайный выбор, при котором чаще выпадают треки, которые вы слушали реже всего (по счётчику прослушиваний).",
        ShuffleModes.Folders => "Папка или альбом играет подряд в порядке плейлиста, затем случайная ещё не игравшая папка; пока не сыграют все папки, они не повторяются.",
        _ => "Весь плейлист один раз тасуется, и треки играют по порядку из этой тасовки: трек не повторится, пока не сыграют все остальные."
    };

    private void ShuffleModeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        string mode = ShuffleModeRandomRadio.IsChecked == true ? ShuffleModes.Random
            : ShuffleModeFavoritesRadio.IsChecked == true ? ShuffleModes.Favorites
            : ShuffleModeRareRadio.IsChecked == true ? ShuffleModes.Rare
            : ShuffleModeFoldersRadio.IsChecked == true ? ShuffleModes.Folders
            : ShuffleModes.Deck;
        if (mode == _settings.ShuffleMode) return;

        _settings.ShuffleMode = mode;
        ShuffleModeDescriptionText.Text = LocalizationService.Translate(ShuffleModeDescription(mode));

        // Колода/история от предыдущего режима шаффла не имеет смысла в новом —
        // начинаем с чистого листа, а не пытаемся домешать её в новую логику.
        _owner.ResetShuffleState();
    }

    private void SaveQueueBetweenRestartsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _owner.SetSaveQueueBetweenRestarts(SaveQueueBetweenRestartsCheckBox.IsChecked == true);
    }

    private void AutoRefreshPlaylistFoldersCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _owner.SetAutoRefreshPlaylistFolders(AutoRefreshPlaylistFoldersCheckBox.IsChecked == true);
    }

    // См. AppSettings.SliderStyle: переключение применяется ко всем окнам сразу через SliderAppearance.
    private void SliderStyleRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.SliderStyle = SliderStyleMaterialRadio.IsChecked == true ? "Material" : "Default";
        _owner.ApplySliderStyle();
    }

    // См. AppSettings.ReplayGainEnabled / ReplayGainReader.
    private void ReplayGainCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.ReplayGainEnabled = ReplayGainCheckBox.IsChecked == true;
        _owner.RefreshReplayGain();
    }

    private void LoudnessScanCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.LoudnessScanEnabled = LoudnessScanCheckBox.IsChecked == true;
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void LimiterCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.LimiterEnabled = LimiterCheckBox.IsChecked == true;
        _owner.ApplyLimiter();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void PlaybackSpeedChangesPitchCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.PlaybackSpeedChangesPitch = PlaybackSpeedChangesPitchCheckBox.IsChecked == true;
        _owner.RefreshPlaybackRateMode();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void ReplayGainAlbumCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.ReplayGainAlbumMode = ReplayGainAlbumCheckBox.IsChecked == true;
        _owner.RefreshReplayGain();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void ReplayGainPreampSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateReplayGainPreampText();
        if (_isInitializing) return;

        _settings.ReplayGainPreampDb = Math.Clamp(Math.Round(ReplayGainPreampSlider.Value * 2) / 2, -12, 12);
        _owner.RefreshReplayGain();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void ReplayGainPreampSlider_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ReplayGainPreampSlider.Value = 0;

    private void UpdateReplayGainPreampText()
    {
        if (ReplayGainPreampValueText is null || ReplayGainPreampSlider is null) return;

        double value = Math.Round(ReplayGainPreampSlider.Value * 2) / 2;
        ReplayGainPreampValueText.Text = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(value > 0 ? "+" : "")}{value:0.#} dB");
    }

    // См. AppSettings.PlaybackChannelMode / ChannelModeSampleProvider.
    private void ChannelModeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.PlaybackChannelMode = ChannelModeMonoRadio.IsChecked == true ? "Mono" : "Stereo";
        _owner.ApplyChannelMode();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    // См. AppSettings.DeviceConnectStyle.
    private void DeviceConnectStyleCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.DeviceConnectStyle = (DeviceConnectStyleCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "Island";
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void DeviceConnectPreviewButton_Click(object sender, RoutedEventArgs e) => _owner.PreviewDeviceConnectOverlay();

    // См. AppSettings.DeviceCardAnimation. Выбранный вариант сразу показывается в острове.
    private void DeviceCardAnimationCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.DeviceCardAnimation = (DeviceCardAnimationCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "Orbit";
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
        _owner.PreviewDeviceConnectOverlay();
    }

    // См. AppSettings.DeviceKindOverrides.
    private void RefreshDeviceKindControls()
    {
        bool wasInitializing = _isInitializing;
        _isInitializing = true;
        try
        {
            string tag = _owner.GetOutputKindOverride();
            DeviceKindCombo.SelectedItem = DeviceKindCombo.Items.Cast<System.Windows.Controls.ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.Ordinal)) ?? DeviceKindCombo.Items[0];
            DeviceKindCombo.IsEnabled = _owner.HasOutputDevice;
            DeviceKindDetectionText.Text = _owner.GetOutputKindDetectionText();
        }
        finally
        {
            _isInitializing = wasInitializing;
        }
    }

    private void DeviceKindCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        _owner.SetOutputKindOverride((DeviceKindCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "");
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
        DeviceKindDetectionText.Text = _owner.GetOutputKindDetectionText();
    }

    // См. AppSettings.PlaybackBalance.
    private void PlaybackBalanceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdatePlaybackBalanceText();
        if (_isInitializing) return;

        _settings.PlaybackBalance = (int)Math.Clamp(Math.Round(PlaybackBalanceSlider.Value), -100, 100);
        _owner.ApplyChannelMode();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    // См. AppSettings.CrossfadeSeconds / CrossfadeSkipSameAlbum; MainWindow подхватывает их на ближайшем тике прогресса.
    private void CrossfadeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateCrossfadeText();
        if (_isInitializing) return;

        _settings.CrossfadeSeconds = Math.Clamp((int)Math.Round(CrossfadeSlider.Value), 0, 12);
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void CrossfadeSlider_MouseDoubleClick(object sender, MouseButtonEventArgs e) => CrossfadeSlider.Value = 0;

    private void CrossfadeSkipSameAlbumCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.CrossfadeSkipSameAlbum = CrossfadeSkipSameAlbumCheckBox.IsChecked == true;
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void UpdateCrossfadeText()
    {
        if (CrossfadeValueText is null || CrossfadeSlider is null) return;

        int seconds = (int)Math.Round(CrossfadeSlider.Value);
        CrossfadeValueText.Text = seconds == 0 ? LocalizationService.Translate("Выкл.") : $"{seconds} {LocalizationService.Translate("с")}";
    }

    // См. AppSettings.GaplessEnabled; MainWindow снимает или готовит заготовку на ближайшем тике прогресса.
    private void GaplessCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.GaplessEnabled = GaplessCheckBox.IsChecked == true;
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    // См. AppSettings.StereoWidthPercent, CrossfeedPercent и LoudnessCompensationPercent; Tag слайдера — его имя без «Slider».
    private void StereoImageSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateStereoImageTexts();
        if (_isInitializing || sender is not System.Windows.Controls.Slider { Tag: string tag } slider) return;

        int value = (int)Math.Round(slider.Value);
        switch (tag)
        {
            case "StereoWidth":
                _settings.StereoWidthPercent = Math.Clamp(value, 0, 200);
                _owner.ApplyStereoImage();
                break;
            case "Crossfeed":
                _settings.CrossfeedPercent = Math.Clamp(value, 0, 100);
                _owner.ApplyStereoImage();
                break;
            default:
                _settings.LoudnessCompensationPercent = Math.Clamp(value, 0, 100);
                _owner.ApplyLoudnessCompensation();
                break;
        }

        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void StereoImageSlider_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.Slider { Tag: string tag } slider) slider.Value = tag == "StereoWidth" ? 100 : 0;
    }

    private void UpdateStereoImageTexts()
    {
        if (StereoWidthValueText is null || StereoWidthSlider is null || CrossfeedValueText is null || CrossfeedSlider is null
            || LoudnessCompensationValueText is null || LoudnessCompensationSlider is null) return;

        StereoWidthValueText.Text = $"{(int)Math.Round(StereoWidthSlider.Value)}%";
        CrossfeedValueText.Text = CrossfeedSlider.Value < 1 ? LocalizationService.Translate("Выкл.") : $"{(int)Math.Round(CrossfeedSlider.Value)}%";
        LoudnessCompensationValueText.Text = LoudnessCompensationSlider.Value < 1 ? LocalizationService.Translate("Выкл.") : $"{(int)Math.Round(LoudnessCompensationSlider.Value)}%";
    }

    private void PlaybackBalanceSlider_MouseDoubleClick(object sender, MouseButtonEventArgs e) => PlaybackBalanceSlider.Value = 0;

    private void UpdatePlaybackBalanceText()
    {
        if (PlaybackBalanceValueText is null || PlaybackBalanceSlider is null) return;

        int value = (int)Math.Round(PlaybackBalanceSlider.Value);
        PlaybackBalanceValueText.Text = value == 0
            ? LocalizationService.Translate("Центр")
            : $"{LocalizationService.Translate(value < 0 ? "Л" : "П")} {Math.Abs(value)}%";
    }

    // См. AppSettings.TrackStateBadgeMode.
    private void TrackStateBadgeModeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.TrackStateBadgeMode = BadgeModeExtensionRadio.IsChecked == true ? "Extension"
            : BadgeModeQualityRadio.IsChecked == true ? "Quality"
            : BadgeModeFileSizeRadio.IsChecked == true ? "FileSize"
            : BadgeModeCustomRadio.IsChecked == true ? "Custom"
            : "State";
        BadgeCustomPartsPanel.Visibility = _settings.TrackStateBadgeMode == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        UpdateBadgePreview();
        _owner.ApplyTrackStateBadgeMode();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void BadgePartCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.TrackStateBadgeShowState = BadgePartStateCheckBox.IsChecked == true;
        _settings.TrackStateBadgeShowExtension = BadgePartExtensionCheckBox.IsChecked == true;
        _settings.TrackStateBadgeShowBitrate = BadgePartBitrateCheckBox.IsChecked == true;
        _settings.TrackStateBadgeShowSampleRate = BadgePartSampleRateCheckBox.IsChecked == true;
        _settings.TrackStateBadgeShowFileSize = BadgePartFileSizeCheckBox.IsChecked == true;
        UpdateBadgePreview();
        _owner.ApplyTrackStateBadgeMode();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void AlbumArtTransitionRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _owner.SetAlbumArtTransitionEnabled(AlbumArtTransitionOffRadio.IsChecked != true);
        _settings.AlbumArtTransitionStyle = AlbumArtTransitionStyleCarouselRadio.IsChecked == true ? "Carousel" : "Slide";
        _owner.ApplyAlbumArtTransitionStyleLive();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void AlbumArtGesturesCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.AlbumArtGesturesEnabled = AlbumArtGesturesCheckBox.IsChecked == true;
    }

    // Экспорт/импорт .lumi (см. LumiProfile.cs): только настройки (тема, акцент, EQ, хоткеи), без плейлиста и избранного.
}
