using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using static Lumisense.BackgroundTask;

namespace Lumisense;

// Создаётся при первом показе страницы (см. SettingsWindow.EnsurePageBuilt).
public sealed partial class LyricsSettingsPage : System.Windows.Controls.UserControl, ILazySettingsPage
{
    private readonly Window _window;
    private readonly AppSettings _settings;
    private readonly ISettingsHost _owner;

    // Глушит Changed-обработчики, пока значения читаются из настроек.
    private bool _isInitializing = true;
    private bool _isWindowClosed;

    internal LyricsSettingsPage(Window window, AppSettings settings, ISettingsHost owner)
    {
        _window = window;
        _settings = settings;
        _owner = owner;

        // StaticResource разбирается до того, как элементы попадут в дерево окна, поэтому ресурсы окна подключаются заранее.
        Resources.MergedDictionaries.Add(window.Resources);
        InitializeComponent();

        SyncedLyricsFontSizeSlider.Value = Math.Clamp(_settings.SyncedLyricsFontSize, 12, 20);
        SyncedLyricsFontSizeValueText.Text = $"{SyncedLyricsFontSizeSlider.Value:0} px";
        string lyricsEffect = _settings.SyncedLyricsHighlightEffect;
        SyncedLyricsEffectNoneRadio.IsChecked = lyricsEffect == "None";
        SyncedLyricsEffectGlowPillRadio.IsChecked = lyricsEffect == "GlowPill";
        SyncedLyricsEffectPillRadio.IsChecked = lyricsEffect == "Pill";
        SyncedLyricsEffectBarRadio.IsChecked = lyricsEffect == "Bar";
        // Старые значения Scale/GlowScale после обновления корректно воспринимаются как Glow.
        SyncedLyricsEffectGlowRadio.IsChecked = lyricsEffect is not ("None" or "GlowPill" or "Pill" or "Bar");
        SyncedLyricsScaleCheckBox.IsChecked = _settings.SyncedLyricsScaleActiveLine;
        SyncedLyricsFadeCheckBox.IsChecked = _settings.SyncedLyricsFadeDistantLines;
        NowPlayingLyricsFontSizeSlider.Value = Math.Clamp(_settings.NowPlayingLyricsFontSize,
            LyricsHighlightStyle.MinNowPlayingFontSize, LyricsHighlightStyle.MaxNowPlayingFontSize);
        NowPlayingLyricsFontSizeValueText.Text = $"{NowPlayingLyricsFontSizeSlider.Value:0} px";
        SyncedLyricsGlowStrengthSlider.Value = Math.Clamp(_settings.SyncedLyricsGlowStrength, 10, 100);
        SyncedLyricsBlurCheckBox.IsChecked = _settings.SyncedLyricsBlurInactive;
        SyncedLyricsBlurStrengthSlider.Value = Math.Clamp(_settings.SyncedLyricsBlurStrength,
            LyricsHighlightStyle.MinBlurStrength, LyricsHighlightStyle.MaxBlurStrength);
        SyncedLyricsFadeStrengthSlider.Value = Math.Clamp(_settings.SyncedLyricsFadeStrength, 0, 100);
        SyncedLyricsScalePercentSlider.Value = Math.Clamp(_settings.SyncedLyricsScalePercent, LyricsHighlightStyle.MinScalePercent, LyricsHighlightStyle.MaxScalePercent);
        int savedVisibleLines = LyricsHighlightStyle.NormalizeVisibleLines(_settings.SyncedLyricsVisibleLines);
        SyncedLyricsVisibleLinesSlider.Value = savedVisibleLines == 0 ? SyncedLyricsVisibleLinesSlider.Maximum : savedVisibleLines;
        UpdateSyncedLyricsHighlightTexts();
        SettingsWindow.SelectComboByTag(LyricsTransitionCombo, LyricsTransitionStyle.Normalize(_settings.SyncedLyricsTransition));
        RefreshLyricsPresetList();
        UseExtraLyricsSourceCheckBox.IsChecked = _settings.UseExtraLyricsSource;
        UseNetEaseLyricsCheckBox.IsChecked = _settings.UseNetEaseLyrics;
        UseQqMusicLyricsCheckBox.IsChecked = _settings.UseQqMusicLyrics;
        UseKugouLyricsCheckBox.IsChecked = _settings.UseKugouLyrics;
        UsePaxsenixLyricsCheckBox.IsChecked = _settings.UsePaxsenixLyrics;
        UseBetterLyricsCheckBox.IsChecked = _settings.UseBetterLyrics;
        UseBinimumLyricsCheckBox.IsChecked = _settings.UseBinimumLyrics;
        UseLyricsPlusLyricsCheckBox.IsChecked = _settings.UseLyricsPlusLyrics;
        ExtraLyricsSourcesPanel.IsEnabled = _settings.UseExtraLyricsSource;
        HideCoverInLyricsCheckBox.IsChecked = _settings.HideCoverInLyricsPanel;
        LyricsAlignCenterRadio.IsChecked = _settings.LyricsTextAlignment == "Center";
        LyricsAlignRightRadio.IsChecked = _settings.LyricsTextAlignment == "Right";
        LyricsAlignLeftRadio.IsChecked = !LyricsAlignCenterRadio.IsChecked.GetValueOrDefault()
                                         && !LyricsAlignRightRadio.IsChecked.GetValueOrDefault();
        LyricsDisplayPlainRadio.IsChecked = _settings.LyricsDisplayMode == "Plain";
        LyricsDisplaySyncedRadio.IsChecked = _settings.LyricsDisplayMode != "Plain";
        UpdateLyricsPreview();
        LyricsPolicyLocalOnlyRadio.IsChecked = _settings.LyricsSearchPolicy == "LocalOnly";
        LyricsPolicyManualOnlyRadio.IsChecked = _settings.LyricsSearchPolicy == "ManualOnly";
        LyricsPolicyAutoExactRadio.IsChecked = !LyricsPolicyLocalOnlyRadio.IsChecked.GetValueOrDefault() && !LyricsPolicyManualOnlyRadio.IsChecked.GetValueOrDefault();
        RefreshLyricsCacheInfo();

        // Выпадающий список источника читает отмеченные радиокнопки, поэтому связывается после загрузки настроек.
        RadioComboLinks.Initialize(this, FindName);
        _isInitializing = false;
    }

    public void MoveContentTo(System.Windows.Controls.Panel host) => LazySettingsPage.MoveChildren(PageLyrics, host);

    public FrameworkElement? FindElement(string name) => FindName(name) as FrameworkElement;

    public void RefreshLocalization() => RefreshLyricsCacheInfo();

    public void MarkWindowClosed() => _isWindowClosed = true;

    private void SyncedLyricsFontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Slider вызывает ValueChanged во время загрузки XAML, когда SyncedLyricsFontSizeValueText ещё может не быть в namescope;
        // до конца InitializeComponent ничего не трогаем.
        if (_isInitializing || SyncedLyricsFontSizeValueText is null) return;

        SyncedLyricsFontSizeValueText.Text = $"{e.NewValue:0} px";
        _settings.SyncedLyricsFontSize = Math.Clamp(Math.Round(e.NewValue), 12, 20);
        UpdateLyricsPreview();
        _owner.ApplySyncedLyricsAppearance();
    }

    private void LyricsPolicyRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.LyricsSearchPolicy = LyricsPolicyLocalOnlyRadio.IsChecked == true ? "LocalOnly"
            : LyricsPolicyManualOnlyRadio.IsChecked == true ? "ManualOnly"
            : "AutoExact";
    }

    private void LyricsTextAlignmentRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.LyricsTextAlignment = LyricsAlignCenterRadio.IsChecked == true ? "Center"
            : LyricsAlignRightRadio.IsChecked == true ? "Right"
            : "Left";
        UpdateLyricsPreview();
        _owner.ApplySyncedLyricsAppearance();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    // См. AppSettings.LyricsDisplayMode.
    private void LyricsDisplayModeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.LyricsDisplayMode = LyricsDisplayPlainRadio.IsChecked == true ? "Plain" : "Synced";
        UpdateLyricsPreview();
        _owner.RefreshLyricsDisplayMode();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    // Один общий предпросмотр в карточке «Текст песни» отражает размер, эффект, выравнивание и режим сразу.
    private void UpdateLyricsPreview()
    {
        LyricsPreview.Apply(_settings);
        MarkLyricsPresetCustom();
    }

    // ----- Пресеты оформления текста песни -----
    private IEnumerable<LyricsPreset> AllLyricsPresets() => LyricsPreset.BuiltIn.Concat(_settings.CustomLyricsPresets);

    private LyricsPreset? FindLyricsPreset(string? name) =>
        string.IsNullOrEmpty(name) ? null : AllLyricsPresets().FirstOrDefault(preset => string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase));

    private void RefreshLyricsPresetList()
    {
        bool wasInitializing = _isInitializing;
        _isInitializing = true;
        try
        {
            LyricsPresetCombo.Items.Clear();
            LyricsPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = LocalizationService.Translate("Свои настройки"), Tag = string.Empty });
            foreach (LyricsPreset preset in AllLyricsPresets())
                LyricsPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = LocalizationService.Translate(preset.Name), Tag = preset.Name });

            LyricsPreset? current = FindLyricsPreset(_settings.LyricsPresetName);
            SettingsWindow.SelectComboByTag(LyricsPresetCombo, current?.Name ?? string.Empty);
        }
        finally
        {
            _isInitializing = wasInitializing;
        }

        UpdateLyricsPresetButtons();
    }

    private void UpdateLyricsPresetButtons()
    {
        string selected = SettingsWindow.SelectedTag(LyricsPresetCombo, string.Empty);
        LyricsPresetDeleteButton.IsEnabled = selected.Length > 0 && !LyricsPreset.IsBuiltInName(selected);
    }

    // Если настройки после выбора пресета изменили вручную и они больше не совпадают с ним, выбранным становится «Свои настройки».
    private void MarkLyricsPresetCustom()
    {
        if (_isInitializing || string.IsNullOrEmpty(_settings.LyricsPresetName)) return;

        LyricsPreset? preset = FindLyricsPreset(_settings.LyricsPresetName);
        if (preset is not null && LyricsPreset.Capture(_settings, preset.Name).HasSameValues(preset)) return;

        _settings.LyricsPresetName = string.Empty;
        RefreshLyricsPresetList();
    }

    private void LyricsPresetCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        string name = SettingsWindow.SelectedTag(LyricsPresetCombo, string.Empty);
        LyricsPreset? preset = FindLyricsPreset(name);
        if (preset is null)
        {
            _settings.LyricsPresetName = string.Empty;
        }
        else
        {
            preset.ApplyTo(_settings);
            ReloadLyricsAppearanceControls();
            _owner.ApplySyncedLyricsAppearance();
            _owner.RefreshLyricsDisplayMode();
        }

        UpdateLyricsPresetButtons();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    // Подтягивает в элементы страницы значения, которые изменил пресет.
    private void ReloadLyricsAppearanceControls()
    {
        bool wasInitializing = _isInitializing;
        _isInitializing = true;
        try
        {
            SyncedLyricsFontSizeSlider.Value = Math.Clamp(_settings.SyncedLyricsFontSize, 12, 20);
            SyncedLyricsFontSizeValueText.Text = $"{SyncedLyricsFontSizeSlider.Value:0} px";
            NowPlayingLyricsFontSizeSlider.Value = Math.Clamp(_settings.NowPlayingLyricsFontSize,
                LyricsHighlightStyle.MinNowPlayingFontSize, LyricsHighlightStyle.MaxNowPlayingFontSize);
            NowPlayingLyricsFontSizeValueText.Text = $"{NowPlayingLyricsFontSizeSlider.Value:0} px";

            string effect = _settings.SyncedLyricsHighlightEffect;
            SyncedLyricsEffectNoneRadio.IsChecked = effect == "None";
            SyncedLyricsEffectGlowPillRadio.IsChecked = effect == "GlowPill";
            SyncedLyricsEffectPillRadio.IsChecked = effect == "Pill";
            SyncedLyricsEffectBarRadio.IsChecked = effect == "Bar";
            SyncedLyricsEffectGlowRadio.IsChecked = effect is not ("None" or "GlowPill" or "Pill" or "Bar");

            SyncedLyricsScaleCheckBox.IsChecked = _settings.SyncedLyricsScaleActiveLine;
            SyncedLyricsFadeCheckBox.IsChecked = _settings.SyncedLyricsFadeDistantLines;
            SyncedLyricsBlurCheckBox.IsChecked = _settings.SyncedLyricsBlurInactive;
            SyncedLyricsGlowStrengthSlider.Value = Math.Clamp(_settings.SyncedLyricsGlowStrength, 10, 100);
            SyncedLyricsBlurStrengthSlider.Value = Math.Clamp(_settings.SyncedLyricsBlurStrength,
                LyricsHighlightStyle.MinBlurStrength, LyricsHighlightStyle.MaxBlurStrength);
            SyncedLyricsFadeStrengthSlider.Value = Math.Clamp(_settings.SyncedLyricsFadeStrength, 0, 100);
            SyncedLyricsScalePercentSlider.Value = Math.Clamp(_settings.SyncedLyricsScalePercent,
                LyricsHighlightStyle.MinScalePercent, LyricsHighlightStyle.MaxScalePercent);
            int visibleLines = LyricsHighlightStyle.NormalizeVisibleLines(_settings.SyncedLyricsVisibleLines);
            SyncedLyricsVisibleLinesSlider.Value = visibleLines == 0 ? SyncedLyricsVisibleLinesSlider.Maximum : visibleLines;
            UpdateSyncedLyricsHighlightTexts();

            LyricsAlignCenterRadio.IsChecked = _settings.LyricsTextAlignment == "Center";
            LyricsAlignRightRadio.IsChecked = _settings.LyricsTextAlignment == "Right";
            LyricsAlignLeftRadio.IsChecked = _settings.LyricsTextAlignment is not ("Center" or "Right");
            LyricsDisplayPlainRadio.IsChecked = _settings.LyricsDisplayMode == "Plain";
            LyricsDisplaySyncedRadio.IsChecked = _settings.LyricsDisplayMode != "Plain";
            HideCoverInLyricsCheckBox.IsChecked = _settings.HideCoverInLyricsPanel;
            SettingsWindow.SelectComboByTag(LyricsTransitionCombo, LyricsTransitionStyle.Normalize(_settings.SyncedLyricsTransition));
            LyricsPreview.Apply(_settings);
        }
        finally
        {
            _isInitializing = wasInitializing;
        }
    }

    private void LyricsTransitionCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isInitializing || LyricsTransitionCombo.SelectedItem is null) return;

        _settings.SyncedLyricsTransition = LyricsTransitionStyle.Normalize(SettingsWindow.SelectedTag(LyricsTransitionCombo, LyricsTransitionStyle.Smooth));
        MarkLyricsPresetCustom();
        _owner.ApplySyncedLyricsAppearance();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void LyricsPresetSaveButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TextInputDialog("Сохранить пресет", "Название пресета:", _settings.LyricsPresetName, _settings) { Owner = _window };
        if (dialog.ShowDialog() != true) return;

        string name = dialog.ResultText.Trim();
        if (name.Length == 0) return;
        if (name.Length > 40) name = name[..40];
        if (LyricsPreset.IsBuiltInName(name))
        {
            // Встроенные пресеты не перезаписываются: сохраняем рядом под другим именем.
            name += " 2";
        }

        _settings.CustomLyricsPresets.RemoveAll(preset => string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase));
        _settings.CustomLyricsPresets.Add(LyricsPreset.Capture(_settings, name));
        _settings.LyricsPresetName = name;
        RefreshLyricsPresetList();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void LyricsPresetDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        string name = SettingsWindow.SelectedTag(LyricsPresetCombo, string.Empty);
        if (name.Length == 0 || LyricsPreset.IsBuiltInName(name)) return;

        _settings.CustomLyricsPresets.RemoveAll(preset => string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase));
        _settings.LyricsPresetName = string.Empty;
        RefreshLyricsPresetList();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void UseExtraLyricsSourceCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.UseExtraLyricsSource = UseExtraLyricsSourceCheckBox.IsChecked == true;
        ExtraLyricsSourcesPanel.IsEnabled = _settings.UseExtraLyricsSource;
        LyricsService.ApplyExtraSourceSettings(_settings);
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void ExtraLyricsServiceCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.UseNetEaseLyrics = UseNetEaseLyricsCheckBox.IsChecked == true;
        _settings.UseQqMusicLyrics = UseQqMusicLyricsCheckBox.IsChecked == true;
        _settings.UseKugouLyrics = UseKugouLyricsCheckBox.IsChecked == true;
        _settings.UsePaxsenixLyrics = UsePaxsenixLyricsCheckBox.IsChecked == true;
        _settings.UseBetterLyrics = UseBetterLyricsCheckBox.IsChecked == true;
        _settings.UseBinimumLyrics = UseBinimumLyricsCheckBox.IsChecked == true;
        _settings.UseLyricsPlusLyrics = UseLyricsPlusLyricsCheckBox.IsChecked == true;
        LyricsService.ApplyExtraSourceSettings(_settings);
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void HideCoverInLyricsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.HideCoverInLyricsPanel = HideCoverInLyricsCheckBox.IsChecked == true;
        MarkLyricsPresetCustom();
        _owner.ApplySyncedLyricsAppearance();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void SyncedLyricsEffectRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.SyncedLyricsHighlightEffect =
            SyncedLyricsEffectNoneRadio.IsChecked == true ? "None"
            : SyncedLyricsEffectGlowPillRadio.IsChecked == true ? "GlowPill"
            : SyncedLyricsEffectPillRadio.IsChecked == true ? "Pill"
            : SyncedLyricsEffectBarRadio.IsChecked == true ? "Bar"
            : "Glow";
        _settings.SyncedLyricsScaleActiveLine = SyncedLyricsScaleCheckBox.IsChecked == true;
        _settings.SyncedLyricsFadeDistantLines = SyncedLyricsFadeCheckBox.IsChecked == true;
        _settings.SyncedLyricsBlurInactive = SyncedLyricsBlurCheckBox.IsChecked == true;
        UpdateSyncedLyricsHighlightTexts();
        UpdateLyricsPreview();
        _owner.ApplySyncedLyricsAppearance();
    }

    private void NowPlayingLyricsFontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing || NowPlayingLyricsFontSizeValueText is null) return;

        NowPlayingLyricsFontSizeValueText.Text = $"{e.NewValue:0} px";
        _settings.NowPlayingLyricsFontSize = (int)Math.Clamp(Math.Round(e.NewValue),
            LyricsHighlightStyle.MinNowPlayingFontSize, LyricsHighlightStyle.MaxNowPlayingFontSize);
        MarkLyricsPresetCustom();
        _owner.ApplySyncedLyricsAppearance();
    }

    private void SyncedLyricsHighlightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // ValueChanged приходит во время загрузки XAML, пока часть элементов ещё не создана.
        if (_isInitializing || SyncedLyricsVisibleLinesValueText is null || SyncedLyricsScalePercentValueText is null ||
            SyncedLyricsFadeStrengthValueText is null || SyncedLyricsFadeStrengthSlider is null ||
            SyncedLyricsScalePercentSlider is null || SyncedLyricsVisibleLinesSlider is null ||
            SyncedLyricsBlurStrengthSlider is null || SyncedLyricsBlurStrengthValueText is null ||
            SyncedLyricsGlowStrengthSlider is null || SyncedLyricsGlowStrengthValueText is null)
            return;

        _settings.SyncedLyricsGlowStrength = (int)Math.Clamp(Math.Round(SyncedLyricsGlowStrengthSlider.Value), 0, 100);

        _settings.SyncedLyricsBlurStrength = (int)Math.Clamp(Math.Round(SyncedLyricsBlurStrengthSlider.Value),
            LyricsHighlightStyle.MinBlurStrength, LyricsHighlightStyle.MaxBlurStrength);

        _settings.SyncedLyricsFadeStrength = (int)Math.Clamp(Math.Round(SyncedLyricsFadeStrengthSlider.Value), 0, 100);
        _settings.SyncedLyricsScalePercent = (int)Math.Clamp(Math.Round(SyncedLyricsScalePercentSlider.Value),
            LyricsHighlightStyle.MinScalePercent, LyricsHighlightStyle.MaxScalePercent);
        // Крайнее правое положение ползунка — «Все строки», в настройках это 0.
        int lines = (int)Math.Round(SyncedLyricsVisibleLinesSlider.Value);
        _settings.SyncedLyricsVisibleLines = lines >= SyncedLyricsVisibleLinesSlider.Maximum ? 0 : LyricsHighlightStyle.NormalizeVisibleLines(lines);
        UpdateSyncedLyricsHighlightTexts();
        UpdateLyricsPreview();
        _owner.ApplySyncedLyricsAppearance();
    }

    private void UpdateSyncedLyricsHighlightTexts()
    {
        SyncedLyricsFadeStrengthValueText.Text = $"{SyncedLyricsFadeStrengthSlider.Value:0}%";
        SyncedLyricsGlowStrengthValueText.Text = $"{SyncedLyricsGlowStrengthSlider.Value:0}%";
        // Ползунок имеет смысл только у стилей со свечением.
        SyncedLyricsGlowStrengthSlider.IsEnabled = LyricsHighlightStyle.HasGlow(_settings.SyncedLyricsHighlightEffect);
        SyncedLyricsBlurStrengthValueText.Text = $"{SyncedLyricsBlurStrengthSlider.Value:0} px";
        SyncedLyricsBlurStrengthSlider.IsEnabled = SyncedLyricsBlurCheckBox.IsChecked == true;
        SyncedLyricsScalePercentValueText.Text = $"+{SyncedLyricsScalePercentSlider.Value:0}%";
        SyncedLyricsVisibleLinesValueText.Text = SyncedLyricsVisibleLinesSlider.Value >= SyncedLyricsVisibleLinesSlider.Maximum
            ? LocalizationService.Translate("Все")
            : $"{SyncedLyricsVisibleLinesSlider.Value:0}";
        // Ползунки степени имеют смысл только при включённом соответствующем приёме.
        SyncedLyricsFadeStrengthSlider.IsEnabled = SyncedLyricsFadeCheckBox.IsChecked == true;
        SyncedLyricsScalePercentSlider.IsEnabled = SyncedLyricsScaleCheckBox.IsChecked == true;
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

        var confirm = LocalizedMessageBox.Show(_window,
            LocalizationService.Get(LocalizationKey.ProfileLyricsCacheClearConfirm),
            LocalizationService.Get(LocalizationKey.ProfileLyricsCacheClearTitle), System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        if (!LyricsService.ClearPastedLyricsCache())
        {
            LocalizedMessageBox.Show(_window, LocalizationService.Get(LocalizationKey.ProfileLyricsCacheClearFailed),
                LocalizationService.Get(LocalizationKey.ProfileLyricsCacheClearErrorTitle), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }

        RefreshLyricsCacheInfo();
    }
}
