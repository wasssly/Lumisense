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
    private void LanguageRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        LocalizationService.ChangeLanguage(_settings,
            LanguageEnglishRadio.IsChecked == true ? LocalizationService.English : LocalizationService.Russian);
    }

    private void ChangelogAutoTranslateCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.ChangelogAutoTranslate = ChangelogAutoTranslateCheckBox.IsChecked == true;
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void ThemeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.Theme = ThemeLightRadio.IsChecked == true ? "Light" : "Dark";

        ApplicationThemeManager.Apply(_settings.IsLightThemeResolved() ? ApplicationTheme.Light : ApplicationTheme.Dark);
        _owner.ApplyAccentColor(); // акцент пересчитывает светлые/тёмные варианты под новую тему
        ReapplyWindowBackdropsAfterThemeChange();
        _owner.ApplyMiniPlayerThemeLive();
    }

    // WPF-UI 4 ставит собственное обновление FluentWindow в очередь Dispatcher после ApplicationThemeManager.Apply и может
    // перезаписать backdrop дефолтным Mica, поэтому повторяем выбранный backdrop на ContextIdle (AppSettings не меняем).
    private void ReapplyWindowBackdropsAfterThemeChange()
    {
        _owner.ApplyWindowBackdrop(forceReapply: true);
        ApplyWindowBackdrop(_settings, forceReapply: true);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _owner.ApplyWindowBackdrop(forceReapply: true);
            if (IsLoaded)
                ApplyWindowBackdrop(_settings, forceReapply: true);
        }), DispatcherPriority.ContextIdle);
    }

    // Клик по карточке пака (Icons/svg/{Pack}, IconPacks в SvgPathIcon.cs) применяется сразу ко всем открытым окнам
    // через IconPacks.SetCurrent (IconPackContext) без перезапуска.
    private void IconPackCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Border { Tag: string pack } || !IconPacks.IsKnown(pack))
            return;

        if (pack == _settings.IconPack) return;

        _settings.IconPack = pack;
        RefreshIconPackCardSelection();

        if (_isInitializing) return;

        IconPacks.SetCurrent(pack);
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    // ПКМ открывает окно со всеми иконками пака (не обязательно выбранного); Show(), а не ShowDialog() — это read-only
    // превью, и модальность заблокировала бы SettingsWindow и косвенно MainWindow.
    private void IconPackCard_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Border { Tag: string pack } || !IconPacks.IsKnown(pack))
            return;

        e.Handled = true;
        new IconPackPreviewWindow(this, pack).Show();
    }

    // Подсвечивает рамкой карточку пака, совпадающего с _settings.IconPack — по аналогии с
    // RefreshAccentSwatchSelection выше.
    private void RefreshIconPackCardSelection()
    {
        (System.Windows.Controls.Border card, string pack)[] cards =
        {
            (IconPackCardDuotone, IconPacks.Duotone),
            (IconPackCardOutline, IconPacks.Outline),
            (IconPackCardBold, IconPacks.Bold),
            (IconPackCardFill, IconPacks.Fill),
            (IconPackCardThin, IconPacks.Thin),
            (IconPackCardFontAwesome, IconPacks.FontAwesome),
        };

        foreach (var (card, pack) in cards)
        {
            card.BorderBrush = pack == _settings.IconPack
                ? (Brush)FindResource("AccentFillColorDefaultBrush")
                : Brushes.Transparent;
        }
    }

    // Клик по карточке значка приложения (AppIcons.cs) применяется сразу во всех окнах и в трее через AppIconContext,
    // без перезапуска, как IconPackCard_MouseLeftButtonDown.
    private void AppIconCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Border { Tag: string icon } || !AppIcons.IsKnown(icon))
            return;

        if (icon == _settings.AppIcon) return;

        _settings.AppIcon = icon;
        RefreshAppIconCardSelection();

        if (_isInitializing) return;

        AppIcons.SetCurrent(icon);
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
        FireAndForget(ShortcutIconSync.SyncAsync(icon), "ShortcutIconSync");
    }

    // Подсвечивает рамкой карточку значка, совпадающего с _settings.AppIcon — по аналогии с
    // RefreshIconPackCardSelection выше.
    private void RefreshAppIconCardSelection()
    {
        (System.Windows.Controls.Border card, string icon)[] cards =
        {
            (AppIconCardGlass, AppIcons.Glass),
            (AppIconCardSpotlight, AppIcons.Spotlight),
            (AppIconCardBacklit, AppIcons.Backlit),
            (AppIconCardStripes, AppIcons.Stripes),
            (AppIconCardBeam, AppIcons.Beam),
        };

        foreach (var (card, icon) in cards)
        {
            card.BorderBrush = icon == _settings.AppIcon
                ? (Brush)FindResource("AccentFillColorDefaultBrush")
                : Brushes.Transparent;
        }
    }

    private void AccentModeRadio_Changed(object sender, RoutedEventArgs e)
    {
        AccentSwatchesPanel.Visibility = AccentManualRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (_isInitializing) return;

        _settings.AccentColorMode = AccentManualRadio.IsChecked == true
            ? "Manual"
            : AccentCoverRadio.IsChecked == true ? "Cover" : "System";
        _owner.ApplyAccentColor();
    }

    private static readonly string[] AccentPresetHexes =
    {
        "#0078D4", "#8764B8", "#E3008C", "#E81123", "#FF8C00", "#FFB900", "#107C10", "#00B7C3"
    };

    private void AccentSwatch_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Border { Background: SolidColorBrush brush }) return;

        // brush.Color.ToString() даёт 8-значный "#AARRGGBB", а пресеты и ColorDialog — 6-значный "#RRGGBB": цвет применился бы,
        // но RefreshAccentSwatchSelection сравнивает строки как есть и потеряла бы подсветку пресета.
        var c = brush.Color;
        ApplyAccentHex($"#{c.R:X2}{c.G:X2}{c.B:X2}");
    }

    private void AccentCustomButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.ColorTranslator.FromHtml(_settings.AccentColorHex),
            FullOpen = true
        };

        // WinForms-диалог — модальный поверх этого же (WPF) окна настроек; хендл окна нужен
        // явно, иначе диалог мог бы открыться за плеером вместо поверх него.
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (dialog.ShowDialog(new Wpf32Window(handle)) != System.Windows.Forms.DialogResult.OK) return;

        var c = dialog.Color;
        ApplyAccentHex($"#{c.R:X2}{c.G:X2}{c.B:X2}");
    }

    private void ApplyAccentHex(string hex)
    {
        _settings.AccentColorHex = hex;
        RefreshAccentSwatchSelection();
        if (_isInitializing) return;

        _owner.ApplyAccentColor();
    }

    // Рамка у пресета, совпадающего с AccentColorHex; при цвете из палитры (не из пресетов) рамки нет — это ожидаемо.
    private void RefreshAccentSwatchSelection()
    {
        System.Windows.Controls.Border[] swatches = { AccentSwatch0, AccentSwatch1, AccentSwatch2, AccentSwatch3,
                               AccentSwatch4, AccentSwatch5, AccentSwatch6, AccentSwatch7 };

        for (int i = 0; i < swatches.Length; i++)
        {
            bool selected = string.Equals(AccentPresetHexes[i], _settings.AccentColorHex,
                StringComparison.OrdinalIgnoreCase);
            swatches[i].BorderBrush = selected
                ? (Brush)FindResource("TextFillColorPrimaryBrush")
                : Brushes.Transparent;
        }
    }

    // Обёртка над HWND для System.Windows.Forms.IWin32Window — ColorDialog просит именно
    // этот интерфейс в качестве owner, а не голый IntPtr.
    internal sealed class Wpf32Window : System.Windows.Forms.IWin32Window
    {
        public Wpf32Window(nint handle) => Handle = handle;
        public nint Handle { get; }
    }

        private void WindowBackdropRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.WindowBackdropType = BackdropBlurRadio.IsChecked == true ? WindowBackdropOptions.Blur
            : BackdropAcrylicRadio.IsChecked == true ? WindowBackdropOptions.Acrylic
            : BackdropMicaAltRadio.IsChecked == true ? WindowBackdropOptions.MicaAlt
            : WindowBackdropOptions.Mica;
        _owner.ApplyWindowBackdrop();
        ApplyWindowBackdrop(_settings); // то же самое — и у этого окна настроек тоже
    }

    private void CoverBaseFromCoverCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.CoverBaseFromCover = CoverBaseFromCoverCheckBox.IsChecked == true;
        _owner.ApplyCoverBaseTheme();
    }
}
