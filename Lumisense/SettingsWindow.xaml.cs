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

public partial class SettingsWindow : FluentWindow
{
    private enum HotkeyTarget { None, PlayPause, Next, Previous, Stop, VolumeUp, VolumeDown, Mute, Shuffle, Repeat, ToggleFavorite, ToggleLyrics, ToggleMiniPlayer, DeleteTrack, SeekForward, SeekBackward }

    private readonly AppSettings _settings;
    private readonly ISettingsHost _owner;
    private bool _isInitializing = true;
    private bool _isSyncingIslandButtons;
    private bool _interfaceScaleRestartNoticeShown;
    private bool _isRefreshingOutputDevices;
    private bool _isRefreshingWasapiMode;
    private CancellationTokenSource? _sourceProbeCts;
    private IReadOnlyList<UpdateSourceProbeResult>? _sourceProbeResults;
    private CancellationTokenSource? _basePackageCts;
    private VelopackBasePackagePlan? _basePackagePlan;
    private bool _isVelopackManagedInstall;
    private string? _basePackageError;
    private string? _lastCheckedReleaseAssetUrl;
    private bool _isLegacyInnoCleanupRunning;
    private string? _legacyInnoCleanupStatusKey;

    // См. LoadDeveloperAvatar — держит BitmapImage живым на время асинхронной загрузки, чтобы
    // его не собрал GC до того, как скачивание завершится.
    private BitmapImage? _developerAvatarBitmap;
    private bool _developerAvatarRequested;

    // Пока не None — окно "слушает" следующее нажатие клавиш и запишет его как новую комбинацию
    private HotkeyTarget _recordingTarget = HotkeyTarget.None;

    // Индекс поиска — ручной перечень опций: подпись, страница, ссылка на элемент (для прокрутки и подсветки)
    // и ключевые слова; разметка не читается.
    private sealed record SettingsSearchEntry(string Label, string PageTitle, string PageKey, string Keywords, FrameworkElement Target)
    {
        // Результаты строятся при вводе запроса, уже после прохода локализации, поэтому подписи переводятся здесь.
        public string DisplayLabel => LocalizationService.Translate(Label);
        public string DisplayPageTitle => LocalizationService.Translate(PageTitle);
    }

    private readonly List<SettingsSearchEntry> _searchIndex = new();
    private readonly ObservableCollection<SettingsSearchEntry> _searchResults = new();

    // Переключает страницу по ключу: при первом открытии и при повторном открытии уже висящего окна
    // (например, "Настройки" из меню мини-плеера ведут на "Мини-плеер", см. MainWindow.ShowSettingsWindow).
    public void NavigateToPage(string? pageKey)
    {
        (pageKey switch
        {
            "About" => NavAbout,
            "Updates" => NavUpdates,
            "Window" => NavWindow,
            "Playback" => NavPlayback,
            "Progress" => NavProgress,
            "Library" => NavLibrary,
            "Cover" => NavCover,
            "Lyrics" => NavLyrics,
            "Integrations" => NavIntegrations,
            "Notifications" => NavNotifications,
            "Equalizer" => NavEqualizer,
            "MiniPlayer" => NavMiniPlayer,
            "Island" => NavIsland,
            "Hotkeys" => NavHotkeys,
            "Profile" => NavProfile,
            _ => NavAppearance
        }).IsChecked = true;
    }

    // Своя копия MainWindow.ApplyWindowBackdrop: применяется к собственному HWND этого окна.
    private void ApplyWindowBackdrop(AppSettings settings, bool forceReapply = false)
    {
        var desiredBackdrop = WindowBackdropOptions.ToWpfUi(settings.WindowBackdropType);
        bool blur = WindowBackdropOptions.IsBlur(settings.WindowBackdropType);

        if (!blur && DwmBlurBackdrop.IsApplied(this))
        {
            DwmBlurBackdrop.Clear(this);
            forceReapply = true;
        }

        // См. MainWindow.ApplyWindowBackdrop: при смене темы WPF-UI может оставить Acrylic в свойстве, но наложить Mica;
        // None → desired заставляет FluentWindow заново применить нужный backdrop.
        if (forceReapply && WindowBackdropType == desiredBackdrop)
            WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;

        WindowBackdropType = desiredBackdrop;
        DwmBlurBackdrop.Sync(this, blur);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyWindowBackdrop(_settings);
    }

    // Замер открытия окна: в журнал одной строкой попадает, сколько длились разбор XAML, заполнение страниц и первая отрисовка.
    private readonly System.Diagnostics.Stopwatch _openClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly List<string> _openMarks = new();
    private long _openLastMark;

    private void MarkOpen(string name)
    {
        long now = _openClock.ElapsedMilliseconds;
        _openMarks.Add($"{name}={now - _openLastMark}ms");
        _openLastMark = now;
    }

    internal SettingsWindow(AppSettings settings, ISettingsHost owner, string? initialPage = null)
    {
        InitializeComponent();
        MarkOpen("initialize-component");
        InitializeRadioCombos();
        InitializeNavScroll();
        WindowSizing.FitToWorkArea(this);
        // Class handlers получают событие даже если дочерний контрол пометил MouseDown
        // обработанным. Это важно для клика по тексту, Slider и другим элементам страницы.
        AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(SettingsWindow_PreviewMouseDown), true);
        AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(SettingsWindow_PreviewMouseMove), true);
        AddHandler(UIElement.LostMouseCaptureEvent, new MouseEventHandler(SettingsRoot_LostMouseCapture), true);

        ApplyWindowBackdrop(settings);

        // Стартовую страницу выбираем здесь, а не IsChecked="True" в XAML: все страницы уже созданы, и NavItem_Checked
        // не падает с NullReferenceException.
        NavigateToPage(initialPage);

        _settings = settings;
        _owner = owner;
        AccessibilityPreferences.ApplyToWindow(this, _settings);

        // Owner намеренно не выставляется: Windows не позволяет owner оказаться выше owned-окна, и клик по главному окну
        // не поднимал бы его над настройками. Позиция — RestoreOrCenterPosition, закрытие — MainWindow.OnClosed.
        ShowInTaskbar = true;
        TaskbarWindowIdentity.AssignWhenSourceReady(this, TaskbarWindowIdentity.Settings);
        RestoreOrCenterPosition(owner);

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

        foreach (var (menu, panel) in ContextMenuOrderPanels())
            PanelReorder.Sort(panel, ContextMenuOrdering.Effective(_settings, menu));

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
        SelectComboByTag(LyricsTransitionCombo, LyricsTransitionStyle.Normalize(_settings.SyncedLyricsTransition));
        RefreshLyricsPresetList();
        NowPlayingBackgroundOrbsRadio.IsChecked = _settings.NowPlayingBackground == "Orbs";
        NowPlayingBackgroundWavesRadio.IsChecked = _settings.NowPlayingBackground == "Waves";
        NowPlayingBackgroundCoverRadio.IsChecked = _settings.NowPlayingBackground == "Cover";
        NowPlayingBackgroundSolidRadio.IsChecked = _settings.NowPlayingBackground == "Solid";
        NowPlayingBackgroundNoneRadio.IsChecked = _settings.NowPlayingBackground == "None";
        NowPlayingBackgroundCloudsRadio.IsChecked = _settings.NowPlayingBackground is not ("Orbs" or "Waves" or "Cover" or "Solid" or "None");
        NowPlayingBackgroundColorPanel.IsEnabled = _settings.NowPlayingBackground == "Solid";
        RefreshNowPlayingBackgroundColorSwatch();
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
        FavoriteHeartSparksRadio.IsChecked = _settings.FavoriteHeartAnimation == "Sparks";
        FavoriteHeartRingRadio.IsChecked = _settings.FavoriteHeartAnimation == "Ring";
        FavoriteHeartNoneRadio.IsChecked = _settings.FavoriteHeartAnimation == "None";
        FavoriteHeartFillRadio.IsChecked = !FavoriteHeartSparksRadio.IsChecked.GetValueOrDefault()
                                           && !FavoriteHeartRingRadio.IsChecked.GetValueOrDefault()
                                           && !FavoriteHeartNoneRadio.IsChecked.GetValueOrDefault();
        LyricsAlignCenterRadio.IsChecked = _settings.LyricsTextAlignment == "Center";
        LyricsAlignRightRadio.IsChecked = _settings.LyricsTextAlignment == "Right";
        LyricsAlignLeftRadio.IsChecked = !LyricsAlignCenterRadio.IsChecked.GetValueOrDefault()
                                         && !LyricsAlignRightRadio.IsChecked.GetValueOrDefault();
        LyricsDisplayPlainRadio.IsChecked = _settings.LyricsDisplayMode == "Plain";
        LyricsDisplaySyncedRadio.IsChecked = _settings.LyricsDisplayMode != "Plain";
        UpdateLyricsPreview();
        UpdateBadgePreview();
        InitializeProgressAppearanceEditor();
        AllVersionsLoadingIndicator.IsAnimationEnabled = !AccessibilityPreferences.ShouldReduceMotion(_settings, AnimationAreas.Loading);
        LyricsPolicyLocalOnlyRadio.IsChecked = _settings.LyricsSearchPolicy == "LocalOnly";
        LyricsPolicyManualOnlyRadio.IsChecked = _settings.LyricsSearchPolicy == "ManualOnly";
        LyricsPolicyAutoExactRadio.IsChecked = !LyricsPolicyLocalOnlyRadio.IsChecked.GetValueOrDefault() && !LyricsPolicyManualOnlyRadio.IsChecked.GetValueOrDefault();

        AlwaysOnTopCheckBox.IsChecked = _settings.AlwaysOnTop;
        RememberVolumeCheckBox.IsChecked = _settings.RememberVolume;
        LogarithmicVolumeCheckBox.IsChecked = _settings.UseLogarithmicVolume;
        InitializeOutputDeviceCombo();
        InitializeWasapiModeCombo();
        InitializeWasapiLatencyCombo();
        RaisePlaybackPriorityCheckBox.IsChecked = _settings.RaisePlaybackPriority;
        TrackLoadTraceCheckBox.IsChecked = _settings.TrackLoadTraceEnabled;
        NeverAutoPlayLastTrackOnStartupCheckBox.IsChecked = _settings.NeverAutoPlayLastTrackOnStartup;
        TrackChangeToastCheckBox.IsChecked = _settings.ShowTrackChangeToast;
        InitializeToastPolicy();
        InitializeToastPositionAndSize();
        InitializeToastMonitorCombo();
        MiniStyleIslandRadio.IsChecked = _settings.MiniPlayerStyle == "Island";
        MiniStyleClassicRadio.IsChecked = !MiniStyleIslandRadio.IsChecked.GetValueOrDefault();
        IslandInactiveNote.Visibility = MiniStyleIslandRadio.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        InitializeIslandMonitorCombo();
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
        SelectComboByTag(IslandLeftButtonCombo, _settings.IslandLeftButton);
        SelectComboByTag(IslandRightButtonCombo, _settings.IslandRightButton);
        SelectComboByTag(IslandEqualizerStyleCombo, _settings.IslandEqualizerStyle);
        SelectComboByTag(IslandShapeCombo, _settings.IslandShape);
        SelectComboByTag(IslandBackdropCombo, _settings.IslandBackdrop);
        SelectComboByTag(IslandSeekStyleCombo, _settings.IslandSeekStyle);
        SelectComboByTag(IslandRingSideCombo, _settings.IslandRingSide);
        IslandLyricsShimmerCheckBox.IsChecked = _settings.IslandLyricsShimmer;
        IslandWordRollCheckBox.IsChecked = _settings.IslandWordRoll;
        IslandLyricsWordRollCheckBox.IsChecked = _settings.IslandLyricsWordRoll;
        IslandReadAlongCheckBox.IsChecked = _settings.IslandReadAlong;
        IslandHideSystemToastCheckBox.IsChecked = _settings.IslandHideSystemToast;
        foreach (var box in IslandMenuActionBoxes())
            box.IsChecked = !_settings.DisabledIslandMenuActions.Contains((string)box.Tag);
        MinimizeToTrayCheckBox.IsChecked = _settings.MinimizeToTrayOnClose;

        // Источник истины для автозапуска — сам реестр (см. StartupManager), а не settings.json —
        // чекбокс всегда показывает то, что реально настроено, а не могло устареть.
        LaunchOnStartupCheckBox.IsChecked = StartupManager.IsEnabled();
        StartHiddenInTrayCheckBox.IsChecked = _settings.StartHiddenInTray;

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

        FileNameNormalizationTemplateTextBox.Text = string.IsNullOrWhiteSpace(_settings.FileNameNormalizationTemplate)
            ? FileNameNormalizer.DefaultTemplate
            : _settings.FileNameNormalizationTemplate;
        FileNameNormalizationResultText.Visibility = Visibility.Collapsed;
        InitializeTrackContextMenuActionCheckBoxes();
        InitializeMiniPlayerContextMenuActionCheckBoxes();
        LoadShuffleModeControls();
        SaveQueueBetweenRestartsCheckBox.IsChecked = _settings.SaveQueueBetweenRestarts;
        AutoRefreshPlaylistFoldersCheckBox.IsChecked = _settings.AutoRefreshPlaylistFolders;
        MiniArtworkProgressStyleMaterialRadio.IsChecked = _settings.MiniPlayerArtworkProgressStyle == "Material";
        MiniArtworkProgressStyleMaterialSliderRadio.IsChecked = _settings.MiniPlayerArtworkProgressStyle == "MaterialSlider";
        MiniArtworkProgressStyleDefaultRadio.IsChecked = _settings.MiniPlayerArtworkProgressStyle is not ("Material" or "MaterialSlider");
        SliderStyleMaterialRadio.IsChecked = _settings.SliderStyle == "Material";
        SliderStyleDefaultRadio.IsChecked = _settings.SliderStyle != "Material";
        ReplayGainCheckBox.IsChecked = _settings.ReplayGainEnabled;
        ReplayGainAlbumCheckBox.IsChecked = _settings.ReplayGainAlbumMode;
        LoudnessScanCheckBox.IsChecked = _settings.LoudnessScanEnabled;
        LimiterCheckBox.IsChecked = _settings.LimiterEnabled;
        PlaybackSpeedChangesPitchCheckBox.IsChecked = _settings.PlaybackSpeedChangesPitch;
        ReplayGainPreampSlider.Value = Math.Clamp(_settings.ReplayGainPreampDb, -12, 12);
        UpdateReplayGainPreampText();
        ChannelModeMonoRadio.IsChecked = _settings.PlaybackChannelMode == "Mono";
        ChannelModeStereoRadio.IsChecked = _settings.PlaybackChannelMode != "Mono";
        PlaybackBalanceSlider.Value = Math.Clamp(_settings.PlaybackBalance, -100, 100);
        DeviceConnectStyleCombo.SelectedItem = DeviceConnectStyleCombo.Items.Cast<System.Windows.Controls.ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, _settings.DeviceConnectStyle, StringComparison.Ordinal))
            ?? DeviceConnectStyleCombo.Items[0];
        DeviceCardAnimationCombo.SelectedItem = DeviceCardAnimationCombo.Items.Cast<System.Windows.Controls.ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, _settings.DeviceCardAnimation, StringComparison.Ordinal))
            ?? DeviceCardAnimationCombo.Items[0];
        RefreshDeviceKindControls();
        UpdatePlaybackBalanceText();
        StereoWidthSlider.Value = Math.Clamp(_settings.StereoWidthPercent, 0, 200);
        CrossfeedSlider.Value = Math.Clamp(_settings.CrossfeedPercent, 0, 100);
        LoudnessCompensationSlider.Value = Math.Clamp(_settings.LoudnessCompensationPercent, 0, 100);
        UpdateStereoImageTexts();
        GaplessCheckBox.IsChecked = _settings.GaplessEnabled;
        CrossfadeSlider.Value = Math.Clamp(_settings.CrossfadeSeconds, 0, 12);
        CrossfadeSkipSameAlbumCheckBox.IsChecked = _settings.CrossfadeSkipSameAlbum;
        UpdateCrossfadeText();
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
        DiscordRichPresenceEnabledCheckBox.IsChecked = _settings.DiscordRichPresenceEnabled;
        DiscordRichPresenceShowTrackInfoCheckBox.IsChecked = _settings.DiscordRichPresenceShowTrackInfo;
        DiscordRichPresenceShowTimelineCheckBox.IsChecked = _settings.DiscordRichPresenceShowTimeline;
        DiscordRichPresenceShowCoverArtCheckBox.IsChecked = _settings.DiscordRichPresenceShowCoverArt;
        DiscordRichPresenceShowButtonsCheckBox.IsChecked = _settings.DiscordRichPresenceShowButtons;
        DiscordRichPresenceShowDownloadButtonCheckBox.IsChecked = _settings.DiscordRichPresenceShowDownloadButton;
        UpdateDiscordRichPresenceConnectionStatus();
        bool carousel = _owner.IsAlbumArtTransitionEnabled && _settings.AlbumArtTransitionStyle == "Carousel";
        AlbumArtTransitionStyleCarouselRadio.IsChecked = carousel;
        AlbumArtTransitionOnRadio.IsChecked = _owner.IsAlbumArtTransitionEnabled && !carousel;
        AlbumArtTransitionOffRadio.IsChecked = !_owner.IsAlbumArtTransitionEnabled;
        AlbumArtGesturesCheckBox.IsChecked = _settings.AlbumArtGesturesEnabled;

        EqualizerEnabledCheckBox.IsChecked = _owner.IsEqualizerEnabled;
        EqualizerAutoHeadroomCheckBox.IsChecked = _owner.IsEqualizerAutoHeadroom;
        DeviceSoundProfilesCheckBox.IsChecked = _owner.IsDeviceSoundProfilesEnabled;
        for (int band = 0; band < EqualizerSampleProvider.BandFrequencies.Length; band++)
        {
            double gain = _owner.GetEqualizerBandGain(band);
            GetEqBandSlider(band).Value = gain;
            GetEqBandValueText(band).Text = FormatEqGain(gain);
        }
        RefreshEqualizerPresetsList();

        // Velopack берёт пакеты из своего feed на GitHub и зеркала не использует: выбор источника там ни на что не влиял бы.
        if (UpdateMigrationGuard.IsVelopackManagedInstall())
        {
            UpdateSourceContentPanel.IsEnabled = false;
            UpdateSourceVelopackHint.Text = LocalizationService.Translate("В этой установке обновления загружаются напрямую с GitHub; зеркала действуют только для установщика EXE/MSI.");
            UpdateSourceVelopackHint.Visibility = Visibility.Visible;
        }

        (_settings.UpdateDownloadSource switch
        {
            "GhProxy" => UpdateSourceGhProxyRadio,
            "GhProxyV4" => UpdateSourceGhProxyV4Radio,
            "GhProxyV6" => UpdateSourceGhProxyV6Radio,
            "GhProxyCdn" => UpdateSourceGhProxyCdnRadio,
            "GhProxyCom" => UpdateSourceGhProxyComRadio,
            "GhFast" => UpdateSourceGhFastRadio,
            _ => UpdateSourceGitHubRadio
        }).IsChecked = true;

        RefreshViewModeRadios();
        MainWindowLayoutModernRadio.IsChecked = _settings.MainWindowLayout == "Modern";
        MainWindowLayoutGlassRadio.IsChecked = _settings.MainWindowLayout == "Glass";
        MainWindowLayoutVinylRadio.IsChecked = _settings.MainWindowLayout == "Vinyl";
        MainWindowLayoutCompactRadio.IsChecked = _settings.MainWindowLayout == "Compact";
        MainWindowLayoutPosterRadio.IsChecked = _settings.MainWindowLayout == "Poster";
        ModernShowSideButtonsCheckBox.IsChecked = _settings.ModernShowSideButtons;
        GlassControlsBackgroundCheckBox.IsChecked = _settings.GlassControlsBackground;
        ControlButtonsBackgroundCheckBox.IsChecked = _settings.ControlButtonsBackground;
        PlayButtonAccentRadio.IsChecked = _settings.PlayButtonAccentBackground;
        PlayButtonPlainRadio.IsChecked = !_settings.PlayButtonAccentBackground;
        PlayButtonSquareRadio.IsChecked = !_settings.PlayButtonRound;
        PlayButtonRoundRadio.IsChecked = _settings.PlayButtonRound;
        MoreMenuSpringRadio.IsChecked = _settings.MoreMenuSpring;
        MoreMenuClassicRadio.IsChecked = !_settings.MoreMenuSpring;
        SelectComboByTag(CompactCoverAlignmentCombo, _settings.CompactCoverAlignment);
        ModernSlotsGrid.IsEnabled = _settings.ModernShowSideButtons;
        SelectModernSlotCombos(MainWindow.NormalizeModernSlots(_settings.ModernControlSlots));

        RefreshHotkeyButtonText(HotkeyTarget.PlayPause);
        RefreshHotkeyButtonText(HotkeyTarget.Next);
        RefreshHotkeyButtonText(HotkeyTarget.Previous);
        RefreshHotkeyButtonText(HotkeyTarget.Stop);
        RefreshHotkeyButtonText(HotkeyTarget.VolumeUp);
        RefreshHotkeyButtonText(HotkeyTarget.VolumeDown);
        RefreshHotkeyButtonText(HotkeyTarget.Mute);
        RefreshHotkeyButtonText(HotkeyTarget.Shuffle);
        RefreshHotkeyButtonText(HotkeyTarget.Repeat);
        RefreshHotkeyButtonText(HotkeyTarget.ToggleFavorite);
        RefreshHotkeyButtonText(HotkeyTarget.ToggleLyrics);
        RefreshHotkeyButtonText(HotkeyTarget.ToggleMiniPlayer);
        RefreshHotkeyButtonText(HotkeyTarget.DeleteTrack);
        RefreshHotkeyButtonText(HotkeyTarget.SeekForward);
        RefreshHotkeyButtonText(HotkeyTarget.SeekBackward);

        SearchResultsList.ItemsSource = _searchResults;
        foreach (var (menu, panel) in ContextMenuOrderPanels())
        {
            string menuKey = menu;
            PanelReorder.Enable(panel, ids => ContextMenuOrderChanged(menuKey, ids));
        }

        BuildSearchIndex();

        RefreshAppVersionText();
        RefreshLyricsCacheInfo();
        RefreshResetRecoveryButton();
        RefreshUpdateSourceProbePresentation();
        RefreshVelopackBasePackagePresentation();
        RefreshLegacyInnoCleanupPresentation();

        NavShowLabelsCheckBox.IsChecked = _settings.SettingsNavigationShowLabels;
        NavLabelModeSelectedRadio.IsChecked = _settings.SettingsNavigationLabelMode == "Selected";
        NavLabelModeBelowRadio.IsChecked = _settings.SettingsNavigationLabelMode != "Selected";
        NavStylePillsRadio.IsChecked = _settings.SettingsNavigationStyle == "Pills";
        NavStyleFloatingRadio.IsChecked = _settings.SettingsNavigationStyle == "FloatingBar";
        NavStyleSidebarRadio.IsChecked = !NavStylePillsRadio.IsChecked.GetValueOrDefault()
                                         && !NavStyleFloatingRadio.IsChecked.GetValueOrDefault();
        ApplyNavigationStyle(_settings.SettingsNavigationStyle);
        UpdateNavOptionsVisibility(animate: false);
        SizeChanged += (_, _) => UpdateFloatingBarMaxWidth();
        Loaded += (_, _) => StartPageWarmUp();

        _isInitializing = false;
        MarkOpen("fill-controls-and-search");
        ContentRendered += (_, _) =>
        {
            MarkOpen("first-render");
            Logger.Info($"TRACE settings-open: {string.Join(", ", _openMarks)}; всего {_openClock.ElapsedMilliseconds}ms");
        };
        LocalizationService.LanguageChanged += LocalizationService_LanguageChanged;
        Loaded += async (_, _) =>
        {
            await RefreshVelopackBasePackagePlanAsync();
            RefreshLegacyInnoCleanupPresentation();
        };
        Closed += (_, _) =>
        {
            _isWindowClosed = true;
            StopAutoScrolling();
            _sourceProbeCts?.Cancel();
            _basePackageCts?.Cancel();
            LocalizationService.LanguageChanged -= LocalizationService_LanguageChanged;
        };
        LocalizationService.Apply(this);
    }

    private void InitializeTrackContextMenuActionCheckBoxes()
    {
        var checkBoxes = new[]
        {
            TrackContextPlayNextCheckBox,
            TrackContextAddToQueueCheckBox,
            TrackContextFindFileCheckBox,
            TrackContextFavoriteCheckBox,
            TrackContextShowInExplorerCheckBox,
            TrackContextCopyNameCheckBox,
            TrackContextCopyPathCheckBox,
            TrackContextCopyFileCheckBox,
            TrackContextExportProcessedCopyCheckBox,
            TrackContextPropertiesCheckBox,
            TrackContextEditTagsCheckBox,
            TrackContextNormalizeFileNameCheckBox,
            TrackContextRemoveFromPlaylistCheckBox,
            TrackContextDeleteFromDiskCheckBox
        };

        foreach (System.Windows.Controls.CheckBox checkBox in checkBoxes)
        {
            if (checkBox.Tag is string actionId)
                checkBox.IsChecked = !_owner.IsTrackContextMenuActionDisabled(actionId);
        }
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

    // CenterOwner не подходит (Owner не выставляется), центрируем вручную; двигавшееся окно открываем на прежнем месте
    // (SettingsWindowLeft/Top). Вместе с ShowInTaskbar это чинит окно, унесённое отключённым монитором за экран.
    private void RestoreOrCenterPosition(ISettingsHost owner)
    {
        if (_settings.SettingsWindowLeft is double savedLeft && _settings.SettingsWindowTop is double savedTop
            && IsPositionOnAnyScreen(savedLeft, savedTop))
        {
            SetPositionProgrammatically(savedLeft, savedTop);
            return;
        }

        double ownerWidth = owner.ActualWidth > 0 ? owner.ActualWidth : owner.Width;
        double ownerHeight = owner.ActualHeight > 0 ? owner.ActualHeight : owner.Height;

        double left = owner.Left + (ownerWidth - Width) / 2;
        double top = owner.Top + (ownerHeight - Height) / 2;

        var ownerBounds = new System.Drawing.Rectangle(
            (int)owner.Left, (int)owner.Top,
            (int)Math.Max(ownerWidth, 1), (int)Math.Max(ownerHeight, 1));
        var workArea = System.Windows.Forms.Screen.FromRectangle(ownerBounds).WorkingArea;

        left = Math.Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
        top = Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));

        SetPositionProgrammatically(left, top);
    }

    // Сохранённая позиция может не попадать ни на один подключённый монитор — проверяем пересечение с рабочей областью
    // любого из них, а не ближайший Screen.FromRectangle.
    private bool IsPositionOnAnyScreen(double left, double top)
    {
        var bounds = new System.Drawing.Rectangle((int)left, (int)top,
            (int)Math.Max(Width, 1), (int)Math.Max(Height, 1));
        return System.Windows.Forms.Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(bounds));
    }

    // Left/Top правятся кодом, а не пользователем: в настройки (OnLocationChanged) пишем только реальное перетаскивание.
    private bool _isApplyingProgrammaticPosition;

    private void SetPositionProgrammatically(double left, double top)
    {
        _isApplyingProgrammaticPosition = true;
        try
        {
            Left = left;
            Top = top;
        }
        finally
        {
            _isApplyingProgrammaticPosition = false;
        }
    }

    // Позиция запоминается при перемещении пользователем (как MiniPlayerLeft/Top) только в памяти; на диск уйдёт со
    // следующим SettingsManager.Save (гарантированно при закрытии, MainWindow.PersistPlaybackAndPlaylistState).
    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (_isApplyingProgrammaticPosition) return;
        if (_isInitializing) return;

        _settings.SettingsWindowLeft = Left;
        _settings.SettingsWindowTop = Top;
    }

    // Вызывается извне, когда закрепление, "поверх окон" или прозрачность переключили прямо на мини-плеере;
    // _isInitializing глушит Changed-обработчики, чтобы не применять настройку повторно и не зациклить обновления.
    public void RefreshMiniPlayerToggles()
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

    // Отмечает миниатюру текущего вида плеера — при открытии окна и извне (MainWindow), когда вид сменили меню заголовка
    // или кнопкой мини-плеера, чтобы страница не отставала от состояния.
    public void RefreshViewModeRadios()
    {
        bool wasInitializing2 = _isInitializing;
        _isInitializing = true;
        try
        {
            switch (_owner.CurrentViewModeName)
            {
                case "Square": ViewModeSquareRadio.IsChecked = true; break;
                case "Rectangular": ViewModeRectangularRadio.IsChecked = true; break;
                case "Mini": ViewModeMiniRadio.IsChecked = true; break;
            }
        }
        finally
        {
            _isInitializing = wasInitializing2;
        }
    }

    private System.Windows.Controls.ComboBox[] ModernSlotCombos() =>
        [ModernSlot0Combo, ModernSlot1Combo, ModernSlot2Combo, ModernSlot3Combo];

    private void SelectModernSlotCombos(IReadOnlyList<string> slots)
    {
        var combos = ModernSlotCombos();
        for (int i = 0; i < combos.Length; i++) SelectComboByTag(combos[i], slots[i]);
    }

    // Кнопки по бокам от «Назад / Пуск / Вперёд» в новом виде: включение и выбор четырёх мест.
    private void ModernLayoutOptions_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        // Список без выбранного пункта значит «ещё не заполнен» (окно в процессе загрузки), а не «Нет»: иначе слоты затираются пустыми.
        if (ModernSlotCombos().Any(combo => combo.SelectedItem is null)) return;

        _settings.ModernShowSideButtons = ModernShowSideButtonsCheckBox.IsChecked == true;
        ModernSlotsGrid.IsEnabled = _settings.ModernShowSideButtons;
        List<string> slots = MainWindow.NormalizeModernSlots(ModernSlotCombos().Select(combo => SelectedTag(combo, string.Empty)));
        _settings.ModernControlSlots = slots;

        // Повторно выбранная кнопка сбрасывается в «Нет»: показываем это в списках сразу.
        bool wasInitializing = _isInitializing;
        _isInitializing = true;
        try
        {
            SelectModernSlotCombos(slots);
        }
        finally
        {
            _isInitializing = wasInitializing;
        }

        _owner.ApplyMainWindowLayoutLive();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void GlassControlsBackground_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.GlassControlsBackground = GlassControlsBackgroundCheckBox.IsChecked == true;
        _owner.ApplyMainWindowLayoutLive();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void ControlButtonsBackground_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.ControlButtonsBackground = ControlButtonsBackgroundCheckBox.IsChecked == true;
        _owner.ApplyMainWindowLayoutLive();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void PlayButtonStyle_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.PlayButtonAccentBackground = PlayButtonAccentRadio.IsChecked == true;
        _owner.ApplyMainWindowLayoutLive();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void MoreMenuStyle_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.MoreMenuSpring = MoreMenuSpringRadio.IsChecked == true;
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void PlayButtonShape_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.PlayButtonRound = PlayButtonRoundRadio.IsChecked == true;
        _owner.ApplyMainWindowLayoutLive();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void CompactCoverAlignment_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.CompactCoverAlignment = SelectedTag(CompactCoverAlignmentCombo, "Left");
        _owner.ApplyMainWindowLayoutLive();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void MainWindowLayoutRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || sender is not System.Windows.Controls.RadioButton { Tag: string layout }) return;

        _settings.MainWindowLayout = layout;
        _owner.ApplyMainWindowLayoutLive();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void PlayerViewModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        if (sender is not System.Windows.Controls.RadioButton { Tag: string modeName }) return;
        _owner.SetPlayerViewModeByName(modeName);
    }

    // Версия в карточке «О плеере» — из assembly metadata (как у UpdateChecker и в release workflow), а не из changelog.
    private void RefreshAppVersionText()
    {
        AppVersionText.Text = LocalizationService.FormatKey(
            LocalizationKey.ApplicationVersion, UpdateChecker.GetCurrentVersion());
    }

    private async Task RefreshVelopackBasePackagePlanAsync()
    {
        _basePackageCts?.Cancel();
        var cts = new CancellationTokenSource();
        _basePackageCts = cts;
        _basePackageError = null;

        try
        {
            // Создание UpdateManager и чтение локальных пакетов — файловый ввод-вывод: на UI-потоке он давал паузу сразу после открытия окна.
            (VelopackUpdateService service, bool isManaged) = await Task.Run(() =>
            {
                var created = new VelopackUpdateService();
                return (created, created.IsManagedInstall);
            }, cts.Token);
            _isVelopackManagedInstall = isManaged;
            if (!_isVelopackManagedInstall)
            {
                _basePackagePlan = VelopackBasePackagePlan.Unavailable(VelopackBasePackageStatus.NotManagedInstall);
                RefreshVelopackBasePackagePresentation();
                return;
            }

            _basePackagePlan = await Task.Run(() => service.GetBasePackagePlanAsync(cts.Token), cts.Token);
            RefreshVelopackBasePackagePresentation();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Окно закрыто, пользователь начал загрузку или запрос был заменён более свежим.
        }
        catch (Exception ex)
        {
            _basePackageError = LocalizationService.Get(LocalizationKey.UpdateFailureNetwork);
            RefreshVelopackBasePackagePresentation();
            Logger.Warn($"Не удалось проверить доступность Velopack base package: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_basePackageCts, cts))
                _basePackageCts = null;
            cts.Dispose();
        }
    }

    private void SetPrepareVelopackBasePackageButtonAvailability(bool isEnabled)
    {
        PrepareVelopackBasePackageButton.IsEnabled = isEnabled;
        PrepareVelopackBasePackageButton.Opacity = isEnabled ? 1.0 : 0.48;
    }

    private void RefreshVelopackBasePackagePresentation()
    {
        VelopackBasePackageTitleText.Text = LocalizationService.Get(LocalizationKey.UpdateVelopackBasePackageTitle);
        VelopackBasePackageDescriptionText.Text = LocalizationService.Get(LocalizationKey.UpdateVelopackBasePackageDescription);
        PrepareVelopackBasePackageButtonText.Text = LocalizationService.Get(LocalizationKey.UpdateVelopackBasePackageDownload);

        if (!_isVelopackManagedInstall || _basePackagePlan?.Status == VelopackBasePackageStatus.NotManagedInstall)
        {
            VelopackBasePackageExpander.Visibility = Visibility.Collapsed;
            return;
        }

        VelopackBasePackageExpander.Visibility = Visibility.Visible;
        if (_basePackagePlan is null)
        {
            SetPrepareVelopackBasePackageButtonAvailability(false);
            VelopackBasePackageStatusText.Text = string.IsNullOrWhiteSpace(_basePackageError)
                ? LocalizationService.Get(LocalizationKey.UpdateVelopackPreparing)
                : LocalizationService.FormatKey(LocalizationKey.UpdateVelopackBasePackageDownloadFailed, _basePackageError);
            return;
        }

        VelopackBasePackagePlan plan = _basePackagePlan;
        string version = plan.CurrentVersion?.ToString() ?? "—";
        switch (plan.Status)
        {
            case VelopackBasePackageStatus.Available when plan.FullPackage is not null:
                SetPrepareVelopackBasePackageButtonAvailability(true);
                VelopackBasePackageStatusText.Text = LocalizationService.FormatKey(
                    LocalizationKey.UpdateVelopackBasePackageAvailable,
                    version,
                    VelopackUpdateDiagnostics.FormatBytes(plan.FullPackage.Size),
                    VelopackUpdateDiagnostics.FormatBytes(plan.RequiredFreeBytes));
                break;

            case VelopackBasePackageStatus.Prepared:
                SetPrepareVelopackBasePackageButtonAvailability(false);
                PrepareVelopackBasePackageButtonText.Text = LocalizationService.Get(LocalizationKey.UpdateVelopackBasePackageReady);
                VelopackBasePackageStatusText.Text = LocalizationService.FormatKey(
                    LocalizationKey.UpdateVelopackBasePackagePrepared, version);
                break;

            case VelopackBasePackageStatus.CurrentPackageUnavailable:
                SetPrepareVelopackBasePackageButtonAvailability(false);
                VelopackBasePackageStatusText.Text = LocalizationService.Get(LocalizationKey.UpdateVelopackBasePackageUnavailable);
                break;

            case VelopackBasePackageStatus.InsufficientDiskSpace:
                SetPrepareVelopackBasePackageButtonAvailability(false);
                VelopackBasePackageStatusText.Text = LocalizationService.FormatKey(
                    LocalizationKey.UpdateVelopackBasePackageInsufficientSpace,
                    VelopackUpdateDiagnostics.FormatBytes(plan.RequiredFreeBytes));
                break;

            default:
                SetPrepareVelopackBasePackageButtonAvailability(false);
                VelopackBasePackageStatusText.Text = LocalizationService.Get(LocalizationKey.UpdateVelopackBasePackageNotManaged);
                break;
        }
    }

    private void RefreshLegacyInnoCleanupPresentation()
    {
        LegacyInnoCleanupTitleText.Text = LocalizationService.Get(LocalizationKey.UpdateLegacyCleanupCardTitle);
        LegacyInnoCleanupDescriptionText.Text = LocalizationService.Get(LocalizationKey.UpdateLegacyCleanupCardDescription);
        RemoveLegacyInnoButtonText.Text = LocalizationService.Get(LocalizationKey.UpdateLegacyCleanupCardButton);

        bool hasLegacyInstall = _isVelopackManagedInstall &&
                                LegacyInnoCleanupService.TryFind(out LegacyInnoCleanupService.LegacyInnoInstall? legacyInstall) &&
                                legacyInstall is not null;
        if (!hasLegacyInstall)
        {
            LegacyInnoCleanupExpander.Visibility = Visibility.Collapsed;
            return;
        }

        LegacyInnoCleanupExpander.Visibility = Visibility.Visible;
        RemoveLegacyInnoButton.IsEnabled = !_isLegacyInnoCleanupRunning;
        string statusKey = _isLegacyInnoCleanupRunning
            ? LocalizationKey.UpdateLegacyCleanupCardRunning
            : _legacyInnoCleanupStatusKey ?? LocalizationKey.UpdateLegacyCleanupCardAvailable;
        LegacyInnoCleanupStatusText.Text = LocalizationService.Get(statusKey);
    }

    private void RemoveLegacyInnoButton_Click(object sender, RoutedEventArgs e)
        => BackgroundTask.FireAndForget(RemoveLegacyInnoButton_ClickAsync(sender, e), nameof(RemoveLegacyInnoButton_Click));

    private async Task RemoveLegacyInnoButton_ClickAsync(object sender, RoutedEventArgs e)
    {
        if (!_isVelopackManagedInstall || _isLegacyInnoCleanupRunning ||
            !LegacyInnoCleanupService.TryFind(out LegacyInnoCleanupService.LegacyInnoInstall? legacyInstall) ||
            legacyInstall is null)
        {
            RefreshLegacyInnoCleanupPresentation();
            return;
        }

        var answer = LocalizedMessageBox.Show(
            this,
            LocalizationService.Get(LocalizationKey.UpdateLegacyCleanupMessage),
            LocalizationService.Get(LocalizationKey.UpdateLegacyCleanupTitle),
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (answer != System.Windows.MessageBoxResult.Yes)
            return;

        if (!LegacyInnoCleanupService.TryStartInteractiveUninstall(
                legacyInstall,
                out System.Diagnostics.Process? uninstallerProcess,
                out string? technicalError) ||
            uninstallerProcess is null)
        {
            _legacyInnoCleanupStatusKey = LocalizationKey.UpdateLegacyCleanupFailed;
            RefreshLegacyInnoCleanupPresentation();
            LocalizedMessageBox.Show(
                this,
                LocalizationService.Get(LocalizationKey.UpdateLegacyCleanupFailed),
                LocalizationService.Get(LocalizationKey.UpdateLegacyCleanupTitle),
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            if (!string.IsNullOrWhiteSpace(technicalError))
                Logger.Warn($"Legacy cleanup не был запущен из Settings: {technicalError}");
            return;
        }

        _isLegacyInnoCleanupRunning = true;
        _legacyInnoCleanupStatusKey = LocalizationKey.UpdateLegacyCleanupCardRunning;
        RefreshLegacyInnoCleanupPresentation();

        try
        {
            await uninstallerProcess.WaitForExitAsync();
            bool stillInstalled = LegacyInnoCleanupService.TryFind(out _);
            if (stillInstalled)
            {
                _legacyInnoCleanupStatusKey = LocalizationKey.UpdateLegacyCleanupCardStillInstalled;
                Logger.Info("Legacy EXE-копия Lumisense осталась после закрытия мастера удаления.");
                RefreshLegacyInnoCleanupPresentation();
                return;
            }

            string? legacyInstallDir = Path.GetDirectoryName(legacyInstall.UninstallerPath);
            if (!string.IsNullOrWhiteSpace(legacyInstallDir))
                LegacyIntegrationRepairService.RepairAfterLegacyCleanup(legacyInstallDir);

            _legacyInnoCleanupStatusKey = LocalizationKey.UpdateLegacyCleanupCardCompleted;
            RefreshLegacyInnoCleanupPresentation();
            if (IsLoaded)
            {
                LocalizedMessageBox.Show(
                    this,
                    LocalizationService.Get(LocalizationKey.UpdateLegacyCleanupCardCompleted),
                    LocalizationService.Get(LocalizationKey.UpdateLegacyCleanupTitle),
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            _legacyInnoCleanupStatusKey = LocalizationKey.UpdateLegacyCleanupCardStillInstalled;
            Logger.Warn($"Не удалось дождаться завершения legacy cleanup: {ex.Message}");
            RefreshLegacyInnoCleanupPresentation();
        }
        finally
        {
            _isLegacyInnoCleanupRunning = false;
            RefreshLegacyInnoCleanupPresentation();
            uninstallerProcess.Dispose();
        }
    }

    private void PrepareVelopackBasePackageButton_Click(object sender, RoutedEventArgs e)
        => BackgroundTask.FireAndForget(PrepareVelopackBasePackageButton_ClickAsync(sender, e), nameof(PrepareVelopackBasePackageButton_Click));

    private async Task PrepareVelopackBasePackageButton_ClickAsync(object sender, RoutedEventArgs e)
    {
        if (_basePackagePlan?.Status != VelopackBasePackageStatus.Available)
            return;

        _basePackageCts?.Cancel();
        var cts = new CancellationTokenSource();
        _basePackageCts = cts;
        SetPrepareVelopackBasePackageButtonAvailability(false);
        var progress = new Progress<int>(value =>
        {
            VelopackBasePackageStatusText.Text = LocalizationService.FormatKey(
                LocalizationKey.UpdateVelopackBasePackageDownloading, value);
        });

        try
        {
            var service = new VelopackUpdateService();
            await service.PrepareBasePackageAsync(_basePackagePlan, progress, cts.Token);
            _basePackagePlan = null;
            await RefreshVelopackBasePackagePlanAsync();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            _basePackagePlan = null;
            await RefreshVelopackBasePackagePlanAsync();
        }
        catch (Exception ex)
        {
            _basePackageError = LocalizationService.Get(LocalizationKey.UpdateFailureNetwork);
            RefreshVelopackBasePackagePresentation();
            Logger.Warn($"Не удалось подготовить Velopack base package: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_basePackageCts, cts))
                _basePackageCts = null;
            cts.Dispose();
        }
    }

    // Ручная проверка (кнопка на странице "О плеере"), в отличие от тихой на старте (MainWindow.CheckForUpdatesOnStartupAsync),
    // всегда показывает результат и не учитывает SkippedUpdateVersion: пользователь явно хочет знать статус.
    private void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
        => BackgroundTask.FireAndForget(CheckUpdatesButton_ClickAsync(sender, e), nameof(CheckUpdatesButton_Click));

    private async Task CheckUpdatesButton_ClickAsync(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        CheckUpdatesButtonSubtitle.Text = LocalizationService.Translate("Проверяем…");
        SetUpdateCheckBusy(true);

        try
        {
            var result = await UpdateChecker.CheckAsync();
            SetUpdateCheckBusy(false);
            if (result.Status != UpdateCheckStatus.Error)
            {
                // Только legacy Inno Setup проверяет EXE-asset. Velopack сам определяет
                // latest full package через release API в момент диагностики.
                _lastCheckedReleaseAssetUrl = result.DeliveryKind == UpdateDeliveryKind.Velopack
                    ? null
                    : result.DownloadUrl;
            }

            switch (result.Status)
            {
                case UpdateCheckStatus.UpdateAvailable:
                    CheckUpdatesButtonSubtitle.Text = LocalizationService.Translate($"Доступна версия {result.LatestVersion}");
                    new UpdateAvailableWindow(result, _settings) { Owner = this }.ShowDialog();
                    break;

                case UpdateCheckStatus.MsiMigrationAvailable:
                    CheckUpdatesButtonSubtitle.Text = LocalizationService.Get(
                        LocalizationKey.UpdateMsiMigrationManualSubtitle);
                    new UpdateAvailableWindow(result, _settings) { Owner = this }.ShowDialog();
                    break;

                case UpdateCheckStatus.UpToDate:
                    CheckUpdatesButtonSubtitle.Text = LocalizationService.Translate($"У вас последняя версия ({result.CurrentVersion})");
                    break;

                case UpdateCheckStatus.Error:
                default:
                    // Если GitHub недоступен, подсказываем запасной путь: релизы дублируются в Telegram-канале.
                    CheckUpdatesButtonSubtitle.Text = UpdateFailureExperience.Describe(result.FailureKind, result.HttpStatusCode)
                        + " " + string.Format(
                            LocalizationService.Translate("Новые версии также публикуются в Telegram: {0}"),
                            UpdateCheckHealth.TelegramChannelName);
                    break;
            }
        }
        finally
        {
            SetUpdateCheckBusy(false);
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    // Пока идёт проверка, вместо галочки в карточке крутится индикатор Material.
    private void SetUpdateCheckBusy(bool busy)
    {
        CheckUpdatesLoadingIndicator.IsAnimationEnabled = !AccessibilityPreferences.ShouldReduceMotion(_settings, AnimationAreas.Loading);
        CheckUpdatesLoadingIndicator.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CheckUpdatesIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
    }

        // Источник применяется сразу: UpdateAvailableWindow использует ту же in-memory модель _settings; выбор сохраняем
        // сразу, чтобы он пережил перезапуск без ожидания общего сохранения.
    private void UpdateSourceRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        if (sender is not System.Windows.Controls.RadioButton { Tag: string key } radio) return;
        if (key != "GitHub" && _settings.UpdateDownloadSource == "GitHub")
        {
            // Диалог внутри Checked оставлял обе радиокнопки отмеченными, поэтому показываем его после обработчика.
            Dispatcher.BeginInvoke(() => ConfirmAndApplyMirror(radio, key));
            return;
        }
        ApplyUpdateSource(key);
    }

    private void ConfirmAndApplyMirror(System.Windows.Controls.RadioButton mirrorRadio, string key)
    {
        if (ConfirmMirrorUse())
        {
            ApplyUpdateSource(key);
            return;
        }

        // Откат без повторного срабатывания Checked и без записи выбора в настройки.
        bool wasInitializing4 = _isInitializing;
        _isInitializing = true;
        try
        {
            mirrorRadio.IsChecked = false;
            UpdateSourceGitHubRadio.IsChecked = true;
        }
        finally
        {
            _isInitializing = wasInitializing4;
        }
    }

    private void ApplyUpdateSource(string key)
    {
        _settings.UpdateDownloadSource = key;
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    // Предупреждаем только при переходе с GitHub на зеркало; переключение между зеркалами уже подтверждено.
    private bool ConfirmMirrorUse()
    {
        if (_settings.UpdateMirrorWarningSuppressed) return true;

        var dialog = new UpdateMirrorWarningDialog(this);
        if (dialog.ShowDialog() != true) return false;

        _settings.UpdateMirrorWarningSuppressed = dialog.DontAskAgain;
        return true;
    }

    private void ProbeUpdateSourcesButton_Click(object sender, RoutedEventArgs e)
        => BackgroundTask.FireAndForget(ProbeUpdateSourcesButton_ClickAsync(sender, e), nameof(ProbeUpdateSourcesButton_Click));

    private async Task ProbeUpdateSourcesButton_ClickAsync(object sender, RoutedEventArgs e)
    {
        if (!ProbeUpdateSourcesButton.IsEnabled) return;

        ProbeUpdateSourcesButton.IsEnabled = false;
        ClearUpdateSourceProbeRows();
        _sourceProbeCts?.Cancel();
        _sourceProbeCts = new CancellationTokenSource();
        UpdateSourceProbeStatusText.Text = LocalizationService.Get(LocalizationKey.UpdateSourceProbeRunning);
        UpdateSourceProbeStatusText.Visibility = Visibility.Visible;

        try
        {
            _sourceProbeResults = await UpdateChecker.ProbeDownloadSourcesAsync(
                _lastCheckedReleaseAssetUrl, _sourceProbeCts.Token);
            RenderUpdateSourceProbeResults(_sourceProbeResults);
        }
        catch (OperationCanceledException)
        {
            // Закрытие окна или новая проверка отменили предыдущий ограниченный test-запрос.
        }
        catch (UpdateSourceProbeAssetResolutionException ex)
        {
            Logger.Warn($"Не удалось определить актуальный release asset для диагностики: {ex.Message}");
            UpdateSourceProbeStatusText.Text = LocalizationService.Get(
                LocalizationKey.UpdateSourceProbeAssetUnavailable);
            UpdateSourceProbeStatusText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось выполнить диагностику источников обновлений: {ex.Message}");
            UpdateSourceProbeStatusText.Text = LocalizationService.Get(LocalizationKey.UpdateSourceProbeNoWorking);
            UpdateSourceProbeStatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            if (IsLoaded)
                ProbeUpdateSourcesButton.IsEnabled = true;
        }
    }

    private void RefreshUpdateSourceProbePresentation()
    {
        ProbeUpdateSourcesTitleText.Text = LocalizationService.Get(LocalizationKey.UpdateSourceProbeTitle);
        ProbeUpdateSourcesSubtitleText.Text = LocalizationService.Get(LocalizationKey.UpdateSourceProbeSubtitle);
    }

    private void RenderUpdateSourceProbeResults(IReadOnlyList<UpdateSourceProbeResult> results)
    {
        foreach (UpdateSourceProbeResult result in results)
        {
            System.Windows.Controls.TextBlock? statusText = GetUpdateSourceProbeText(result.Key);
            if (statusText is null) continue;

            bool isSlow = result.IsAvailable &&
                          (result.BytesPerSecond < 128 * 1024 || result.ResponseMilliseconds > 1500);
            if (!result.IsAvailable)
            {
                statusText.Text = LocalizationService.Get(LocalizationKey.UpdateSourceProbeFailed);
                statusText.Foreground = new SolidColorBrush(Color.FromRgb(224, 82, 82));
            }
            else if (isSlow)
            {
                statusText.Text = LocalizationService.FormatKey(LocalizationKey.UpdateSourceProbeRow,
                    LocalizationService.Get(LocalizationKey.UpdateSourceProbeSlow),
                    result.ResponseMilliseconds, FormatProbeRate(result.BytesPerSecond));
                statusText.Foreground = new SolidColorBrush(Color.FromRgb(216, 158, 34));
            }
            else
            {
                statusText.Text = LocalizationService.FormatKey(LocalizationKey.UpdateSourceProbeRow,
                    LocalizationService.Get(LocalizationKey.UpdateSourceProbeGood),
                    result.ResponseMilliseconds, FormatProbeRate(result.BytesPerSecond));
                statusText.Foreground = new SolidColorBrush(Color.FromRgb(63, 174, 106));
            }

            statusText.Visibility = Visibility.Visible;
        }

        UpdateSourceProbeResult? recommended = results
            .Where(result => result.IsAvailable)
            .OrderByDescending(result => result.BytesPerSecond)
            .ThenBy(result => result.ResponseMilliseconds)
            .FirstOrDefault();
        UpdateSourceProbeStatusText.Text = recommended is null
            ? LocalizationService.Get(LocalizationKey.UpdateSourceProbeNoWorking)
            : LocalizationService.FormatKey(LocalizationKey.UpdateSourceProbeRecommended,
                LocalizationService.Translate(recommended.DisplayName));
        UpdateSourceProbeStatusText.Visibility = Visibility.Visible;
    }

    private System.Windows.Controls.TextBlock? GetUpdateSourceProbeText(string sourceKey) => sourceKey switch
    {
        "GitHub" => UpdateSourceGitHubProbeText,
        "GhProxy" => UpdateSourceGhProxyProbeText,
        "GhProxyV4" => UpdateSourceGhProxyV4ProbeText,
        "GhProxyV6" => UpdateSourceGhProxyV6ProbeText,
        "GhProxyCdn" => UpdateSourceGhProxyCdnProbeText,
        "GhProxyCom" => UpdateSourceGhProxyComProbeText,
        "GhFast" => UpdateSourceGhFastProbeText,
        _ => null
    };

    private void ClearUpdateSourceProbeRows()
    {
        foreach (string sourceKey in UpdateChecker.DownloadSources.Select(source => source.Key))
        {
            System.Windows.Controls.TextBlock? statusText = GetUpdateSourceProbeText(sourceKey);
            if (statusText is null) continue;
            statusText.Text = string.Empty;
            statusText.Visibility = Visibility.Collapsed;
        }
    }

    private static string FormatProbeRate(double bytesPerSecond)
    {
        const double kib = 1024;
        const double mib = kib * 1024;
        string megabytes = LocalizationService.IsEnglish ? "MB" : "МБ";
        string kilobytes = LocalizationService.IsEnglish ? "KB" : "КБ";
        return bytesPerSecond >= mib
            ? $"{bytesPerSecond / mib:F1} {megabytes}"
            : bytesPerSecond >= kib
                ? $"{bytesPerSecond / kib:F0} {kilobytes}"
                : $"{bytesPerSecond:F0} B";
    }


    // Элемент списка версий (AllVersionsList в XAML): обёртка над ReleaseListItem с готовыми строками, чтобы DataTemplate
    // был набором биндингов без конвертеров.
    private sealed record VersionListItemViewModel(
        string TitleText, string SubtitleText, string ActionText, bool CanInstall, ReleaseListItem Release);

    private bool _allVersionsLoaded;
    private bool _allVersionsLoading;
    private IReadOnlyList<ReleaseListItem>? _loadedReleases;

    // Список грузится лениво — при первом раскрытии аккордеона и один раз за жизнь окна, чтобы не бить по сети зря.
    private void AllVersionsExpander_Expanded(object sender, RoutedEventArgs e)
        => BackgroundTask.FireAndForget(AllVersionsExpander_ExpandedAsync(sender, e), nameof(AllVersionsExpander_Expanded));

    private async Task AllVersionsExpander_ExpandedAsync(object sender, RoutedEventArgs e)
    {
        // Expanded срабатывает ещё при разборе XAML, когда элементы ниже не созданы: тогда ничего не грузим,
        // загрузка стартует при показе вкладки (раньше NullReferenceException оставлял _allVersionsLoading взведённым).
        if (AllVersionsLoadingText is null) return;

        if (_allVersionsLoaded || _allVersionsLoading) return;
        await LoadAllVersionsAsync();
    }

    // Загружает список при показе вкладки «Обновления», если аккордеон уже раскрыт (раскрыт по умолчанию), а список ещё не получен.
    private void StartAllVersionsLoadIfNeeded()
    {
        if (!AllVersionsExpanderControl.IsExpanded || _allVersionsLoaded || _allVersionsLoading) return;
        BackgroundTask.FireAndForget(LoadAllVersionsAsync(), nameof(StartAllVersionsLoadIfNeeded));
    }

    private void AllVersionsRetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_allVersionsLoading) return;
        BackgroundTask.FireAndForget(LoadAllVersionsAsync(), nameof(AllVersionsRetryButton_Click));
    }

    // Загружает список версий; неудача не помечает список загруженным, поэтому «Повторить» и повторное раскрытие пробуют снова.
    // Сбой GitHub (лимит 403/429, сеть) обрабатывает UpdateChecker.GetAllReleasesAsync: повторы и сохранённая копия списка.
    private async Task LoadAllVersionsAsync()
    {
        _allVersionsLoading = true;
        try
        {
            AllVersionsLoadingText.Visibility = Visibility.Visible;
            AllVersionsErrorText.Visibility = Visibility.Collapsed;
            AllVersionsStaleText.Visibility = Visibility.Collapsed;
            AllVersionsRetryButton.Visibility = Visibility.Collapsed;
            AllVersionsRetryButton.IsEnabled = false;

            // Сначала сразу показываем сохранённый список (если есть), а свежий подгружаем следом: раньше при медленном GitHub
            // или VPN окно до конца запроса показывало только неменяющуюся надпись о загрузке.
            if (_loadedReleases is null && UpdateChecker.GetCachedReleases() is { Releases.Count: > 0 } cached)
            {
                _loadedReleases = cached.Releases;
                RenderAllVersions(cached.Releases);
                AllVersionsLoadingText.Text = LocalizationService.Translate("Обновляем список версий…");
            }

            var attemptProgress = new Progress<int>(attempt =>
            {
                if (_loadedReleases is null)
                    AllVersionsLoadingText.Text = string.Format(
                        LocalizationService.Translate("Загружаем список версий… попытка {0} из {1}"),
                        attempt, ReleaseListRetryPolicy.MaxAttempts);
            });
            ReleaseListResult releaseResult = await UpdateChecker.GetAllReleasesAsync(attemptProgress: attemptProgress);
            AllVersionsLoadingText.Visibility = Visibility.Collapsed;

            if (!releaseResult.IsSuccess)
            {
                AllVersionsErrorText.Text = DescribeVersionListFailure(releaseResult.FailureKind, releaseResult.HttpStatusCode,
                    releaseResult.RateLimitResetAt, releaseResult);
                AllVersionsErrorText.Visibility = Visibility.Visible;
                AllVersionsRetryButton.Visibility = Visibility.Visible;
                return;
            }

            IReadOnlyList<ReleaseListItem> releases = releaseResult.Releases;
            if (releases.Count == 0)
            {
                _allVersionsLoaded = true;
                AllVersionsErrorText.Text = LocalizationService.Translate("На GitHub пока нет ни одного опубликованного релиза.");
                AllVersionsErrorText.Visibility = Visibility.Visible;
                return;
            }

            _allVersionsLoaded = !releaseResult.IsFromCache;
            _loadedReleases = releases;
            RenderAllVersions(releases);

            if (releaseResult.IsFromCache)
            {
                string reason = DescribeVersionListFailure(releaseResult.RefreshFailureKind, releaseResult.RefreshHttpStatusCode,
                    releaseResult.RateLimitResetAt, null);
                string saved = releaseResult.CachedAt is { } cachedAt
                    ? string.Format(LocalizationService.Translate("Показан сохранённый список от {0}."), cachedAt.ToLocalTime().ToString("g"))
                    : LocalizationService.Translate("Показан сохранённый список.");
                AllVersionsStaleText.Text = $"{saved} {reason}";
                AllVersionsStaleText.Visibility = Visibility.Visible;
                AllVersionsRetryButton.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _allVersionsLoading = false;
            AllVersionsRetryButton.IsEnabled = true;
        }
    }

    // Лимит запросов GitHub объясняем словами; прочие сбои — стандартным текстом.
    private static string DescribeVersionListFailure(UpdateFailureKind kind, int? httpStatusCode, DateTimeOffset? resetAt,
        ReleaseListResult? fullResult)
    {
        if (UpdateFailureExperience.IsRateLimit(kind, httpStatusCode))
            return UpdateFailureExperience.DescribeRateLimit(httpStatusCode!.Value, resetAt);

        return fullResult is not null
            ? UpdateFailureExperience.DescribeVersionListFailure(fullResult)
            : UpdateFailureExperience.Describe(kind, httpStatusCode);
    }

    private void RenderAllVersions(IReadOnlyList<ReleaseListItem> releases)
    {
        string currentVersion = UpdateChecker.GetCurrentVersion();
        bool velopackInstall = UpdateMigrationGuard.IsVelopackManagedInstall();

        AllVersionsList.ItemsSource = releases
            // GitHub отдаёт релизы уже в порядке "сначала новые", но не гарантирует это явно —
            // сортируем сами по дате публикации, чтобы порядок не зависел от их API.
            .OrderByDescending(r => r.PublishedAt ?? System.DateTimeOffset.MinValue)
            .Select(r =>
            {
                bool isCurrent = string.Equals(r.Version, currentVersion, System.StringComparison.OrdinalIgnoreCase);

                string title = $"v{r.Version}" +
                    (isCurrent ? $" · {LocalizationService.Translate("Текущая версия")}" : "") +
                    (r.IsPrerelease ? $" · {LocalizationService.Translate("Пререлиз")}" : "");
                string subtitle = r.PublishedAt is { } published
                    ? published.LocalDateTime.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo(
                        LocalizationService.IsEnglish ? "en-US" : "ru-RU"))
                    : LocalizationService.Translate("Дата публикации неизвестна");

                // Inno EXE поверх Velopack-установки создал бы вторую копию, поэтому там кнопка только открывает релиз.
                bool canInstall = velopackInstall
                    ? !string.IsNullOrEmpty(r.ReleaseNotesUrl)
                    : !string.IsNullOrEmpty(r.ExeDownloadUrl) && !string.IsNullOrEmpty(r.ExeSha256);
                if (!velopackInstall && string.IsNullOrEmpty(r.ExeDownloadUrl))
                    subtitle += $" · {LocalizationService.Translate("В релизе нет .exe-установщика")}";
                else if (!velopackInstall && string.IsNullOrEmpty(r.ExeSha256))
                    subtitle += $" · {LocalizationService.Translate("В релизе отсутствует SHA-256 установщика")}";

                string action = LocalizationService.Translate(velopackInstall ? "Открыть релиз"
                    : isCurrent ? "Переустановить" : "Установить");

                return new VersionListItemViewModel(title, subtitle, action, canInstall, r);
            })
            .ToList();
    }

    // Тот же диалог, что при обычном обнаружении обновления; не проверяет, что версия новее, поэтому подходит и для отката:
    // CurrentVersion — реальная текущая, диалог показывает обе рядом (это и есть предупреждение об откате).
    private void VersionListItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: VersionListItemViewModel item }) return;

        if (UpdateMigrationGuard.IsVelopackManagedInstall())
        {
            if (!string.IsNullOrEmpty(item.Release.ReleaseNotesUrl)) OpenUrl(item.Release.ReleaseNotesUrl);
            return;
        }

        var result = new UpdateCheckResult
        {
            Status = UpdateCheckStatus.UpdateAvailable,
            CurrentVersion = UpdateChecker.GetCurrentVersion(),
            LatestVersion = item.Release.Version,
            DownloadUrl = item.Release.ExeDownloadUrl,
            InstallerSha256 = item.Release.ExeSha256,
            ReleaseNotesUrl = item.Release.ReleaseNotesUrl,
            ReleaseNotes = item.Release.ReleaseNotes,
        };

        try
        {
            new UpdateAvailableWindow(result, _settings) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            Logger.Error($"Не удалось открыть окно информации об обновлении: {ex}");
            LocalizedMessageBox.Show(this,
                "Не удалось открыть информацию об обновлении. Подробности записаны в журнал.",
                "Ошибка обновления", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }
}
