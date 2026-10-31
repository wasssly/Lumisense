using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media.Animation;

namespace Lumisense;

/// <summary>Плавно ведёт бегунок Slider к новому значению, когда оно меняется скачком (клик по дорожке, клавиши, значение из кода).
/// Само Slider.Value меняется сразу, поэтому обработчики настроек получают итоговое значение без промежуточных; анимируется
/// только показываемое положение. При перетаскивании бегунка движение короткое, чтобы он не отставал от курсора, а при скачке
/// (клик по дорожке, клавиши, значение из кода) длинное и с плавным торможением.</summary>
public static class SliderSmoothing
{
    private static readonly Duration JumpDuration = new(TimeSpan.FromMilliseconds(380));
    private static readonly Duration DragDuration = new(TimeSpan.FromMilliseconds(130));

    /// <summary>Общий выключатель, который задаёт приложение по настройке раздела «Слайдеры в настройках».</summary>
    public static bool IsAnimationEnabled { get; set; } = true;

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(SliderSmoothing),
            new PropertyMetadata(false, OnIsEnabledChanged));

    // NaN — анимации нет, и показывается настоящее Slider.Value.
    private static readonly DependencyProperty DisplayValueProperty =
        DependencyProperty.RegisterAttached("DisplayValue", typeof(double), typeof(SliderSmoothing),
            new PropertyMetadata(double.NaN, OnDisplayValueChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    /// <summary>Значение, по которому нужно рисовать бегунок: во время анимации промежуточное, иначе Slider.Value.</summary>
    public static double ShownValue(Slider slider)
    {
        double shown = (double)slider.GetValue(DisplayValueProperty);
        return double.IsNaN(shown) ? slider.Value : shown;
    }

    private static void OnIsEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not Slider slider) return;

        slider.ValueChanged -= Slider_ValueChanged;
        if ((bool)e.NewValue) slider.ValueChanged += Slider_ValueChanged;
    }

    private static void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var slider = (Slider)sender;
        double display = (double)slider.GetValue(DisplayValueProperty);
        double from = double.IsNaN(display) ? e.OldValue : display;

        bool animate = IsAnimationEnabled && slider.IsLoaded && slider.IsVisible && Math.Abs(e.NewValue - from) > 1e-9;
        if (!animate)
        {
            slider.BeginAnimation(DisplayValueProperty, null);
            if (FindPart<Track>(slider, "PART_Track") is { } plain
                && !BindingOperations.IsDataBound(plain, Track.ValueProperty))
            {
                plain.Value = e.NewValue;
            }
            return;
        }

        // Track следит за Slider.Value привязкой и уже получил новое значение. Привязку снимаем и ведём Track сами, иначе
        // он не покажет промежуточные положения; до первого кадра анимации ставим прежнее положение, чтобы не мигнуть концом.
        // При перетаскивании Track всегда держим на настоящем значении: Thumb считает сдвиг мыши от своего положения, и если
        // он отстанет от курсора, Slider будет добавлять отставание к значению на каждом движении (бегунок дёргается).
        bool dragging = IsThumbDragging(slider);
        SetTrackValue(slider, dragging ? e.NewValue : from);
        var animation = new DoubleAnimation(from, e.NewValue, dragging ? DragDuration : JumpDuration)
        {
            EasingFunction = dragging
                ? new CubicEase { EasingMode = EasingMode.EaseOut }
                : new QuinticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        slider.BeginAnimation(DisplayValueProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    // Template.FindName бросает InvalidOperationException, пока шаблон слайдера не применён, а значения в окнах настроек
    // выставляются ещё в конструкторе. Исключение там оставляло окно настроек пустым, поэтому частей до загрузки просто нет.
    private static T? FindPart<T>(Slider slider, string name) where T : class
    {
        if (!slider.IsLoaded || slider.Template is null) return null;

        try
        {
            return slider.Template.FindName(name, slider) as T;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsThumbDragging(Slider slider) =>
        FindPart<Track>(slider, "PART_Track") is { Thumb.IsDragging: true };

    private static void OnDisplayValueChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not Slider slider) return;

        // Во время перетаскивания Track остаётся на настоящем значении, анимируется только рисуемое положение.
        SetTrackValue(slider, IsThumbDragging(slider) ? slider.Value : ShownValue(slider));

        // Material-вид и точки-ориентиры рисуют сами и не знают про промежуточное положение.
        FindPart<UIElement>(slider, "PART_MaterialVisual")?.InvalidateVisual();
        FindPart<UIElement>(slider, "PART_MilestoneOverlay")?.InvalidateVisual();
    }

    private static void SetTrackValue(Slider slider, double value)
    {
        if (FindPart<Track>(slider, "PART_Track") is not { } track) return;

        if (BindingOperations.IsDataBound(track, Track.ValueProperty))
            BindingOperations.ClearBinding(track, Track.ValueProperty);
        track.Value = value;
    }
}
