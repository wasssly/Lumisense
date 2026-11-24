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
    private void MiniStyleRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.MiniPlayerStyle = MiniStyleIslandRadio.IsChecked == true ? "Island" : "Classic";
        IslandInactiveNote.Visibility = MiniStyleIslandRadio.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        _owner.ApplyMiniPlayerStyleLive();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void SaveIslandSettings()
    {
        _owner.ApplyIslandSettingsLive();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void IslandScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing || IslandScaleValueText is null) return;

        _settings.IslandScalePercent = (int)Math.Round(e.NewValue);
        IslandScaleValueText.Text = $"{_settings.IslandScalePercent}%";
        SaveIslandSettings();
    }

    private void IslandWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing || IslandWidthValueText is null) return;

        _settings.IslandWidthPercent = (int)Math.Round(e.NewValue);
        IslandWidthValueText.Text = $"{_settings.IslandWidthPercent}%";
        SaveIslandSettings();
    }

    private void IslandTopGapSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing || IslandTopGapValueText is null) return;

        _settings.IslandTopGap = (int)Math.Round(e.NewValue);
        IslandTopGapValueText.Text = $"{_settings.IslandTopGap} px";
        SaveIslandSettings();
    }

    private void IslandHideSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing || IslandHideValueText is null) return;

        _settings.IslandHideSeconds = (int)Math.Round(e.NewValue);
        IslandHideValueText.Text = $"{_settings.IslandHideSeconds} с";
        SaveIslandSettings();
    }

    private void IslandCollapseSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing || IslandCollapseValueText is null) return;

        _settings.IslandCollapseSeconds = (int)Math.Round(e.NewValue);
        IslandCollapseValueText.Text = $"{_settings.IslandCollapseSeconds} с";
        SaveIslandSettings();
    }

    private void IslandOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.IslandTrackToast = IslandTrackToastCheckBox.IsChecked == true;
        _settings.IslandAutoCollapse = IslandAutoCollapseCheckBox.IsChecked == true;
        _settings.IslandShowEqualizer = IslandEqualizerCheckBox.IsChecked == true;
        _settings.IslandCompactShowDevice = IslandCompactDeviceCheckBox.IsChecked == true;
        _settings.IslandAccentRim = IslandAccentRimCheckBox.IsChecked == true;
        _settings.IslandProgressRim = IslandProgressRimCheckBox.IsChecked == true;
        _settings.IslandBlurTransitions = IslandBlurTransitionsCheckBox.IsChecked == true;
        _settings.IslandMotionBlur = IslandMotionBlurCheckBox.IsChecked == true;
        _settings.IslandCoverPreview = IslandCoverPreviewCheckBox.IsChecked == true;
        _settings.IslandExternalVolume = IslandExternalVolumeCheckBox.IsChecked == true;
        _settings.IslandQuietInGames = IslandQuietInGamesCheckBox.IsChecked == true;
        _settings.IslandSwipeInverted = IslandSwipeInvertedCheckBox.IsChecked == true;
        _settings.IslandRoundCover = IslandRoundCoverCheckBox.IsChecked == true;
        _settings.IslandNotchHoverOpen = IslandNotchHoverOpenCheckBox.IsChecked == true;
        _settings.IslandAcceptDrops = IslandAcceptDropsCheckBox.IsChecked == true;
        _settings.IslandLyricsShimmer = IslandLyricsShimmerCheckBox.IsChecked == true;
        _settings.IslandWordRoll = IslandWordRollCheckBox.IsChecked == true;
        _settings.IslandLyricsWordRoll = IslandLyricsWordRollCheckBox.IsChecked == true;
        _settings.IslandReadAlong = IslandReadAlongCheckBox.IsChecked == true;
        _settings.IslandHideSystemToast = IslandHideSystemToastCheckBox.IsChecked == true;
        SaveIslandSettings();
    }

    private System.Collections.Generic.IEnumerable<System.Windows.Controls.CheckBox> IslandMenuActionBoxes() =>
        [IslandMenuPlayPauseCheckBox, IslandMenuNextCheckBox, IslandMenuPreviousCheckBox,
         IslandMenuHideCheckBox, IslandMenuExpandCheckBox, IslandMenuNowPlayingCheckBox, IslandMenuSpeedCheckBox,
         IslandMenuPitchCheckBox, IslandMenuOverlayCheckBox, IslandMenuSettingsCheckBox];

    private void IslandMenuAction_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.DisabledIslandMenuActions = [.. IslandMenuActionBoxes().Where(box => box.IsChecked != true).Select(box => (string)box.Tag)];
        SaveIslandSettings();
    }

    private void IslandLookCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.IslandEqualizerStyle = SelectedTag(IslandEqualizerStyleCombo, "Bars");
        _settings.IslandShape = SelectedTag(IslandShapeCombo, "Capsule");
        _settings.IslandBackdrop = SelectedTag(IslandBackdropCombo, "Glow");
        _settings.IslandSeekStyle = SelectedTag(IslandSeekStyleCombo, "Plain");
        _settings.IslandRingSide = SelectedTag(IslandRingSideCombo, "Off");
        SaveIslandSettings();
    }

    private static void SelectComboByTag(System.Windows.Controls.ComboBox combo, string tag) =>
        combo.SelectedItem = combo.Items.Cast<System.Windows.Controls.ComboBoxItem>()
            .FirstOrDefault(item => item.Tag as string == tag) ?? combo.Items[0];

    private static string SelectedTag(System.Windows.Controls.ComboBox combo, string fallback) =>
        combo.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string tag } ? tag : fallback;

    private void IslandSideButtonCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isInitializing || _isSyncingIslandButtons) return;

        string left = SelectedTag(IslandLeftButtonCombo, "Favorite"), right = SelectedTag(IslandRightButtonCombo, "Lyrics");
        if (left == right)
        {
            // Выбранную функцию забирает у соседней кнопки, отдавая ей прежнюю функцию этой: кнопки меняются местами.
            _isSyncingIslandButtons = true;
            if (ReferenceEquals(sender, IslandLeftButtonCombo))
            {
                right = _settings.IslandLeftButton;
                SelectComboByTag(IslandRightButtonCombo, right);
            }
            else
            {
                left = _settings.IslandRightButton;
                SelectComboByTag(IslandLeftButtonCombo, left);
            }
            _isSyncingIslandButtons = false;
        }

        _settings.IslandLeftButton = left;
        _settings.IslandRightButton = right;
        SaveIslandSettings();
    }

    private void IslandMonitorCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.IslandMonitor =
            IslandMonitorCombo.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string tag } ? tag : "";
        _owner.ApplyIslandMonitorLive();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void MiniSizePresetRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.MiniPlayerSizePreset = MiniSizeCompactRadio.IsChecked == true ? MiniPlayerSizePreset.Compact
            : MiniSizeCurrentRadio.IsChecked == true ? MiniPlayerSizePreset.Current
            : MiniPlayerSizePreset.Classic;
        _owner.ApplyMiniPlayerSizePresetLive();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void MiniInfoModeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.MiniPlayerInfoMode = MiniInfoOnlyTitleRadio.IsChecked == true ? "TitleOnly"
            : MiniInfoRemainingRadio.IsChecked == true ? "TitleRemaining"
            : "TitleArtist";
        _owner.ApplyMiniPlayerInfoModeLive();
    }

    private void MiniArtistMarqueeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.MiniPlayerArtistMarquee = MiniArtistMarqueeCheckBox.IsChecked == true;
        _owner.ApplyMiniPlayerInfoModeLive();
    }
}
