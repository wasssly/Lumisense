using System;
using System.Windows;
using static Lumisense.BackgroundTask;

namespace Lumisense;

// Создаётся при первом показе страницы (см. SettingsWindow.EnsurePageBuilt).
public sealed partial class CoverSettingsPage : System.Windows.Controls.UserControl, ILazySettingsPage
{
    private readonly Window _window;
    private readonly AppSettings _settings;
    private readonly ISettingsHost _owner;

    // Глушит Changed-обработчики, пока значения читаются из настроек.
    private bool _isInitializing = true;

    internal CoverSettingsPage(Window window, AppSettings settings, ISettingsHost owner)
    {
        _window = window;
        _settings = settings;
        _owner = owner;

        // StaticResource разбирается до того, как элементы попадут в дерево окна, поэтому ресурсы окна подключаются заранее.
        Resources.MergedDictionaries.Add(window.Resources);
        InitializeComponent();

        NowPlayingBackgroundOrbsRadio.IsChecked = _settings.NowPlayingBackground == "Orbs";
        NowPlayingBackgroundWavesRadio.IsChecked = _settings.NowPlayingBackground == "Waves";
        NowPlayingBackgroundCoverRadio.IsChecked = _settings.NowPlayingBackground == "Cover";
        NowPlayingBackgroundSolidRadio.IsChecked = _settings.NowPlayingBackground == "Solid";
        NowPlayingBackgroundNoneRadio.IsChecked = _settings.NowPlayingBackground == "None";
        NowPlayingBackgroundCloudsRadio.IsChecked = _settings.NowPlayingBackground is not ("Orbs" or "Waves" or "Cover" or "Solid" or "None");
        NowPlayingBackgroundColorPanel.IsEnabled = _settings.NowPlayingBackground == "Solid";
        RefreshNowPlayingBackgroundColorSwatch();
        FavoriteHeartSparksRadio.IsChecked = _settings.FavoriteHeartAnimation == "Sparks";
        FavoriteHeartRingRadio.IsChecked = _settings.FavoriteHeartAnimation == "Ring";
        FavoriteHeartNoneRadio.IsChecked = _settings.FavoriteHeartAnimation == "None";
        FavoriteHeartFillRadio.IsChecked = !FavoriteHeartSparksRadio.IsChecked.GetValueOrDefault()
                                           && !FavoriteHeartRingRadio.IsChecked.GetValueOrDefault()
                                           && !FavoriteHeartNoneRadio.IsChecked.GetValueOrDefault();
        UpdateBadgePreview();
        BadgeModeExtensionRadio.IsChecked = _settings.TrackStateBadgeMode == "Extension";
        BadgeModeQualityRadio.IsChecked = _settings.TrackStateBadgeMode == "Quality";
        BadgeModeFileSizeRadio.IsChecked = _settings.TrackStateBadgeMode == "FileSize";
        BadgeModeCustomRadio.IsChecked = _settings.TrackStateBadgeMode == "Custom";
        BadgeModeStateRadio.IsChecked = _settings.TrackStateBadgeMode is not ("Extension" or "Quality" or "FileSize" or "Custom");
        BadgePartStateCheckBox.IsChecked = _settings.TrackStateBadgeShowState;
        BadgePartExtensionCheckBox.IsChecked = _settings.TrackStateBadgeShowExtension;
        BadgePartBitrateCheckBox.IsChecked = _settings.TrackStateBadgeShowBitrate;
        BadgePartSampleRateCheckBox.IsChecked = _settings.TrackStateBadgeShowSampleRate;
        BadgePartFileSizeCheckBox.IsChecked = _settings.TrackStateBadgeShowFileSize;
        BadgeCustomPartsPanel.Visibility = _settings.TrackStateBadgeMode == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        bool carousel = _owner.IsAlbumArtTransitionEnabled && _settings.AlbumArtTransitionStyle == "Carousel";
        AlbumArtTransitionStyleCarouselRadio.IsChecked = carousel;
        AlbumArtTransitionOnRadio.IsChecked = _owner.IsAlbumArtTransitionEnabled && !carousel;
        AlbumArtTransitionOffRadio.IsChecked = !_owner.IsAlbumArtTransitionEnabled;
        AlbumArtGesturesCheckBox.IsChecked = _settings.AlbumArtGesturesEnabled;

        // Выпадающий список подписи читает отмеченные радиокнопки, поэтому связывается после загрузки настроек.
        RadioComboLinks.Initialize(this, FindName);
        _isInitializing = false;
    }

    public void MoveContentTo(System.Windows.Controls.Panel host) => LazySettingsPage.MoveChildren(PageCover, host);

    public FrameworkElement? FindElement(string name) => FindName(name) as FrameworkElement;

    private void UpdateBadgePreview()
    {
        BadgePreviewText.Text = _settings.TrackStateBadgeMode switch
        {
            "Extension" => "FLAC",
            "Quality" => "320 kbps · 44.1 kHz",
            "FileSize" => "8.4 MB",
            "Custom" => TrackBadgeFormatter.Join(
                    _settings.TrackStateBadgeShowState ? LocalizationService.Get(LocalizationKey.TrackStatePlaying) : null,
                    _settings.TrackStateBadgeShowExtension ? "FLAC" : null,
                    _settings.TrackStateBadgeShowBitrate ? "320 kbps" : null,
                    _settings.TrackStateBadgeShowSampleRate ? "44.1 kHz" : null,
                    _settings.TrackStateBadgeShowFileSize ? "8.4 MB" : null)
                ?? LocalizationService.Get(LocalizationKey.TrackStatePlaying),
            _ => LocalizationService.Get(LocalizationKey.TrackStatePlaying)
        };
    }

    private void NowPlayingBackgroundRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.NowPlayingBackground = NowPlayingBackgroundOrbsRadio.IsChecked == true ? "Orbs"
            : NowPlayingBackgroundWavesRadio.IsChecked == true ? "Waves"
            : NowPlayingBackgroundCoverRadio.IsChecked == true ? "Cover"
            : NowPlayingBackgroundSolidRadio.IsChecked == true ? "Solid"
            : NowPlayingBackgroundNoneRadio.IsChecked == true ? "None"
            : "Clouds";
        NowPlayingBackgroundColorPanel.IsEnabled = _settings.NowPlayingBackground == "Solid";
        _owner.ApplyNowPlayingBackgroundLive();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void NowPlayingBackgroundColorButton_Click(object sender, RoutedEventArgs e)
    {
        System.Drawing.Color initialColor;
        try
        {
            initialColor = System.Drawing.ColorTranslator.FromHtml(_settings.NowPlayingBackgroundColor);
        }
        catch
        {
            // Сохранённый цвет повреждён: диалог открывается с цветом по умолчанию.
            initialColor = System.Drawing.Color.FromArgb(0x10, 0x12, 0x18);
        }

        using var dialog = new System.Windows.Forms.ColorDialog { Color = initialColor, FullOpen = true };
        var handle = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
        if (dialog.ShowDialog(new SettingsWindow.Wpf32Window(handle)) != System.Windows.Forms.DialogResult.OK) return;

        var color = dialog.Color;
        _settings.NowPlayingBackgroundColor = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        RefreshNowPlayingBackgroundColorSwatch();
        _owner.ApplyNowPlayingBackgroundLive();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void RefreshNowPlayingBackgroundColorSwatch()
    {
        try
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_settings.NowPlayingBackgroundColor);
            NowPlayingBackgroundColorSwatch.Background = new System.Windows.Media.SolidColorBrush(color);
        }
        catch
        {
            NowPlayingBackgroundColorSwatch.Background = System.Windows.Media.Brushes.Transparent;
        }
    }

    private void FavoriteHeartAnimationRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.FavoriteHeartAnimation = FavoriteHeartSparksRadio.IsChecked == true ? HeartAnimation.Sparks
            : FavoriteHeartRingRadio.IsChecked == true ? HeartAnimation.Ring
            : FavoriteHeartNoneRadio.IsChecked == true ? HeartAnimation.None
            : HeartAnimation.Fill;
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
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
}
