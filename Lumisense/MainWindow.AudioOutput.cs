using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls.Primitives;
using WpfSlider = System.Windows.Controls.Slider;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using static Lumisense.BackgroundTask;

namespace Lumisense;

// Устройство вывода: WASAPI-плеер, выбор и смена устройства, восстановление после сбоя, диагностика, освобождение файла.
// Вынесено из MainWindow.xaml.cs только ради навигации, логика и порядок вызовов не менялись.
public partial class MainWindow
{
    // 30 мс вызывали щелчки на стыке треков (drain не успевал дождаться реального опустошения
    // WASAPI-буфера) — 60 мс безопасный минимум для плеера с realtime DSP (SoundTouch, эквалайзер). По умолчанию 100 мс:
    // при загрузке процессора под 100% 60 мс не хватало, и звук щёлкал. Размер выбирает пользователь (AppSettings.WasapiLatencyMilliseconds);
    // поле статическое, потому что от него считаются паузы drain в статических методах смены трека.
    private const int DefaultWasapiLatencyMilliseconds = 100;
    private static int _wasapiLatencyMilliseconds = DefaultWasapiLatencyMilliseconds;
    private static int WasapiRequestedLatencyMilliseconds => _wasapiLatencyMilliseconds;

    // Следующее открытие устройства идёт через системное, но сохранённый выбор пользователя остаётся прежним.
    private bool _forceSystemOutputOnce;

    private void InitializeOutputDevice(ISampleProvider sampleProvider)
    {
        try
        {
            CreateAndInitializeWasapiPlayer(sampleProvider);
        }
        catch (Exception ex) when (!string.IsNullOrWhiteSpace(_settings.OutputDeviceName))
        {
            // Endpoint мог исчезнуть между перечислением и Init (USB/Bluetooth). Однократно переходим на системный
            // endpoint, не меняя сохранённый выбор: следующий трек снова попробует выбранное устройство.
            Logger.Error("Не удалось инициализировать выбранное устройство вывода; используется системное устройство", ex);
            DisposeOutputDeviceSafely();
            _forceSystemOutputOnce = true;
            CreateAndInitializeWasapiPlayer(sampleProvider);
        }
    }

    private void CreateAndInitializeWasapiPlayer(ISampleProvider sampleProvider)
    {
        EnsureOutputDevice();
        var initializationTimer = Stopwatch.StartNew();
        _audioOutputSession.Initialize(sampleProvider);
        initializationTimer.Stop();
        _outputDiagnostics.LastInitializationMilliseconds = initializationTimer.ElapsedMilliseconds;

        if (_outputDevice is WasapiPlayer wasapiPlayer)
        {
            WaveFormat format = wasapiPlayer.OutputWaveFormat;
            _outputDiagnostics.ActiveFormat = $"{format.SampleRate / 1000.0:0.#} kHz · {format.Channels} ch · {format.BitsPerSample}-bit";
        }
    }

    private void EnsureOutputDevice()
    {
        if (_outputDevice is not null) return;

        string savedDeviceKey = _settings.OutputDeviceName;
        bool systemOutputForced = _forceSystemOutputOnce;
        _forceSystemOutputOnce = false;
        string requestedDeviceKey = systemOutputForced ? AudioOutputDeviceService.SystemDefaultDeviceName : savedDeviceKey;
        AudioOutputDeviceService.ResolvedEndpoint resolved = _audioOutputDeviceManager.Resolve(requestedDeviceKey);
        _outputDiagnostics.ResetForNewEndpoint();
        _outputDiagnostics.ActiveDeviceKey = resolved.ActiveDeviceKey;

        bool usedFallback = resolved.UsedFallback ||
            systemOutputForced && !AudioOutputRecoveryPolicy.FollowsSystemDefault(savedDeviceKey);
        if (usedFallback)
        {
            // Выбор в настройках не перезаписываем: устройство может вернуться (см. TryRestoreSelectedOutputDevice).
            _outputDiagnostics.FallbackFrom = AudioOutputDeviceManager.GetFallbackSourceKey(savedDeviceKey, usedFallback: true);
            Logger.Warn($"Выбранное WASAPI-устройство недоступно: {savedDeviceKey}. Используется системное устройство Windows.");
            _settingsWindow?.RefreshOutputDeviceSelection();
        }
        else
        {
            _outputDiagnostics.FallbackFrom = null;
            // Старые WaveOut-ключи мигрируют к устойчивому endpoint-ID без сброса выбора.
            if (AudioOutputDeviceManager.ShouldPersistActiveKey(
                    requestedDeviceKey, resolved.ActiveDeviceKey, resolved.UsedFallback))
            {
                _settings.OutputDeviceName = resolved.ActiveDeviceKey;
                FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
                _settingsWindow?.RefreshOutputDeviceSelection();
            }
        }

        try
        {
            _outputEndpoint = resolved.Device;
            bool wantsExclusiveMode = string.Equals(_settings.WasapiMode, "Exclusive", StringComparison.OrdinalIgnoreCase);
            try
            {
                _outputDevice = BuildWasapiPlayer(_outputEndpoint, wantsExclusiveMode);
                _outputDiagnostics.ActiveWasapiMode = wantsExclusiveMode ? "Exclusive" : "Shared";
            }
            catch (Exception ex) when (wantsExclusiveMode)
            {
                // Не все устройства/форматы поддерживают монопольный режим — откатываемся на
                // общий, чтобы плеер не остался нерабочим.
                Logger.Warn($"Не удалось открыть устройство в монопольном режиме WASAPI, переключение на общий: {ex.Message}");
                _settings.WasapiMode = "Shared";
                FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
                _settingsWindow?.RefreshWasapiModeSelection();
                _outputDevice = BuildWasapiPlayer(_outputEndpoint, useExclusiveMode: false);
                _outputDiagnostics.ActiveWasapiMode = "Shared";
            }
            _audioOutputSession.Attach(_outputDevice!, _outputEndpoint!);
        }
        catch
        {
            resolved.Device.Dispose();
            _outputEndpoint = null;
            throw;
        }
    }

    // Применяет AppSettings.RaisePlaybackPriority: AboveNormal, пока включено; Normal возвращаем, только если приоритет повысили мы.
    public void ApplyPlaybackProcessPriority() => PlaybackProcessPriority.Apply(_settings.RaisePlaybackPriority);

    private WasapiPlayer BuildWasapiPlayer(MMDevice device, bool useExclusiveMode)
    {
        _wasapiLatencyMilliseconds = _settings.WasapiLatencyMilliseconds is 60 or 100 or 200
            ? _settings.WasapiLatencyMilliseconds
            : DefaultWasapiLatencyMilliseconds;
        var builder = new WasapiPlayerBuilder()
            .WithDevice(device)
            .WithEventSync()
            .WithLatency(WasapiRequestedLatencyMilliseconds)
            .WithCategory(AudioStreamCategory.Media)
            // "Pro Audio" — дефолт NAudio для этого параметра (мы сузили до "Audio" без причины);
            // эта MMCSS-категория защищает поток от щелчков при всплеске нагрузки (запуск игры).
            .WithMmcssThreadPriority("Pro Audio");
        builder = useExclusiveMode ? builder.WithExclusiveMode() : builder.WithSharedMode();
        return builder.Build();
    }

    // Вызывается из SettingsWindow сразу после выбора устройства. Снимок разделяет
    // запрошенную latency профиля и фактическую latency, которую WasapiPlayer получил после Init.
    internal AudioOutputRuntimeStatus GetOutputDeviceRuntimeStatus()
    {
        WasapiPlayer? player = _outputDevice as WasapiPlayer;
        string activeKey = _outputDevice is null ? _settings.OutputDeviceName : _outputDiagnostics.ActiveDeviceKey;
        string? activeEndpointId = player?.DeviceId ?? TryGetActiveOutputEndpointId();
        return new AudioOutputRuntimeStatus(
            AudioOutputDeviceService.GetDisplayName(activeKey),
            string.IsNullOrWhiteSpace(_outputDiagnostics.FallbackFrom) ? null : AudioOutputDeviceService.GetDisplayName(_outputDiagnostics.FallbackFrom),
            _outputDevice is not null,
            $"WASAPI {_outputDiagnostics.ActiveWasapiMode} · WasapiPlayer",
            WasapiRequestedLatencyMilliseconds,
            player?.LatencyMilliseconds,
            _outputDiagnostics.ActiveFormat,
            activeEndpointId,
            GetOutputPlaybackState(_outputDevice),
            AudioOutputRecoveryPolicy.FollowsSystemDefault(_settings.OutputDeviceName),
            _outputDiagnostics.LastInitializationMilliseconds,
            _outputDiagnostics.RecoveryCount,
            _outputDiagnostics.LastRecoveryReason,
            _outputDiagnostics.MeaningfulDeviceEventCount,
            _outputDiagnostics.LastDeviceEventKind,
            _outputDiagnostics.LastDeviceEventEndpointId);
    }

    // Отчёт формируется только по явному действию пользователя для clipboard. Он не включает
    // путь, название, исполнителя или другие данные текущего трека.
    internal string BuildAudioDiagnosticsReport() =>
        AudioDiagnosticsReportFormatter.Format(UpdateChecker.GetCurrentVersion(), GetOutputDeviceRuntimeStatus());

    AudioOutputRuntimeStatus ISettingsAudioHost.GetOutputDeviceRuntimeStatus() => GetOutputDeviceRuntimeStatus();
    string ISettingsAudioHost.BuildAudioDiagnosticsReport() => BuildAudioDiagnosticsReport();

    private string? TryGetActiveOutputEndpointId()
    {
        try
        {
            return _outputEndpoint?.ID;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось прочитать активный WASAPI endpoint: {ex.Message}");
            return null;
        }
    }

    private static string GetOutputPlaybackState(IWavePlayer? outputDevice) => outputDevice?.PlaybackState switch
    {
        NAudio.Wave.PlaybackState.Playing => "Воспроизводится",
        NAudio.Wave.PlaybackState.Paused => "На паузе",
        NAudio.Wave.PlaybackState.Stopped => "Остановлен",
        _ => "Не инициализирован"
    };

    public void ApplyOutputDeviceSelection()
    {
        if (_isExiting) return;

        _outputDiagnostics.FallbackFrom = null;
        string? currentPath = _currentTrackPath;
        TimeSpan position = _audioFile?.CurrentTime ?? TimeSpan.Zero;
        bool wasPlaying = _isPlaying;
        StopPlayback(disposeOnly: true);
        DisposeOutputDeviceSafely();

        if (!string.IsNullOrWhiteSpace(currentPath) && File.Exists(currentPath))
        {
            LoadAndPlay(currentPath, autoPlay: wasPlaying, startPosition: position,
                changeOrigin: TrackChangeOrigin.Automatic);
        }
    }

    private void AudioOutputEndpointMonitor_EndpointChanged(object? sender, AudioOutputEndpointChangedEventArgs e)
    {
        if (_isExiting || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        _ = Dispatcher.BeginInvoke(new Action(() => HandleAudioOutputEndpointChanged(e)), DispatcherPriority.Background);
    }

    private void HandleAudioOutputEndpointChanged(AudioOutputEndpointChangedEventArgs e)
    {
        if (_isExiting)
            return;

        // Список и карточка Settings должны сразу отражать подключение/отключение, но callback переносим с Core Audio worker
        // thread на Dispatcher; PropertyValueChanged приходит часто (например, громкость) и не требует пересборки ComboBox или recovery.
        if (e.Kind != AudioOutputEndpointChangeKind.DevicePropertiesChanged && _settingsWindow?.IsLoaded == true)
            _settingsWindow.RefreshOutputDeviceSelection();

        if (_outputDevice is null || _audioOutputRecoveryCoordinator.IsInProgress ||
            e.Kind == AudioOutputEndpointChangeKind.DevicePropertiesChanged)
            return;

        RecordMeaningfulOutputDeviceEvent(e);
        if (e.Kind is AudioOutputEndpointChangeKind.DeviceAdded or AudioOutputEndpointChangeKind.DeviceStateChanged &&
            TryRestoreSelectedOutputDevice())
            return;

        if (e.Kind == AudioOutputEndpointChangeKind.DefaultDeviceChanged)
        {
            QueueSystemDefaultEndpointRecovery(e.EndpointId);
            return;
        }

        string? activeEndpointId = TryGetActiveOutputEndpointId();
        bool activeEndpointBecameUnavailable =
            string.Equals(activeEndpointId, e.EndpointId, StringComparison.Ordinal) &&
            (e.Kind == AudioOutputEndpointChangeKind.DeviceRemoved ||
             e.Kind == AudioOutputEndpointChangeKind.DeviceStateChanged && e.State is not DeviceState.Active);
        if (!activeEndpointBecameUnavailable)
            return;

        const string reason = "активное устройство вывода отключено или стало недоступно";
        Logger.Warn($"{reason}; выполняется восстановление WASAPI.");
        RecoverOutputDeviceAfterFailure(new InvalidOperationException(reason), _isPlaying, expectedDeviceEvent: true);
    }

    // Выбранное устройство вернулось после временного отката на системное: переоткрываем вывод на нём с той же позиции.
    private bool TryRestoreSelectedOutputDevice()
    {
        if (!AudioOutputDeviceManager.ShouldRestoreSavedEndpoint(
                _settings.OutputDeviceName, _outputDiagnostics.ActiveDeviceKey,
                outputIsFallback: !string.IsNullOrWhiteSpace(_outputDiagnostics.FallbackFrom)) ||
            !AudioOutputDeviceService.IsAvailable(_settings.OutputDeviceName))
            return false;

        Logger.Info("Выбранное устройство вывода снова доступно; возврат с системного устройства.");
        ApplyOutputDeviceSelection();
        return true;
    }

    private void RecordMeaningfulOutputDeviceEvent(AudioOutputEndpointChangedEventArgs e)
    {
        _outputDiagnostics.RecordDeviceEvent(e.Kind, e.EndpointId);
    }

    private void QueueSystemDefaultEndpointRecovery(string? changedDefaultEndpointId)
    {
        if (!AudioOutputRecoveryPolicy.FollowsSystemDefault(_settings.OutputDeviceName))
        {
            Logger.Info("Смена системного устройства Windows не влияет на явно выбранный WASAPI endpoint.");
            return;
        }

        _pendingSystemDefaultEndpointId = changedDefaultEndpointId;
        _systemDefaultEndpointDebounceTimer.Stop();
        _systemDefaultEndpointDebounceTimer.Interval = TimeSpan.FromMilliseconds(SystemDefaultEndpointDebounceMilliseconds);
        _systemDefaultEndpointDebounceTimer.Start();
        Logger.Info("Windows изменил системное устройство вывода; endpoint будет подтверждён перед восстановлением WASAPI.");
    }

    private void SystemDefaultEndpointDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _systemDefaultEndpointDebounceTimer.Stop();
        if (_isExiting || _outputDevice is null || _audioOutputRecoveryCoordinator.IsInProgress)
            return;

        DateTime now = DateTime.UtcNow;
        DateTime lastRecoveryStartedUtc = _audioOutputRecoveryCoordinator.LastStartedUtc;
        double elapsedSinceRecovery = (now - lastRecoveryStartedUtc).TotalMilliseconds;
        if (lastRecoveryStartedUtc != DateTime.MinValue && elapsedSinceRecovery < OutputRecoveryCooldownMilliseconds)
        {
            // Endpoint notifications часто приходят пачкой. Не теряем последнее default-событие,
            // если recovery ещё stabilizes, а ждём окончание уже действующего cooldown.
            _systemDefaultEndpointDebounceTimer.Interval = TimeSpan.FromMilliseconds(
                Math.Max(1, OutputRecoveryCooldownMilliseconds - elapsedSinceRecovery));
            _systemDefaultEndpointDebounceTimer.Start();
            return;
        }

        string? changedDefaultEndpointId = _pendingSystemDefaultEndpointId;
        _pendingSystemDefaultEndpointId = null;
        string? activeEndpointId = TryGetActiveOutputEndpointId();
        if (!AudioOutputRecoveryPolicy.ShouldRecoverAfterDefaultDeviceChanged(
                _settings.OutputDeviceName, activeEndpointId, changedDefaultEndpointId))
        {
            Logger.Info("Смена default WASAPI endpoint уже отражена активным устройством; recovery не требуется.");
            return;
        }

        const string reason = "Windows изменил системное устройство вывода";
        Logger.Info($"{reason}; выполняется controlled recovery WASAPI.");
        RecoverOutputDeviceAfterFailure(new InvalidOperationException(reason), _isPlaying, expectedDeviceEvent: true);
    }

    // Устройство могло исчезнуть при Play/Pause, прислать PlaybackStopped с ошибкой или сообщить через Core Audio endpoint event:
    // один controlled retry через Windows audio mapper лучше повторных ошибок (после отключения USB/Bluetooth это уже новое устройство).
    private void RecoverOutputDeviceAfterFailure(Exception error, bool resumePlayback, bool expectedDeviceEvent = false)
    {
        if (expectedDeviceEvent)
            Logger.Warn($"Восстановление WASAPI после события endpoint: {error.Message}");
        else
            Logger.Error("Ошибка устройства вывода; выполняется восстановление через системное устройство", error);
        if (_isExiting) return;

        DateTime now = DateTime.UtcNow;
        OutputRecoveryReason reason = expectedDeviceEvent
            ? OutputRecoveryReason.EndpointUnavailable
            : OutputRecoveryReason.PlaybackFailure;
        var request = new OutputRecoveryRequest(
            reason, error.Message, resumePlayback, expectedDeviceEvent);
        OutputRecoveryExecutionResult execution = _audioOutputRecoveryService.Execute(
            request,
            now,
            CapturePlaybackRecoverySnapshot,
            ExecutePlaybackRecovery,
            out OutputRecoveryDecision decision);

        if (!execution.Started)
            return;

        SetTrackUserState(TrackUserState.Loading);
        _outputDiagnostics.RecoveryCount = execution.RecoveryCount;
        _outputDiagnostics.LastRecoveryReason = expectedDeviceEvent
            ? error.Message
            : "Ошибка WASAPI при инициализации или воспроизведении";
        if (!execution.Completed)
        {
            StopPlayback();
            DisposeOutputDeviceSafely();
            SetTrackUserState(TrackUserState.Error);
        }
    }

    private PlaybackRecoverySnapshot? CapturePlaybackRecoverySnapshot()
    {
        string? currentPath = _currentTrackPath;
        if (string.IsNullOrWhiteSpace(currentPath) || !File.Exists(currentPath))
            return null;

        return new PlaybackRecoverySnapshot(
            currentPath,
            _audioFile?.CurrentTime ?? TimeSpan.Zero,
            _isPlaying,
            _settings.OutputDeviceName,
            TryGetActiveOutputEndpointId());
    }

    private void ExecutePlaybackRecovery(PlaybackRecoverySnapshot snapshot)
    {
        StopPlayback(disposeOnly: true);
        DisposeOutputDeviceSafely();
        _forceSystemOutputOnce = true;
        LoadAndPlay(snapshot.TrackPath, autoPlay: snapshot.WasPlaying,
            startPosition: snapshot.Position, changeOrigin: TrackChangeOrigin.Automatic);
    }

    private void DisposeOutputDeviceSafely()
    {
        try
        {
            _audioOutputSession.Release();
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось освободить WASAPI-устройство вывода", ex);
        }
        finally
        {
            _outputDevice = null;
            _outputEndpoint = null;
        }
    }

    // Координация с внешней записью в файл (теги/обложка, см. TrackTagsWindow.SaveButton_Click), пока файл открыт NAudio-потоком:
    // null, если играет другой трек; иначе освобождает хендл и возвращает точку для ResumeAfterExternalWrite после записи.
    public (TimeSpan Position, bool WasPlaying)? ReleaseFileForExternalWrite(string filePath)
    {
        if (!PathEquals(filePath, _currentTrackPath) || _audioFile == null) return null;

        var snapshot = (_audioFile.CurrentTime, _isPlaying);
        StopPlayback(disposeOnly: true);
        return snapshot;
    }

    public void ResumeAfterExternalWrite(string filePath, (TimeSpan Position, bool WasPlaying) snapshot)
    {
        LoadAndPlay(filePath, autoPlay: snapshot.WasPlaying, startPosition: snapshot.Position,
            changeOrigin: TrackChangeOrigin.ExternalEdit);
    }
}
