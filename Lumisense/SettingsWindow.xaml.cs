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

    private void BuildSearchIndex()
    {
        void Add(string label, string pageTitle, string pageKey, FrameworkElement target, string extraKeywords = "")
            => _searchIndex.Add(new SettingsSearchEntry(label, pageTitle, pageKey,
                $"{label} {LocalizationService.ToEnglish(label)} {extraKeywords}".ToLowerInvariant(), target));

        Add("Язык интерфейса", "Оформление", "Appearance", InterfaceLanguageRadioGroup, "язык русский english language locale локализация");
        Add("Тема", "Оформление", "Appearance", ThemeRadioGroup, "тёмная светлая цвет тема оформление dark light");
        Add("Навигация в настройках", "Оформление", "Appearance", NavigationStyleCard, "навигация меню таблетки панель боковая плавающая нижняя разделы названия подписи текст pills sidebar floating navigation labels");
        Add("Акцентный цвет", "Оформление", "Appearance", AccentModeRadioGroup, "акцент цвет палитра accent color");
        Add("Основа окна", "Оформление", "Appearance", BackdropRadioGroup, "mica alt acrylic blur акрил размытие блюр подложка фон backdrop более плотная и контрастная основа windows 11 22h2 сильнее прозрачность");
        Add("Цвет основы от текущей обложки", "Оформление", "Appearance", CoverBaseFromCoverCheckBox, "обложка cover основа фон окно цвет theme");
        Add("Доступность", "Оформление", "Appearance", AccessibilityCard, "масштаб интерфейса текст размер доступность движение анимация анимации по разделам слайдеры accessibility scale motion animations sliders");
        Add("Анимация смены обложки", "Обложка и Now Playing", "Cover", AlbumArtTransitionOnRadio, "анимация обложка переход трек itunes слайд карусель соседние предыдущая следующая fly transition carousel album art cover");
        Add("Подпись под названием трека", "Обложка и Now Playing", "Cover", TrackStateBadgeModeRadioGroup, "подпись состояние трека расширение формат качество битрейт размер файла badge extension format bitrate size flac mp3 и частота свой набор отметить что показывать");
        Add("Жесты на обложке", "Обложка и Now Playing", "Cover", AlbumArtGesturesCheckBox, "жесты обложка касание свайп пуск пауза громкость следующий предыдущий gesture swipe cover");
        Add("Вид главного окна", "Окно и запуск", "Window", MainWindowLayoutCard, "оформление новый классический крупная обложка кнопки управление layout modern classic");
        Add("Вид плеера", "Окно и запуск", "Window", PlayerViewModeCard, "квадратный прямоугольный мини плеер вид размер окна square rectangular mini");
        Add("Поверх всех окон", "Окно и запуск", "Window", AlwaysOnTopCheckBox, "topmost всегда сверху главное окно");
        Add("Сворачивать в трей при закрытии", "Окно и запуск", "Window", MinimizeToTrayCheckBox, "трей закрытие свернуть tray");
        Add("Запускать вместе с Windows", "Окно и запуск", "Window", LaunchOnStartupCheckBox, "автозапуск запуск windows автозагрузка startup");
        Add("Запускать свёрнутым в трей", "Окно и запуск", "Window", StartHiddenInTrayCheckBox, "запуск свёрнутым трей автозапуск скрыто hidden startup tray");
        Add("Запоминать громкость между запусками", "Воспроизведение", "Playback", RememberVolumeCheckBox, "громкость запуск volume");
        Add("Логарифмическая регулировка громкости", "Воспроизведение", "Playback", LogarithmicVolumeCheckBox, "громкость логарифм слух дБ db volume logarithmic");
        Add("Не запускать трек при старте", "Воспроизведение", "Playback", NeverAutoPlayLastTrackOnStartupCheckBox, "старт запуск продолжить воспроизведение последний трек пауза resume autoplay");
        Add("Автоматически обновлять папки плейлиста", "Воспроизведение", "Playback", AutoRefreshPlaylistFoldersCheckBox, "папки плейлист новые файлы автообновление слежение watcher refresh auto folders");
        Add("Очистить кэш интернет-обложек", "Библиотека и файлы", "Library", ClearArtworkCacheButton, "кэш обложка интернет очистить удалить cover cache artwork image");
        Add("Нормализация имён файлов", "Библиотека и файлы", "Library", NormalizePlaylistFileNamesButton, "нормализация имя файл шаблон переименование artist title album track extension rename");
        Add("Действия контекстного меню трека", "Библиотека и файлы", "Library", TrackContextFavoriteCheckBox, "контекстное меню правый клик пкм трек плейлист скрыть отключить действия проводник копировать теги свойства удалить найти файл замена недоступный locate relink missing");
        Add("Найти файл", "Библиотека и файлы", "Library", TrackContextFindFileCheckBox, "контекстное меню заменить недоступный файл путь locate relink missing file");
        Add("Discord Rich Presence", "Интеграции", "Integrations", DiscordRichPresenceEnabledCheckBox, "discord статус rich presence rpc активность" );
        Add("Подключить Discord", "Интеграции", "Integrations", ConnectDiscordButton, "discord подключить connection rich presence статус" );
        Add("Открыть журнал Discord", "Интеграции", "Integrations", OpenDiscordDiagnosticsLogButton, "discord журнал лог диагностика ошибка rich presence" );
        Add("Приватность Discord: название и исполнитель", "Интеграции", "Integrations", DiscordRichPresenceShowTrackInfoCheckBox, "discord приватность название исполнитель трек" );
        Add("Приватность Discord: таймлайн", "Интеграции", "Integrations", DiscordRichPresenceShowTimelineCheckBox, "discord приватность время прогресс таймлайн" );
        Add("Приватность Discord: кнопка GitHub", "Интеграции", "Integrations", DiscordRichPresenceShowButtonsCheckBox, "discord кнопка github ссылки статус button");
        Add("Приватность Discord: кнопка Скачать Lumisense", "Интеграции", "Integrations", DiscordRichPresenceShowDownloadButtonCheckBox, "discord кнопка скачать download ссылки статус button");
        Add("Приватность Discord: обложка трека", "Интеграции", "Integrations", DiscordRichPresenceShowCoverArtCheckBox, "discord обложка cover art musicbrainz itunes deezer" );
        Add("Эквалайзер", "Эквалайзер", "Equalizer", EqualizerEnabledCheckBox, "equalizer эквалайзер частоты полосы бас звук eq");
        Add("Звук для каждого устройства", "Эквалайзер", "Equalizer", DeviceSoundProfilesCheckBox, "профиль устройство наушники колонки эквалайзер баланс запоминать device profile");
        Add("Авто-преамп эквалайзера", "Эквалайзер", "Equalizer", EqualizerAutoHeadroomCheckBox, "преамп headroom клиппинг перегрузка громкость эквалайзер eq подъём");
        Add("Монитор острова", "Остров", "Island", IslandMonitorCombo, "остров монитор экран дисплей island monitor display");
        Add("Настройки острова", "Остров", "Island", IslandCustomCard, "остров круг капсула вид свёрнутого только обложка размер отступ размытие эквалайзер обводка скрыть средняя кнопка колесо громкость обложка игры раскрываться сворачиваться перетягивание свайп круглая кнопки избранное повтор текст песни island scale blur equalizer lag тормоза swipe round cover ширина width эквалайзер точки полоски фон свечение матрица уведомление контекстное меню пункты волна перемотка сегменты мерцание backdrop glow stars matrix seek shimmer");
        Add("Вид мини-плеера: карточка или остров", "Мини-плеер", "MiniPlayer", MiniStyleCard, "остров капсула dynamic island монитор экран вид мини плеер island monitor style");
        Add("Прозрачность окна мини-плеера", "Мини-плеер", "MiniPlayer", MiniOpacitySlider, "прозрачность opacity мини плеер");
        Add("Поверх всех окон (мини-плеер)", "Мини-плеер", "MiniPlayer", MiniAlwaysOnTopCheckBox, "topmost мини плеер");
        Add("Закрепить положение (мини-плеер)", "Мини-плеер", "MiniPlayer", MiniPinnedCheckBox, "закрепить перетаскивание pin мини плеер");
        Add("Прилипание к краям экрана (мини-плеер)", "Мини-плеер", "MiniPlayer", MiniSnapToEdgesCheckBox, "прилипание магнит края экран snap edge мини плеер");
        Add("Вторая кнопка в мини-плеере", "Мини-плеер", "MiniPlayer", MiniSecondaryButtonRadioGroup, "вторая кнопка повтор перемешать избранное сердечко favorite shuffle repeat мини плеер");
        Add("Прокручивать длинного исполнителя", "Мини-плеер", "MiniPlayer", MiniArtistMarqueeCheckBox, "исполнитель артист прокрутка бегущая строка marquee мини плеер");
        Add("Отображение обложки (мини-плеер)", "Мини-плеер", "MiniPlayer", MiniPlayerArtworkStyleRadioGroup, "обложка винил пластинка вращение круглая artwork vinyl rotate мини плеер по умолчанию без вращения");
        Add("Показывать полосу прогресса (мини-плеер)", "Мини-плеер", "MiniPlayer", MiniShowProgressCheckBox, "полоса прогресс progress bar скрыть мини плеер");
        Add("Прогресс вокруг обложки (мини-плеер)", "Мини-плеер", "MiniPlayer", MiniShowArtworkProgressCheckBox, "контур скруглённый квадрат прогресс обложка арт мини плеер artwork outline");
        Add("Толщина контура вокруг обложки", "Мини-плеер", "MiniPlayer", MiniArtworkProgressThicknessSlider, "толщина линия контур прогресс обложка мини плеер artwork outline thickness");
        Add("Цвет контура прогресса (мини-плеер)", "Мини-плеер", "MiniPlayer", MiniArtworkProgressColorModeRadioGroup, "акцент фиксированный цвет палитра контур прогресс обложка мини плеер artwork outline color");
        Add("Пуск / пауза", "Горячие клавиши", "Hotkeys", HotkeyPlayPauseButton, "play pause горячая клавиша");
        Add("Следующий трек", "Горячие клавиши", "Hotkeys", HotkeyNextButton, "next горячая клавиша");
        Add("Предыдущий трек", "Горячие клавиши", "Hotkeys", HotkeyPreviousButton, "previous горячая клавиша");
        Add("Стоп", "Горячие клавиши", "Hotkeys", HotkeyStopButton, "stop горячая клавиша");
        Add("Громкость +", "Горячие клавиши", "Hotkeys", HotkeyVolumeUpButton, "volume up громкость горячая клавиша");
        Add("Громкость -", "Горячие клавиши", "Hotkeys", HotkeyVolumeDownButton, "volume down громкость горячая клавиша");
        Add("Без звука", "Горячие клавиши", "Hotkeys", HotkeyMuteButton, "mute без звука горячая клавиша");
        Add("Перемешать", "Горячие клавиши", "Hotkeys", HotkeyShuffleButton, "shuffle перемешать горячая клавиша");
        Add("Режим повтора", "Горячие клавиши", "Hotkeys", HotkeyRepeatButton, "repeat повтор горячая клавиша");
        Add("Избранное: текущий трек", "Горячие клавиши", "Hotkeys", HotkeyToggleFavoriteButton, "избранное favorite текущий трек переключить горячая клавиша");
        Add("Показать / скрыть текст", "Горячие клавиши", "Hotkeys", HotkeyToggleLyricsButton, "текст lyrics панель показать скрыть переключить горячая клавиша");
        Add("Переключить мини-плеер", "Горячие клавиши", "Hotkeys", HotkeyToggleMiniPlayerButton, "мини плеер mini player переключить показать скрыть горячая клавиша");
        Add("Удалить трек с диска", "Горячие клавиши", "Hotkeys", HotkeyDeleteTrackButton, "delete удалить трек диск горячая клавиша");
        Add("Перемотка вперёд", "Горячие клавиши", "Hotkeys", HotkeySeekForwardButton, "seek forward вперёд перемотка 5 секунд горячая клавиша");
        Add("Перемотка назад", "Горячие клавиши", "Hotkeys", HotkeySeekBackwardButton, "seek backward назад перемотка 5 секунд горячая клавиша");
        Add("Сбросить все горячие клавиши", "Горячие клавиши", "Hotkeys", HotkeysResetAllButton, "сброс сбросить очистить все горячие клавиши reset all hotkeys clear");
        Add("Режим шаффла", "Воспроизведение", "Playback", ShuffleModeRadioGroup, "шаффл перемешать shuffle режим колода случайный любимые редкие папки альбомы чаще по папкам");
        Add("Баланс левого и правого канала", "Воспроизведение", "Playback", PlaybackBalanceSlider, "баланс левый правый наушник канал balance left right pan earphone");
        Add("Измерять громкость файлов без тегов", "Воспроизведение", "Playback", LoudnessScanCheckBox, "громкость измерение анализ replaygain r128 lufs без тегов нормализация");
        Add("Кроссфейд при автопереходе", "Воспроизведение", "Playback", CrossfadeSlider, "кроссфейд crossfade плавный переход наложение треков");
        Add("Переход между треками без паузы", "Воспроизведение", "Playback", GaplessCheckBox, "гэплесс gapless без паузы переход трек альбом бесшовный");
        Add("Ширина стереобазы", "Воспроизведение", "Playback", StereoWidthSlider, "ширина стерео база stereo width моно шире");
        Add("Кроссфид для наушников", "Воспроизведение", "Playback", CrossfeedSlider, "кроссфид crossfeed наушники стерео голова");
        Add("Тонкомпенсация громкости", "Воспроизведение", "Playback", LoudnessCompensationSlider, "тонкомпенсация loudness громкость низы тихо");
        Add("Анимация подключения устройства", "Воспроизведение", "Playback", DeviceConnectStyleCombo, "подключение устройство наушники bluetooth анимация карточка connect device airpods");
        Add("Анимация карточки устройства", "Воспроизведение", "Playback", DeviceCardAnimationCombo, "анимация карточка устройство волна орбита свечение раскрытие wave orbit glow animation");
        Add("Значок устройства", "Воспроизведение", "Playback", DeviceKindCombo, "значок иконка устройство наушники колонки вручную icon headphones speakers override");
        Add("Устройство вывода", "Воспроизведение", "Playback", OutputDeviceCombo, "звук аудио устройство вывод наушники колонки динамики speakers headphones audio output wasapi shared");
        Add("Защита от клиппинга (лимитер)", "Воспроизведение", "Playback", LimiterCheckBox, "лимитер клиппинг перегрузка искажения limiter clipping громкость пики");
        Add("Скорость меняет тон", "Воспроизведение", "Playback", PlaybackSpeedChangesPitchCheckBox, "скорость тон винил pitch rate speed ускорить замедлить");
        Add("ReplayGain: альбомный режим", "Воспроизведение", "Playback", ReplayGainAlbumCheckBox, "replaygain альбом album режим громкость");
        Add("ReplayGain: предусилитель", "Воспроизведение", "Playback", ReplayGainPreampSlider, "replaygain предусилитель preamp громкость усиление дб");
        Add("ReplayGain", "Воспроизведение", "Playback", ReplayGainCheckBox, "replaygain громкость выравнивание нормализация gain");
        Add("Каналы: стерео и моно", "Воспроизведение", "Playback", ChannelModeRadioGroup, "каналы стерео моно mono stereo downmix один наушник channels по умолчанию");
        Add("Уведомление о смене трека", "Уведомления", "Notifications", TrackChangeToastCheckBox, "уведомление тост смена трека toast notification");
        Add("Полоса воспроизведения", "Полоса и ползунки", "Progress", ProgressStyleWaveformRadio, "полоса воспроизведения прогресс seek слайдер waveform форма звука soundcloud material волна цвет толщина ручка форма концов предпросмотр для каждого окна progress bar color thickness wave thumb per window как в главном окне по умолчанию обычная волнистая линия android slider ползунок с зазором");
        Add("Режим текста песни", "Тексты песен", "Lyrics", LyricsDisplayPlainRadio, "текст песни синхронный обычный lrc txt переключить режим synced plain lyrics если он есть по умолчанию");
        Add("Показать пример уведомления", "Уведомления", "Notifications", ToastPreviewButton, "уведомление пример предпросмотр показать toast preview");
        Add("Расположение уведомления", "Уведомления", "Notifications", ToastPosTopLeftRadio, "уведомление угол расположение позиция монитор экран размер position monitor screen size сверху по центру справа снизу слева");
        Add("Своё положение уведомления", "Уведомления", "Notifications", ToastPickPositionButton, "уведомление перенести перетащить положение позиция свой custom drag move position toast notification");
        Add("Когда показывать", "Уведомления", "Notifications", ToastPolicyEveryTrackChangeRadio, "уведомление тост смена трека воспроизведение ручной выбор policy toast notification playback manual только при начале воспроизведения моём выборе");
        Add("Размер уведомления", "Уведомления", "Notifications", ToastSizeSmallRadio, "размер уведомление тост маленький средний большой size toast notification");
        Add("Выравнивание текста песни", "Тексты песен", "Lyrics", LyricsAlignLeftRadio, "слева по центру справа выравнивание текст песни lyrics alignment now playing");
        Add("Дополнительный источник текстов", "Тексты песен", "Lyrics", UseExtraLyricsSourceCheckBox, "текст песни lyrics источник netease qq kugou paxsenix lyricify русские исполнители extra source");
        Add("Фон Now Playing", "Обложка и Now Playing", "Cover", NowPlayingBackgroundCloudsRadio, "фон now playing размытая обложка один цвет без фона облака цветные шары волны background orbs clouds waves простой тёмный");
        Add("Пресет оформления текста песни", "Тексты песен", "Lyrics", LyricsPresetCombo, "пресет оформление текст песни караоке apple music классика минимализм сохранить свой preset lyrics style");
        Add("Переход между строками текста", "Тексты песен", "Lyrics", LyricsTransitionCombo, "переход анимация строк текст песни apple music плавный быстрый волна прокрутка lyrics transition animation");
        Add("Скрывать обложку при тексте песни", "Тексты песен", "Lyrics", HideCoverInLyricsCheckBox, "обложка скрыть текст песни больше места lyrics cover hide");
        Add("Анимация избранного", "Обложка и Now Playing", "Cover", FavoriteHeartFillRadio, "сердечко избранное анимация заливка искры кольцо favorite heart animation");
        Add("Размер мини-плеера", "Мини-плеер", "MiniPlayer", MiniPlayerSizePresetRadioGroup, "размер мини плеер компактный классический текущий 1.21 size preset");
        Add("Цвет уведомления", "Уведомления", "Notifications", ToastColorModeRadioGroup, "цвет уведомления обложка акцент свой окрашивание фон toast color cover accent tint стандартный по теме умолчанию акцентный приложения");
        Add("Прозрачность уведомления", "Уведомления", "Notifications", ToastOpacitySlider, "прозрачность фона уведомление тост opacity transparency toast");
        Add("Ширина уведомления", "Уведомления", "Notifications", ToastWidthSlider, "ширина уведомление тост размер width toast notification size");
        Add("Экспортировать настройки", "Профиль", "Profile", ExportProfileButton, "экспорт настройки профиль lumi файл backup export profile");
        Add("Импортировать настройки", "Профиль", "Profile", ImportProfileButton, "импорт настройки профиль lumi файл backup import restore profile");
        Add("Кэш текстов песен", "Тексты песен", "Lyrics", ClearLyricsCacheButton, "текст lyrics кэш очистить память локальный cache lyrics clear");
        Add("Сбросить плеер к исходному состоянию", "Профиль", "Profile", ResetPlayerButton, "сброс сбросить умолчание reset default настройки factory");
        Add("Вернуть состояние до последнего сброса", "Профиль", "Profile", RestoreResetSnapshotButton, "восстановить вернуть точка сброса backup restore reset recovery");
        Add("Полный сброс данных", "Профиль", "Profile", ResetAllDataButton, "сброс удалить очистить стереть настройки плейлисты избранное история пресеты wipe erase all data reset");
        Add("О плеере", "О плеере", "About", AboutInfoCard, "версия lumisense о программе о плеере");
        Add("Источник загрузки обновлений", "Обновления", "Updates", UpdateSourceGitHubRadio, "update mirror зеркало gh-proxy обновление скачать источник v4.gh proxy.org только ipv4 v6.gh ipv6 cdn.gh через cdn");
        Add("Все версии", "Обновления", "Updates", AllVersionsExpanderControl, "версии история версия откат downgrade install version releases обновление скачать установить zip exe установщик");
        Add("Проверить обновления", "Обновления", "Updates", CheckUpdatesButton, "обновление update github версия проверить");
        Add("Проверить зеркала", "Обновления", "Updates", ProbeUpdateSourcesButton, "зеркало скорость проверка источник mirror speed probe gh-proxy ghfast");
        Add("Скачать базовый пакет", "Обновления", "Updates", PrepareVelopackBasePackageButton, "velopack базовый пакет полный пакет компактные обновления delta base package");
        Add("Удалить старую EXE-копию", "Обновления", "Updates", RemoveLegacyInnoButton, "удалить uninstall деинсталлятор старая exe msi migration cleanup compact updates");
        Add("История изменений", "О плеере", "About", ChangelogButton, "патчноуты changelog версии история изменений");
        Add("Разработчик", "О плеере", "About", DeveloperGitHubButton, "разработчик автор github telegram wasssly ссылки контакты аватар");
        Add("Открыть папку с логами", "О плеере", "About", OpenLogsButton, "логи log ошибка краш crash диагностика");
        Add("Фон у кнопок управления", "Окно и запуск", "Window", ControlButtonsBackgroundCheckBox, "кнопки плашка подложка прозрачные без фона buttons background plate transparent");
        Add("Вид кнопки Пуск/Пауза", "Окно и запуск", "Window", PlayButtonStyleRadioGroup, "акцентный фон как у остальных кнопок play pause accent");
        Add("Меню «Ещё» (три точки)", "Окно и запуск", "Window", MoreMenuStyleRadioGroup, "три точки очередь текст песни меню пружина анимация dynamic menu more");
        Add("Форма кнопки Пуск/Пауза", "Окно и запуск", "Window", PlayButtonShapeRadioGroup, "квадратный круглый круг квадрат по умолчанию форма кнопки play pause square round circle shape");
        Add("Диагностика производительности", "О плеере", "About", DiagnosticsButton, "проверка нагрузка процессор память утечки тест фоновое наблюдение за ресурсами watchdog diagnostics performance cpu memory leak");
        Add("Автоматически переводить журнал изменений на английский", "Оформление", "Appearance", ChangelogAutoTranslateCheckBox, "чейнджлог changelog перевод английский google translate история изменений патчноуты");
        Add("Показывать названия рядом с иконками", "Оформление", "Appearance", NavShowLabelsCheckBox, "подписи названия иконки навигация labels icons");
        Add("Сохранить пресет текста песни", "Тексты песен", "Lyrics", LyricsPresetSaveButton, "пресет сохранить удалить preset save delete lyrics style");
        Add("Размер шрифта текста песни", "Тексты песен", "Lyrics", SyncedLyricsFontSizeSlider, "шрифт размер текст font size lyrics");
        Add("Размер текста в Now Playing", "Тексты песен", "Lyrics", NowPlayingLyricsFontSizeSlider, "шрифт размер текст песни font size lyrics");
        Add("Эффект активной строки", "Тексты песен", "Lyrics", SyncedLyricsEffectGlowRadio, "свечение подсветка активная строка glow highlight effect и подложка только полоска слева без эффекта");
        Add("Сила свечения активной строки", "Тексты песен", "Lyrics", SyncedLyricsGlowStrengthSlider, "свечение glow");
        Add("Увеличивать активную строку", "Тексты песен", "Lyrics", SyncedLyricsScaleCheckBox, "масштаб увеличение scale zoom");
        Add("Степень увеличения активной строки", "Тексты песен", "Lyrics", SyncedLyricsScalePercentSlider, "масштаб процент scale zoom");
        Add("Приглушать дальние строки", "Тексты песен", "Lyrics", SyncedLyricsFadeCheckBox, "прозрачность затемнение dim fade");
        Add("Степень приглушения дальних строк", "Тексты песен", "Lyrics", SyncedLyricsFadeStrengthSlider, "прозрачность затемнение dim fade");
        Add("Размывать неактивные строки", "Тексты песен", "Lyrics", SyncedLyricsBlurCheckBox, "размытие blur");
        Add("Сила размытия неактивных строк", "Тексты песен", "Lyrics", SyncedLyricsBlurStrengthSlider, "размытие blur");
        Add("Видимых строк в Now Playing", "Тексты песен", "Lyrics", SyncedLyricsVisibleLinesSlider, "количество строк lines count");
        Add("Откуда брать тексты", "Тексты песен", "Lyrics", LyricsPolicyRadioGroup, "только локальные lrclib интернет автоматически источник local online policy искать точное совпадение по моему запросу");
        Add("NetEase Cloud Music", "Тексты песен", "Lyrics", UseNetEaseLyricsCheckBox, "тексты источник дополнительный lyrics source netease");
        Add("QQ Music", "Тексты песен", "Lyrics", UseQqMusicLyricsCheckBox, "тексты источник дополнительный lyrics source qq");
        Add("Kugou", "Тексты песен", "Lyrics", UseKugouLyricsCheckBox, "тексты источник дополнительный lyrics source");
        Add("Paxsenix (Apple Music)", "Тексты песен", "Lyrics", UsePaxsenixLyricsCheckBox, "тексты источник дополнительный lyrics source apple music");
        Add("Better Lyrics", "Тексты песен", "Lyrics", UseBetterLyricsCheckBox, "тексты источник дополнительный lyrics source");
        Add("Binimum", "Тексты песен", "Lyrics", UseBinimumLyricsCheckBox, "тексты источник дополнительный lyrics source");
        Add("LyricsPlus", "Тексты песен", "Lyrics", UseLyricsPlusLyricsCheckBox, "тексты источник дополнительный lyrics source");
        Add("Состав подписи под названием трека", "Обложка и Now Playing", "Cover", BadgePartStateCheckBox, "состояние расширение битрейт частота дискретизации размер файла badge bitrate sample rate extension file size");
        Add("Режим WASAPI", "Воспроизведение", "Playback", WasapiModeCombo, "wasapi exclusive shared эксклюзивный общий");
        Add("Размер буфера WASAPI", "Воспроизведение", "Playback", WasapiLatencyCombo, "задержка latency buffer wasapi");
        Add("Повышенный приоритет плеера", "Воспроизведение", "Playback", RaisePlaybackPriorityCheckBox, "priority процесс приоритет");
        Add("Копировать аудиодиагностику", "Воспроизведение", "Playback", CopyAudioDiagnosticsButton, "диагностика устройство audio diagnostics");
        Add("Подробный trace подготовки трека", "Воспроизведение", "Playback", TrackLoadTraceCheckBox, "trace журнал отладка логи");
        Add("Сохранять очередь «Играть следующим» между запусками", "Воспроизведение", "Playback", SaveQueueBetweenRestartsCheckBox, "очередь queue play next");
        Add("Не смешивать треки одного альбома", "Воспроизведение", "Playback", CrossfadeSkipSameAlbumCheckBox, "кроссфейд crossfade album альбом");
        Add("Область настройки полосы прогресса", "Полоса и ползунки", "Progress", ProgressScopeRadioGroup, "одинаковая для всех окон отдельная для каждого scope shared per window своя окна по умолчанию");
        Add("Окно, для которого настраивается полоса", "Полоса и ползунки", "Progress", ProgressSurfaceMainRadio, "главное окно мини-плеер now playing window surface");
        Add("Цвет полосы прогресса", "Полоса и ползунки", "Progress", ProgressColorRadioGroup, "акцент обложка свой цвет accent cover custom color от обложки текущего трека");
        Add("Толщина дорожки прогресса", "Полоса и ползунки", "Progress", ProgressThicknessSlider, "толщина thickness");
        Add("Размер ручки прогресса", "Полоса и ползунки", "Progress", ProgressThumbSlider, "ручка thumb handle size");
        Add("Форма концов полосы прогресса", "Полоса и ползунки", "Progress", ProgressShapeRoundRadio, "скруглённые прямые round square");
        Add("Высота волны прогресса", "Полоса и ползунки", "Progress", ProgressWaveAmplitudeSlider, "волна wave amplitude");
        Add("Длина волны прогресса", "Полоса и ползунки", "Progress", ProgressWaveLengthSlider, "волна wave length");
        Add("Скорость волны прогресса", "Полоса и ползунки", "Progress", ProgressWaveSpeedSlider, "волна wave speed");
        Add("Волна бежит и на паузе", "Полоса и ползунки", "Progress", ProgressWaveAlwaysCheckBox, "волна пауза wave pause");
        Add("Ручка на волне", "Полоса и ползунки", "Progress", ProgressWaveHandleCheckBox, "волна ручка wave handle");
        Add("Сделать контур волнистым", "Полоса и ползунки", "Progress", ProgressRingMakeWavyButton, "волна контур ring wavy");
        Add("Вид столбиков прогресса", "Полоса и ползунки", "Progress", ProgressWaveformWaveRadio, "тонкие толстые столбики waveform bars скруглённые");
        Add("Зазор между столбиками прогресса", "Полоса и ползунки", "Progress", ProgressWaveformGapSlider, "столбики waveform gap");
        Add("Прозрачность непроигранной части", "Полоса и ползунки", "Progress", ProgressWaveformOpacitySlider, "прозрачность opacity waveform");
        Add("Показывать время в точке под курсором", "Полоса и ползунки", "Progress", ProgressHoverTimeCheckBox, "наведение время hover time");
        Add("Подсвечивать участок до курсора", "Полоса и ползунки", "Progress", ProgressHoverHighlightCheckBox, "наведение подсветка hover highlight");
        Add("Сбросить вид полосы", "Полоса и ползунки", "Progress", ProgressAppearanceResetButton, "сброс reset");
        Add("Стиль ползунков", "Полоса и ползунки", "Progress", SliderStyleRadioGroup, "ползунок слайдер громкость обычный material slider volume тонкая ручка с зазором как в android");
        Add("Играть следующим", "Библиотека и файлы", "Library", TrackContextPlayNextCheckBox, "контекстное меню трека context menu очередь queue play next");
        Add("Добавить в очередь", "Библиотека и файлы", "Library", TrackContextAddToQueueCheckBox, "контекстное меню трека context menu очередь queue");
        Add("Показать в проводнике", "Библиотека и файлы", "Library", TrackContextShowInExplorerCheckBox, "контекстное меню трека context menu explorer папка файл show in explorer");
        Add("Копировать имя трека", "Библиотека и файлы", "Library", TrackContextCopyNameCheckBox, "контекстное меню трека context menu copy name");
        Add("Копировать путь к файлу", "Библиотека и файлы", "Library", TrackContextCopyPathCheckBox, "контекстное меню трека context menu copy path");
        Add("Копировать файл", "Библиотека и файлы", "Library", TrackContextCopyFileCheckBox, "контекстное меню трека context menu copy file");
        Add("Сохранить обработанную копию", "Библиотека и файлы", "Library", TrackContextExportProcessedCopyCheckBox, "контекстное меню трека context menu export processed copy экспорт эквалайзер");
        Add("Свойства", "Библиотека и файлы", "Library", TrackContextPropertiesCheckBox, "контекстное меню трека context menu properties информация");
        Add("Изменить теги", "Библиотека и файлы", "Library", TrackContextEditTagsCheckBox, "контекстное меню трека context menu теги tags редактор tag editor");
        Add("Нормализовать имя файла", "Библиотека и файлы", "Library", TrackContextNormalizeFileNameCheckBox, "контекстное меню трека context menu имена файлов normalize file name");
        Add("Убрать из плейлиста", "Библиотека и файлы", "Library", TrackContextRemoveFromPlaylistCheckBox, "контекстное меню трека context menu remove from playlist");
        Add("Удалить с диска", "Библиотека и файлы", "Library", TrackContextDeleteFromDiskCheckBox, "контекстное меню трека context menu delete from disk корзина");
        Add("Шаблон нормализации имён файлов", "Библиотека и файлы", "Library", FileNameNormalizationTemplateTextBox, "шаблон template исполнитель название имена файлов");
        Add("Положение обложки в уведомлении", "Уведомления", "Notifications", ToastArtLeftRadio, "слева справа cover position left right");
        Add("Выравнивание текста в уведомлении", "Уведомления", "Notifications", ToastTextLeftRadio, "слева по центру справа text alignment");
        Add("Прижать обложку к тексту", "Уведомления", "Notifications", ToastArtNextToTextCheckBox, "обложка текст cover text");
        Add("Монитор уведомления", "Уведомления", "Notifications", ToastMonitorCombo, "монитор экран monitor screen");
        Add("Сила окрашивания уведомления", "Уведомления", "Notifications", ToastColorStrengthSlider, "цвет окрашивание color tint");
        Add("Полосы эквалайзера", "Эквалайзер", "Equalizer", EqBand0Slider, "31 62 125 250 500 1k 2k 4k 8k 16k гц hz частоты усиление bands gain");
        Add("Пресеты эквалайзера", "Эквалайзер", "Equalizer", EqualizerPresetComboBox, "применить сохранить удалить поделиться импортировать экспорт preset apply save delete share import export");
        Add("Сбросить настройки эквалайзера", "Эквалайзер", "Equalizer", EqualizerResetButton, "сбросить всё reset flat");
        Add("Включать автоматически при обнаружении игры или оверлея", "Мини-плеер", "MiniPlayer", GameOverlayAutoDetectCheckBox, "игра оверлей совместимость game overlay compatibility");
        Add("Держать режим совместимости с играми включённым всегда", "Мини-плеер", "MiniPlayer", GameOverlayCompatibilityCheckBox, "игра оверлей совместимость game overlay compatibility always");
        Add("Действия контекстного меню мини-плеера", "Мини-плеер", "MiniPlayer", MiniContextSettingsCheckBox, "контекстное меню настройки now playing закрепить поверх окон совместимость вторая кнопка скорость тон прозрачность прилипание полоса прогресса расположение кнопок context menu actions полный экран с играми и оверлеем выбор второй кнопки");
        Add("Расположение кнопок (мини-плеер)", "Мини-плеер", "MiniPlayer", MiniButtonsLayoutRadioGroup, "снизу сбоку кнопки управление buttons below на месте обложки и названия размер окна не меняется");
        Add("Стиль контура прогресса вокруг обложки", "Мини-плеер", "MiniPlayer", MiniPlayerArtworkProgressStyleRadioGroup, "обычный волнистый wavy outline material волнистая линия как в android slider ползунок с зазором");
        Add("Что показывать под названием (мини-плеер)", "Мини-плеер", "MiniPlayer", MiniInfoModeRadioGroup, "исполнитель альбом artist album ничего только название трека оставшееся время");
        Add("Вид свёрнутого острова", "Остров", "Island", IslandShapeCombo, "капсула круг notch nook вырез capsule circle shape");
        Add("Notch Nook: раскрывать при наведении курсора", "Остров", "Island", IslandNotchHoverOpenCheckBox, "вырез наведение hover notch");
        Add("Размер острова", "Остров", "Island", IslandScaleSlider, "масштаб scale size");
        Add("Ширина острова", "Остров", "Island", IslandWidthSlider, "width");
        Add("Отступ острова от верхнего края", "Остров", "Island", IslandTopGapSlider, "отступ сверху gap top");
        Add("Скрытие острова по средней кнопке мыши", "Остров", "Island", IslandHideSlider, "скрыть средняя кнопка колесо hide middle click");
        Add("Сворачивание острова после ухода курсора", "Остров", "Island", IslandCollapseSlider, "таймер задержка collapse delay");
        Add("Раскрываться при смене трека", "Остров", "Island", IslandTrackToastCheckBox, "смена трека уведомление track change");
        Add("Не показывать уведомление о смене трека в виде «Остров»", "Остров", "Island", IslandHideSystemToastCheckBox, "уведомление toast");
        Add("Сворачиваться, когда курсор ушёл", "Остров", "Island", IslandAutoCollapseCheckBox, "collapse cursor");
        Add("Эквалайзер в свёрнутом виде", "Остров", "Island", IslandEqualizerCheckBox, "полоски equalizer bars");
        Add("Вместо эквалайзера показывать устройство вывода", "Остров", "Island", IslandCompactDeviceCheckBox, "устройство значок device icon");
        Add("Обводка цветом акцента во время игры", "Остров", "Island", IslandAccentRimCheckBox, "акцент обводка игра accent rim");
        Add("Полоса прогресса трека вокруг свёрнутого острова", "Остров", "Island", IslandProgressRimCheckBox, "контур прогресс ring progress");
        Add("Кольцо прогресса рядом с островом", "Остров", "Island", IslandRingSideCombo, "кольцо круг прогресс слева справа ring progress side");
        Add("Размытие при смене вида", "Остров", "Island", IslandBlurTransitionsCheckBox, "blur transitions анимация");
        Add("Клик по обложке открывает её крупно", "Остров", "Island", IslandCoverPreviewCheckBox, "обложка просмотр cover preview");
        Add("Показывать громкость, когда её меняют не на острове", "Остров", "Island", IslandExternalVolumeCheckBox, "громкость volume");
        Add("Не раскрываться при смене трека в играх", "Остров", "Island", IslandQuietInGamesCheckBox, "игры games quiet");
        Add("Перетягивание острова: влево — назад, вправо — вперёд", "Остров", "Island", IslandSwipeInvertedCheckBox, "свайп swipe перетягивание");
        Add("Круглая обложка", "Остров", "Island", IslandRoundCoverCheckBox, "round cover");
        Add("Принимать перетаскиваемые файлы и папки", "Остров", "Island", IslandAcceptDropsCheckBox, "drag drop перетаскивание файлы");
        Add("Левая кнопка раскрытого острова", "Остров", "Island", IslandLeftButtonCombo, "кнопка button left");
        Add("Правая кнопка раскрытого острова", "Остров", "Island", IslandRightButtonCombo, "кнопка button right");
        Add("Эквалайзер свёрнутого острова", "Остров", "Island", IslandEqualizerStyleCombo, "полоски equalizer style");
        Add("Фон раскрытого острова", "Остров", "Island", IslandBackdropCombo, "фон backdrop background");
        Add("Полоса перемотки раскрытого острова", "Остров", "Island", IslandSeekStyleCombo, "перемотка seek bar");
        Add("Мерцание, пока ищется текст песни", "Остров", "Island", IslandLyricsShimmerCheckBox, "текст песни поиск lyrics shimmer");
        Add("Название трека катается по буквам", "Остров", "Island", IslandWordRollCheckBox, "word roll анимация название трек буквы");
        Add("Строки песни в свёрнутом острове катаются по буквам", "Остров", "Island", IslandLyricsWordRollCheckBox, "word roll анимация строка песни текст буквы свёрнутый");
        Add("Подсветка слов в тексте песни (бета)", "Остров", "Island", IslandReadAlongCheckBox, "read along слова подсветка пилюля текст песни караоке");
        Add("Размытие движения при растяжении", "Остров", "Island", IslandMotionBlurCheckBox, "motion blur растяжение");
        Add("Действия контекстного меню острова", "Остров", "Island", IslandMenuPlayPauseCheckBox, "контекстное меню пауза следующий предыдущий скрыть развернуть now playing скорость тон совместимость настройки context menu actions");
        Add("Масштаб интерфейса", "Оформление", "Appearance", InterfaceScaleSlider, "размер текст scale interface size");
        Add("Все анимации", "Оформление", "Appearance", AnimationsMasterCheckBox, "движение анимация слайдеры переходы обложка текст песни полоса мини-плеер бегущая строка остров меню трея сердечко индикаторы загрузки motion animations reduce");
        Add("Now Playing: фон и переходы", "Оформление", "Appearance", AnimationNowPlayingCheckBox, "анимация фон now playing animation background");
        Add("Показывать дополнительные кнопки по бокам от кнопок управления", "Окно и запуск", "Window", ModernShowSideButtonsCheckBox, "кнопки слоты управление extra buttons slots");
        Add("Подложка у кнопок управления (вид «Стекло»)", "Окно и запуск", "Window", GlassControlsBackgroundCheckBox, "стекло glass подложка controls background");
        Add("Выравнивание обложки в виде «Компакт»", "Окно и запуск", "Window", CompactCoverAlignmentCombo, "компакт обложка выравнивание compact cover alignment");
    }

    private void SettingsSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => RefreshSearchResults();

    private void RefreshSearchResults()
    {
        string query = SettingsSearchBox.Text.Trim();

        if (query.Length == 0)
        {
            NavCategoriesPanel.Visibility = Visibility.Visible;
            SearchResultsHost.Visibility = Visibility.Collapsed;
            UpdateSearchOverlayVisibility();
            return;
        }

        NavCategoriesPanel.Visibility = Visibility.Collapsed;
        SearchResultsHost.Visibility = Visibility.Visible;
        UpdateSearchOverlayVisibility();

        string queryLower = query.ToLowerInvariant();
        _searchResults.Clear();
        foreach (var entry in _searchIndex)
        {
            if (entry.Keywords.Contains(queryLower, StringComparison.Ordinal))
                _searchResults.Add(entry);
        }

        bool hasResults = _searchResults.Count > 0;
        SearchResultsList.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
        SearchEmptyState.Visibility = hasResults ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SearchResultItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SettingsSearchEntry entry }) return;

        System.Windows.Controls.RadioButton navButton = entry.PageKey switch
        {
            "Appearance" => NavAppearance,
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
            "Updates" => NavUpdates,
            "About" => NavAbout,
            _ => NavAppearance
        };
        navButton.IsChecked = true;

        // Возвращаемся к обычному виду навигации — поиск своё дело сделал
        SettingsSearchBox.Text = string.Empty;

        // Ждём, пока страница станет видимой и разложится по месту, и только потом
        // прокручиваем к нужному элементу и подсвечиваем его
        Dispatcher.InvokeAsync(() =>
        {
            // Часть результатов поиска лежит в свёрнутых Expander (FluentExpanderStyle в App.xaml): BringIntoView и подсветка
            // скрытого элемента ничего не покажут, поэтому заранее разворачиваем все Expander-предки.
            ExpandAncestorExpanders(entry.Target);

            // Раскрытие аккордеона меняет раскладку: ждём ещё один цикл, иначе прокрутка и подсветка считались бы
            // по устаревшим координатам.
            Dispatcher.InvokeAsync(() =>
            {
                FrameworkElement shown = NearestVisible(entry.Target);
                shown.BringIntoView();
                SearchHighlightAdorner.Flash(shown);
            }, DispatcherPriority.Loaded);
        }, DispatcherPriority.Loaded);
    }

    // Часть настроек видна только при определённом выборе (параметры волны при стиле «Волна»): у скрытого элемента
    // подсветка ничего не покажет, поэтому берётся ближайший видимый предок, то есть карточка с переключателем.
    private static FrameworkElement NearestVisible(FrameworkElement target)
    {
        DependencyObject? current = target;
        while (current is not null)
        {
            if (current is FrameworkElement { IsVisible: true } visible) return visible;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return target;
    }

    private static void ExpandAncestorExpanders(DependencyObject element)
    {
        var current = System.Windows.Media.VisualTreeHelper.GetParent(element);
        while (current != null)
        {
            if (current is System.Windows.Controls.Expander expander) expander.IsExpanded = true;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
    }

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
    private sealed class Wpf32Window : System.Windows.Forms.IWin32Window
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

    public void ApplyAccessibilityPreferences() => AccessibilityPreferences.ApplyToWindow(this, _settings);

    private bool _isAutoScrolling;
    private Cursor? _previousCursor;
    private Point _autoScrollOrigin;
    private System.Windows.Controls.ScrollViewer? _autoScrollViewer;
    private bool _autoScrollHooked;
    private double _autoScrollVelocity, _autoScrollOffset;
    private long _autoScrollLastFrame;

    private void SettingsWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;

        if (_isAutoScrolling)
        {
            StopAutoScrolling();
            e.Handled = true;
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        _autoScrollViewer = FindVisualAncestor<System.Windows.Controls.ScrollViewer>(source)
                             ?? PART_ContentScroll;
        if (_autoScrollViewer is null || _autoScrollViewer.ScrollableHeight <= 0) return;

        _autoScrollOrigin = e.GetPosition(RootLayout);
        _previousCursor = Cursor;
        _isAutoScrolling = true;
        Mouse.Capture(this, CaptureMode.SubTree);
        Cursor = Cursors.ScrollNS;
        StopWheelScroll();
        _autoScrollOffset = _autoScrollViewer.VerticalOffset;
        _autoScrollVelocity = 0;
        _autoScrollLastFrame = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!_autoScrollHooked)
        {
            _autoScrollHooked = true;
            CompositionTarget.Rendering += AutoScrollFrame;
        }

        e.Handled = true;
    }

    private void SettingsWindow_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isAutoScrolling || _autoScrollViewer is null) return;

        Point current = Mouse.GetPosition(RootLayout);
        double distance = current.Y - _autoScrollOrigin.Y;
        Cursor = distance < -4 ? Cursors.ScrollN : distance > 4 ? Cursors.ScrollS : Cursors.ScrollNS;
        e.Handled = true;
    }

    private void SettingsRoot_LostMouseCapture(object sender, MouseEventArgs e)
    {
        StopAutoScrolling();
    }

    // Автопрокрутка идёт по кадрам отрисовки, а не по таймеру: скорость сглаживается, поэтому страница разгоняется и тормозит плавно,
    // а положение не округляется до пикселя на каждом тике. Чем дальше курсор от точки запуска, тем быстрее страница.
    private const double AutoScrollDeadZone = 8, AutoScrollMaxSpeed = 2000, AutoScrollSmoothingSeconds = 0.12;

    private void AutoScrollFrame(object? sender, EventArgs e)
    {
        if (!_isAutoScrolling || _autoScrollViewer is null) return;

        double dt = Math.Min(System.Diagnostics.Stopwatch.GetElapsedTime(_autoScrollLastFrame).TotalSeconds, 0.05);
        _autoScrollLastFrame = System.Diagnostics.Stopwatch.GetTimestamp();
        if (dt <= 0) return;

        double distance = Mouse.GetPosition(RootLayout).Y - _autoScrollOrigin.Y;
        double effective = Math.Max(0, Math.Abs(distance) - AutoScrollDeadZone);
        double target = Math.Sign(distance) * Math.Min(AutoScrollMaxSpeed, 6.5 * effective + 0.015 * effective * effective);
        _autoScrollVelocity += (target - _autoScrollVelocity) * (1 - Math.Exp(-dt / AutoScrollSmoothingSeconds));
        if (Math.Abs(_autoScrollVelocity) < 0.5 && target == 0) return;

        _autoScrollOffset = Math.Clamp(_autoScrollOffset + _autoScrollVelocity * dt, 0, _autoScrollViewer.ScrollableHeight);
        _autoScrollViewer.ScrollToVerticalOffset(_autoScrollOffset);
    }

    private void StopAutoScrolling()
    {
        if (!_isAutoScrolling && !_autoScrollHooked) return;
        _isAutoScrolling = false;
        if (_autoScrollHooked)
        {
            _autoScrollHooked = false;
            CompositionTarget.Rendering -= AutoScrollFrame;
        }

        _autoScrollVelocity = 0;
        if (Mouse.Captured is not null) Mouse.Capture(null);
        Cursor = _previousCursor ?? Cursors.Arrow;
        _previousCursor = null;
        _autoScrollViewer = null;
    }

    private void SettingsRoot_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Вертикальные Slider (в первую очередь эквалайзер) используют колесо для изменения
        // своего значения и сами помечают событие обработанным. Не перехватываем их здесь.
        if (FindVisualAncestor<System.Windows.Controls.Slider>(e.OriginalSource as DependencyObject) is not null)
            return;

        var scrollViewer = FindVisualAncestor<System.Windows.Controls.ScrollViewer>(e.OriginalSource as DependencyObject)
                           ?? PART_ContentScroll;
        if (scrollViewer.ScrollableHeight <= 0) return;

        ScrollByWheel(scrollViewer, -e.Delta * 0.45);
        e.Handled = true;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        for (DependencyObject? current = source; current is not null;)
        {
            if (current is T match) return match;

            // TextBlock.Text часто отдаёт Run как OriginalSource, а он FrameworkContentElement, не Visual:
            // VisualTreeHelper.GetParent для него бросает InvalidOperationException.
            current = current switch
            {
                System.Windows.Media.Visual visual =>
                    System.Windows.Media.VisualTreeHelper.GetParent(visual),
                System.Windows.Media.Media3D.Visual3D visual3D =>
                    System.Windows.Media.VisualTreeHelper.GetParent(visual3D),
                System.Windows.FrameworkContentElement contentElement =>
                    contentElement.Parent,
                _ => null
            };
        }

        return null;
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
            var restartDialog = new ScaleRestartDialog(this, _settings);
            restartDialog.ShowDialog();
            // «Позже» относится только к текущему изменению. При следующем изменении
            // масштаба уведомление должно появиться снова.
            _interfaceScaleRestartNoticeShown = false;
        }
    }

    private IEnumerable<(string Menu, System.Windows.Controls.Panel Panel)> ContextMenuOrderPanels() =>
    [
        (ContextMenuOrdering.Track, TrackContextMenuActionsPanel),
        (ContextMenuOrdering.Mini, MiniContextMenuActionsPanel),
        (ContextMenuOrdering.Island, IslandMenuActionsPanel),
    ];

    private void ContextMenuOrderChanged(string menu, IReadOnlyList<string> ids)
    {
        if (_isInitializing) return;

        // Порядок по умолчанию не хранится: так новые пункты будущих версий попадут на свои места сами.
        if (ids.SequenceEqual(ContextMenuOrdering.Defaults[menu]))
            _settings.ContextMenuOrder.Remove(menu);
        else
            _settings.ContextMenuOrder[menu] = [.. ids];
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void ContextMenuOrderReset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string menu }) return;

        foreach (var (key, panel) in ContextMenuOrderPanels())
        {
            if (key != menu) continue;
            PanelReorder.Sort(panel, ContextMenuOrdering.Defaults[menu]);
            ContextMenuOrderChanged(menu, PanelReorder.Ids(panel));
        }
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
            SelectComboByTag(LyricsPresetCombo, current?.Name ?? string.Empty);
        }
        finally
        {
            _isInitializing = wasInitializing;
        }

        UpdateLyricsPresetButtons();
    }

    private void UpdateLyricsPresetButtons()
    {
        string selected = SelectedTag(LyricsPresetCombo, string.Empty);
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

        string name = SelectedTag(LyricsPresetCombo, string.Empty);
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
            SelectComboByTag(LyricsTransitionCombo, LyricsTransitionStyle.Normalize(_settings.SyncedLyricsTransition));
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

        _settings.SyncedLyricsTransition = LyricsTransitionStyle.Normalize(SelectedTag(LyricsTransitionCombo, LyricsTransitionStyle.Smooth));
        MarkLyricsPresetCustom();
        _owner.ApplySyncedLyricsAppearance();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void LyricsPresetSaveButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TextInputDialog("Сохранить пресет", "Название пресета:", _settings.LyricsPresetName, _settings) { Owner = this };
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
        string name = SelectedTag(LyricsPresetCombo, string.Empty);
        if (name.Length == 0 || LyricsPreset.IsBuiltInName(name)) return;

        _settings.CustomLyricsPresets.RemoveAll(preset => string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase));
        _settings.LyricsPresetName = string.Empty;
        RefreshLyricsPresetList();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

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

    private void ToastPreviewButton_Click(object sender, RoutedEventArgs e) => _owner.PreviewTrackChangeToast();

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
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (dialog.ShowDialog(new Wpf32Window(handle)) != System.Windows.Forms.DialogResult.OK) return;

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

    private void FavoriteHeartAnimationRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.FavoriteHeartAnimation = FavoriteHeartSparksRadio.IsChecked == true ? HeartAnimation.Sparks
            : FavoriteHeartRingRadio.IsChecked == true ? HeartAnimation.Ring
            : FavoriteHeartNoneRadio.IsChecked == true ? HeartAnimation.None
            : HeartAnimation.Fill;
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


    private void AlwaysOnTopCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.AlwaysOnTop = AlwaysOnTopCheckBox.IsChecked == true;

        // Пока активен мини-плеер, поверх окон управляет отдельная мини-настройка —
        // обычную применяем только когда плеер в обычном виде
        if (!_owner.IsMiniMode)
            _owner.Topmost = _settings.AlwaysOnTop;
    }

    private void RememberVolumeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.RememberVolume = RememberVolumeCheckBox.IsChecked == true;
    }

    private void LogarithmicVolumeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.UseLogarithmicVolume = LogarithmicVolumeCheckBox.IsChecked == true;
        _owner.RefreshVolumeCurve();
    }

    private void NeverAutoPlayLastTrackOnStartupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.NeverAutoPlayLastTrackOnStartup = NeverAutoPlayLastTrackOnStartupCheckBox.IsChecked == true;
    }

    private void TrackLoadTraceCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.TrackLoadTraceEnabled = TrackLoadTraceCheckBox.IsChecked == true;
        AudioDiagnosticsCopyStatusText.Text = _settings.TrackLoadTraceEnabled
            ? LocalizationService.Translate("Trace подготовки трека включён")
            : LocalizationService.Translate("Trace подготовки трека выключен");
    }

    private DiagnosticsWindow? _diagnosticsWindow;

    private void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_diagnosticsWindow is { IsLoaded: true })
        {
            _diagnosticsWindow.Activate();
            return;
        }

        // Без владельца: долгая проверка не должна прерываться, если закрыть окно настроек.
        _diagnosticsWindow = new DiagnosticsWindow(_settings, _owner.GetDiagnosticsHost())
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        _diagnosticsWindow.Closed += (_, _) => _diagnosticsWindow = null;
        _diagnosticsWindow.Show();
    }

    private void ClearArtworkCacheButton_Click(object sender, RoutedEventArgs e)
        => BackgroundTask.FireAndForget(ClearArtworkCacheButton_ClickAsync(sender, e), nameof(ClearArtworkCacheButton_Click));

    private async Task ClearArtworkCacheButton_ClickAsync(object sender, RoutedEventArgs e)
    {
        var confirmation = LocalizedMessageBox.Show(this,
            "Удалить локально сохранённые интернет-обложки?\n\nПри следующем поиске нужные изображения будут скачаны заново.",
            "Очистить кэш обложек?", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (confirmation != System.Windows.MessageBoxResult.Yes) return;

        ClearArtworkCacheButton.IsEnabled = false;
        ArtworkCacheClearResultText.Visibility = Visibility.Collapsed;
        try
        {
            var result = await System.Threading.Tasks.Task.Run(CoverArtSearchWindow.ClearArtworkCache);
            ArtworkCacheClearResultText.Text = result.DeletedFiles == 0
                ? "Кэш обложек уже пуст."
                : $"Удалено файлов: {result.DeletedFiles}; освобождено: {FormatArtworkCacheSize(result.FreedBytes)}."
                  + (result.FailedFiles > 0 ? $" Не удалось удалить файлов: {result.FailedFiles}." : string.Empty);
            ArtworkCacheClearResultText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ArtworkCacheClearResultText.Text = LocalizationService.Translate($"Не удалось очистить кэш: {ex.Message}");
            ArtworkCacheClearResultText.Visibility = Visibility.Visible;
        }
        finally
        {
            ClearArtworkCacheButton.IsEnabled = true;
        }
    }

    private static string FormatArtworkCacheSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} Б";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} КБ";
        return $"{bytes / (1024.0 * 1024.0):0.#} МБ";
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

    private void RefreshResetRecoveryButton() =>
        RestoreResetSnapshotButton.IsEnabled = SettingsResetRecoveryService.HasRecoverySnapshot;

    private void ClearLyricsCacheButton_Click(object sender, RoutedEventArgs e)
    {
        if (LyricsService.GetPastedLyricsCacheInfo().IsEmpty)
        {
            RefreshLyricsCacheInfo();
            return;
        }

        var confirm = LocalizedMessageBox.Show(this,
            LocalizationService.Get(LocalizationKey.ProfileLyricsCacheClearConfirm),
            LocalizationService.Get(LocalizationKey.ProfileLyricsCacheClearTitle), System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        if (!LyricsService.ClearPastedLyricsCache())
        {
            LocalizedMessageBox.Show(this, LocalizationService.Get(LocalizationKey.ProfileLyricsCacheClearFailed),
                LocalizationService.Get(LocalizationKey.ProfileLyricsCacheClearErrorTitle), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }

        RefreshLyricsCacheInfo();
    }

    private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
    {
        // Показанные результаты поиска хранят уже выведенные подписи: пересобираем их на новом языке.
        if (SettingsSearchBox.Text.Trim().Length > 0) RefreshSearchResults();

        RefreshAppVersionText();
        InitializeOutputDeviceCombo();
        InitializeToastMonitorCombo();
        InitializeIslandMonitorCombo();
        UpdateDiscordRichPresenceConnectionStatus();

        for (int band = 0; band < EqualizerSampleProvider.BandFrequencies.Length; band++)
            GetEqBandValueText(band).Text = FormatEqGain(_owner.GetEqualizerBandGain(band));

        if (_loadedReleases is not null)
            RenderAllVersions(_loadedReleases);

        RefreshLyricsCacheInfo();
        RefreshUpdateSourceProbePresentation();
        RefreshVelopackBasePackagePresentation();
        RefreshLegacyInnoCleanupPresentation();
        RefreshMiniArtworkProgressThicknessPresentation();
        if (_sourceProbeResults is not null)
            RenderUpdateSourceProbeResults(_sourceProbeResults);
    }

    private void DiscordRichPresenceEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.DiscordRichPresenceEnabled = DiscordRichPresenceEnabledCheckBox.IsChecked == true;
        UpdateDiscordRichPresenceConnectionStatus();
        _owner.ApplyDiscordRichPresenceSettingsLive();
    }

    private void ConnectDiscordButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.DiscordRichPresenceEnabled = true;
        DiscordRichPresenceEnabledCheckBox.IsChecked = true;
        DiscordRichPresenceLogger.Info("Пользователь включил Discord Rich Presence из настроек.");
        UpdateDiscordRichPresenceConnectionStatus();
        _owner.ApplyDiscordRichPresenceSettingsLive();
    }

    private void OpenDiscordDiagnosticsLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = DiscordRichPresenceLogger.LogFilePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            LocalizedMessageBox.Show(this, $"Не удалось открыть журнал Discord:\n{ex.Message}",
                "Discord Rich Presence", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private void UpdateDiscordRichPresenceConnectionStatus()
    {
        ConnectDiscordButton.Content = LocalizationService.Translate(_settings.DiscordRichPresenceEnabled
            ? "Обновить подключение Discord"
            : "Подключить Discord");
        DiscordRichPresenceConnectionStatusText.Text = LocalizationService.Translate(_settings.DiscordRichPresenceEnabled
            ? "Discord Rich Presence включён. При начале воспроизведения Lumisense обновит ваш статус."
            : "Нажмите «Подключить Discord», чтобы включить Rich Presence с официальным приложением Lumisense.");
    }

    private void DiscordRichPresencePrivacyCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.DiscordRichPresenceShowTrackInfo = DiscordRichPresenceShowTrackInfoCheckBox.IsChecked == true;
        _settings.DiscordRichPresenceShowTimeline = DiscordRichPresenceShowTimelineCheckBox.IsChecked == true;
        _settings.DiscordRichPresenceShowCoverArt = DiscordRichPresenceShowCoverArtCheckBox.IsChecked == true;
        _settings.DiscordRichPresenceShowButtons = DiscordRichPresenceShowButtonsCheckBox.IsChecked == true;
        _settings.DiscordRichPresenceShowDownloadButton = DiscordRichPresenceShowDownloadButtonCheckBox.IsChecked == true;
        _owner.ApplyDiscordRichPresenceSettingsLive();
    }

    private int _outputDeviceLoadVersion;
    private bool _isWindowClosed;

    // Готовый список показывается сразу, а актуальный перечисляется в фоне: чтение имён endpoint (COM) на UI-потоке при открытии
    // окна давало заметную паузу. Без кэша (первое открытие до прогрева, смена устройств) список строится на месте, как раньше.
    private void InitializeOutputDeviceCombo()
    {
        IReadOnlyList<AudioOutputDeviceService.Option>? cached = AudioOutputDeviceService.CachedDevices;
        if (cached is null)
        {
            PopulateOutputDeviceCombo(AudioOutputDeviceService.GetAvailableDevices());
            return;
        }

        PopulateOutputDeviceCombo(cached);
        FireAndForget(RefreshOutputDevicesInBackgroundAsync(cached, ++_outputDeviceLoadVersion), "RefreshOutputDevicesAsync");
    }

    private async Task RefreshOutputDevicesInBackgroundAsync(
        IReadOnlyList<AudioOutputDeviceService.Option> shown, int version)
    {
        IReadOnlyList<AudioOutputDeviceService.Option>? fresh = await AudioOutputDeviceService.TryGetAvailableDevicesAsync();

        // Окно закрыто или список уже перестроили более новым запросом; сбой перечисления оставляет показанный список.
        if (fresh is null || _isWindowClosed || version != _outputDeviceLoadVersion) return;
        if (fresh.Count == shown.Count && fresh.Zip(shown).All(pair => pair.First == pair.Second)) return;

        PopulateOutputDeviceCombo(fresh);
    }

    private void PopulateOutputDeviceCombo(IReadOnlyList<AudioOutputDeviceService.Option> devices)
    {
        _isRefreshingOutputDevices = true;
        try
        {
            OutputDeviceCombo.Items.Clear();
            var systemDefault = new System.Windows.Controls.ComboBoxItem
            {
                Content = LocalizationService.Translate("Системное устройство по умолчанию"),
                Tag = AudioOutputDeviceService.SystemDefaultDeviceName
            };
            OutputDeviceCombo.Items.Add(systemDefault);

            foreach (AudioOutputDeviceService.Option device in devices)
            {
                OutputDeviceCombo.Items.Add(new System.Windows.Controls.ComboBoxItem
                {
                    Content = device.DisplayName,
                    Tag = AudioOutputDeviceService.ComposePersistedKey(device)
                });
            }

            // Старые профили хранили имя/номер WaveOut: до первого playback приводим его к endpoint-ID, иначе ComboBox не найдёт
            // Tag и сбросит рабочий выбор на системное устройство.
            if (!string.IsNullOrWhiteSpace(_settings.OutputDeviceName) &&
                !AudioOutputDeviceService.IsEndpointPersistedKey(_settings.OutputDeviceName))
            {
                string? migratedKey = AudioOutputDeviceService.FindAvailablePersistedKey(_settings.OutputDeviceName, devices);
                if (!string.IsNullOrWhiteSpace(migratedKey))
                {
                    _settings.OutputDeviceName = migratedKey;
                    FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
                }
            }

            var selected = OutputDeviceCombo.Items.Cast<System.Windows.Controls.ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, _settings.OutputDeviceName, StringComparison.OrdinalIgnoreCase));
            bool fallbackToSystemDefault = selected is null;
            // Сохранённый выбор не сбрасываем: отключённое устройство может вернуться, и плеер переключится на него.
            OutputDeviceCombo.SelectedItem = selected ?? systemDefault;

            RefreshOutputDeviceStatus(fallbackToSystemDefault);
        }
        finally
        {
            _isRefreshingOutputDevices = false;
        }
    }

    public void RefreshOutputDeviceSelection() => InitializeOutputDeviceCombo();

    public void RefreshOutputDeviceRuntimeStatus() => RefreshOutputDeviceStatus();

    // Список из двух фиксированных пунктов задан прямо в XAML (в отличие от ComboBox устройств
    // выше) — набор режимов WASAPI не меняется во время работы приложения, перестраивать нечего.
    private void InitializeWasapiModeCombo()
    {
        _isRefreshingWasapiMode = true;
        try
        {
            var selected = WasapiModeCombo.Items.Cast<System.Windows.Controls.ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, _settings.WasapiMode, StringComparison.OrdinalIgnoreCase));
            WasapiModeCombo.SelectedItem = selected ?? WasapiModeCombo.Items[0];
        }
        finally
        {
            _isRefreshingWasapiMode = false;
        }
    }

    private void InitializeWasapiLatencyCombo()
    {
        _isRefreshingWasapiMode = true;
        try
        {
            var selected = WasapiLatencyCombo.Items.Cast<System.Windows.Controls.ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, _settings.WasapiLatencyMilliseconds.ToString(), StringComparison.Ordinal));
            WasapiLatencyCombo.SelectedItem = selected ?? WasapiLatencyCombo.Items[1];
        }
        finally
        {
            _isRefreshingWasapiMode = false;
        }
    }

    private void WasapiLatencyCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isInitializing || _isRefreshingWasapiMode) return;

        int milliseconds = WasapiLatencyCombo.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string tag }
                           && int.TryParse(tag, out int parsed)
            ? parsed
            : 100;
        if (_settings.WasapiLatencyMilliseconds == milliseconds) return;

        _settings.WasapiLatencyMilliseconds = milliseconds;
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void RaisePlaybackPriorityCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.RaisePlaybackPriority = RaisePlaybackPriorityCheckBox.IsChecked == true;
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
        _owner.ApplyPlaybackProcessPriority();
    }

    // Вызывается из MainWindow после автоотката с монопольного на общий режим (см.
    // EnsureOutputDevice) — ComboBox должен сразу показать реальное значение.
    public void RefreshWasapiModeSelection() => InitializeWasapiModeCombo();

    private void WasapiModeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isInitializing || _isRefreshingWasapiMode) return;

        string selectedMode = WasapiModeCombo.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string tag }
            ? tag
            : "Shared";
        if (string.Equals(_settings.WasapiMode, selectedMode, StringComparison.OrdinalIgnoreCase)) return;

        _settings.WasapiMode = selectedMode;
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
        RefreshOutputDeviceStatus();
        _owner.ApplyOutputDeviceSelection();
    }

    private void RefreshOutputDeviceStatus(bool fellBackToSystemDefault = false)
    {
        var runtime = _owner.GetOutputDeviceRuntimeStatus();
        OutputDeviceRuntimeTitleText.Text = LocalizationService.Translate("Фактическое устройство");
        OutputDeviceRuntimeValueText.Text = string.Format(
            LocalizationService.Translate("Активно: {0}"),
            runtime.ActiveDeviceName);
        string format = runtime.OutputFormat ?? LocalizationService.Translate("будет определён при запуске");
        string actualLatency = runtime.ActualLatencyMilliseconds?.ToString()
            ?? LocalizationService.Translate("будет определена при запуске");
        string recovery = runtime.RecoveryCount == 0
            ? LocalizationService.Translate("восстановлений: нет")
            : string.Format(
                LocalizationService.Translate("восстановлений: {0}; последняя причина: {1}"),
                runtime.RecoveryCount,
                LocalizationService.Translate(runtime.LastRecoveryReason ?? "не указана"));
        OutputDeviceRuntimeModeText.Text = string.Format(
            LocalizationService.Translate("Движок: {0} · состояние: {1}\nФормат вывода: {2}\nФактическая задержка: {3} мс (запрошено: {4} мс) · init: {5} мс · {6}"),
            runtime.Engine,
            LocalizationService.Translate(runtime.PlaybackState),
            format,
            actualLatency,
            runtime.RequestedLatencyMilliseconds,
            runtime.InitializationMilliseconds,
            recovery);

        string routing = runtime.FollowsSystemDefault
            ? LocalizationService.Translate("системное устройство Windows по умолчанию")
            : LocalizationService.Translate("явно выбранный WASAPI endpoint");
        string lastEvent = runtime.LastDeviceEventKind is null
            ? LocalizationService.Translate("нет")
            : string.Format(
                LocalizationService.Translate("{0}: {1}"),
                LocalizationService.Translate(GetOutputDeviceEventName(runtime.LastDeviceEventKind.Value)),
                runtime.LastDeviceEventEndpointId ?? "—");
        OutputDeviceRuntimeDiagnosticsText.Text = string.Format(
            LocalizationService.Translate("Маршрутизация: {0}\nEndpoint ID: {1}\nСобытия WASAPI: {2}; последнее: {3}"),
            routing,
            runtime.ActiveEndpointId ?? "—",
            runtime.MeaningfulDeviceEventCount,
            lastEvent);

        RefreshDeviceKindControls();
        OutputDeviceSignalPathText.Text = runtime.SignalPath is null
            ? string.Empty
            : LocalizationService.Translate("Путь сигнала") + ": " + runtime.SignalPath.Text;
        OutputDeviceSignalPathText.Visibility = runtime.SignalPath is null ? Visibility.Collapsed : Visibility.Visible;

        OutputDeviceStatusText.Text = runtime.FallbackFrom is { Length: > 0 }
            ? string.Format(
                LocalizationService.Translate("Выбранное устройство «{0}» недоступно. Lumisense использует: {1}."),
                runtime.FallbackFrom,
                runtime.ActiveDeviceName)
            : fellBackToSystemDefault
                ? LocalizationService.Translate("Выбранное устройство недоступно. Будет использовано системное устройство Windows.")
                : !runtime.IsInitialized
                    ? string.Format(
                        LocalizationService.Translate("Устройство будет применено при следующем запуске воспроизведения: {0}."),
                        runtime.ActiveDeviceName)
                    : LocalizationService.Translate("Выбранное устройство применяется сразу; текущий трек продолжится с сохранённой позиции.");
    }

    private static string GetOutputDeviceEventName(AudioOutputEndpointChangeKind kind) => kind switch
    {
        AudioOutputEndpointChangeKind.DeviceAdded => "устройство подключено",
        AudioOutputEndpointChangeKind.DeviceRemoved => "устройство отключено",
        AudioOutputEndpointChangeKind.DeviceStateChanged => "состояние устройства изменено",
        AudioOutputEndpointChangeKind.DefaultDeviceChanged => "изменено системное устройство по умолчанию",
        AudioOutputEndpointChangeKind.DevicePropertiesChanged => "свойства устройства изменены",
        _ => "не указано"
    };

    private void CopyAudioDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(_owner.BuildAudioDiagnosticsReport());
            AudioDiagnosticsCopyStatusText.Text = LocalizationService.Translate("Аудиодиагностика скопирована в буфер обмена");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось скопировать аудиодиагностику: {ex.Message}");
            AudioDiagnosticsCopyStatusText.Text = LocalizationService.Translate("Не удалось скопировать аудиодиагностику");
        }
    }

    private void OutputDeviceCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isInitializing || _isRefreshingOutputDevices) return;

        string selectedDeviceName = OutputDeviceCombo.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string tag }
            ? tag
            : AudioOutputDeviceService.SystemDefaultDeviceName;
        if (string.Equals(_settings.OutputDeviceName, selectedDeviceName, StringComparison.OrdinalIgnoreCase)) return;

        _settings.OutputDeviceName = selectedDeviceName;
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
        RefreshOutputDeviceStatus();
        _owner.ApplyOutputDeviceSelection();
    }
}
