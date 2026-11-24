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
