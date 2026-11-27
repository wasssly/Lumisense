using System;
using System.Windows;
using Wpf.Ui.Controls;
using static Lumisense.BackgroundTask;

namespace Lumisense;

// Создаётся при первом показе страницы (см. SettingsWindow.EnsurePageBuilt).
public sealed partial class NotificationsSettingsPage : System.Windows.Controls.UserControl, ILazySettingsPage
{
    private readonly Window _window;
    private readonly AppSettings _settings;
    private readonly ISettingsHost _owner;

    // Глушит Changed-обработчики, пока значения читаются из настроек.
    private bool _isInitializing = true;

    internal NotificationsSettingsPage(Window window, AppSettings settings, ISettingsHost owner)
    {
        _window = window;
        _settings = settings;
        _owner = owner;

        // StaticResource разбирается до того, как элементы попадут в дерево окна, поэтому ресурсы окна подключаются заранее.
        Resources.MergedDictionaries.Add(window.Resources);
        InitializeComponent();

        TrackChangeToastCheckBox.IsChecked = _settings.ShowTrackChangeToast;
        InitializeToastPolicy();
        InitializeToastPositionAndSize();
        RefreshMonitorCombo();
        _isInitializing = false;
    }

    public void MoveContentTo(System.Windows.Controls.Panel host) => LazySettingsPage.MoveChildren(PageNotifications, host);

    public FrameworkElement? FindElement(string name) => FindName(name) as FrameworkElement;

    private void TrackChangeToastCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.ShowTrackChangeToast = TrackChangeToastCheckBox.IsChecked == true;
    }

    private void InitializeToastPolicy()
    {
        ToastPolicyPlaybackOnlyRadio.IsChecked = _settings.TrackChangeToastPolicy == "PlaybackOnly";
        ToastPolicyManualOnlyRadio.IsChecked = _settings.TrackChangeToastPolicy == "ManualOnly";
        ToastPolicyEveryTrackChangeRadio.IsChecked = !ToastPolicyPlaybackOnlyRadio.IsChecked.GetValueOrDefault()
            && !ToastPolicyManualOnlyRadio.IsChecked.GetValueOrDefault();
    }

    private void ToastPolicyRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.TrackChangeToastPolicy = ToastPolicyPlaybackOnlyRadio.IsChecked == true ? "PlaybackOnly"
            : ToastPolicyManualOnlyRadio.IsChecked == true ? "ManualOnly"
            : "EveryTrackChange";
    }

    private void InitializeToastPositionAndSize()
    {
        ToastPosTopLeftRadio.IsChecked = _settings.TrackChangeToastPosition == "TopLeft";
        ToastPosTopCenterRadio.IsChecked = _settings.TrackChangeToastPosition == "TopCenter";
        ToastPosTopRightRadio.IsChecked = _settings.TrackChangeToastPosition == "TopRight";
        ToastPosBottomLeftRadio.IsChecked = _settings.TrackChangeToastPosition == "BottomLeft";
        ToastPosBottomCenterRadio.IsChecked = _settings.TrackChangeToastPosition == "BottomCenter";
        ToastCustomPositionText.Visibility = _settings.TrackChangeToastPosition == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        ToastPosBottomRightRadio.IsChecked = _settings.TrackChangeToastPosition != "Custom"
                                              && !ToastPosTopLeftRadio.IsChecked.GetValueOrDefault()
                                              && !ToastPosTopCenterRadio.IsChecked.GetValueOrDefault()
                                              && !ToastPosTopRightRadio.IsChecked.GetValueOrDefault()
                                              && !ToastPosBottomLeftRadio.IsChecked.GetValueOrDefault()
                                              && !ToastPosBottomCenterRadio.IsChecked.GetValueOrDefault();

        ToastSizeSmallRadio.IsChecked = _settings.TrackChangeToastSize == "Small";
        ToastSizeLargeRadio.IsChecked = _settings.TrackChangeToastSize == "Large";
        ToastSizeMediumRadio.IsChecked = !ToastSizeSmallRadio.IsChecked.GetValueOrDefault()
                                          && !ToastSizeLargeRadio.IsChecked.GetValueOrDefault();

        ToastArtRightRadio.IsChecked = _settings.TrackChangeToastArtSide == "Right";
        ToastArtLeftRadio.IsChecked = !ToastArtRightRadio.IsChecked.GetValueOrDefault();

        ToastTextCenterRadio.IsChecked = _settings.TrackChangeToastTextAlignment == "Center";
        ToastTextRightRadio.IsChecked = _settings.TrackChangeToastTextAlignment == "Right";
        ToastTextLeftRadio.IsChecked = !ToastTextCenterRadio.IsChecked.GetValueOrDefault()
                                        && !ToastTextRightRadio.IsChecked.GetValueOrDefault();

        ToastArtNextToTextCheckBox.IsChecked = _settings.TrackChangeToastArtNextToText;
        ToastArtNextToTextPanel.Visibility = ToastTextCenterRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        ToastWidthSlider.Value = Math.Clamp(_settings.TrackChangeToastWidth, ToastWidthSlider.Minimum, ToastWidthSlider.Maximum);
        ToastOpacitySlider.Value = Math.Clamp(Math.Round(_settings.TrackChangeToastOpacity * 100), ToastOpacitySlider.Minimum, ToastOpacitySlider.Maximum);
        UpdateToastOpacityValueText();
        ToastColorCoverRadio.IsChecked = _settings.TrackChangeToastColorMode == "Cover";
        ToastColorAccentRadio.IsChecked = _settings.TrackChangeToastColorMode == "Accent";
        ToastColorCustomRadio.IsChecked = _settings.TrackChangeToastColorMode == "Custom";
        ToastColorDefaultRadio.IsChecked = _settings.TrackChangeToastColorMode is not ("Cover" or "Accent" or "Custom");
        ToastColorStrengthSlider.Value = Math.Clamp(Math.Round(_settings.TrackChangeToastColorStrength * 100),
            ToastColorStrengthSlider.Minimum, ToastColorStrengthSlider.Maximum);
        ToastColorStrengthValueText.Text = $"{(int)Math.Round(ToastColorStrengthSlider.Value)}%";
        UpdateToastColorPanels();
        UpdateToastWidthValueText();
    }

    // Мониторы пересобираются при каждом открытии и смене языка: состав экранов мог измениться.
    public void RefreshMonitorCombo()
    {
        bool wasInitializing = _isInitializing;
        _isInitializing = true;
        try
        {
            SettingsMonitorCombo.Fill(ToastMonitorCombo, _settings.TrackChangeToastMonitor, "Автоматически (тот же монитор, что и окно плеера)");
        }
        finally
        {
            _isInitializing = wasInitializing;
        }
    }

    private void ToastPositionRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.TrackChangeToastPosition = ToastPosTopLeftRadio.IsChecked == true ? "TopLeft"
            : ToastPosTopCenterRadio.IsChecked == true ? "TopCenter"
            : ToastPosTopRightRadio.IsChecked == true ? "TopRight"
            : ToastPosBottomLeftRadio.IsChecked == true ? "BottomLeft"
            : ToastPosBottomCenterRadio.IsChecked == true ? "BottomCenter"
            : "BottomRight";
        ToastCustomPositionText.Visibility = Visibility.Collapsed;
    }

    private void ToastPickPositionButton_Click(object sender, RoutedEventArgs e) => _owner.PickToastPosition(ToastPositionPicked);

    // Карточка могла оказаться на другом мониторе (MainWindow.PickToastPosition обновил TrackChangeToastMonitor),
    // поэтому список мониторов перечитываем из настроек.
    private void ToastPositionPicked(ToastPositionPickResult? result)
    {
        if (result is { } picked)
        {
            _settings.TrackChangeToastPosition = "Custom";
            _settings.TrackChangeToastCustomX = picked.X;
            _settings.TrackChangeToastCustomY = picked.Y;

            ToastPosTopLeftRadio.IsChecked = false;
            ToastPosTopCenterRadio.IsChecked = false;
            ToastPosTopRightRadio.IsChecked = false;
            ToastPosBottomLeftRadio.IsChecked = false;
            ToastPosBottomCenterRadio.IsChecked = false;
            ToastPosBottomRightRadio.IsChecked = false;
            ToastCustomPositionText.Visibility = Visibility.Visible;
            RefreshMonitorCombo();
        }

        if (_window.IsLoaded) _window.Activate();
    }

    private void ToastSizeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.TrackChangeToastSize = ToastSizeSmallRadio.IsChecked == true ? "Small"
            : ToastSizeLargeRadio.IsChecked == true ? "Large"
            : "Medium";
    }

    private void ToastArtSideRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.TrackChangeToastArtSide = ToastArtRightRadio.IsChecked == true ? "Right" : "Left";
    }

    private void ToastTextAlignmentRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.TrackChangeToastTextAlignment = ToastTextCenterRadio.IsChecked == true ? "Center"
            : ToastTextRightRadio.IsChecked == true ? "Right"
            : "Left";
        ToastArtNextToTextPanel.Visibility = ToastTextCenterRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ToastArtNextToTextCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.TrackChangeToastArtNextToText = ToastArtNextToTextCheckBox.IsChecked == true;
    }

    private void ToastMonitorCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.TrackChangeToastMonitor =
            ToastMonitorCombo.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string tag } ? tag : "";
    }

    // Слайдер целочисленный (IsSnapToTickEnabled, шаг 10px) — плавнее шагами в 1px пользователю
    // не нужно, а подпись рядом остаётся короткой и читаемой ("300 px").
    private void ToastWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateToastWidthValueText();
        if (_isInitializing) return;

        _settings.TrackChangeToastWidth = ToastWidthSlider.Value;
    }

    private void ToastOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateToastOpacityValueText();
        if (_isInitializing) return;

        _settings.TrackChangeToastOpacity = ToastOpacitySlider.Value / 100.0;
    }

    // См. AppSettings.TrackChangeToastColorMode: после смены режима или цвета сразу показываем пример, чтобы результат был виден.
    private void ToastColorModeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.TrackChangeToastColorMode = ToastColorCoverRadio.IsChecked == true ? "Cover"
            : ToastColorAccentRadio.IsChecked == true ? "Accent"
            : ToastColorCustomRadio.IsChecked == true ? "Custom"
            : "Default";
        UpdateToastColorPanels();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
        _owner.PreviewTrackChangeToast();
    }

    private void ToastColorStrengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ToastColorStrengthValueText is null) return;

        ToastColorStrengthValueText.Text = $"{(int)Math.Round(ToastColorStrengthSlider.Value)}%";
        if (_isInitializing) return;

        _settings.TrackChangeToastColorStrength = ToastColorStrengthSlider.Value / 100.0;
    }

    private void ToastColorPickButton_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Media.Color initial = ToastColorCalculator.TryParse(_settings.TrackChangeToastColorHex)
            ?? System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD4);
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(initial.R, initial.G, initial.B),
            FullOpen = true
        };

        var handle = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
        if (dialog.ShowDialog(new SettingsWindow.Wpf32Window(handle)) != System.Windows.Forms.DialogResult.OK) return;

        var picked = dialog.Color;
        _settings.TrackChangeToastColorHex = $"#{picked.R:X2}{picked.G:X2}{picked.B:X2}";
        UpdateToastColorPanels();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
        _owner.PreviewTrackChangeToast();
    }

    private void UpdateToastColorPanels()
    {
        string mode = _settings.TrackChangeToastColorMode;
        ToastColorStrengthPanel.Visibility = mode is "Cover" or "Accent" or "Custom" ? Visibility.Visible : Visibility.Collapsed;
        ToastColorCustomPanel.Visibility = mode == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        System.Windows.Media.Color swatch = ToastColorCalculator.TryParse(_settings.TrackChangeToastColorHex)
            ?? System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD4);
        ToastColorSwatch.Background = new System.Windows.Media.SolidColorBrush(swatch);
    }

    private void UpdateToastOpacityValueText()
    {
        ToastOpacityValueText.Text = $"{(int)Math.Round(ToastOpacitySlider.Value)}%";
    }

    private void UpdateToastWidthValueText()
    {
        ToastWidthValueText.Text = $"{(int)Math.Round(ToastWidthSlider.Value)} px";
    }

    private void ToastPreviewButton_Click(object sender, RoutedEventArgs e) => _owner.PreviewTrackChangeToast();
}
