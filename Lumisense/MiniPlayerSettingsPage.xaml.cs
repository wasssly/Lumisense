using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using static Lumisense.BackgroundTask;

namespace Lumisense;

// Создаётся при первом показе страницы (см. SettingsWindow.EnsurePageBuilt).
public sealed partial class MiniPlayerSettingsPage : System.Windows.Controls.UserControl, ILazySettingsPage
{
    private readonly SettingsWindow _window;
    private readonly AppSettings _settings;
    private readonly ISettingsHost _owner;

    // Глушит Changed-обработчики, пока значения читаются из настроек.
    private bool _isInitializing = true;

    internal MiniPlayerSettingsPage(SettingsWindow window, AppSettings settings, ISettingsHost owner)
    {
        _window = window;
        _settings = settings;
        _owner = owner;

        // StaticResource разбирается до того, как элементы попадут в дерево окна, поэтому ресурсы окна подключаются заранее.
        Resources.MergedDictionaries.Add(window.Resources);
        InitializeComponent();

        MiniStyleIslandRadio.IsChecked = _settings.MiniPlayerStyle == "Island";
        MiniStyleClassicRadio.IsChecked = !MiniStyleIslandRadio.IsChecked.GetValueOrDefault();
        MiniOpacitySlider.Value = _settings.MiniPlayerOpacity;
        MiniOpacityValueText.Text = $"{(int)Math.Round(_settings.MiniPlayerOpacity * 100)}%";
        MiniAlwaysOnTopCheckBox.IsChecked = _settings.MiniPlayerAlwaysOnTop;
        GameOverlayCompatibilityCheckBox.IsChecked = _settings.GameOverlayCompatibilityMode;
        GameOverlayAutoDetectCheckBox.IsChecked = _settings.GameOverlayCompatibilityAutoDetect;
        MiniPinnedCheckBox.IsChecked = _settings.MiniPlayerPinned;
        MiniSnapToEdgesCheckBox.IsChecked = _settings.MiniPlayerSnapToEdges;
        MiniSecondaryShuffleRadio.IsChecked = _settings.MiniPlayerSecondaryButton == "Shuffle";
        MiniSecondaryFavoriteRadio.IsChecked = _settings.MiniPlayerSecondaryButton == "Favorite";
        MiniSecondaryRepeatRadio.IsChecked = !MiniSecondaryShuffleRadio.IsChecked.GetValueOrDefault()
                                              && !MiniSecondaryFavoriteRadio.IsChecked.GetValueOrDefault();
        MiniButtonsOverlayRadio.IsChecked = _settings.MiniPlayerButtonsLayout == "Overlay";
        MiniButtonsBelowRadio.IsChecked = !MiniButtonsOverlayRadio.IsChecked.GetValueOrDefault();
        MiniArtworkVinylRadio.IsChecked = _settings.MiniPlayerArtworkStyle == "Vinyl";
        MiniArtworkStaticCircleRadio.IsChecked = _settings.MiniPlayerArtworkStyle == "StaticCircle";
        MiniArtworkDefaultRadio.IsChecked = !MiniArtworkVinylRadio.IsChecked.GetValueOrDefault()
                                             && !MiniArtworkStaticCircleRadio.IsChecked.GetValueOrDefault();
        MiniShowProgressCheckBox.IsChecked = _settings.MiniPlayerShowProgress;
        MiniShowArtworkProgressCheckBox.IsChecked = _settings.MiniPlayerShowArtworkProgress;
        MiniArtworkProgressThicknessSlider.Value = _settings.MiniPlayerArtworkProgressThickness;
        RefreshMiniArtworkProgressThicknessPresentation();
        MiniArtworkProgressFixedRadio.IsChecked = _settings.MiniPlayerArtworkProgressColorMode == "Fixed";
        MiniArtworkProgressAccentRadio.IsChecked = !MiniArtworkProgressFixedRadio.IsChecked.GetValueOrDefault();
        MiniArtworkProgressColorSwatchesPanel.Visibility = MiniArtworkProgressFixedRadio.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
        RefreshMiniArtworkProgressColorSwatchSelection();
        MiniInfoOnlyTitleRadio.IsChecked = _settings.MiniPlayerInfoMode == "TitleOnly";
        MiniSizeCurrentRadio.IsChecked = _settings.MiniPlayerSizePreset == MiniPlayerSizePreset.Current;
        MiniSizeCompactRadio.IsChecked = _settings.MiniPlayerSizePreset == MiniPlayerSizePreset.Compact;
        MiniSizeClassicRadio.IsChecked = !MiniSizeCurrentRadio.IsChecked.GetValueOrDefault()
                                         && !MiniSizeCompactRadio.IsChecked.GetValueOrDefault();
        MiniInfoRemainingRadio.IsChecked = _settings.MiniPlayerInfoMode == "TitleRemaining";
        MiniInfoArtistRadio.IsChecked = !MiniInfoOnlyTitleRadio.IsChecked.GetValueOrDefault()
                                         && !MiniInfoRemainingRadio.IsChecked.GetValueOrDefault();
        MiniArtistMarqueeCheckBox.IsChecked = _settings.MiniPlayerArtistMarquee;

        InitializeMiniPlayerContextMenuActionCheckBoxes();
        MiniArtworkProgressStyleMaterialRadio.IsChecked = _settings.MiniPlayerArtworkProgressStyle == "Material";
        MiniArtworkProgressStyleMaterialSliderRadio.IsChecked = _settings.MiniPlayerArtworkProgressStyle == "MaterialSlider";
        MiniArtworkProgressStyleDefaultRadio.IsChecked = _settings.MiniPlayerArtworkProgressStyle is not ("Material" or "MaterialSlider");

        ContextMenuOrderLink.Attach(MiniContextMenuActionsPanel, ContextMenuOrdering.Mini, _settings, () => _isInitializing);

        // Выпадающие списки радиогрупп читают отмеченные радиокнопки, поэтому связываются после загрузки настроек.
        RadioComboLinks.Initialize(this, FindName);
        _isInitializing = false;
    }

    public void MoveContentTo(System.Windows.Controls.Panel host) => LazySettingsPage.MoveChildren(PageMiniPlayer, host);

    public FrameworkElement? FindElement(string name) => FindName(name) as FrameworkElement;

    // Прозрачность мини-плеера — как громкость в главном окне: Slider не ловит мышь (IsHitTestVisible="False"),
    // клик и перетаскивание по всей полосе обрабатывает прозрачный Border поверх него.

    private bool _isDraggingOpacityOverlay;

    private void MiniOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        MiniOpacityValueText.Text = $"{(int)Math.Round(e.NewValue * 100)}%";

        if (_isInitializing) return;

        _settings.MiniPlayerOpacity = e.NewValue;
        _owner.ApplyMiniPlayerOpacityLive(e.NewValue);
    }

    private void MiniOpacityOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var overlay = (FrameworkElement)sender;
        overlay.CaptureMouse();
        _isDraggingOpacityOverlay = true;
        MiniOpacitySlider.Focus();
        UpdateSliderValueFromMouse(MiniOpacitySlider, e.GetPosition(overlay).X, overlay.ActualWidth);
    }

    private void MiniOpacityOverlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingOpacityOverlay) return;
        var overlay = (FrameworkElement)sender;
        UpdateSliderValueFromMouse(MiniOpacitySlider, e.GetPosition(overlay).X, overlay.ActualWidth);
    }

    private void MiniOpacityOverlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var overlay = (FrameworkElement)sender;
        overlay.ReleaseMouseCapture();
        _isDraggingOpacityOverlay = false;
    }

    private static void UpdateSliderValueFromMouse(System.Windows.Controls.Slider slider, double positionX, double width)
    {
        if (width <= 0) return;

        double ratio = Math.Clamp(positionX / width, 0.0, 1.0);
        slider.Value = slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
    }

    private void MiniAlwaysOnTopCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.MiniPlayerAlwaysOnTop = MiniAlwaysOnTopCheckBox.IsChecked == true;
        _owner.ApplyMiniPlayerTopmostLive(_settings.MiniPlayerAlwaysOnTop);
    }

    private void GameOverlayCompatibilityCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.GameOverlayCompatibilityMode = GameOverlayCompatibilityCheckBox.IsChecked == true;
        _owner.ApplyMiniPlayerOverlayCompatibilityLive(_owner.EffectiveGameOverlayCompatibilityEnabled);
    }

    private void GameOverlayAutoDetectCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.GameOverlayCompatibilityAutoDetect = GameOverlayAutoDetectCheckBox.IsChecked == true;
        _owner.ApplyGameOverlayAutoDetectSettingLive();
    }

    private void MiniPinnedCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _owner.SetMiniPlayerPinned(MiniPinnedCheckBox.IsChecked == true);
    }

    private void MiniSnapToEdgesCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.MiniPlayerSnapToEdges = MiniSnapToEdgesCheckBox.IsChecked == true;
    }

    private void MiniSecondaryButtonRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _owner.SetMiniPlayerSecondaryButtonMode(
            MiniSecondaryShuffleRadio.IsChecked == true ? "Shuffle"
            : MiniSecondaryFavoriteRadio.IsChecked == true ? "Favorite"
            : "Repeat");
    }

    private void MiniButtonsLayoutRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.MiniPlayerButtonsLayout = MiniButtonsOverlayRadio.IsChecked == true ? "Overlay" : "Below";
        _owner.ApplyMiniPlayerButtonsLayoutLive();
    }

    private void MiniArtworkStyleRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.MiniPlayerArtworkStyle = MiniArtworkVinylRadio.IsChecked == true
            ? "Vinyl"
            : MiniArtworkStaticCircleRadio.IsChecked == true ? "StaticCircle" : "Default";
        _owner.ApplyMiniPlayerArtworkStyleLive();
    }

    private void MiniShowProgressCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.MiniPlayerShowProgress = MiniShowProgressCheckBox.IsChecked == true;
        _owner.ApplyMiniPlayerProgressBarVisibilityLive();
    }

    // См. AppSettings.MiniPlayerArtworkProgressStyle.
    private void MiniArtworkProgressStyleRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.MiniPlayerArtworkProgressStyle = MiniArtworkProgressStyleMaterialRadio.IsChecked == true ? "Material"
            : MiniArtworkProgressStyleMaterialSliderRadio.IsChecked == true ? "MaterialSlider"
            : "Default";
        _owner.ApplyMiniPlayerArtworkProgressVisibilityLive();
        _window.ReloadProgressControls();
    }

    private void MiniShowArtworkProgressCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.MiniPlayerShowArtworkProgress = MiniShowArtworkProgressCheckBox.IsChecked == true;
        _owner.ApplyMiniPlayerArtworkProgressVisibilityLive();
        _window.ReloadProgressControls();
    }

    private void MiniArtworkProgressThicknessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // ValueChanged может сработать прямо при чтении XAML (ещё до присваивания _settings).
        // До завершения конструктора не меняем модель и не обновляем визуальные подписи.
        if (_isInitializing) return;

        double thickness = Math.Round(MiniArtworkProgressThicknessSlider.Value * 2, MidpointRounding.AwayFromZero) / 2;
        if (Math.Abs(MiniArtworkProgressThicknessSlider.Value - thickness) > 0.001)
        {
            MiniArtworkProgressThicknessSlider.Value = thickness;
            return;
        }

        _settings.MiniPlayerArtworkProgressThickness = thickness;
        RefreshMiniArtworkProgressThicknessPresentation();
        _owner.ApplyMiniPlayerArtworkProgressThicknessLive();
    }

    internal void RefreshMiniArtworkProgressThicknessPresentation()
    {
        MiniArtworkProgressThicknessLabelText.Text = LocalizationService.Get(LocalizationKey.MiniArtworkProgressThicknessLabel);
        MiniArtworkProgressThicknessDescriptionText.Text = LocalizationService.Get(LocalizationKey.MiniArtworkProgressThicknessDescription);
        MiniArtworkProgressThicknessValueText.Text = string.Format(
            CultureInfo.CurrentCulture, "{0:0.0} DIP", _settings.MiniPlayerArtworkProgressThickness);
    }

    private void MiniArtworkProgressColorModeRadio_Changed(object sender, RoutedEventArgs e)
    {
        bool fixedColor = MiniArtworkProgressFixedRadio.IsChecked == true;
        MiniArtworkProgressColorSwatchesPanel.Visibility = fixedColor ? Visibility.Visible : Visibility.Collapsed;
        if (_isInitializing) return;

        _settings.MiniPlayerArtworkProgressColorMode = fixedColor ? "Fixed" : "Accent";
        _owner.ApplyMiniPlayerArtworkProgressColorLive();
    }

    private void MiniArtworkProgressColorSwatch_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Border { Background: SolidColorBrush brush }) return;

        var color = brush.Color;
        ApplyMiniArtworkProgressColorHex($"#{color.R:X2}{color.G:X2}{color.B:X2}");
    }

    private void MiniArtworkProgressColorCustomButton_Click(object sender, RoutedEventArgs e)
    {
        System.Drawing.Color initialColor;
        try
        {
            initialColor = System.Drawing.ColorTranslator.FromHtml(_settings.MiniPlayerArtworkProgressColorHex);
        }
        catch
        {
            // Сохранённый цвет повреждён (не HTML-формат): диалог открывается со стандартным синим.
            initialColor = System.Drawing.Color.FromArgb(0x00, 0x78, 0xD4);
        }

        using var dialog = new System.Windows.Forms.ColorDialog { Color = initialColor, FullOpen = true };
        var handle = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
        if (dialog.ShowDialog(new SettingsWindow.Wpf32Window(handle)) != System.Windows.Forms.DialogResult.OK) return;

        var color = dialog.Color;
        ApplyMiniArtworkProgressColorHex($"#{color.R:X2}{color.G:X2}{color.B:X2}");
    }

    private void ApplyMiniArtworkProgressColorHex(string hex)
    {
        _settings.MiniPlayerArtworkProgressColorHex = hex;
        RefreshMiniArtworkProgressColorSwatchSelection();
        if (_isInitializing) return;

        _owner.ApplyMiniPlayerArtworkProgressColorLive();
    }

    private void RefreshMiniArtworkProgressColorSwatchSelection()
    {
        System.Windows.Controls.Border[] swatches =
        {
            MiniArtworkProgressColorSwatch0, MiniArtworkProgressColorSwatch1,
            MiniArtworkProgressColorSwatch2, MiniArtworkProgressColorSwatch3,
            MiniArtworkProgressColorSwatch4, MiniArtworkProgressColorSwatch5,
            MiniArtworkProgressColorSwatch6, MiniArtworkProgressColorSwatch7
        };

        for (int i = 0; i < swatches.Length; i++)
        {
            bool selected = string.Equals(AppearanceSettingsPage.AccentPresetHexes[i], _settings.MiniPlayerArtworkProgressColorHex,
                StringComparison.OrdinalIgnoreCase);
            swatches[i].BorderBrush = selected
                ? (Brush)FindResource("TextFillColorPrimaryBrush")
                : Brushes.Transparent;
        }
    }

    private void MiniStyleRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.MiniPlayerStyle = MiniStyleIslandRadio.IsChecked == true ? "Island" : "Classic";
        _window.UpdateIslandInactiveNote();
        _owner.ApplyMiniPlayerStyleLive();
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

    private void InitializeMiniPlayerContextMenuActionCheckBoxes()
    {
        var checkBoxes = new[]
        {
            MiniContextSettingsCheckBox,
            MiniContextNowPlayingCheckBox,
            MiniContextPinCheckBox,
            MiniContextTopmostCheckBox,
            MiniContextOverlayCompatibilityCheckBox,
            MiniContextSecondaryButtonCheckBox,
            MiniContextPlaybackRateCheckBox,
            MiniContextPitchCheckBox,
            MiniContextOpacityCheckBox,
            MiniContextSnapToEdgesCheckBox,
            MiniContextShowProgressCheckBox,
            MiniContextShowArtworkProgressCheckBox,
            MiniContextArtworkStyleCheckBox,
            MiniContextButtonsLayoutCheckBox
        };

        foreach (System.Windows.Controls.CheckBox checkBox in checkBoxes)
        {
            if (checkBox.Tag is string actionId)
                checkBox.IsChecked = !_owner.IsMiniPlayerContextMenuActionDisabled(actionId);
        }
    }

    private void MiniPlayerContextMenuActionCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        if (sender is not System.Windows.Controls.CheckBox { Tag: string actionId } checkBox) return;

        _owner.SetMiniPlayerContextMenuActionDisabled(actionId, checkBox.IsChecked != true);
    }

    // Вызывается извне, когда закрепление, "поверх окон" или прозрачность переключили прямо на мини-плеере;
    // _isInitializing глушит Changed-обработчики, чтобы не применять настройку повторно и не зациклить обновления.
    internal void RefreshMiniPlayerToggles()
    {
        bool wasInitializing1 = _isInitializing;
        _isInitializing = true;
        try
        {
            MiniAlwaysOnTopCheckBox.IsChecked = _settings.MiniPlayerAlwaysOnTop;
            MiniPinnedCheckBox.IsChecked = _settings.MiniPlayerPinned;
            MiniSnapToEdgesCheckBox.IsChecked = _settings.MiniPlayerSnapToEdges;
            MiniSecondaryShuffleRadio.IsChecked = _settings.MiniPlayerSecondaryButton == "Shuffle";
            MiniSecondaryFavoriteRadio.IsChecked = _settings.MiniPlayerSecondaryButton == "Favorite";
            MiniSecondaryRepeatRadio.IsChecked = !MiniSecondaryShuffleRadio.IsChecked.GetValueOrDefault()
                                                  && !MiniSecondaryFavoriteRadio.IsChecked.GetValueOrDefault();
            MiniOpacitySlider.Value = _settings.MiniPlayerOpacity;
            MiniOpacityValueText.Text = $"{(int)Math.Round(_settings.MiniPlayerOpacity * 100)}%";
        }
        finally
        {
            // Без finally исключение выше оставило бы флаг включённым, и окно перестало бы сохранять любые изменения.
            _isInitializing = wasInitializing1;
        }
    }

    internal void MakeArtworkProgressWavy()
    {
        MiniShowArtworkProgressCheckBox.IsChecked = true;
        MiniArtworkProgressStyleMaterialRadio.IsChecked = true;
    }

    private void ContextMenuOrderReset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string menu } && menu == ContextMenuOrdering.Mini)
            ContextMenuOrderLink.Reset(MiniContextMenuActionsPanel, menu, _settings);
    }
}
