using System.Windows;
using System.Windows.Media;

namespace Lumisense;

// Общая анимация «бегущей волны» для Material-индикаторов: амплитуда плавно растёт при воспроизведении и спадает на паузе,
// фаза бежит со скоростью WaveSpeed. Кадры подписываются только пока элемент виден и волна движется или выпрямляется.
public abstract class MaterialWaveElement : FrameworkElement
{
    // Время выхода амплитуды на максимум или в ноль.
    private const double AmplitudeEaseSeconds = 0.4;

    // Скорость бега волны (px/с) задаёт потомок: у полосы и кольца вокруг обложки она своя.
    protected abstract double WaveSpeed { get; }

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

        Phase = (Phase + WaveSpeed * dt) % Wavelength;
        InvalidateVisual();

        if (!IsWaving && AmplitudeFactor <= 0.001)
            UpdateAnimationState();
    }
}
