using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using static Lumisense.BackgroundTask;

namespace Lumisense;

// Создаётся при первом показе страницы (см. SettingsWindow.EnsurePageBuilt).
public sealed partial class PlaybackSettingsPage : System.Windows.Controls.UserControl, ILazySettingsPage
{
    private readonly AppSettings _settings;
    private readonly ISettingsHost _owner;

    // Глушит Changed-обработчики, пока значения читаются из настроек.
    private bool _isInitializing = true;
    private bool _isWindowClosed;
    private bool _isRefreshingOutputDevices;
    private bool _isRefreshingWasapiMode;
    private int _outputDeviceLoadVersion;

    internal PlaybackSettingsPage(Window window, AppSettings settings, ISettingsHost owner)
    {
        _settings = settings;
        _owner = owner;

        // StaticResource разбирается до того, как элементы попадут в дерево окна, поэтому ресурсы окна подключаются заранее.
        Resources.MergedDictionaries.Add(window.Resources);
        InitializeComponent();

        RememberVolumeCheckBox.IsChecked = _settings.RememberVolume;
        LogarithmicVolumeCheckBox.IsChecked = _settings.UseLogarithmicVolume;
        InitializeOutputDeviceCombo();
        InitializeWasapiModeCombo();
        InitializeWasapiLatencyCombo();
        RaisePlaybackPriorityCheckBox.IsChecked = _settings.RaisePlaybackPriority;
        TrackLoadTraceCheckBox.IsChecked = _settings.TrackLoadTraceEnabled;
        NeverAutoPlayLastTrackOnStartupCheckBox.IsChecked = _settings.NeverAutoPlayLastTrackOnStartup;
        LoadShuffleModeControls();
        SaveQueueBetweenRestartsCheckBox.IsChecked = _settings.SaveQueueBetweenRestarts;
        AutoRefreshPlaylistFoldersCheckBox.IsChecked = _settings.AutoRefreshPlaylistFolders;
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

        // Выпадающие списки радиогрупп читают отмеченные радиокнопки, поэтому связываются после загрузки настроек.
        RadioComboLinks.Initialize(this, FindName);
        _isInitializing = false;
    }

    public void MoveContentTo(System.Windows.Controls.Panel host) => LazySettingsPage.MoveChildren(PagePlayback, host);

    public FrameworkElement? FindElement(string name) => FindName(name) as FrameworkElement;

    public void RefreshLocalization() => InitializeOutputDeviceCombo();

    public void RefreshPlaybackBalance() => PlaybackBalanceSlider.Value = Math.Clamp(_settings.PlaybackBalance, -100, 100);

    public void MarkWindowClosed() => _isWindowClosed = true;

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


    private void LoadShuffleModeControls()
    {
        string mode = ShuffleModes.Normalize(_settings.ShuffleMode);
        ShuffleModeDeckRadio.IsChecked = mode == ShuffleModes.Deck;
        ShuffleModeRandomRadio.IsChecked = mode == ShuffleModes.Random;
        ShuffleModeFavoritesRadio.IsChecked = mode == ShuffleModes.Favorites;
        ShuffleModeRareRadio.IsChecked = mode == ShuffleModes.Rare;
        ShuffleModeFoldersRadio.IsChecked = mode == ShuffleModes.Folders;
        ShuffleModeDescriptionText.Text = LocalizationService.Translate(ShuffleModeDescription(mode));
    }

    private static string ShuffleModeDescription(string mode) => mode switch
    {
        ShuffleModes.Random => "Каждый следующий трек выбирается случайно из всего плейлиста, повторы возможны, но не два раза подряд.",
        ShuffleModes.Favorites => "Случайный выбор, при котором треки из избранного выпадают втрое чаще остальных.",
        ShuffleModes.Rare => "Случайный выбор, при котором чаще выпадают треки, которые вы слушали реже всего (по счётчику прослушиваний).",
        ShuffleModes.Folders => "Папка или альбом играет подряд в порядке плейлиста, затем случайная ещё не игравшая папка; пока не сыграют все папки, они не повторяются.",
        _ => "Весь плейлист один раз тасуется, и треки играют по порядку из этой тасовки: трек не повторится, пока не сыграют все остальные."
    };

    private void ShuffleModeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        string mode = ShuffleModeRandomRadio.IsChecked == true ? ShuffleModes.Random
            : ShuffleModeFavoritesRadio.IsChecked == true ? ShuffleModes.Favorites
            : ShuffleModeRareRadio.IsChecked == true ? ShuffleModes.Rare
            : ShuffleModeFoldersRadio.IsChecked == true ? ShuffleModes.Folders
            : ShuffleModes.Deck;
        if (mode == _settings.ShuffleMode) return;

        _settings.ShuffleMode = mode;
        ShuffleModeDescriptionText.Text = LocalizationService.Translate(ShuffleModeDescription(mode));

        // Колода/история от предыдущего режима шаффла не имеет смысла в новом —
        // начинаем с чистого листа, а не пытаемся домешать её в новую логику.
        _owner.ResetShuffleState();
    }

    private void SaveQueueBetweenRestartsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _owner.SetSaveQueueBetweenRestarts(SaveQueueBetweenRestartsCheckBox.IsChecked == true);
    }

    private void AutoRefreshPlaylistFoldersCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _owner.SetAutoRefreshPlaylistFolders(AutoRefreshPlaylistFoldersCheckBox.IsChecked == true);
    }

    // См. AppSettings.ReplayGainEnabled / ReplayGainReader.
    private void ReplayGainCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.ReplayGainEnabled = ReplayGainCheckBox.IsChecked == true;
        _owner.RefreshReplayGain();
    }

    private void LoudnessScanCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.LoudnessScanEnabled = LoudnessScanCheckBox.IsChecked == true;
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void LimiterCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.LimiterEnabled = LimiterCheckBox.IsChecked == true;
        _owner.ApplyLimiter();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void PlaybackSpeedChangesPitchCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.PlaybackSpeedChangesPitch = PlaybackSpeedChangesPitchCheckBox.IsChecked == true;
        _owner.RefreshPlaybackRateMode();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void ReplayGainAlbumCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.ReplayGainAlbumMode = ReplayGainAlbumCheckBox.IsChecked == true;
        _owner.RefreshReplayGain();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void ReplayGainPreampSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateReplayGainPreampText();
        if (_isInitializing) return;

        _settings.ReplayGainPreampDb = Math.Clamp(Math.Round(ReplayGainPreampSlider.Value * 2) / 2, -12, 12);
        _owner.RefreshReplayGain();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void ReplayGainPreampSlider_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ReplayGainPreampSlider.Value = 0;

    private void UpdateReplayGainPreampText()
    {
        if (ReplayGainPreampValueText is null || ReplayGainPreampSlider is null) return;

        double value = Math.Round(ReplayGainPreampSlider.Value * 2) / 2;
        ReplayGainPreampValueText.Text = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(value > 0 ? "+" : "")}{value:0.#} dB");
    }

    // См. AppSettings.PlaybackChannelMode / ChannelModeSampleProvider.
    private void ChannelModeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.PlaybackChannelMode = ChannelModeMonoRadio.IsChecked == true ? "Mono" : "Stereo";
        _owner.ApplyChannelMode();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    // См. AppSettings.DeviceConnectStyle.
    private void DeviceConnectStyleCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.DeviceConnectStyle = (DeviceConnectStyleCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "Island";
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void DeviceConnectPreviewButton_Click(object sender, RoutedEventArgs e) => _owner.PreviewDeviceConnectOverlay();

    // См. AppSettings.DeviceCardAnimation. Выбранный вариант сразу показывается в острове.
    private void DeviceCardAnimationCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.DeviceCardAnimation = (DeviceCardAnimationCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "Orbit";
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
        _owner.PreviewDeviceConnectOverlay();
    }

    // См. AppSettings.DeviceKindOverrides.
    private void RefreshDeviceKindControls()
    {
        bool wasInitializing = _isInitializing;
        _isInitializing = true;
        try
        {
            string tag = _owner.GetOutputKindOverride();
            DeviceKindCombo.SelectedItem = DeviceKindCombo.Items.Cast<System.Windows.Controls.ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.Ordinal)) ?? DeviceKindCombo.Items[0];
            DeviceKindCombo.IsEnabled = _owner.HasOutputDevice;
            DeviceKindDetectionText.Text = _owner.GetOutputKindDetectionText();
        }
        finally
        {
            _isInitializing = wasInitializing;
        }
    }

    private void DeviceKindCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        _owner.SetOutputKindOverride((DeviceKindCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "");
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
        DeviceKindDetectionText.Text = _owner.GetOutputKindDetectionText();
    }

    // См. AppSettings.PlaybackBalance.
    private void PlaybackBalanceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdatePlaybackBalanceText();
        if (_isInitializing) return;

        _settings.PlaybackBalance = (int)Math.Clamp(Math.Round(PlaybackBalanceSlider.Value), -100, 100);
        _owner.ApplyChannelMode();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    // См. AppSettings.CrossfadeSeconds / CrossfadeSkipSameAlbum; MainWindow подхватывает их на ближайшем тике прогресса.
    private void CrossfadeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateCrossfadeText();
        if (_isInitializing) return;

        _settings.CrossfadeSeconds = Math.Clamp((int)Math.Round(CrossfadeSlider.Value), 0, 12);
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void CrossfadeSlider_MouseDoubleClick(object sender, MouseButtonEventArgs e) => CrossfadeSlider.Value = 0;

    private void CrossfadeSkipSameAlbumCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.CrossfadeSkipSameAlbum = CrossfadeSkipSameAlbumCheckBox.IsChecked == true;
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void UpdateCrossfadeText()
    {
        if (CrossfadeValueText is null || CrossfadeSlider is null) return;

        int seconds = (int)Math.Round(CrossfadeSlider.Value);
        CrossfadeValueText.Text = seconds == 0 ? LocalizationService.Translate("Выкл.") : $"{seconds} {LocalizationService.Translate("с")}";
    }

    // См. AppSettings.GaplessEnabled; MainWindow снимает или готовит заготовку на ближайшем тике прогресса.
    private void GaplessCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.GaplessEnabled = GaplessCheckBox.IsChecked == true;
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    // См. AppSettings.StereoWidthPercent, CrossfeedPercent и LoudnessCompensationPercent; Tag слайдера — его имя без «Slider».
    private void StereoImageSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateStereoImageTexts();
        if (_isInitializing || sender is not System.Windows.Controls.Slider { Tag: string tag } slider) return;

        int value = (int)Math.Round(slider.Value);
        switch (tag)
        {
            case "StereoWidth":
                _settings.StereoWidthPercent = Math.Clamp(value, 0, 200);
                _owner.ApplyStereoImage();
                break;
            case "Crossfeed":
                _settings.CrossfeedPercent = Math.Clamp(value, 0, 100);
                _owner.ApplyStereoImage();
                break;
            default:
                _settings.LoudnessCompensationPercent = Math.Clamp(value, 0, 100);
                _owner.ApplyLoudnessCompensation();
                break;
        }

        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void StereoImageSlider_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.Slider { Tag: string tag } slider) slider.Value = tag == "StereoWidth" ? 100 : 0;
    }

    private void UpdateStereoImageTexts()
    {
        if (StereoWidthValueText is null || StereoWidthSlider is null || CrossfeedValueText is null || CrossfeedSlider is null
            || LoudnessCompensationValueText is null || LoudnessCompensationSlider is null) return;

        StereoWidthValueText.Text = $"{(int)Math.Round(StereoWidthSlider.Value)}%";
        CrossfeedValueText.Text = CrossfeedSlider.Value < 1 ? LocalizationService.Translate("Выкл.") : $"{(int)Math.Round(CrossfeedSlider.Value)}%";
        LoudnessCompensationValueText.Text = LoudnessCompensationSlider.Value < 1 ? LocalizationService.Translate("Выкл.") : $"{(int)Math.Round(LoudnessCompensationSlider.Value)}%";
    }

    private void PlaybackBalanceSlider_MouseDoubleClick(object sender, MouseButtonEventArgs e) => PlaybackBalanceSlider.Value = 0;

    private void UpdatePlaybackBalanceText()
    {
        if (PlaybackBalanceValueText is null || PlaybackBalanceSlider is null) return;

        int value = (int)Math.Round(PlaybackBalanceSlider.Value);
        PlaybackBalanceValueText.Text = value == 0
            ? LocalizationService.Translate("Центр")
            : $"{LocalizationService.Translate(value < 0 ? "Л" : "П")} {Math.Abs(value)}%";
    }
}
