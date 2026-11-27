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
        ReloadProgressControls();
    }

    private void MiniShowArtworkProgressCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.MiniPlayerShowArtworkProgress = MiniShowArtworkProgressCheckBox.IsChecked == true;
        _owner.ApplyMiniPlayerArtworkProgressVisibilityLive();
        ReloadProgressControls();
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

    private void RefreshMiniArtworkProgressThicknessPresentation()
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
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (dialog.ShowDialog(new Wpf32Window(handle)) != System.Windows.Forms.DialogResult.OK) return;

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
            bool selected = string.Equals(AccentPresetHexes[i], _settings.MiniPlayerArtworkProgressColorHex,
                StringComparison.OrdinalIgnoreCase);
            swatches[i].BorderBrush = selected
                ? (Brush)FindResource("TextFillColorPrimaryBrush")
                : Brushes.Transparent;
        }
    }
}
