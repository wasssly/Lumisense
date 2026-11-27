using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using static Lumisense.BackgroundTask;

namespace Lumisense;

// Создаётся при первом показе страницы (см. SettingsWindow.EnsurePageBuilt).
public sealed partial class IslandSettingsPage : System.Windows.Controls.UserControl, ILazySettingsPage
{
    private readonly AppSettings _settings;
    private readonly ISettingsHost _owner;

    // Глушит Changed-обработчики, пока значения читаются из настроек.
    private bool _isInitializing = true;
    private bool _isSyncingIslandButtons;

    internal IslandSettingsPage(Window window, AppSettings settings, ISettingsHost owner)
    {
        _settings = settings;
        _owner = owner;

        // StaticResource разбирается до того, как элементы попадут в дерево окна, поэтому ресурсы окна подключаются заранее.
        Resources.MergedDictionaries.Add(window.Resources);
        InitializeComponent();

        UpdateInactiveNote();
        RefreshMonitorCombo();
        IslandScaleSlider.Value = _settings.IslandScalePercent;
        IslandScaleValueText.Text = $"{_settings.IslandScalePercent}%";
        IslandWidthSlider.Value = _settings.IslandWidthPercent;
        IslandWidthValueText.Text = $"{_settings.IslandWidthPercent}%";
        IslandTopGapSlider.Value = _settings.IslandTopGap;
        IslandTopGapValueText.Text = $"{_settings.IslandTopGap} px";
        IslandHideSlider.Value = _settings.IslandHideSeconds;
        IslandHideValueText.Text = $"{_settings.IslandHideSeconds} с";
        IslandCollapseSlider.Value = _settings.IslandCollapseSeconds;
        IslandCollapseValueText.Text = $"{_settings.IslandCollapseSeconds} с";
        IslandTrackToastCheckBox.IsChecked = _settings.IslandTrackToast;
        IslandAutoCollapseCheckBox.IsChecked = _settings.IslandAutoCollapse;
        IslandEqualizerCheckBox.IsChecked = _settings.IslandShowEqualizer;
        IslandCompactDeviceCheckBox.IsChecked = _settings.IslandCompactShowDevice;
        IslandAccentRimCheckBox.IsChecked = _settings.IslandAccentRim;
        IslandProgressRimCheckBox.IsChecked = _settings.IslandProgressRim;
        IslandBlurTransitionsCheckBox.IsChecked = _settings.IslandBlurTransitions;
        IslandMotionBlurCheckBox.IsChecked = _settings.IslandMotionBlur;
        IslandCoverPreviewCheckBox.IsChecked = _settings.IslandCoverPreview;
        IslandExternalVolumeCheckBox.IsChecked = _settings.IslandExternalVolume;
        IslandQuietInGamesCheckBox.IsChecked = _settings.IslandQuietInGames;
        IslandSwipeInvertedCheckBox.IsChecked = _settings.IslandSwipeInverted;
        IslandRoundCoverCheckBox.IsChecked = _settings.IslandRoundCover;
        IslandNotchHoverOpenCheckBox.IsChecked = _settings.IslandNotchHoverOpen;
        IslandAcceptDropsCheckBox.IsChecked = _settings.IslandAcceptDrops;
        SettingsWindow.SelectComboByTag(IslandLeftButtonCombo, _settings.IslandLeftButton);
        SettingsWindow.SelectComboByTag(IslandRightButtonCombo, _settings.IslandRightButton);
        SettingsWindow.SelectComboByTag(IslandEqualizerStyleCombo, _settings.IslandEqualizerStyle);
        SettingsWindow.SelectComboByTag(IslandShapeCombo, _settings.IslandShape);
        SettingsWindow.SelectComboByTag(IslandBackdropCombo, _settings.IslandBackdrop);
        SettingsWindow.SelectComboByTag(IslandSeekStyleCombo, _settings.IslandSeekStyle);
        SettingsWindow.SelectComboByTag(IslandRingSideCombo, _settings.IslandRingSide);
        SettingsWindow.SelectComboByTag(IslandMoveKeyCombo, _settings.IslandMoveKey);
        IslandLyricsShimmerCheckBox.IsChecked = _settings.IslandLyricsShimmer;
        IslandWordRollCheckBox.IsChecked = _settings.IslandWordRoll;
        IslandLyricsWordRollCheckBox.IsChecked = _settings.IslandLyricsWordRoll;
        IslandReadAlongCheckBox.IsChecked = _settings.IslandReadAlong;
        IslandHideSystemToastCheckBox.IsChecked = _settings.IslandHideSystemToast;
        foreach (var box in IslandMenuActionBoxes())
            box.IsChecked = !_settings.DisabledIslandMenuActions.Contains((string)box.Tag);

        ContextMenuOrderLink.Attach(IslandMenuActionsPanel, ContextMenuOrdering.Island, _settings, () => _isInitializing);

        // Выпадающие списки радиогрупп читают отмеченные радиокнопки, поэтому связываются после загрузки настроек.
        RadioComboLinks.Initialize(this, FindName);
        _isInitializing = false;
    }

    public void MoveContentTo(System.Windows.Controls.Panel host) => LazySettingsPage.MoveChildren(PageIsland, host);

    public FrameworkElement? FindElement(string name) => FindName(name) as FrameworkElement;

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

    private IEnumerable<System.Windows.Controls.CheckBox> IslandMenuActionBoxes() =>
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

        _settings.IslandEqualizerStyle = SettingsWindow.SelectedTag(IslandEqualizerStyleCombo, "Bars");
        _settings.IslandShape = SettingsWindow.SelectedTag(IslandShapeCombo, "Capsule");
        _settings.IslandBackdrop = SettingsWindow.SelectedTag(IslandBackdropCombo, "Glow");
        _settings.IslandSeekStyle = SettingsWindow.SelectedTag(IslandSeekStyleCombo, "Plain");
        _settings.IslandRingSide = SettingsWindow.SelectedTag(IslandRingSideCombo, "Off");
        _settings.IslandMoveKey = SettingsWindow.SelectedTag(IslandMoveKeyCombo, "Control");
        SaveIslandSettings();
    }

    private void IslandSideButtonCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isInitializing || _isSyncingIslandButtons) return;

        string left = SettingsWindow.SelectedTag(IslandLeftButtonCombo, "Favorite"), right = SettingsWindow.SelectedTag(IslandRightButtonCombo, "Lyrics");
        if (left == right)
        {
            // Выбранную функцию забирает у соседней кнопки, отдавая ей прежнюю функцию этой: кнопки меняются местами.
            _isSyncingIslandButtons = true;
            if (ReferenceEquals(sender, IslandLeftButtonCombo))
            {
                right = _settings.IslandLeftButton;
                SettingsWindow.SelectComboByTag(IslandRightButtonCombo, right);
            }
            else
            {
                left = _settings.IslandRightButton;
                SettingsWindow.SelectComboByTag(IslandLeftButtonCombo, left);
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

    internal void RefreshMonitorCombo()
    {
        bool wasInitializing = _isInitializing;
        _isInitializing = true;
        try
        {
            SettingsMonitorCombo.Fill(IslandMonitorCombo, _settings.IslandMonitor, "Основной монитор");
        }
        finally
        {
            _isInitializing = wasInitializing;
        }
    }

    internal void UpdateInactiveNote() =>
        IslandInactiveNote.Visibility = _settings.MiniPlayerStyle == "Island" ? Visibility.Collapsed : Visibility.Visible;

    private void ContextMenuOrderReset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string menu } && menu == ContextMenuOrdering.Island)
            ContextMenuOrderLink.Reset(IslandMenuActionsPanel, menu, _settings);
    }
}
