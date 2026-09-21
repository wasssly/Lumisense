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

namespace Lumisense;

public partial class SettingsWindow : FluentWindow
{
    private enum HotkeyTarget { None, PlayPause, Next, Previous, Stop, VolumeUp, VolumeDown, Mute, Shuffle, Repeat, ToggleFavorite, ToggleLyrics, ToggleMiniPlayer, DeleteTrack, SeekForward, SeekBackward }

    private readonly AppSettings _settings;
    private readonly MainWindow _owner;
    private bool _isInitializing = true;
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

    // Пока не None — окно "слушает" следующее нажатие клавиш и запишет его как новую комбинацию
    private HotkeyTarget _recordingTarget = HotkeyTarget.None;

    // Индекс поиска — ручной перечень опций: подпись, страница, ссылка на элемент (для прокрутки и подсветки)
    // и ключевые слова; разметка не читается.
    private sealed record SettingsSearchEntry(string Label, string PageTitle, string PageKey, string Keywords, FrameworkElement Target);

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
            "Integrations" => NavIntegrations,
            "Notifications" => NavNotifications,
            "Equalizer" => NavEqualizer,
            "MiniPlayer" => NavMiniPlayer,
            "Hotkeys" => NavHotkeys,
            "Profile" => NavProfile,
            _ => NavAppearance
        }).IsChecked = true;
    }

    // Своя копия MainWindow.ApplyWindowBackdrop: применяется к собственному HWND этого окна.
    private void ApplyWindowBackdrop(AppSettings settings, bool forceReapply = false)
    {
        var desiredBackdrop = settings.WindowBackdropType == "Acrylic"
            ? Wpf.Ui.Controls.WindowBackdropType.Acrylic
            : Wpf.Ui.Controls.WindowBackdropType.Mica;

        // См. MainWindow.ApplyWindowBackdrop: при смене темы WPF-UI может оставить Acrylic в свойстве, но наложить Mica;
        // None → desired заставляет FluentWindow заново применить нужный backdrop.
        if (forceReapply && WindowBackdropType == desiredBackdrop)
            WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;

        WindowBackdropType = desiredBackdrop;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyWindowBackdrop(_settings);
    }

    public SettingsWindow(AppSettings settings, MainWindow owner, string? initialPage = null)
    {
        InitializeComponent();
        _autoScrollTimer.Tick += AutoScrollTimer_Tick;
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

        BackdropAcrylicRadio.IsChecked = _settings.WindowBackdropType == "Acrylic";
        BackdropMicaRadio.IsChecked = !BackdropAcrylicRadio.IsChecked.GetValueOrDefault();
        CoverBaseFromCoverCheckBox.IsChecked = _settings.CoverBaseFromCover;

        InterfaceScaleSlider.Value = AccessibilityPreferences.NormalizeScale(_settings.InterfaceScale) * 100;
        InterfaceScaleValueText.Text = $"{InterfaceScaleSlider.Value:0}%";
        ReduceMotionCheckBox.IsChecked = _settings.ReduceMotion;

        SyncedLyricsFontSizeSlider.Value = Math.Clamp(_settings.SyncedLyricsFontSize, 12, 20);
        SyncedLyricsFontSizeValueText.Text = $"{SyncedLyricsFontSizeSlider.Value:0} px";
        SyncedLyricsEffectNoneRadio.IsChecked = _settings.SyncedLyricsHighlightEffect == "None";
        // Старые значения Scale/GlowScale после обновления корректно воспринимаются как Glow.
        SyncedLyricsEffectGlowRadio.IsChecked = !SyncedLyricsEffectNoneRadio.IsChecked.GetValueOrDefault();
        LyricsPolicyLocalOnlyRadio.IsChecked = _settings.LyricsSearchPolicy == "LocalOnly";
        LyricsPolicyManualOnlyRadio.IsChecked = _settings.LyricsSearchPolicy == "ManualOnly";
        LyricsPolicyAutoExactRadio.IsChecked = !LyricsPolicyLocalOnlyRadio.IsChecked.GetValueOrDefault() && !LyricsPolicyManualOnlyRadio.IsChecked.GetValueOrDefault();

        AlwaysOnTopCheckBox.IsChecked = _settings.AlwaysOnTop;
        RememberVolumeCheckBox.IsChecked = _settings.RememberVolume;
        LogarithmicVolumeCheckBox.IsChecked = _settings.UseLogarithmicVolume;
        InitializeOutputDeviceCombo();
        InitializeWasapiModeCombo();
        TrackLoadTraceCheckBox.IsChecked = _settings.TrackLoadTraceEnabled;
        NeverAutoPlayLastTrackOnStartupCheckBox.IsChecked = _settings.NeverAutoPlayLastTrackOnStartup;
        TrackChangeToastCheckBox.IsChecked = _settings.ShowTrackChangeToast;
        InitializeToastPolicy();
        InitializeToastPositionAndSize();
        InitializeToastMonitorCombo();
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
        MiniInfoRemainingRadio.IsChecked = _settings.MiniPlayerInfoMode == "TitleRemaining";
        MiniInfoArtistRadio.IsChecked = !MiniInfoOnlyTitleRadio.IsChecked.GetValueOrDefault()
                                         && !MiniInfoRemainingRadio.IsChecked.GetValueOrDefault();

        FileNameNormalizationTemplateTextBox.Text = string.IsNullOrWhiteSpace(_settings.FileNameNormalizationTemplate)
            ? FileNameNormalizer.DefaultTemplate
            : _settings.FileNameNormalizationTemplate;
        FileNameNormalizationResultText.Visibility = Visibility.Collapsed;
        InitializeTrackContextMenuActionCheckBoxes();
        InitializeMiniPlayerContextMenuActionCheckBoxes();
        ImprovedShuffleCheckBox.IsChecked = _settings.UseImprovedShuffle;
        SaveQueueBetweenRestartsCheckBox.IsChecked = _settings.SaveQueueBetweenRestarts;
        ProgressBarWaveformRadio.IsChecked = _settings.ProgressBarStyle == "Waveform";
        ProgressBarSliderRadio.IsChecked = !ProgressBarWaveformRadio.IsChecked.GetValueOrDefault();
        ReplayGainCheckBox.IsChecked = _settings.ReplayGainEnabled;
        DiscordRichPresenceEnabledCheckBox.IsChecked = _settings.DiscordRichPresenceEnabled;
        DiscordRichPresenceShowTrackInfoCheckBox.IsChecked = _settings.DiscordRichPresenceShowTrackInfo;
        DiscordRichPresenceShowTimelineCheckBox.IsChecked = _settings.DiscordRichPresenceShowTimeline;
        DiscordRichPresenceShowCoverArtCheckBox.IsChecked = _settings.DiscordRichPresenceShowCoverArt;
        UpdateDiscordRichPresenceConnectionStatus();
        AlbumArtTransitionOnRadio.IsChecked = _owner.IsAlbumArtTransitionEnabled;
        AlbumArtTransitionOffRadio.IsChecked = !_owner.IsAlbumArtTransitionEnabled;
        AlbumArtGesturesCheckBox.IsChecked = _settings.AlbumArtGesturesEnabled;

        EqualizerEnabledCheckBox.IsChecked = _owner.IsEqualizerEnabled;
        EqualizerBypassCheckBox.IsChecked = _owner.IsEqualizerBypass;
        for (int band = 0; band < EqualizerSampleProvider.BandFrequencies.Length; band++)
        {
            double gain = _owner.GetEqualizerBandGain(band);
            GetEqBandSlider(band).Value = gain;
            GetEqBandValueText(band).Text = FormatEqGain(gain);
        }
        RefreshEqualizerPresetsList();

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
        BuildSearchIndex();

        RefreshAppVersionText();
        LoadDeveloperAvatar();
        RefreshLyricsCacheInfo();
        RefreshResetRecoveryButton();
        RefreshUpdateSourceProbePresentation();
        RefreshVelopackBasePackagePresentation();
        RefreshLegacyInnoCleanupPresentation();

        _isInitializing = false;
        LocalizationService.LanguageChanged += LocalizationService_LanguageChanged;
        Loaded += async (_, _) =>
        {
            await RefreshVelopackBasePackagePlanAsync();
            RefreshLegacyInnoCleanupPresentation();
        };
        Closed += (_, _) =>
        {
            StopAutoScrolling();
            _autoScrollTimer.Tick -= AutoScrollTimer_Tick;
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
    private void RestoreOrCenterPosition(Window owner)
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
        _isInitializing = true;
        MiniAlwaysOnTopCheckBox.IsChecked = _settings.MiniPlayerAlwaysOnTop;
        MiniPinnedCheckBox.IsChecked = _settings.MiniPlayerPinned;
        MiniSnapToEdgesCheckBox.IsChecked = _settings.MiniPlayerSnapToEdges;
        MiniSecondaryShuffleRadio.IsChecked = _settings.MiniPlayerSecondaryButton == "Shuffle";
        MiniSecondaryFavoriteRadio.IsChecked = _settings.MiniPlayerSecondaryButton == "Favorite";
        MiniSecondaryRepeatRadio.IsChecked = !MiniSecondaryShuffleRadio.IsChecked.GetValueOrDefault()
                                              && !MiniSecondaryFavoriteRadio.IsChecked.GetValueOrDefault();
        MiniOpacitySlider.Value = _settings.MiniPlayerOpacity;
        MiniOpacityValueText.Text = $"{(int)Math.Round(_settings.MiniPlayerOpacity * 100)}%";
        _isInitializing = false;
    }

    // Отмечает миниатюру текущего вида плеера — при открытии окна и извне (MainWindow), когда вид сменили меню заголовка
    // или кнопкой мини-плеера, чтобы страница не отставала от состояния.
    public void RefreshViewModeRadios()
    {
        _isInitializing = true;
        switch (_owner.CurrentViewModeName)
        {
            case "Square": ViewModeSquareRadio.IsChecked = true; break;
            case "Rectangular": ViewModeRectangularRadio.IsChecked = true; break;
            case "Mini": ViewModeMiniRadio.IsChecked = true; break;
        }
        _isInitializing = false;
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
            var service = new VelopackUpdateService();
            _isVelopackManagedInstall = service.IsManagedInstall;
            if (!_isVelopackManagedInstall)
            {
                _basePackagePlan = VelopackBasePackagePlan.Unavailable(VelopackBasePackageStatus.NotManagedInstall);
                RefreshVelopackBasePackagePresentation();
                return;
            }

            _basePackagePlan = await service.GetBasePackagePlanAsync(cts.Token);
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

    private async void RemoveLegacyInnoButton_Click(object sender, RoutedEventArgs e)
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

    private async void PrepareVelopackBasePackageButton_Click(object sender, RoutedEventArgs e)
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
    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        CheckUpdatesButtonSubtitle.Text = LocalizationService.Translate("Проверяем…");

        try
        {
            var result = await UpdateChecker.CheckAsync();
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
                    CheckUpdatesButtonSubtitle.Text = UpdateFailureExperience.Describe(
                        result.FailureKind, result.HttpStatusCode);
                    break;
            }
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

        // Источник применяется сразу: UpdateAvailableWindow использует ту же in-memory модель _settings; выбор сохраняем
        // сразу, чтобы он пережил перезапуск без ожидания общего сохранения.
    private void UpdateSourceRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        if (sender is not System.Windows.Controls.RadioButton { Tag: string key }) return;
        _settings.UpdateDownloadSource = key;
        SettingsManager.Save(_settings);
    }

    private async void ProbeUpdateSourcesButton_Click(object sender, RoutedEventArgs e)
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
    private IReadOnlyList<ReleaseListItem>? _loadedReleases;

    // Список грузится лениво — при первом раскрытии аккордеона и один раз за жизнь окна, чтобы не бить по сети зря.
    private async void AllVersionsExpander_Expanded(object sender, RoutedEventArgs e)
    {
        if (_allVersionsLoaded) return;
        _allVersionsLoaded = true;

        ReleaseListResult releaseResult = await UpdateChecker.GetAllReleasesAsync();

        AllVersionsLoadingText.Visibility = Visibility.Collapsed;

        if (!releaseResult.IsSuccess)
        {
            AllVersionsErrorText.Text = UpdateFailureExperience.DescribeVersionListFailure(releaseResult);
            AllVersionsErrorText.Visibility = Visibility.Visible;
            return;
        }

        IReadOnlyList<ReleaseListItem> releases = releaseResult.Releases;
        if (releases.Count == 0)
        {
            AllVersionsErrorText.Text = LocalizationService.Translate("На GitHub пока нет ни одного опубликованного релиза.");
            AllVersionsErrorText.Visibility = Visibility.Visible;
            return;
        }

        _loadedReleases = releases;
        RenderAllVersions(releases);
    }

    private void RenderAllVersions(IReadOnlyList<ReleaseListItem> releases)
    {
        string currentVersion = UpdateChecker.GetCurrentVersion();

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

                bool canInstall = !string.IsNullOrEmpty(r.ExeDownloadUrl) && !string.IsNullOrEmpty(r.ExeSha256);
                if (string.IsNullOrEmpty(r.ExeDownloadUrl))
                    subtitle += $" · {LocalizationService.Translate("В релизе нет .exe-установщика")}";
                else if (string.IsNullOrEmpty(r.ExeSha256))
                    subtitle += $" · {LocalizationService.Translate("В релизе отсутствует SHA-256 установщика")}";

                string action = LocalizationService.Translate(isCurrent ? "Переустановить" : "Установить");

                return new VersionListItemViewModel(title, subtitle, action, canInstall, r);
            })
            .ToList();
    }

    // Тот же диалог, что при обычном обнаружении обновления; не проверяет, что версия новее, поэтому подходит и для отката:
    // CurrentVersion — реальная текущая, диалог показывает обе рядом (это и есть предупреждение об откате).
    private void VersionListItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: VersionListItemViewModel item }) return;

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
            => _searchIndex.Add(new SettingsSearchEntry(label, pageTitle, pageKey, $"{label} {extraKeywords}".ToLowerInvariant(), target));

        Add("Язык интерфейса", "Оформление", "Appearance", LanguageRussianRadio, "язык русский english language locale локализация");
        Add("Тема", "Оформление", "Appearance", ThemeDarkRadio, "тёмная светлая цвет тема оформление dark light");
        Add("Акцентный цвет", "Оформление", "Appearance", AccentSystemRadio, "акцент цвет палитра accent color");
        Add("Основа окна", "Оформление", "Appearance", BackdropMicaRadio, "mica acrylic blur акрил размытие блюр подложка фон backdrop");
        Add("Цвет основы от текущей обложки", "Оформление", "Appearance", CoverBaseFromCoverCheckBox, "обложка cover основа фон окно цвет theme");
        Add("Доступность", "Оформление", "Appearance", AccessibilityCard, "масштаб интерфейса текст размер доступность движение анимация accessibility scale motion");
        Add("Анимация смены обложки", "Оформление", "Appearance", AlbumArtTransitionOnRadio, "анимация обложка переход трек itunes слайд fly transition album art cover");
        Add("Жесты на обложке", "Оформление", "Appearance", AlbumArtGesturesCheckBox, "жесты обложка касание свайп пуск пауза громкость следующий предыдущий gesture swipe cover");
        Add("Вид плеера", "Окно и запуск", "Window", PlayerViewModeCard, "квадратный прямоугольный мини плеер вид размер окна square rectangular mini");
        Add("Поверх всех окон", "Окно и запуск", "Window", AlwaysOnTopCheckBox, "topmost всегда сверху главное окно");
        Add("Сворачивать в трей при закрытии", "Окно и запуск", "Window", MinimizeToTrayCheckBox, "трей закрытие свернуть tray");
        Add("Запускать вместе с Windows", "Окно и запуск", "Window", LaunchOnStartupCheckBox, "автозапуск запуск windows автозагрузка startup");
        Add("Запускать свёрнутым в трей", "Окно и запуск", "Window", StartHiddenInTrayCheckBox, "запуск свёрнутым трей автозапуск скрыто hidden startup tray");
        Add("Запоминать громкость между запусками", "Воспроизведение", "Playback", RememberVolumeCheckBox, "громкость запуск volume");
        Add("Логарифмическая регулировка громкости", "Воспроизведение", "Playback", LogarithmicVolumeCheckBox, "громкость логарифм слух дБ db volume logarithmic");
        Add("Не запускать трек при старте", "Воспроизведение", "Playback", NeverAutoPlayLastTrackOnStartupCheckBox, "старт запуск продолжить воспроизведение последний трек пауза resume autoplay");
        Add("Очистить кэш интернет-обложек", "Воспроизведение", "Playback", ClearArtworkCacheButton, "кэш обложка интернет очистить удалить cover cache artwork image");
        Add("Нормализация имён файлов", "Воспроизведение", "Playback", NormalizePlaylistFileNamesButton, "нормализация имя файл шаблон переименование artist title album track extension rename");
        Add("Действия контекстного меню трека", "Воспроизведение", "Playback", TrackContextFavoriteCheckBox, "контекстное меню правый клик пкм трек плейлист скрыть отключить действия проводник копировать теги свойства удалить найти файл замена недоступный locate relink missing");
        Add("Найти файл", "Воспроизведение", "Playback", TrackContextFindFileCheckBox, "контекстное меню заменить недоступный файл путь locate relink missing file");
        Add("Discord Rich Presence", "Интеграции", "Integrations", DiscordRichPresenceEnabledCheckBox, "discord статус rich presence rpc активность" );
        Add("Подключить Discord", "Интеграции", "Integrations", ConnectDiscordButton, "discord подключить connection rich presence статус" );
        Add("Открыть журнал Discord", "Интеграции", "Integrations", OpenDiscordDiagnosticsLogButton, "discord журнал лог диагностика ошибка rich presence" );
        Add("Приватность Discord: название и исполнитель", "Интеграции", "Integrations", DiscordRichPresenceShowTrackInfoCheckBox, "discord приватность название исполнитель трек" );
        Add("Приватность Discord: таймлайн", "Интеграции", "Integrations", DiscordRichPresenceShowTimelineCheckBox, "discord приватность время прогресс таймлайн" );
        Add("Приватность Discord: обложка трека", "Интеграции", "Integrations", DiscordRichPresenceShowCoverArtCheckBox, "discord обложка cover art musicbrainz itunes deezer" );
        Add("Эквалайзер", "Эквалайзер", "Equalizer", EqualizerEnabledCheckBox, "equalizer эквалайзер частоты полосы бас звук eq");
        Add("EQ Bypass", "Эквалайзер", "Equalizer", EqualizerBypassCheckBox, "bypass обход эквалайзер eq временно сравнение фильтры");
        Add("Прозрачность окна мини-плеера", "Мини-плеер", "MiniPlayer", MiniOpacitySlider, "прозрачность opacity мини плеер");
        Add("Поверх всех окон (мини-плеер)", "Мини-плеер", "MiniPlayer", MiniAlwaysOnTopCheckBox, "topmost мини плеер");
        Add("Закрепить положение (мини-плеер)", "Мини-плеер", "MiniPlayer", MiniPinnedCheckBox, "закрепить перетаскивание pin мини плеер");
        Add("Прилипание к краям экрана (мини-плеер)", "Мини-плеер", "MiniPlayer", MiniSnapToEdgesCheckBox, "прилипание магнит края экран snap edge мини плеер");
        Add("Вторая кнопка в мини-плеере", "Мини-плеер", "MiniPlayer", MiniSecondaryRepeatRadio, "вторая кнопка повтор перемешать избранное сердечко favorite shuffle repeat мини плеер");
        Add("Отображение обложки (мини-плеер)", "Мини-плеер", "MiniPlayer", MiniArtworkVinylRadio, "обложка винил пластинка вращение круглая artwork vinyl rotate мини плеер");
        Add("Показывать полосу прогресса (мини-плеер)", "Мини-плеер", "MiniPlayer", MiniShowProgressCheckBox, "полоса прогресс progress bar скрыть мини плеер");
        Add("Прогресс вокруг обложки (мини-плеер)", "Мини-плеер", "MiniPlayer", MiniShowArtworkProgressCheckBox, "контур скруглённый квадрат прогресс обложка арт мини плеер artwork outline");
        Add("Толщина контура вокруг обложки", "Мини-плеер", "MiniPlayer", MiniArtworkProgressThicknessSlider, "толщина линия контур прогресс обложка мини плеер artwork outline thickness");
        Add("Цвет контура прогресса (мини-плеер)", "Мини-плеер", "MiniPlayer", MiniArtworkProgressAccentRadio, "акцент фиксированный цвет палитра контур прогресс обложка мини плеер artwork outline color");
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
        Add("Шаффл без повторов", "Воспроизведение", "Playback", ImprovedShuffleCheckBox, "шаффл перемешать shuffle bag колода без повторов");
        Add("Устройство вывода", "Воспроизведение", "Playback", OutputDeviceCombo, "звук аудио устройство вывод наушники колонки динамики speakers headphones audio output wasapi shared");
        Add("Полоса воспроизведения", "Воспроизведение", "Playback", ProgressBarWaveformRadio, "waveform форма звука soundcloud полоса прогресс seek слайдер");
        Add("ReplayGain", "Воспроизведение", "Playback", ReplayGainCheckBox, "replaygain громкость выравнивание нормализация gain");
        Add("Уведомление о смене трека", "Уведомления", "Notifications", TrackChangeToastCheckBox, "уведомление тост смена трека toast notification");
        Add("Расположение уведомления", "Уведомления", "Notifications", ToastPosTopLeftRadio, "уведомление угол расположение позиция монитор экран размер position monitor screen size");
        Add("Когда показывать", "Уведомления", "Notifications", ToastPolicyEveryTrackChangeRadio, "уведомление тост смена трека воспроизведение ручной выбор policy toast notification playback manual");
        Add("Размер уведомления", "Уведомления", "Notifications", ToastSizeSmallRadio, "размер уведомление тост маленький средний большой size toast notification");
        Add("Ширина уведомления", "Уведомления", "Notifications", ToastWidthSlider, "ширина уведомление тост размер width toast notification size");
        Add("Экспортировать настройки", "Профиль", "Profile", ExportProfileButton, "экспорт настройки профиль lumi файл backup export profile");
        Add("Импортировать настройки", "Профиль", "Profile", ImportProfileButton, "импорт настройки профиль lumi файл backup import restore profile");
        Add("Кэш текстов песен", "Профиль", "Profile", ClearLyricsCacheButton, "текст lyrics кэш очистить память локальный cache lyrics clear");
        Add("Сбросить плеер к исходному состоянию", "Профиль", "Profile", ResetPlayerButton, "сброс сбросить умолчание reset default настройки factory");
        Add("Вернуть состояние до последнего сброса", "Профиль", "Profile", RestoreResetSnapshotButton, "восстановить вернуть точка сброса backup restore reset recovery");
        Add("О плеере", "О плеере", "About", AboutInfoCard, "версия lumisense о программе о плеере");
        Add("Источник загрузки обновлений", "Обновления", "Updates", UpdateSourceGitHubRadio, "update mirror зеркало gh-proxy обновление скачать источник");
        Add("Все версии", "Обновления", "Updates", AllVersionsExpanderControl, "версии история версия откат downgrade install version releases обновление скачать установить zip exe установщик");
        Add("Проверить обновления", "Обновления", "Updates", CheckUpdatesButton, "обновление update github версия проверить");
        Add("Удалить старую EXE-копию", "Обновления", "Updates", RemoveLegacyInnoButton, "удалить uninstall деинсталлятор старая exe msi migration cleanup compact updates");
        Add("Список изменений", "О плеере", "About", ChangelogButton, "патчноуты changelog версии история изменений");
        Add("Разработчик", "О плеере", "About", DeveloperGitHubButton, "разработчик автор github telegram wasssly ссылки контакты аватар");
        Add("Открыть папку с логами", "О плеере", "About", OpenLogsButton, "логи log ошибка краш crash диагностика");
    }

    private void SettingsSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        string query = SettingsSearchBox.Text.Trim();

        if (query.Length == 0)
        {
            NavCategoriesPanel.Visibility = Visibility.Visible;
            SearchResultsHost.Visibility = Visibility.Collapsed;
            return;
        }

        NavCategoriesPanel.Visibility = Visibility.Collapsed;
        SearchResultsHost.Visibility = Visibility.Visible;

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
            "Integrations" => NavIntegrations,
            "Notifications" => NavNotifications,
            "Equalizer" => NavEqualizer,
            "MiniPlayer" => NavMiniPlayer,
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
                entry.Target.BringIntoView();
                SearchHighlightAdorner.Flash(entry.Target);
            }, DispatcherPriority.Loaded);
        }, DispatcherPriority.Loaded);
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

    private void ThemeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.Theme = ThemeLightRadio.IsChecked == true ? "Light" : "Dark";

        ApplicationThemeManager.Apply(_settings.IsLightThemeResolved() ? ApplicationTheme.Light : ApplicationTheme.Dark);
        _owner.ApplyAccentColor(); // акцент пересчитывает светлые/тёмные варианты под новую тему
        ReapplyWindowBackdropsAfterThemeChange();
        _owner.ApplyTrayTheme(_settings.IsLightThemeResolved());
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
        _ = SettingsManager.SaveAsync(_settings);
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
        _ = SettingsManager.SaveAsync(_settings);
    }

    // Подсвечивает рамкой карточку значка, совпадающего с _settings.AppIcon — по аналогии с
    // RefreshIconPackCardSelection выше.
    private void RefreshAppIconCardSelection()
    {
        (System.Windows.Controls.Border card, string icon)[] cards =
        {
            (AppIconCardClassic, AppIcons.Classic),
            (AppIconCardAurora, AppIcons.Aurora),
            (AppIconCardLavender, AppIcons.Lavender),
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
        _settings.WindowBackdropType = BackdropAcrylicRadio.IsChecked == true ? "Acrylic" : "Mica";
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
    private readonly DispatcherTimer _autoScrollTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };

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
        _autoScrollTimer.Start();
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

    private void AutoScrollTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isAutoScrolling || _autoScrollViewer is null) return;

        Point current = Mouse.GetPosition(RootLayout);
        double distance = current.Y - _autoScrollOrigin.Y;
        if (Math.Abs(distance) < 4) return;

        // Чем дальше курсор от точки запуска, тем быстрее движется страница. Умножение на
        // 0.12 даёт мягкую скорость, близкую к стандартной автопрокрутке браузеров.
        double delta = Math.Clamp(distance * 0.12, -24, 24);
        double nextOffset = Math.Clamp(
            _autoScrollViewer.VerticalOffset + delta,
            0,
            _autoScrollViewer.ScrollableHeight);
        _autoScrollViewer.ScrollToVerticalOffset(nextOffset);
    }

    private void StopAutoScrolling()
    {
        if (!_isAutoScrolling && !_autoScrollTimer.IsEnabled) return;
        _isAutoScrolling = false;
        _autoScrollTimer.Stop();
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

        double offset = scrollViewer.VerticalOffset - e.Delta * 0.45;
        scrollViewer.ScrollToVerticalOffset(Math.Clamp(offset, 0, scrollViewer.ScrollableHeight));
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

    private void ReduceMotionCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.ReduceMotion = ReduceMotionCheckBox.IsChecked == true;
        _owner.ApplyAccessibilityPreferences();
    }

    private void SyncedLyricsFontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Slider вызывает ValueChanged во время загрузки XAML, когда SyncedLyricsFontSizeValueText ещё может не быть в namescope;
        // до конца InitializeComponent ничего не трогаем.
        if (_isInitializing || SyncedLyricsFontSizeValueText is null) return;

        SyncedLyricsFontSizeValueText.Text = $"{e.NewValue:0} px";
        _settings.SyncedLyricsFontSize = Math.Clamp(Math.Round(e.NewValue), 12, 20);
        _owner.ApplySyncedLyricsAppearance();
    }

    private void LyricsPolicyRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.LyricsSearchPolicy = LyricsPolicyLocalOnlyRadio.IsChecked == true ? "LocalOnly"
            : LyricsPolicyManualOnlyRadio.IsChecked == true ? "ManualOnly"
            : "AutoExact";
    }

    private void SyncedLyricsEffectRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.SyncedLyricsHighlightEffect = SyncedLyricsEffectNoneRadio.IsChecked == true
            ? "None"
            : "Glow";
        _owner.ApplySyncedLyricsAppearance();
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

    private async void ClearArtworkCacheButton_Click(object sender, RoutedEventArgs e)
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

    private void RefreshLyricsCacheInfo()
    {
        LyricsCacheInfo info = LyricsService.GetPastedLyricsCacheInfo();
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
        RefreshAppVersionText();
        InitializeOutputDeviceCombo();
        InitializeToastMonitorCombo();
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
            DiscordRichPresenceLogger.Info("Пользователь открыл журнал диагностики Discord из настроек.");
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
        _owner.ApplyDiscordRichPresenceSettingsLive();
    }

    private void InitializeOutputDeviceCombo()
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

            foreach (AudioOutputDeviceService.Option device in AudioOutputDeviceService.GetAvailableDevices())
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
                string? migratedKey = AudioOutputDeviceService.FindAvailablePersistedKey(_settings.OutputDeviceName);
                if (!string.IsNullOrWhiteSpace(migratedKey))
                {
                    _settings.OutputDeviceName = migratedKey;
                    _ = SettingsManager.SaveAsync(_settings);
                }
            }

            var selected = OutputDeviceCombo.Items.Cast<System.Windows.Controls.ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, _settings.OutputDeviceName, StringComparison.OrdinalIgnoreCase));
            bool fallbackToSystemDefault = selected is null;
            OutputDeviceCombo.SelectedItem = selected ?? systemDefault;
            if (fallbackToSystemDefault)
                _settings.OutputDeviceName = AudioOutputDeviceService.SystemDefaultDeviceName;

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
        _ = SettingsManager.SaveAsync(_settings);
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
        _ = SettingsManager.SaveAsync(_settings);
        RefreshOutputDeviceStatus();
        _owner.ApplyOutputDeviceSelection();
    }

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
        ToastPosBottomRightRadio.IsChecked = !ToastPosTopLeftRadio.IsChecked.GetValueOrDefault()
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

        ToastWidthSlider.Value = Math.Clamp(_settings.TrackChangeToastWidth, ToastWidthSlider.Minimum, ToastWidthSlider.Maximum);
        UpdateToastWidthValueText();
    }

    // Мониторы собираем при каждом открытии: состав экранов мог измениться, а окно всё равно создаётся заново
    // (MainWindow.ShowSettingsWindow), так что кэш не нужен.
    private void InitializeToastMonitorCombo()
    {
        // Items.Clear и SelectedItem вызывают SelectionChanged: при смене языка выбранный DeviceName должен остаться
        // настройкой, а не временно стать пустым «Автоматически».
        bool wasInitializing = _isInitializing;
        _isInitializing = true;
        try
        {
            string savedMonitor = _settings.TrackChangeToastMonitor;
            ToastMonitorCombo.Items.Clear();

            var autoItem = new System.Windows.Controls.ComboBoxItem
            {
                Content = LocalizationService.Translate("Автоматически (тот же монитор, что и окно плеера)"), Tag = ""
            };
            ToastMonitorCombo.Items.Add(autoItem);

            var screens = System.Windows.Forms.Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                var s = screens[i];
                string label = LocalizationService.Translate($"Монитор {i + 1} — {s.Bounds.Width}×{s.Bounds.Height}") +
                    (s.Primary ? LocalizationService.Translate(" (основной)") : "");
                ToastMonitorCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = label, Tag = s.DeviceName });
            }

            var selected = ToastMonitorCombo.Items.Cast<System.Windows.Controls.ComboBoxItem>()
                .FirstOrDefault(i => (string)i.Tag == savedMonitor);
            ToastMonitorCombo.SelectedItem = selected ?? autoItem;
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

    private void UpdateToastWidthValueText()
    {
        ToastWidthValueText.Text = $"{(int)Math.Round(ToastWidthSlider.Value)} px";
    }

    private void MinimizeToTrayCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.MinimizeToTrayOnClose = MinimizeToTrayCheckBox.IsChecked == true;
    }

    private void LaunchOnStartupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        StartupManager.SetEnabled(LaunchOnStartupCheckBox.IsChecked == true);
    }

    private void StartHiddenInTrayCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.StartHiddenInTray = StartHiddenInTrayCheckBox.IsChecked == true;
    }

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

    private void MiniShowArtworkProgressCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.MiniPlayerShowArtworkProgress = MiniShowArtworkProgressCheckBox.IsChecked == true;
        _owner.ApplyMiniPlayerArtworkProgressVisibilityLive();
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

    private void MiniInfoModeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.MiniPlayerInfoMode = MiniInfoOnlyTitleRadio.IsChecked == true ? "TitleOnly"
            : MiniInfoRemainingRadio.IsChecked == true ? "TitleRemaining"
            : "TitleArtist";
        _owner.ApplyMiniPlayerInfoModeLive();
    }

    private void EqualizerEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _owner.SetEqualizerEnabled(EqualizerEnabledCheckBox.IsChecked == true);
    }

    private void EqualizerBypassCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _owner.SetEqualizerBypass(EqualizerBypassCheckBox.IsChecked == true);
    }

    // Общий обработчик для всех 10 слайдеров полос — номер полосы передаётся через Tag
    // (см. SettingsWindow.xaml), а не десятью одинаковыми по сути методами.
    private void EqualizerBandSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing) return;
        if (sender is not System.Windows.Controls.Slider { Tag: string tagStr } slider) return;
        if (!int.TryParse(tagStr, out int band)) return;

        GetEqBandValueText(band).Text = FormatEqGain(slider.Value);
        _owner.SetEqualizerBandGain(band, slider.Value);
    }

    // WPF не прокручивает Slider колесом сам: шаг — SmallChange за деление; Value поднимет EqualizerBandSlider_ValueChanged
    // (текст и звук, как при перетаскивании), а e.Handled не даёт листать страницу настроек.
    private void EqualizerBandSlider_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not System.Windows.Controls.Slider slider) return;

        double step = slider.SmallChange > 0 ? slider.SmallChange : 0.5;
        double newValue = slider.Value + Math.Sign(e.Delta) * step;
        slider.Value = Math.Clamp(newValue, slider.Minimum, slider.Maximum);

        e.Handled = true;
    }

    private void EqualizerResetButton_Click(object sender, RoutedEventArgs e)
    {
        _owner.ResetEqualizer();

        // _isInitializing глушит ValueChanged при сбросе слайдеров в 0: ResetEqualizer уже применил всё разом,
        // а десять отдельных SetEqualizerBandGain были бы лишними.
        _isInitializing = true;
        for (int band = 0; band < EqualizerSampleProvider.BandFrequencies.Length; band++)
        {
            GetEqBandSlider(band).Value = 0;
            GetEqBandValueText(band).Text = FormatEqGain(0);
        }
        _isInitializing = false;
    }

    private static string FormatEqGain(double gainDb)
    {
        var culture = System.Globalization.CultureInfo.GetCultureInfo(
            LocalizationService.IsEnglish ? "en-US" : "ru-RU");
        string unit = LocalizationService.IsEnglish ? "dB" : "дБ";
        return $"{(gainDb > 0 ? "+" : "")}{gainDb.ToString("0.#", culture)} {unit}";
    }

    private void RefreshEqualizerPresetsList()
    {
        string? previouslySelected = (EqualizerPresetComboBox.SelectedItem as EqualizerPreset)?.Name;

        EqualizerPresetComboBox.ItemsSource = null;
        EqualizerPresetComboBox.ItemsSource = _owner.EqualizerPresets;

        if (previouslySelected != null)
            EqualizerPresetComboBox.SelectedItem = _owner.EqualizerPresets.FirstOrDefault(p => p.Name == previouslySelected);

        UpdateEqualizerPresetButtonsState();
    }

    private void UpdateEqualizerPresetButtonsState()
    {
        bool hasSelection = EqualizerPresetComboBox.SelectedItem != null;
        EqualizerApplyPresetButton.IsEnabled = hasSelection;
        EqualizerDeletePresetButton.IsEnabled = hasSelection;
        EqualizerExportPresetButton.IsEnabled = hasSelection;
    }

    private void EqualizerPresetComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => UpdateEqualizerPresetButtonsState();

    private void EqualizerSavePresetButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedName = (EqualizerPresetComboBox.SelectedItem as EqualizerPreset)?.Name ?? "";
        var dialog = new TextInputDialog("Сохранить пресет", "Название пресета:", selectedName, _settings) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.ResultText.Length == 0) return;

        _owner.SaveEqualizerPreset(dialog.ResultText);
        RefreshEqualizerPresetsList();
        EqualizerPresetComboBox.SelectedItem = _owner.EqualizerPresets.FirstOrDefault(p => p.Name == dialog.ResultText.Trim());
    }

    private void EqualizerApplyPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (EqualizerPresetComboBox.SelectedItem is not EqualizerPreset preset) return;

        _owner.ApplyEqualizerPreset(preset);

        // Пресет применился в MainWindow — подтягиваем актуальные значения обратно в слайдеры,
        // так же как при обычном открытии окна (см. _isInitializing выше).
        _isInitializing = true;
        for (int band = 0; band < EqualizerSampleProvider.BandFrequencies.Length; band++)
        {
            double gain = _owner.GetEqualizerBandGain(band);
            GetEqBandSlider(band).Value = gain;
            GetEqBandValueText(band).Text = FormatEqGain(gain);
        }
        _isInitializing = false;

        if (EqualizerEnabledCheckBox.IsChecked != true)
        {
            EqualizerEnabledCheckBox.IsChecked = true;
            _owner.SetEqualizerEnabled(true);
        }
    }

    private void EqualizerDeletePresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (EqualizerPresetComboBox.SelectedItem is not EqualizerPreset preset) return;

        var confirm = LocalizedMessageBox.Show(
            this,
            $"Удалить пресет \"{preset.Name}\"?",
            "Удаление пресета",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);

        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        _owner.DeleteEqualizerPreset(preset);
        RefreshEqualizerPresetsList();
    }

    // "Поделиться" — сохраняет выбранный пресет в отдельный .json-файл, который можно переслать
    // как обычный файл (мессенджер, почта, флешка); получатель добавляет его через "Импортировать".
    private void EqualizerExportPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (EqualizerPresetComboBox.SelectedItem is not EqualizerPreset preset) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Поделиться пресетом эквалайзера",
            Filter = "Пресет эквалайзера (*.json)|*.json",
            FileName = $"{preset.Name}.json"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _owner.ExportEqualizerPreset(preset, dialog.FileName);
        }
        catch (Exception ex)
        {
            LocalizedMessageBox.Show(this, $"Не удалось сохранить пресет:\n{ex.Message}", "Ошибка",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void EqualizerImportPresetButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Импортировать пресет эквалайзера",
            Filter = "Пресет эквалайзера (*.json)|*.json|Все файлы (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        var imported = _owner.ImportEqualizerPresetFromFile(dialog.FileName);
        if (imported == null)
        {
            LocalizedMessageBox.Show(this, "Не удалось прочитать пресет — файл повреждён или это не пресет Lumisense.",
                "Ошибка", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return;
        }

        RefreshEqualizerPresetsList();
        EqualizerPresetComboBox.SelectedItem = imported;
    }

    private System.Windows.Controls.Slider GetEqBandSlider(int band) => band switch
    {
        0 => EqBand0Slider,
        1 => EqBand1Slider,
        2 => EqBand2Slider,
        3 => EqBand3Slider,
        4 => EqBand4Slider,
        5 => EqBand5Slider,
        6 => EqBand6Slider,
        7 => EqBand7Slider,
        8 => EqBand8Slider,
        _ => EqBand9Slider
    };

    private System.Windows.Controls.TextBlock GetEqBandValueText(int band) => band switch
    {
        0 => EqBand0ValueText,
        1 => EqBand1ValueText,
        2 => EqBand2ValueText,
        3 => EqBand3ValueText,
        4 => EqBand4ValueText,
        5 => EqBand5ValueText,
        6 => EqBand6ValueText,
        7 => EqBand7ValueText,
        8 => EqBand8ValueText,
        _ => EqBand9ValueText
    };

    private void TrackContextMenuActionCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        if (sender is not System.Windows.Controls.CheckBox { Tag: string actionId } checkBox) return;

        _owner.SetTrackContextMenuActionDisabled(actionId, checkBox.IsChecked != true);
    }

    private void FileNameNormalizationTemplateTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_isInitializing) return;

        // Пустой шаблон безопасно возвращается к дефолтному в момент запуска операции. Не
        // переписываем TextBox во время набора, чтобы не ломать редактирование пользователю.
        _settings.FileNameNormalizationTemplate = string.IsNullOrWhiteSpace(FileNameNormalizationTemplateTextBox.Text)
            ? FileNameNormalizer.DefaultTemplate
            : FileNameNormalizationTemplateTextBox.Text.Trim();
        FileNameNormalizationResultText.Visibility = Visibility.Collapsed;
    }

    private async void NormalizePlaylistFileNamesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.FileNameNormalizationTemplate = string.IsNullOrWhiteSpace(FileNameNormalizationTemplateTextBox.Text)
            ? FileNameNormalizer.DefaultTemplate
            : FileNameNormalizationTemplateTextBox.Text.Trim();

        NormalizePlaylistFileNamesButton.IsEnabled = false;
        FileNameNormalizationResultText.Text = LocalizationService.Translate("Подготавливается предпросмотр файлов…");
        FileNameNormalizationResultText.Visibility = Visibility.Visible;

        try
        {
            FileNameNormalizer.RenameResult? result = await _owner.NormalizePlaylistFileNamesAsync(this);
            if (result is null)
            {
                FileNameNormalizationResultText.Text = LocalizationService.Translate("Нормализация отменена.");
                return;
            }

            string errors = result.Errors.Count > 0 ? $" Ошибок: {result.Errors.Count}." : string.Empty;
            FileNameNormalizationResultText.Text =
                LocalizationService.Translate($"Готово. Переименовано: {result.RenamedCount}; пропущено: {result.SkippedCount}.{errors}");
        }
        catch (Exception ex)
        {
            FileNameNormalizationResultText.Text = LocalizationService.Translate($"Не удалось нормализовать имена: {ex.Message}");
        }
        finally
        {
            NormalizePlaylistFileNamesButton.IsEnabled = true;
        }
    }

    private void ImprovedShuffleCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.UseImprovedShuffle = ImprovedShuffleCheckBox.IsChecked == true;

        // Колода/история от предыдущего режима шаффла не имеет смысла в новом —
        // начинаем с чистого листа, а не пытаемся домешать её в новую логику.
        _owner.ResetShuffleState();
    }

    private void SaveQueueBetweenRestartsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _owner.SetSaveQueueBetweenRestarts(SaveQueueBetweenRestartsCheckBox.IsChecked == true);
    }

    // См. AppSettings.ProgressBarStyle / WaveformView. MainWindow.ApplyProgressBarStyle сама
    // разбирается, нужно ли при этом (пере)считать форму волны для уже загруженного трека.
    private void ProgressBarStyleRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.ProgressBarStyle = ProgressBarWaveformRadio.IsChecked == true ? "Waveform" : "Slider";
        _owner.ApplyProgressBarStyle();
    }

    // См. AppSettings.ReplayGainEnabled / ReplayGainReader.
    private void ReplayGainCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.ReplayGainEnabled = ReplayGainCheckBox.IsChecked == true;
        _owner.RefreshReplayGain();
    }

    private void AlbumArtTransitionRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _owner.SetAlbumArtTransitionEnabled(AlbumArtTransitionOnRadio.IsChecked == true);
    }

    private void AlbumArtGesturesCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.AlbumArtGesturesEnabled = AlbumArtGesturesCheckBox.IsChecked == true;
    }

    // Экспорт/импорт .lumi (см. LumiProfile.cs): только настройки (тема, акцент, EQ, хоткеи), без плейлиста и избранного.

    private void ExportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var saveDialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = LumiProfileIO.FileFilter,
            DefaultExt = LumiProfileIO.FileExtension,
            FileName = "Lumisense" + LumiProfileIO.FileExtension
        };
        if (saveDialog.ShowDialog(this) != true) return;

        try
        {
            LumiProfileIO.Export(saveDialog.FileName, _settings);

            LocalizedMessageBox.Show(this, "Настройки сохранены.", "Экспорт завершён",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            LocalizedMessageBox.Show(this, $"Не удалось сохранить файл:\n{ex.Message}", "Ошибка экспорта",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void ImportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var openDialog = new Microsoft.Win32.OpenFileDialog { Filter = LumiProfileIO.FileFilter };
        if (openDialog.ShowDialog(this) != true) return;

        var profile = LumiProfileIO.TryReadFile(openDialog.FileName);
        if (profile == null)
        {
            LocalizedMessageBox.Show(this, "Не удалось прочитать этот файл — он повреждён или это не .lumi-профиль.",
                "Ошибка импорта", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return;
        }

        LumiProfileIO.Apply(profile.Settings, _settings);
        LocalizationService.ChangeLanguage(_settings, _settings.Language);
        _owner.ApplyImportedSettingsLive();
        SettingsManager.Save(_settings);

        LocalizedMessageBox.Show(this,
            "Настройки импортированы.\n\nЧасть из них (хоткеи, эквалайзер, поведение трея и мини-плеера) применится полностью после перезапуска плеера.",
            "Импорт завершён", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);

        // Поля окна читаются из _settings один раз в конструкторе и после импорта не переприменяются — проще переоткрыть
        // окно (MainWindow.ShowSettingsWindow), чем обновлять каждое поле формы.
        Close();
        _owner.ShowSettingsWindow("Profile");
    }

    // Необратимый массовый сброс (AppSettings/LumiProfileIO.ResetToDefaults, ResetPlayerButton), устроенный как импорт
    // профиля: подтверждение, сброс, сохранение, живое применение части, предупреждение о перезапуске, переоткрытие окна.
    private void ResetPlayerButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = LocalizedMessageBox.Show(this,
            "Сбросить тему, акцент, подложку окна, вид и размер плеера, громкость, шафл/повтор, горячие клавиши, мини-плеер и остальные настройки к значениям по умолчанию?\n\n" +
            "Плейлист, избранное, история прослушиваний, статистика и сохранённые пресеты эквалайзера затронуты не будут.",
            "Сбросить плеер?", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        if (!SettingsResetRecoveryService.TryCreateSnapshot(_settings))
        {
            LocalizedMessageBox.Show(this, LocalizationService.Get(LocalizationKey.ProfileResetSnapshotCreateFailedSettings),
                LocalizationService.Get(LocalizationKey.ProfileResetCancelledTitle), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return;
        }

        LumiProfileIO.ResetToDefaults(_settings);
        LocalizationService.ChangeLanguage(_settings, _settings.Language);
        _owner.ApplyImportedSettingsLive();
        SettingsManager.Save(_settings);

        LocalizedMessageBox.Show(this,
            "Плеер сброшен к исходным настройкам.\n\nЧасть из них (хоткеи, эквалайзер, поведение трея и мини-плеера, размер и положение окна) применится полностью после перезапуска плеера.",
            "Сброс завершён", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);

        Close();
        _owner.ShowSettingsWindow("Profile");
    }

    private void RestoreResetSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = LocalizedMessageBox.Show(this,
            LocalizationService.Get(LocalizationKey.ProfileRestoreConfirm),
            LocalizationService.Get(LocalizationKey.ProfileRestoreConfirmTitle), System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        if (!_owner.TryRestoreLastSettingsReset())
        {
            LocalizedMessageBox.Show(this, LocalizationService.Get(LocalizationKey.ProfileRestoreUnavailable),
                LocalizationService.Get(LocalizationKey.ProfileRestoreUnavailableTitle), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            RefreshResetRecoveryButton();
            return;
        }

        LocalizationService.ChangeLanguage(_settings, _settings.Language);
        LocalizedMessageBox.Show(this,
            LocalizationService.Get(LocalizationKey.ProfileRestoreCompleted),
            LocalizationService.Get(LocalizationKey.ProfileRestoreCompletedTitle), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        Close();
        _owner.ShowSettingsWindow("Profile");
    }

    private void ResetAllDataButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = LocalizedMessageBox.Show(this,
            "Будут удалены настройки, сохранённые плейлисты, избранное, история прослушиваний, статистика и пресеты эквалайзера.\n\n" +
            "Аудиофайлы на диске не удаляются. Продолжить?",
            "Полный сброс данных", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        var secondConfirm = LocalizedMessageBox.Show(this,
            LocalizationService.Get(LocalizationKey.ProfileResetFullConfirm),
            "Подтвердите полный сброс", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (secondConfirm != System.Windows.MessageBoxResult.Yes) return;

        if (!SettingsResetRecoveryService.TryCreateSnapshot(_settings))
        {
            LocalizedMessageBox.Show(this, LocalizationService.Get(LocalizationKey.ProfileResetSnapshotCreateFailedFull),
                LocalizationService.Get(LocalizationKey.ProfileResetCancelledTitle), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return;
        }

        _owner.ResetAllUserData();
        LocalizedMessageBox.Show(this,
            "Данные очищены. Для полного применения стандартных настроек перезапустите Lumisense.",
            "Сброс завершён", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        Close();
        _owner.ShowSettingsWindow("Profile");
    }

    // Навигация: каждый пункт слева — RadioButton с Tag = ключ страницы; Checked прячет все страницы и показывает выбранную
    // ("О плеере" — тоже просто страница, а не отдельное окно).

    private void NavItem_Checked(object sender, RoutedEventArgs e)
    {
        // Полное имя типа, а не using System.Windows.Controls.Primitives; чтобы не столкнуть RadioButton
        // с Wpf.Ui.Controls.Button, который в этом файле используется как просто "Button".
        if (sender is not System.Windows.Controls.RadioButton { Tag: string key }) return;

        // Защита от раннего вызова до присвоения полей страниц в InitializeComponent (например, из-за IsChecked в XAML).
        if (PageAppearance is null) return;

        PageAppearance.Visibility = key == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        PageWindow.Visibility = key == "Window" ? Visibility.Visible : Visibility.Collapsed;
        PagePlayback.Visibility = key == "Playback" ? Visibility.Visible : Visibility.Collapsed;
        PageIntegrations.Visibility = key == "Integrations" ? Visibility.Visible : Visibility.Collapsed;
        PageNotifications.Visibility = key == "Notifications" ? Visibility.Visible : Visibility.Collapsed;
        PageEqualizer.Visibility = key == "Equalizer" ? Visibility.Visible : Visibility.Collapsed;
        PageMiniPlayer.Visibility = key == "MiniPlayer" ? Visibility.Visible : Visibility.Collapsed;
        PageHotkeys.Visibility = key == "Hotkeys" ? Visibility.Visible : Visibility.Collapsed;
        PageProfile.Visibility = key == "Profile" ? Visibility.Visible : Visibility.Collapsed;
        PageUpdates.Visibility = key == "Updates" ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = key == "About" ? Visibility.Visible : Visibility.Collapsed;
        if (key == "Updates" && IsLoaded && _basePackagePlan is null)
            _ = RefreshVelopackBasePackagePlanAsync();

        // Один ScrollViewer на все страницы (см. SettingsWindow.xaml) без сброса помнил бы прокрутку прошлой вкладки;
        // SearchResultItem_Click позже отложенно прокрутит к найденному элементу и просто переопределит позицию.
        PART_ContentScroll.ScrollToTop();

        FrameworkElement? activePage = key switch
        {
            "Appearance" => PageAppearance,
            "Window" => PageWindow,
            "Playback" => PagePlayback,
            "Integrations" => PageIntegrations,
            "Notifications" => PageNotifications,
            "Equalizer" => PageEqualizer,
            "MiniPlayer" => PageMiniPlayer,
            "Hotkeys" => PageHotkeys,
            "Profile" => PageProfile,
            "Updates" => PageUpdates,
            "About" => PageAbout,
            _ => null
        };
        if (activePage is not null)
            AnimateSettingsPage(activePage);
    }

    private static void AnimateSettingsPage(FrameworkElement page)
    {
        page.Opacity = 0;
        var translate = new System.Windows.Media.TranslateTransform(0, 10);
        page.RenderTransform = translate;
        page.RenderTransformOrigin = new Point(0.5, 0);

        var easing = new System.Windows.Media.Animation.CubicEase
        {
            EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
        };
        page.BeginAnimation(UIElement.OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = easing
            });
        translate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new System.Windows.Media.Animation.DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = easing
            });
    }

    // Список изменений и настройки не должны быть открыты одновременно: MainWindow.ShowChangelogWindow (симметрично
    // ShowSettingsWindow) централизует переключение окон и закроет это окно.
    private void ChangelogButton_Click(object sender, RoutedEventArgs e) => _owner.ShowChangelogWindow();

    // Process.Start с UseShellExecute=true: в .NET Core без этого флага URL напрямую не открывается (как у MoreButton_Click
    // в UpdateAvailableWindow); try/catch на случай отсутствия браузера по умолчанию.
    private void DeveloperGitHubButton_Click(object sender, RoutedEventArgs e) => OpenUrl("https://github.com/wasssly");

    private void OpenRepositoryButton_Click(object sender, RoutedEventArgs e) => OpenUrl("https://github.com/wasssly/Lumisense");

    private void DeveloperTelegramButton_Click(object sender, RoutedEventArgs e) => OpenUrl("https://t.me/dontwritetoblame");

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e) => Logger.OpenLogsFolder();

    // Загружает аватар один раз и сохраняет его локально, чтобы последующие открытия Settings
    // не зависели от сети и не создавали новый HTTP-запрос каждый раз.
    private async void LoadDeveloperAvatar()
    {
        const string avatarUrl = "https://github.com/wasssly.png?size=96";
        string cacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumisense", "Cache");
        string cachePath = Path.Combine(cacheDirectory, "developer-avatar-96.png");
        string temporaryPath = Path.Combine(cacheDirectory, $"developer-avatar-{Guid.NewGuid():N}.part");

        try
        {
            byte[] bytes;
            bool cacheIsFresh = File.Exists(cachePath) &&
                                DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < TimeSpan.FromDays(30);
            if (cacheIsFresh)
            {
                bytes = await File.ReadAllBytesAsync(cachePath);
            }
            else
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Lumisense/1.0");
                bytes = await client.GetByteArrayAsync(avatarUrl);
                if (bytes.Length == 0 || bytes.Length > 2 * 1024 * 1024) return;

                Directory.CreateDirectory(cacheDirectory);
                await File.WriteAllBytesAsync(temporaryPath, bytes);
                File.Move(temporaryPath, cachePath, true);
            }

            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            _developerAvatarBitmap = bitmap;
            DeveloperAvatarBrush.ImageSource = bitmap;
        }
        catch
        {
            // При недоступной сети или повреждённом кэше остаётся XAML placeholder.
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch { }
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Нет браузера по умолчанию и т.п. — не критично, просто ничего не открылось
        }
    }

    // Запись хоткея: клик по кнопке включает режим записи, следующее нажатие (с Ctrl/Alt/Shift) сохраняется как
    // глобальная комбинация и сразу перерегистрируется в GlobalMediaHotKeys без перезапуска.

    private void HotkeyPlayPauseButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.PlayPause);
    private void HotkeyNextButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.Next);
    private void HotkeyPreviousButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.Previous);
    private void HotkeyStopButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.Stop);
    private void HotkeyVolumeUpButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.VolumeUp);
    private void HotkeyVolumeDownButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.VolumeDown);
    private void HotkeyMuteButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.Mute);
    private void HotkeyShuffleButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.Shuffle);
    private void HotkeyRepeatButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.Repeat);
    private void HotkeyToggleFavoriteButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.ToggleFavorite);
    private void HotkeyToggleLyricsButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.ToggleLyrics);
    private void HotkeyToggleMiniPlayerButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.ToggleMiniPlayer);
    private void HotkeyDeleteTrackButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.DeleteTrack);
    private void HotkeySeekForwardButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.SeekForward);
    private void HotkeySeekBackwardButton_Click(object sender, RoutedEventArgs e) => BeginRecording(HotkeyTarget.SeekBackward);

    private void HotkeyPlayPauseClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.PlayPause);
    private void HotkeyNextClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.Next);
    private void HotkeyPreviousClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.Previous);
    private void HotkeyStopClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.Stop);
    private void HotkeyVolumeUpClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.VolumeUp);
    private void HotkeyVolumeDownClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.VolumeDown);
    private void HotkeyMuteClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.Mute);
    private void HotkeyShuffleClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.Shuffle);
    private void HotkeyRepeatClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.Repeat);
    private void HotkeyToggleFavoriteClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.ToggleFavorite);
    private void HotkeyToggleLyricsClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.ToggleLyrics);
    private void HotkeyToggleMiniPlayerClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.ToggleMiniPlayer);
    private void HotkeyDeleteTrackClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.DeleteTrack);
    private void HotkeySeekForwardClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.SeekForward);
    private void HotkeySeekBackwardClearButton_Click(object sender, RoutedEventArgs e) => ClearHotkey(HotkeyTarget.SeekBackward);

    private void BeginRecording(HotkeyTarget target)
    {
        // Если уже что-то записывали, но не закончили — просто отменяем ту запись
        CancelRecording();

        _recordingTarget = target;
        GetHotkeyButton(target).Content = LocalizationService.Translate("Нажмите комбинацию…");
    }

    private void CancelRecording()
    {
        if (_recordingTarget == HotkeyTarget.None) return;

        var target = _recordingTarget;
        _recordingTarget = HotkeyTarget.None;
        RefreshHotkeyButtonText(target);
    }

    private void SettingsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recordingTarget == HotkeyTarget.None) return;

        e.Handled = true;

        // Alt-комбинации в WPF приходят как Key.System, реальная клавиша — в SystemKey
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            CancelRecording();
            return;
        }

        // Одни только модификаторы не считаем нажатием — ждём клавишу вместе с ними
        if (IsModifierKey(key)) return;

        // Не используем Keyboard.Modifiers: в PreviewKeyDown он ненадёжно определяет правые Ctrl/Alt/Shift
        // (на некоторых клавиатурах/раскладках видит только левые), поэтому опрашиваем Keyboard.IsKeyDown для обеих сторон.
        var isCtrl = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
        var isAlt = Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt);
        var isShift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        var isWinDown = Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);

        if (!isCtrl && !isAlt && !isShift && !isWinDown)
        {
            // Глобальная комбинация без модификатора будет перехватывать обычный ввод
            // во всех остальных окнах и приложениях — не даём её записать
            GetHotkeyButton(_recordingTarget).Content = LocalizationService.Translate("Нужен Ctrl/Alt/Shift/Win…");
            return;
        }

        var binding = new HotkeyBinding
        {
            Ctrl = isCtrl,
            Alt = isAlt,
            Shift = isShift,
            Win = isWinDown,
            Key = key.ToString()
        };

        var target = _recordingTarget;
        _recordingTarget = HotkeyTarget.None;

        SetHotkeyBinding(target, binding);
        RefreshHotkeyButtonText(target);
        _owner.ReapplyHotkeys();
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    private void ClearHotkey(HotkeyTarget target)
    {
        CancelRecording();

        SetHotkeyBinding(target, new HotkeyBinding());
        RefreshHotkeyButtonText(target);
        _owner.ReapplyHotkeys();
    }

    private void SetHotkeyBinding(HotkeyTarget target, HotkeyBinding binding)
    {
        switch (target)
        {
            case HotkeyTarget.PlayPause: _settings.HotkeyPlayPause = binding; break;
            case HotkeyTarget.Next: _settings.HotkeyNext = binding; break;
            case HotkeyTarget.Previous: _settings.HotkeyPrevious = binding; break;
            case HotkeyTarget.Stop: _settings.HotkeyStop = binding; break;
            case HotkeyTarget.VolumeUp: _settings.HotkeyVolumeUp = binding; break;
            case HotkeyTarget.VolumeDown: _settings.HotkeyVolumeDown = binding; break;
            case HotkeyTarget.Mute: _settings.HotkeyMute = binding; break;
            case HotkeyTarget.Shuffle: _settings.HotkeyShuffle = binding; break;
            case HotkeyTarget.Repeat: _settings.HotkeyRepeat = binding; break;
            case HotkeyTarget.ToggleFavorite: _settings.HotkeyToggleFavorite = binding; break;
            case HotkeyTarget.ToggleLyrics: _settings.HotkeyToggleLyrics = binding; break;
            case HotkeyTarget.ToggleMiniPlayer: _settings.HotkeyToggleMiniPlayer = binding; break;
            case HotkeyTarget.DeleteTrack: _settings.HotkeyDeleteTrack = binding; break;
            case HotkeyTarget.SeekForward: _settings.HotkeySeekForward = binding; break;
            case HotkeyTarget.SeekBackward: _settings.HotkeySeekBackward = binding; break;
        }
    }

    private HotkeyBinding GetHotkeyBinding(HotkeyTarget target) => target switch
    {
        HotkeyTarget.PlayPause => _settings.HotkeyPlayPause,
        HotkeyTarget.Next => _settings.HotkeyNext,
        HotkeyTarget.Previous => _settings.HotkeyPrevious,
        HotkeyTarget.Stop => _settings.HotkeyStop,
        HotkeyTarget.VolumeUp => _settings.HotkeyVolumeUp,
        HotkeyTarget.VolumeDown => _settings.HotkeyVolumeDown,
        HotkeyTarget.Mute => _settings.HotkeyMute,
        HotkeyTarget.Shuffle => _settings.HotkeyShuffle,
        HotkeyTarget.Repeat => _settings.HotkeyRepeat,
        HotkeyTarget.ToggleFavorite => _settings.HotkeyToggleFavorite,
        HotkeyTarget.ToggleLyrics => _settings.HotkeyToggleLyrics,
        HotkeyTarget.ToggleMiniPlayer => _settings.HotkeyToggleMiniPlayer,
        HotkeyTarget.DeleteTrack => _settings.HotkeyDeleteTrack,
        HotkeyTarget.SeekForward => _settings.HotkeySeekForward,
        HotkeyTarget.SeekBackward => _settings.HotkeySeekBackward,
        _ => new HotkeyBinding()
    };

    private Button GetHotkeyButton(HotkeyTarget target) => target switch
    {
        HotkeyTarget.PlayPause => HotkeyPlayPauseButton,
        HotkeyTarget.Next => HotkeyNextButton,
        HotkeyTarget.Previous => HotkeyPreviousButton,
        HotkeyTarget.Stop => HotkeyStopButton,
        HotkeyTarget.VolumeUp => HotkeyVolumeUpButton,
        HotkeyTarget.VolumeDown => HotkeyVolumeDownButton,
        HotkeyTarget.Mute => HotkeyMuteButton,
        HotkeyTarget.Shuffle => HotkeyShuffleButton,
        HotkeyTarget.Repeat => HotkeyRepeatButton,
        HotkeyTarget.ToggleFavorite => HotkeyToggleFavoriteButton,
        HotkeyTarget.ToggleLyrics => HotkeyToggleLyricsButton,
        HotkeyTarget.ToggleMiniPlayer => HotkeyToggleMiniPlayerButton,
        HotkeyTarget.DeleteTrack => HotkeyDeleteTrackButton,
        HotkeyTarget.SeekForward => HotkeySeekForwardButton,
        HotkeyTarget.SeekBackward => HotkeySeekBackwardButton,
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };

    private void RefreshHotkeyButtonText(HotkeyTarget target)
    {
        GetHotkeyButton(target).Content = FormatBinding(GetHotkeyBinding(target));
    }

    private static string FormatBinding(HotkeyBinding binding)
    {
        if (binding.IsEmpty) return "Не задано";

        var parts = new List<string>();
        if (binding.Ctrl) parts.Add("Ctrl");
        if (binding.Alt) parts.Add("Alt");
        if (binding.Shift) parts.Add("Shift");
        if (binding.Win) parts.Add("Win");
        parts.Add(DisplayKeyName(binding.Key));

        return string.Join(" + ", parts);
    }

    // Немного облагораживаем отображение некоторых клавиш, чьи имена в System.Windows.Input.Key
    // не совсем очевидны пользователю (например, Key.Next — это на самом деле PageDown)
    private static string DisplayKeyName(string keyName) => keyName switch
    {
        "Left" => "←",
        "Right" => "→",
        "Up" => "↑",
        "Down" => "↓",
        "Next" => "PageDown",
        "Prior" => "PageUp",
        "OemPlus" => "+",
        "OemMinus" => "-",
        "OemComma" => ",",
        "OemPeriod" => ".",
        "Escape" => "Esc",
        _ => keyName
    };
}
