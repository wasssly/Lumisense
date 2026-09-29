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

namespace Lumisense;

// Звук: эквалайзер и пресеты, скорость/питч, громкость (кривая, ReplayGain, mute) и попап управления воспроизведением.
// Вынесено из MainWindow.xaml.cs только ради навигации, логика не менялась.
public partial class MainWindow
{
    // Эквалайзер: настройки читаются/пишутся здесь, а не в SettingsWindow — EqualizerSampleProvider живёт только пока что-то
    // играет (пересоздаётся в LoadAndPlay), а EqualizerEnabled/EqualizerBandGainsDb должны сохраняться и без воспроизведения.

    // Заполняет только что созданный _equalizer сохранёнными настройками — вызывается из
    // LoadAndPlay при каждой смене трека, потому что сам _equalizer живёт не дольше трека.
    private void ApplyEqualizerGainsFromSettings()
    {
        if (_equalizer == null) return;

        var saved = _settings.EqualizerBandGainsDb;
        for (int band = 0; band < EqualizerSampleProvider.BandFrequencies.Length; band++)
            _equalizer.SetBandGain(band, band < saved.Length ? saved[band] : 0);

        _equalizer.Enabled = _settings.EqualizerEnabled && !_settings.EqualizerBypass;
    }

    public bool IsEqualizerEnabled => _settings.EqualizerEnabled;

    private void ApplyPlaybackRateToCurrentStream()
    {
        if (_tempoProvider != null)
            _tempoProvider.Tempo = _runtimePlaybackRate;
    }

    private void ReapplySavedPlaybackRateAfterTrackReady(int generation)
    {
        // SoundTouch создаётся в фоне, а WaveOut инициализируется на UI-потоке: повторяем установку после Init через Dispatcher,
        // чтобы поздний сброс Tempo при восстановлении последнего трека на старте её не затёр.
        Dispatcher.BeginInvoke(() =>
        {
            if (_isExiting || generation != _audioPlaybackCoordinator.CurrentGeneration || _tempoProvider == null)
                return;

            ApplyPlaybackRateToCurrentStream();
        }, DispatcherPriority.ContextIdle);
    }

    private static double NormalizePlaybackRate(double speed) =>
        Math.Round(Math.Clamp(speed, 0.5, 2.0), 2);

    private void SetPlaybackRate(double speed, bool persist)
    {
        double clamped = NormalizePlaybackRate(speed);
        _runtimePlaybackRate = clamped;
        _settings.PlaybackSpeed = clamped;

        ApplyPlaybackRateToCurrentStream();

        if (PlaybackRateValueText != null)
            PlaybackRateValueText.Text = FormatPlaybackRate(clamped);

        if (PlaybackRateSlider != null && Math.Abs(PlaybackRateSlider.Value - clamped) > 0.0001)
        {
            _isUpdatingPlaybackRateControl = true;
            try { PlaybackRateSlider.Value = clamped; }
            finally { _isUpdatingPlaybackRateControl = false; }
        }

        if (persist && !_isApplyingStartupSettings && !_isExiting)
            FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    public void ApplyPlaybackRateLive(double speed) => SetPlaybackRate(speed, persist: false);

    // Мини-плеер использует тот же путь, что и главный ползунок: темп применяется к текущему
    // SoundTouch-потоку, сохраняется и синхронизирует основной контрол без отдельной логики.
    public void SetPlaybackRateFromMiniPlayer(double speed) => SetPlaybackRate(speed, persist: true);

    // Аналогичная точка входа для тона. Если главный Slider существует, его ValueChanged уже
    // обновит SoundTouch, текст и сохранение; до его создания применяем всё напрямую.
    public void SetPlaybackPitchFromMiniPlayer(double semitones)
    {
        double clamped = Math.Clamp(semitones, -12.0, 12.0);
        if (PlaybackPitchSlider != null && Math.Abs(PlaybackPitchSlider.Value - clamped) > 0.0001)
        {
            PlaybackPitchSlider.Value = clamped;
            return;
        }

        ApplyPlaybackPitchLive(clamped);
        if (PlaybackPitchValueText != null)
            PlaybackPitchValueText.Text = FormatPlaybackPitch(clamped);
        PersistPlaybackSettingsAfterUserChange();
    }

    public void ApplyPlaybackPitchLive(double semitones)
    {
        double clamped = Math.Clamp(semitones, -12.0, 12.0);
        _settings.PlaybackPitchSemitones = clamped;
        if (_tempoProvider != null)
            _tempoProvider.PitchSemiTones = clamped;
    }

    public bool IsEqualizerBypass => _settings.EqualizerBypass;

    public void SetEqualizerEnabled(bool enabled)
    {
        _settings.EqualizerEnabled = enabled;
        if (_equalizer != null)
            _equalizer.Enabled = enabled && !_settings.EqualizerBypass;
    }

    // Bypass не сбрасывает ни флаг включения EQ, ни полосы, ни пресеты. Благодаря этому
    // пользователь может сравнить звук «с EQ / без EQ» и вернуть обработку одним кликом.
    public void SetEqualizerBypass(bool bypass)
    {
        _settings.EqualizerBypass = bypass;
        if (_equalizer != null)
            _equalizer.Enabled = _settings.EqualizerEnabled && !bypass;
    }

    public double GetEqualizerBandGain(int band) =>
        band >= 0 && band < _settings.EqualizerBandGainsDb.Length ? _settings.EqualizerBandGainsDb[band] : 0;

    // Вызывается при каждом движении слайдера полосы: сразу сохраняет значение и, если что-то играет, применяет его к фильтру,
    // чтобы звук менялся вживую.
    public void SetEqualizerBandGain(int band, double gainDb)
    {
        if (band < 0 || band >= EqualizerSampleProvider.BandFrequencies.Length) return;

        // EqualizerBandGainsDb из settings.json другой версии мог иметь иное число полос — расширяем массив, а не падаем
        // с IndexOutOfRange.
        if (_settings.EqualizerBandGainsDb.Length <= band)
        {
            var resized = new double[EqualizerSampleProvider.BandFrequencies.Length];
            Array.Copy(_settings.EqualizerBandGainsDb, resized, _settings.EqualizerBandGainsDb.Length);
            _settings.EqualizerBandGainsDb = resized;
        }

        _settings.EqualizerBandGainsDb[band] = gainDb;
        _equalizer?.SetBandGain(band, gainDb);
    }

    // Кнопка "Сбросить" в настройках — обнуляет все полосы разом.
    public void ResetEqualizer()
    {
        _settings.EqualizerBandGainsDb = new double[EqualizerSampleProvider.BandFrequencies.Length];
        ApplyEqualizerGainsFromSettings();
    }

    public IReadOnlyList<EqualizerPreset> EqualizerPresets => _settings.EqualizerPresets;

    // Сохраняет ТЕКУЩИЕ значения полос как пресет; занятое имя тихо перезаписывается — чаще всего пользователь
    // пересохраняет под тем же названием после донастройки, а не хочет дубликатов.
    public void SaveEqualizerPreset(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return;

        var gains = (double[])_settings.EqualizerBandGainsDb.Clone();
        var existing = _settings.EqualizerPresets.FirstOrDefault(p => p.Name == name);
        if (existing != null)
            existing.GainsDb = gains;
        else
            _settings.EqualizerPresets.Add(new EqualizerPreset { Name = name, GainsDb = gains });

        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    // Применяет пресет к текущим настройкам эквалайзера — через SetEqualizerBandGain
    // по каждой полосе, чтобы (если сейчас что-то играет) звук изменился сразу же, живьём.
    public void ApplyEqualizerPreset(EqualizerPreset preset)
    {
        for (int band = 0; band < EqualizerSampleProvider.BandFrequencies.Length; band++)
            SetEqualizerBandGain(band, band < preset.GainsDb.Length ? preset.GainsDb[band] : 0);

        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    public void DeleteEqualizerPreset(EqualizerPreset preset)
    {
        _settings.EqualizerPresets.Remove(preset);
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    // Экспорт пресета в .json — тот же формат, что в settings.json (EqualizerPreset), поэтому файл можно переслать и импортировать
    // обратно без специального протокола.
    public void ExportEqualizerPreset(EqualizerPreset preset, string filePath)
    {
        string json = JsonSerializer.Serialize(preset, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);
    }

    // Импортирует пресет из файла ExportEqualizerPreset (в том числе чужого); занятое имя получает суффикс " (2)", " (3)",
    // а не перезаписывает чужую настройку; null, если файл повреждён или не похож на пресет.
    public EqualizerPreset? ImportEqualizerPresetFromFile(string filePath)
    {
        const long maxPresetBytes = 512 * 1024;
        EqualizerPreset? preset;
        try
        {
            if (!File.Exists(filePath) || new FileInfo(filePath).Length > maxPresetBytes)
                return null;

            string json = File.ReadAllText(filePath);
            preset = JsonSerializer.Deserialize<EqualizerPreset>(json, new JsonSerializerOptions { MaxDepth = 8 });
        }
        catch
        {
            return null;
        }

        if (preset == null || string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > 200 ||
            preset.GainsDb is null || preset.GainsDb.Length == 0 || preset.GainsDb.Length > 32 ||
            preset.GainsDb.Any(g => !double.IsFinite(g) || g < -100 || g > 100))
            return null;

        string baseName = preset.Name.Trim();
        string name = baseName;
        int suffix = 2;
        while (_settings.EqualizerPresets.Any(p => p.Name == name))
            name = $"{baseName} ({suffix++})";
        preset.Name = name;

        _settings.EqualizerPresets.Add(preset);
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
        return preset;
    }

    private void VolumeOverlay_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var overlay = (FrameworkElement)sender;
        overlay.CaptureMouse();
        _isDraggingVolumeOverlay = true;
        VolumeSlider.Focus();
        UpdateSliderValueFromMouse(VolumeSlider, e.GetPosition(overlay).X, overlay.ActualWidth);
    }

    private void VolumeOverlay_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDraggingVolumeOverlay) return;
        var overlay = (FrameworkElement)sender;
        UpdateSliderValueFromMouse(VolumeSlider, e.GetPosition(overlay).X, overlay.ActualWidth);
    }

    private void VolumeOverlay_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var overlay = (FrameworkElement)sender;
        overlay.ReleaseMouseCapture();
        _isDraggingVolumeOverlay = false;
    }

    private static void UpdateSliderValueFromMouse(System.Windows.Controls.Slider slider, double positionX, double width)
    {
        if (width <= 0) return;

        double ratio = Math.Clamp(positionX / width, 0.0, 1.0);
        slider.Value = slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
    }

    // Двигает тот же VolumeSlider (хоткеи громкости), поэтому сохранение и подпись процентов работают как обычно.
    void IIntegrationHost.ChangeVolumeBy(double delta) => ChangeVolumeBy(delta);

    private void ChangeVolumeBy(double delta)
    {
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, VolumeSlider.Minimum, VolumeSlider.Maximum);
    }

    // Положение ползунка (0..1) → множитель амплитуды: обычно мягкая audio-taper кривая, в логарифмическом режиме — через
    // децибелы [MinDb, 0] и 10^(dB/20), чтобы ход воспринимался равномерно, а не сжатым в нижние 10-20%.
    private const double MinVolumeDb = -40.0; // тише практически не слышно — дальше просто тишина
    private const double LinearVolumeExponent = 2.0;

    private float ToOutputVolume(double sliderValue)
    {
        sliderValue = Math.Clamp(sliderValue, 0.0, 1.0);

        if (!_settings.UseLogarithmicVolume)
            return (float)Math.Pow(sliderValue, LinearVolumeExponent);

        if (sliderValue <= 0.0) return 0f;

        double db = MinVolumeDb * (1.0 - sliderValue);
        double raw = Math.Pow(10.0, db / 20.0);

        // 10^(dB/20) при sliderValue → 0 стремится к "полу", а не к 0: без перенормировки последний отрезок хода давал
        // резкий скачок к тишине вместо плавного затухания.
        double floor = Math.Pow(10.0, MinVolumeDb / 20.0);
        return (float)((raw - floor) / (1.0 - floor));
    }

    public void RefreshVolumeCurve()
    {
        if (_audioFile != null)
            _audioFile.Volume = ComputeAudioFileVolume(VolumeSlider.Value);
    }

    // Домножает обычную громкость (см. ToOutputVolume) на _replayGainFactor — то же место
    // конвейера (AudioFileReader.Volume, до эквалайзера).
    private float ComputeAudioFileVolume(double sliderValue) => ComputeAudioFileVolume(sliderValue, _replayGainFactor);

    private float ComputeAudioFileVolume(double sliderValue, double replayGainFactor) =>
        (float)(ToOutputVolume(sliderValue) * replayGainFactor);

    public void RefreshReplayGain()
    {
        var previous = Interlocked.Exchange(ref _replayGainCts, null);
        previous?.Cancel();

        string? path = _currentTrackPath;
        if (!_settings.ReplayGainEnabled || path == null)
        {
            _replayGainFactor = 1.0;
            if (_audioFile != null)
                _audioFile.Volume = ComputeAudioFileVolume(VolumeSlider.Value);
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _replayGainCts = cts;
        int generation = _audioPlaybackCoordinator.CurrentGeneration;
        FireAndForget(RefreshReplayGainAsync(path, generation, cts), "RefreshReplayGainAsync");
    }

    private async Task RefreshReplayGainAsync(string path, int generation, CancellationTokenSource cts)
    {
        try
        {
            double gain = await Task.Run(() => ReplayGainReader.GetTrackGainLinear(path), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (_isExiting || generation != _audioPlaybackCoordinator.CurrentGeneration ||
                !string.Equals(path, _currentTrackPath, StringComparison.Ordinal)) return;

            _replayGainFactor = gain;
            if (_audioFile != null)
                _audioFile.Volume = ComputeAudioFileVolume(VolumeSlider.Value);
        }
        catch (OperationCanceledException)
        {
            // Новая загрузка, настройка или shutdown отменили устаревший расчёт.
        }
        catch (Exception ex)
        {
            Logger.Error($"Не удалось обновить ReplayGain для файла: {path}", ex);
        }
        finally
        {
            if (ReferenceEquals(_replayGainCts, cts)) _replayGainCts = null;
            cts.Dispose();
        }
    }


    // Одно деление колеса = 5%, как и хоткеи громкости. e.Delta положителен при прокрутке "от себя".
    private void VolumeRow_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        ChangeVolumeBy(Math.Sign(e.Delta) * 0.02);
        e.Handled = true;
    }

    void IIntegrationHost.ToggleMute() => ToggleMute();

    private void ToggleMute()
    {
        if (VolumeSlider.Value > 0)
        {
            _lastNonZeroVolume = VolumeSlider.Value;
            VolumeSlider.Value = 0;
        }
        else
        {
            VolumeSlider.Value = _lastNonZeroVolume > 0 ? _lastNonZeroVolume : 0.3;
        }
    }

    private static string FormatPlaybackRate(double value) => $"{value:0.00}×";

    private void PlaybackRateSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // ValueChanged вызывается во время InitializeComponent для XAML Value=1.0.
        // Пока runtime-state не загружен из settings.json, это событие игнорируется.
        if (!_playbackRateIsReady || _isUpdatingPlaybackRateControl) return;
        SetPlaybackRate(e.NewValue, persist: true);
        ApplyPlaybackRateToCurrentStream();
    }

    private static string FormatPlaybackPitch(double semitones) =>
        $"{semitones:+0;-0;0} st";

    private void PlaybackRateSlider_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        SetPlaybackRate(1.0, persist: true);
        e.Handled = true;
    }

    private void PlaybackPitchSlider_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        PlaybackPitchSlider.Value = 0.0;
        e.Handled = true;
    }

    private void PlaybackPitchSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PlaybackPitchValueText != null)
            PlaybackPitchValueText.Text = FormatPlaybackPitch(e.NewValue);
        ApplyPlaybackPitchLive(e.NewValue);
        PersistPlaybackSettingsAfterUserChange();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_audioFile != null)
            _audioFile.Volume = ComputeAudioFileVolume(e.NewValue);

        if (VolumeValueText != null)
            VolumeValueText.Text = $"{(int)Math.Round(e.NewValue * 100)}%";

        if (e.NewValue > 0)
            _lastNonZeroVolume = e.NewValue;

        if (SpeakerIcon != null)
        {
            SpeakerIcon.Icon = e.NewValue <= 0.0 ? "IconSpeakerMute" : "IconSpeaker";
        }

        VolumeChanged?.Invoke(e.NewValue);
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e) => ToggleMute();

    private void PlaybackRatePersistenceTimer_Tick(object? sender, EventArgs e)
    {
        if (_isApplyingStartupSettings || _isExiting || PlaybackRateSlider == null) return;

        double sliderValue = Math.Round(Math.Clamp(PlaybackRateSlider.Value, 0.5, 2.0), 2);
        if (Math.Abs(sliderValue - _runtimePlaybackRate) <= 0.0001) return;

        // Watcher страхует случай, когда визуальный Slider изменился, но ValueChanged не
        // дошёл до setter из-за особенностей Popup/мыши. Источником состояния остаётся setter.
        SetPlaybackRate(sliderValue, persist: false);
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void PersistPlaybackSettingsAfterUserChange()
    {
        if (_isApplyingStartupSettings || _isOpeningPlaybackControlPopup || _isExiting) return;
        _settings.PlaybackSpeed = _runtimePlaybackRate;
        FireAndForget(SettingsManager.SaveAsync(_settings), "SavePlaybackSettingsAsync");
    }

    private const double PlaybackRateWheelStep = 0.05;
    private const double PlaybackPitchWheelStep = 1.0;

    private void ChangePlaybackRateBy(double delta)
    {
        SetPlaybackRate(_runtimePlaybackRate + delta, persist: true);
    }

    private void OpenPlaybackControlPopup()
    {
        if (PlaybackControlPopup.IsOpen) return;

        _isOpeningPlaybackControlPopup = true;
        PlaybackControlPopup.IsOpen = true;
    }

    private void PlaybackControlPopup_Closed(object? sender, EventArgs e)
    {
        if (_isExiting || _isApplyingStartupSettings) return;
        SetPlaybackRate(PlaybackRateSlider.Value, persist: true);
        _settings.PlaybackPitchSemitones = Math.Clamp(PlaybackPitchSlider.Value, -12.0, 12.0);
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void PlaybackControlPopup_Opened(object? sender, EventArgs e)
    {
        try
        {
            SetPlaybackRate(_runtimePlaybackRate, persist: false);
            PlaybackPitchSlider.Value = Math.Clamp(_settings.PlaybackPitchSemitones, -12.0, 12.0);
            PlaybackPitchValueText.Text = FormatPlaybackPitch(PlaybackPitchSlider.Value);
            ApplyPlaybackPitchLive(PlaybackPitchSlider.Value);
        }
        finally
        {
            _isOpeningPlaybackControlPopup = false;
            PlaybackRateSlider.Focus();
        }
    }

    private void PlaybackControlButton_Click(object sender, RoutedEventArgs e)
    {
        if (PlaybackControlPopup.IsOpen)
            PlaybackControlPopup.IsOpen = false;
        else
            OpenPlaybackControlPopup();
        e.Handled = true;
    }

    private void PlaybackControlButton_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        OpenPlaybackControlPopup();
        ChangePlaybackRateBy(Math.Sign(e.Delta) * PlaybackRateWheelStep);
        e.Handled = true;
    }

    private void PlaybackRateSlider_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        ChangePlaybackRateBy(Math.Sign(e.Delta) * PlaybackRateWheelStep);
        e.Handled = true;
    }

    private void PlaybackPitchSlider_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        PlaybackPitchSlider.Value = Math.Clamp(
            PlaybackPitchSlider.Value + Math.Sign(e.Delta) * PlaybackPitchWheelStep,
            PlaybackPitchSlider.Minimum,
            PlaybackPitchSlider.Maximum);
        e.Handled = true;
    }
}
