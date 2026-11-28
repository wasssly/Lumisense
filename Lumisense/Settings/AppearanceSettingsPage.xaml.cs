using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using static Lumisense.BackgroundTask;

namespace Lumisense;

// Создаётся при первом показе страницы (см. SettingsWindow.EnsurePageBuilt).
public sealed partial class AppearanceSettingsPage : System.Windows.Controls.UserControl, ILazySettingsPage
{
    private readonly SettingsWindow _window;
    private readonly AppSettings _settings;
    private readonly ISettingsHost _owner;

    // Глушит Changed-обработчики, пока значения читаются из настроек.
    private bool _isInitializing = true;
    private bool _interfaceScaleRestartNoticeShown;

    internal AppearanceSettingsPage(SettingsWindow window, AppSettings settings, ISettingsHost owner)
    {
        _window = window;
        _settings = settings;
        _owner = owner;

        // StaticResource разбирается до того, как элементы попадут в дерево окна, поэтому ресурсы окна подключаются заранее.
        Resources.MergedDictionaries.Add(window.Resources);
        InitializeComponent();

        LanguageEnglishRadio.IsChecked = string.Equals(_settings.Language, LocalizationService.English, StringComparison.OrdinalIgnoreCase);
        LanguageRussianRadio.IsChecked = !LanguageEnglishRadio.IsChecked.GetValueOrDefault();
        ChangelogAutoTranslateCheckBox.IsChecked = _settings.ChangelogAutoTranslate;

        ThemeLightRadio.IsChecked = _settings.Theme == "Light";
        ThemeDarkRadio.IsChecked = !ThemeLightRadio.IsChecked.GetValueOrDefault();

        RefreshIconPackCardSelection();
        RefreshAppIconCardSelection();

        AccentManualRadio.IsChecked = _settings.AccentColorMode == "Manual";
        AccentCoverRadio.IsChecked = _settings.AccentColorMode == "Cover";
        AccentSystemRadio.IsChecked = !AccentManualRadio.IsChecked.GetValueOrDefault()
                                      && !AccentCoverRadio.IsChecked.GetValueOrDefault();
        AccentSwatchesPanel.Visibility = AccentManualRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RefreshAccentSwatchSelection();

        BackdropCard.Visibility = WindowBackdropOptions.IsSelectionSupportedNow ? Visibility.Visible : Visibility.Collapsed;
        string backdrop = WindowBackdropOptions.Normalize(_settings.WindowBackdropType);
        BackdropAcrylicRadio.IsChecked = backdrop == WindowBackdropOptions.Acrylic;
        BackdropBlurRadio.IsChecked = backdrop == WindowBackdropOptions.Blur;
        BackdropMicaAltRadio.IsChecked = backdrop == WindowBackdropOptions.MicaAlt;
        BackdropMicaRadio.IsChecked = backdrop == WindowBackdropOptions.Mica;
        CoverBaseFromCoverCheckBox.IsChecked = _settings.CoverBaseFromCover;

        InterfaceScaleSlider.Value = AccessibilityPreferences.NormalizeScale(_settings.InterfaceScale) * 100;
        InterfaceScaleValueText.Text = $"{InterfaceScaleSlider.Value:0}%";
        // «Снизить движение» — это все разделы выключенными, поэтому отдельного флажка у него нет.
        foreach (var box in AnimationAreaBoxes())
            box.IsChecked = !_settings.ReduceMotion && !_settings.DisabledAnimationAreas.Contains((string)box.Tag);
        RefreshAnimationsMasterCheckBox();

        NavShowLabelsCheckBox.IsChecked = _settings.SettingsNavigationShowLabels;
        NavLabelModeSelectedRadio.IsChecked = _settings.SettingsNavigationLabelMode == "Selected";
        NavLabelModeBelowRadio.IsChecked = _settings.SettingsNavigationLabelMode != "Selected";
        NavStylePillsRadio.IsChecked = _settings.SettingsNavigationStyle == "Pills";
        NavStyleFloatingRadio.IsChecked = _settings.SettingsNavigationStyle == "FloatingBar";
        NavStyleSidebarRadio.IsChecked = !NavStylePillsRadio.IsChecked.GetValueOrDefault()
                                         && !NavStyleFloatingRadio.IsChecked.GetValueOrDefault();
        UpdateNavOptionsVisibility(animate: false);

        // Выпадающие списки радиогрупп читают отмеченные радиокнопки, поэтому связываются после загрузки настроек.
        RadioComboLinks.Initialize(this, FindName);
        _isInitializing = false;
    }

    public void MoveContentTo(System.Windows.Controls.Panel host) => LazySettingsPage.MoveChildren(PageAppearance, host);

    public FrameworkElement? FindElement(string name) => FindName(name) as FrameworkElement;

    internal static readonly string[] AccentPresetHexes =
    {
        "#0078D4", "#8764B8", "#E3008C", "#E81123", "#FF8C00", "#FFB900", "#107C10", "#00B7C3"
    };

    private void LanguageRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        LanguageSettings.Change(_settings,
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
        _window.ReapplyWindowBackdropsAfterThemeChange();
        _owner.ApplyMiniPlayerThemeLive();
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
        new IconPackPreviewWindow(_window, pack).Show();
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
            SetCardSelected(card, pack == _settings.IconPack);
        }
    }

    // Активная карточка: заливка акцентным тоном, тонкая рамка акцентного цвета и галочка в углу. Внутри карточки лежит Grid:
    // подложка с заливкой, содержимое и значок-галочка.
    private void SetCardSelected(System.Windows.Controls.Border card, bool selected)
    {
        card.BorderBrush = selected ? (Brush)FindResource("AccentFillColorDefaultBrush") : Brushes.Transparent;
        if (card.Child is not System.Windows.Controls.Grid { Children.Count: 3 } layers
            || layers.Children[0] is not System.Windows.Controls.Border tint
            || layers.Children[2] is not System.Windows.Controls.Border badge)
            return;

        bool wasSelected = badge.Visibility == Visibility.Visible;
        var visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        tint.Visibility = visibility;
        badge.Visibility = visibility;
        if (!selected || wasSelected || _isInitializing || SpringMotion.Reduced(AnimationAreas.SettingsWindow)) return;

        // Галочка вырастает из середины значка и проявляется.
        var ease = new System.Windows.Media.Animation.BackEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut, Amplitude = 0.6 };
        var grow = new System.Windows.Media.Animation.DoubleAnimation(0.5, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease };
        if (badge.RenderTransform is ScaleTransform scale)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }

        badge.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
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
            SetCardSelected(card, icon == _settings.AppIcon);
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
        var handle = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
        if (dialog.ShowDialog(new SettingsWindow.Wpf32Window(handle)) != System.Windows.Forms.DialogResult.OK) return;

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

    private void WindowBackdropRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.WindowBackdropType = BackdropBlurRadio.IsChecked == true ? WindowBackdropOptions.Blur
            : BackdropAcrylicRadio.IsChecked == true ? WindowBackdropOptions.Acrylic
            : BackdropMicaAltRadio.IsChecked == true ? WindowBackdropOptions.MicaAlt
            : WindowBackdropOptions.Mica;
        _owner.ApplyWindowBackdrop();
        _window.ApplyOwnBackdrop(); // то же самое — и у этого окна настроек тоже
    }

    private void CoverBaseFromCoverCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.CoverBaseFromCover = CoverBaseFromCoverCheckBox.IsChecked == true;
        _owner.ApplyCoverBaseTheme();
    }

    private IEnumerable<System.Windows.Controls.CheckBox> AnimationAreaBoxes() =>
        AnimationAreasPanel.Children.OfType<System.Windows.Controls.CheckBox>();

    private void AnimationAreaCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        SaveAnimationAreaSelection();
    }

    // Общая галочка: если включено всё — выключает всё, иначе включает всё. Само переключение WPF (три состояния) не нужно.
    private void AnimationsMasterCheckBox_Click(object sender, RoutedEventArgs e)
    {
        bool enable = !AnimationAreaBoxes().All(box => box.IsChecked == true);
        bool wasInitializing = _isInitializing;
        _isInitializing = true;
        try
        {
            foreach (var box in AnimationAreaBoxes()) box.IsChecked = enable;
        }
        finally
        {
            _isInitializing = wasInitializing;
        }

        SaveAnimationAreaSelection();
    }

    // «Снизить движение» хранится как раньше: он включён, когда выключены все разделы.
    private void SaveAnimationAreaSelection()
    {
        bool allOff = AnimationAreaBoxes().All(box => box.IsChecked != true);
        _settings.ReduceMotion = allOff;
        _settings.DisabledAnimationAreas = allOff
            ? []
            : AnimationAreas.Normalize(AnimationAreaBoxes().Where(box => box.IsChecked != true).Select(box => (string)box.Tag));
        RefreshAnimationsMasterCheckBox();
        _owner.ApplyAccessibilityPreferences();
    }

    private void RefreshAnimationsMasterCheckBox()
    {
        bool allOn = AnimationAreaBoxes().All(box => box.IsChecked == true);
        bool allOff = AnimationAreaBoxes().All(box => box.IsChecked != true);
        AnimationsMasterCheckBox.IsChecked = allOn ? true : allOff ? false : null;
    }

    private void InterfaceScaleSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Клик по дорожке обрабатывает сам Slider (IsMoveToPointEnabled); для Thumb блокируем только захват бегунка,
        // чтобы точечный клик менял масштаб без перетаскивания.
        if (e.OriginalSource is Thumb)
            e.Handled = true;
    }

    private void InterfaceScaleSlider_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && e.OriginalSource is Thumb)
            e.Handled = true;
    }

    private void InterfaceScaleSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.Slider slider && slider.IsMouseCaptured)
            slider.ReleaseMouseCapture();
    }

    private void InterfaceScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing || InterfaceScaleValueText is null) return;

        _settings.InterfaceScale = AccessibilityPreferences.NormalizeScale(e.NewValue / 100d);
        InterfaceScaleValueText.Text = $"{_settings.InterfaceScale * 100:0}%";

        // Масштаб WPF безопасно применяется при создании окон; перестройка открытых окон на лету может оставить мини-плеер
        // в смешанном DPI/layout (например, 100% → 135% → 100%), поэтому значение сохраняем и применяем после перезапуска.
        if (!_interfaceScaleRestartNoticeShown)
        {
            _interfaceScaleRestartNoticeShown = true;
            var restartDialog = new ScaleRestartDialog(_window, _settings);
            restartDialog.ShowDialog();
            // «Позже» относится только к текущему изменению. При следующем изменении
            // масштаба уведомление должно появиться снова.
            _interfaceScaleRestartNoticeShown = false;
        }
    }

    private void NavStyleRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.SettingsNavigationStyle = NavStylePillsRadio.IsChecked == true ? "Pills"
            : NavStyleFloatingRadio.IsChecked == true ? "FloatingBar"
            : "Sidebar";
        _window.ApplyNavigationStyleFromSettings();
        UpdateNavOptionsVisibility(animate: true);
    }

    private void NavShowLabelsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.SettingsNavigationShowLabels = NavShowLabelsCheckBox.IsChecked == true;
        _window.ApplyNavigationStyleFromSettings();
        UpdateNavOptionsVisibility(animate: true);
    }

    private void NavLabelModeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.SettingsNavigationLabelMode = NavLabelModeSelectedRadio.IsChecked == true ? "Selected" : "Below";
        _window.ApplyNavigationStyleFromSettings();
    }

    // Параметры названий относятся только к плавающей панели: блок появляется при её выборе, а выбор способа показа — когда
    // включён флажок «Показывать названия».
    private void UpdateNavOptionsVisibility(bool animate)
    {
        bool floating = _settings.SettingsNavigationStyle == "FloatingBar";
        SetNavOptionVisible(NavLabelOptionsPanel, floating, animate);
        SetNavOptionVisible(NavLabelModePanel, floating && _settings.SettingsNavigationShowLabels, animate);
    }

    private void SetNavOptionVisible(FrameworkElement element, bool show, bool animate)
    {
        bool wasHidden = element.Visibility != Visibility.Visible;
        element.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        if (show && wasHidden && animate && !AccessibilityPreferences.ShouldReduceMotion(_settings, AnimationAreas.SettingsWindow))
        {
            element.BeginAnimation(UIElement.OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = SettingsWindow.NavEasing });
        }
    }
}
