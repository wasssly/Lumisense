using System.Windows;
using System.Windows.Media;

namespace Lumisense;

// Общая анимация «бегущей волны» для Material-индикаторов: амплитуда плавно растёт при воспроизведении и спадает на паузе,
// фаза бежит с частотой WaveFrequency. Кадры подписываются только пока элемент виден и волна движется или выпрямляется.
public abstract class MaterialWaveElement : FrameworkElement
{
    // Скорость бега волны задаётся частотой (периодов в секунду), а не px/с: у полосы и кольца вокруг обложки разная длина волны,
    // и при одной скорости в px/с кольцо казалось бы быстрее. 2 периода/с — темп кольца мини-плеера.
    protected const double WaveFrequency = 2.0;
    private const double AmplitudeEaseSeconds = 0.4;

    // Длина волны задаётся потомком (для кольца подгоняется под периметр, чтобы шов замыкался).
    protected abstract double Wavelength { get; }

    public static readonly DependencyProperty IsWavingProperty = DependencyProperty.Register(
        nameof(IsWaving), typeof(bool), typeof(MaterialWaveElement),
        new FrameworkPropertyMetadata(false, OnAnimationInputChanged));

    // true — трек играет: волна есть и бежит; false — пауза или остановка: волна плавно выпрямляется.
    public bool IsWaving
    {
        get => (bool)GetValue(IsWavingProperty);
        set => SetValue(IsWavingProperty, value);
    }

    public static readonly DependencyProperty IsAnimationEnabledProperty = DependencyProperty.Register(
        nameof(IsAnimationEnabled), typeof(bool), typeof(MaterialWaveElement),
        new FrameworkPropertyMetadata(true, OnAnimationInputChanged));

    // false при «Меньше анимации»: волна не движется, форма меняется сразу.
    public bool IsAnimationEnabled
    {
        get => (bool)GetValue(IsAnimationEnabledProperty);
        set => SetValue(IsAnimationEnabledProperty, value);
    }

    protected double AmplitudeFactor;
    protected double Phase;
    private TimeSpan _lastFrame;
    private bool _isRendering;

    protected MaterialWaveElement()
    {
        Loaded += (_, _) => UpdateAnimationState();
        Unloaded += (_, _) => StopRendering();
        IsVisibleChanged += (_, _) => UpdateAnimationState();
    }

    private static void OnAnimationInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MaterialWaveElement)d).UpdateAnimationState();

    // Кадры нужны только пока окно видно и волна бежит или выпрямляется: на паузе элемент ничего не тратит.
    private void UpdateAnimationState()
    {
        double target = IsWaving ? 1.0 : 0.0;

        if (IsAnimationEnabled && IsLoaded && IsVisible && (IsWaving || AmplitudeFactor > 0.001))
        {
            StartRendering();
            return;
        }

        AmplitudeFactor = target;
        StopRendering();
        InvalidateVisual();
    }

    private void StartRendering()
    {
        if (_isRendering) return;
        _isRendering = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopRendering()
    {
        if (!_isRendering) return;
        CompositionTarget.Rendering -= OnRendering;
        _isRendering = false;
        _lastFrame = TimeSpan.Zero;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        TimeSpan now = ((RenderingEventArgs)e).RenderingTime;

        // WPF может вызвать событие несколько раз за один кадр.
        if (now == _lastFrame) return;

        double dt = _lastFrame == TimeSpan.Zero ? 0.0 : Math.Min((now - _lastFrame).TotalSeconds, 0.1);
        _lastFrame = now;

        double target = IsWaving ? 1.0 : 0.0;
        double step = dt / AmplitudeEaseSeconds;
        AmplitudeFactor = AmplitudeFactor < target
            ? Math.Min(target, AmplitudeFactor + step)
            : Math.Max(target, AmplitudeFactor - step);

        Phase = (Phase + Wavelength * WaveFrequency * dt) % Wavelength;
        InvalidateVisual();

        if (!IsWaving && AmplitudeFactor <= 0.001)
            UpdateAnimationState();
    }
}
