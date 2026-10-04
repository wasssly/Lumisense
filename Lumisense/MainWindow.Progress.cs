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

// Перемотка и прогресс воспроизведения: seek, перетаскивание по полосе, таймер прогресса. Вынесено из
// MainWindow.xaml.cs только ради навигации, логика не менялась.
public partial class MainWindow
{
    private void SeekCurrentAudioFile(TimeSpan position)
    {
        if (_audioFile is null)
            return;

        _audioFile.CurrentTime = position;
        _tempoProvider?.Clear();
    }

    public void ExternalSeekRatio(double ratio)
    {
        if (_audioFile == null) return;

        var newTime = TimeSpan.FromSeconds(_audioFile.TotalTime.TotalSeconds * Math.Clamp(ratio, 0.0, 1.0));
        SeekCurrentAudioFile(newTime);
        ProgressSlider.Value = newTime.TotalSeconds;
        CurrentTimeText.Text = newTime.ToString(@"mm\:ss");
    }

    private void ProgressOverlay_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var overlay = (FrameworkElement)sender;
        overlay.CaptureMouse();
        _isDraggingProgressOverlay = true;
        _isUserInteractingWithProgress = true;
        ProgressSlider.Focus();
        UpdateSliderValueFromMouse(ProgressSlider, e.GetPosition(overlay).X, overlay.ActualWidth);
    }

    private void ProgressOverlay_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDraggingProgressOverlay) return;
        var overlay = (FrameworkElement)sender;
        UpdateSliderValueFromMouse(ProgressSlider, e.GetPosition(overlay).X, overlay.ActualWidth);
    }

    private void ProgressOverlay_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var overlay = (FrameworkElement)sender;
        overlay.ReleaseMouseCapture();
        _isDraggingProgressOverlay = false;
        _isUserInteractingWithProgress = false;
    }

    // Общий шаг 5 секунд с клампингом по границам трека и обновлением UI для колеса над прогресс-баром и хоткеев
    // (_integrations.HotKeys.SeekForwardPressed/SeekBackwardPressed).
    void IIntegrationHost.SeekBy(double seconds) => SeekBy(seconds);

    private void SeekBy(double seconds)
    {
        if (_audioFile == null) return;

        var newTime = _audioFile.CurrentTime + TimeSpan.FromSeconds(seconds);
        if (newTime < TimeSpan.Zero) newTime = TimeSpan.Zero;
        if (newTime > _audioFile.TotalTime) newTime = _audioFile.TotalTime;

        SeekCurrentAudioFile(newTime);
        ProgressSlider.Value = newTime.TotalSeconds;
        CurrentTimeText.Text = newTime.ToString(@"mm\:ss");
        RaiseProgressChanged(newTime.TotalSeconds, _audioFile.TotalTime.TotalSeconds);
    }

    // Колесо над прогресс-баром перематывает с тем же шагом, что хоткеи; e.Delta > 0 (вверх) — вперёд, как в VolumeRow_MouseWheel.
    private void ProgressOverlay_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (_audioFile == null) return;

        SeekBy(Math.Sign(e.Delta) * 5);
        e.Handled = true;
    }

    // Флагом _isSyncingProgressFromPlayback метод управляет сам: иначе ProgressSlider_ValueChanged принял бы присваивание
    // за перемотку пользователем и дёргал бы _audioFile.CurrentTime.
    private void SetProgressSliderValue(double seconds)
    {
        _isSyncingProgressFromPlayback = true;
        ProgressSlider.Value = seconds;
        _isSyncingProgressFromPlayback = false;
    }

    private void StopProgressTimerAndAnimation()
    {
        _progressTimer.Stop();
        FlushPlaybackClock();
        _isSyncingProgressFromPlayback = false;
    }

    private void FlushPlaybackClock()
    {
        if (!_playbackClock.IsRunning) return;
        _playbackClock.Stop();
        _settings.TotalListenSeconds += _playbackClock.Elapsed.TotalSeconds;
        _playbackClock.Reset();
    }

    private void ProgressTimer_Tick(object? sender, EventArgs e)
    {
        // Пока ползунок зажат — значение не трогаем: неточность перемотки в mp3/aac иначе "дёргала" бы ползунок.
        if (_audioFile == null || _isUserInteractingWithProgress) return;

        // SetProgressSliderValue вызывает ProgressSlider_ValueChanged, который уже обновляет
        // синхронный текст для этой позиции — повторный вызов здесь дублировал эту работу.
        SetProgressSliderValue(_audioFile.CurrentTime.TotalSeconds);

        RaiseProgressChanged(_audioFile.CurrentTime.TotalSeconds, _audioFile.TotalTime.TotalSeconds);

        // UI-таймер продолжает синхронизировать позицию и в паузе, но статистика и отметка
        // прослушивания относятся только к фактическому воспроизведению.
        if (!_isPlaying)
            return;

        _settings.StatsStartedAt ??= DateTime.Now.ToString("O");

        // Накапливаем только естественный прирост позиции между тиками. Скачок назад или
        // вперёд больше одного интервала — это перемотка, а не прозвучавший звук.
        double position = _audioFile.CurrentTime.TotalSeconds;
        if (_lastTickPositionSeconds >= 0)
        {
            double delta = position - _lastTickPositionSeconds;
            if (delta > 0 && delta <= MaxNaturalTickAdvanceSeconds)
                _actuallyPlayedSeconds += delta;
        }
        _lastTickPositionSeconds = position;

        // Засчитывается, только когда реально прозвучала половина трека. Раньше проверялась
        // сама позиция, поэтому перетаскивание ползунка в конец сразу давало +1 к статистике.
        if (!_halfPlayCounted && _audioFile.TotalTime.TotalSeconds > 0
            && _actuallyPlayedSeconds >= _audioFile.TotalTime.TotalSeconds / 2.0)
        {
            _halfPlayCounted = true;
            if (_currentTrackPath != null)
                PlayCountManager.Increment(_currentTrackPath);
        }

        if (++_ticksSinceLastAutoSave >= AutoSaveEveryNTicks)
        {
            _ticksSinceLastAutoSave = 0;
            PersistPlaybackAndPlaylistState(asyncSave: true);
        }
    }

    private void ProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        CurrentTimeText.Text = TimeSpan.FromSeconds(e.NewValue).ToString(@"mm\:ss");

        // Общая точка любого изменения позиции (перемотка, таймер, SeekBy): проще синхронизировать waveform здесь, чем дублировать.
        double progressRatio = ProgressSlider.Maximum > 0 ? e.NewValue / ProgressSlider.Maximum : 0;
        ProgressWaveform.Progress = progressRatio;
        ProgressMaterial.Progress = progressRatio;
        UpdateMainWindowSyncedLyrics(TimeSpan.FromSeconds(e.NewValue));

        // Пропускаем seek, если это сам таймер обновил слайдер под текущую позицию воспроизведения —
        // иначе будет лишняя перемотка 4 раза в секунду даже когда никто не трогает ползунок
        if (_isSyncingProgressFromPlayback) return;

        // Во всех остальных случаях — клик в любую точку трека, перетаскивание ползунка
        // или стрелки клавиатуры — сразу перематываем воспроизведение, точно как громкость
        if (_audioFile != null)
            SeekCurrentAudioFile(TimeSpan.FromSeconds(e.NewValue));
    }
}
