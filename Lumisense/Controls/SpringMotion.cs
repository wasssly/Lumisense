using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ShapePath = System.Windows.Shapes.Path;

namespace Lumisense;

// Присоединяемые поведения пружинных компонентов designbookmark.com/motion: Toggle switch и Checkbox list.
// Шаблоны контролов лежат в XAML, а движение считают SpringValue и общий SpringRunner с пружинами из MotionTokens.
internal static class SpringMotion
{
    // Настройки задаёт MainWindow при запуске; окно настроек и главное окно работают с одним и тем же объектом.
    public static AppSettings? Settings { get; set; }

    public static bool Reduced(string? area) =>
        Settings is { } settings
        && (area is null ? settings.ReduceMotion : AccessibilityPreferences.ShouldReduceMotion(settings, area));

    // ----- Toggle switch -----

    public static readonly DependencyProperty SpringToggleProperty = DependencyProperty.RegisterAttached(
        "SpringToggle", typeof(bool), typeof(SpringMotion), new PropertyMetadata(false, OnSpringToggleChanged));

    public static bool GetSpringToggle(DependencyObject element) => (bool)element.GetValue(SpringToggleProperty);

    public static void SetSpringToggle(DependencyObject element, bool value) => element.SetValue(SpringToggleProperty, value);

    // Кружок: одна пружина ведёт позицию (0..1), вторая получает пинок скоростью и «растягивает» кружок в сторону хода,
    // потом он округляется обратно. Формулы render() — как в исходнике компонента.
    private sealed class ToggleState
    {
        public FrameworkElement? Knob { get; set; }
        public TranslateTransform Translate { get; } = new();
        public SpringValue Position { get; } = new();
        public SpringValue Stretch { get; } = new();
        public double BaseWidth { get; set; }
        public double Travel { get; set; }
        public double GrowMax { get; set; }
        public bool On { get; set; }
        public SpringTrack? Track { get; set; }
    }

    private static readonly ConditionalWeakTable<ToggleButton, ToggleState> ToggleStates = new();

    private static void OnSpringToggleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ToggleButton toggle) return;

        toggle.Loaded -= Toggle_Loaded;
        toggle.Checked -= Toggle_StateChanged;
        toggle.Unchecked -= Toggle_StateChanged;
        toggle.Indeterminate -= Toggle_StateChanged;
        if (e.NewValue is not true) return;

        toggle.Loaded += Toggle_Loaded;
        toggle.Checked += Toggle_StateChanged;
        toggle.Unchecked += Toggle_StateChanged;
        toggle.Indeterminate += Toggle_StateChanged;
        if (toggle.IsLoaded) BindToggle(toggle);
    }

    private static void Toggle_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggle) BindToggle(toggle);
    }

    private static ToggleState? BindToggle(ToggleButton toggle)
    {
        toggle.ApplyTemplate();
        if (toggle.Template?.FindName("Knob", toggle) is not FrameworkElement knob) return null;

        ToggleState state = ToggleStates.GetOrCreateValue(toggle);
        if (ReferenceEquals(state.Knob, knob)) return state;

        double rootWidth = (toggle.Template.FindName("Root", toggle) is FrameworkElement { Width: > 0 } root) ? root.Width : 44;
        state.Knob = knob;
        state.BaseWidth = knob.Width;
        state.Travel = rootWidth - 2 * knob.Margin.Left - state.BaseWidth;
        state.GrowMax = 10 * state.BaseWidth / 26;
        MotionTokens.TogglePosition.ApplyTo(state.Position);
        MotionTokens.ToggleStretch.ApplyTo(state.Stretch);
        knob.RenderTransform = state.Translate;

        // Первая раскладка не анимируется: переключатель сразу стоит там, где должен.
        state.On = toggle.IsChecked == true;
        state.Position.Value = state.Position.Target = state.On ? 1 : 0;
        state.Position.Velocity = 0;
        state.Stretch.Value = state.Stretch.Target = 0;
        state.Stretch.Velocity = 0;
        ApplyToggle(state);
        return state;
    }

    private static void ApplyToggle(ToggleState state)
    {
        if (state.Knob is null) return;

        double p = state.Position.Value;
        double grow = Math.Clamp(state.Stretch.Value, 0, 1) * state.GrowMax;
        double x = p * state.Travel;
        state.Knob.Width = state.BaseWidth + grow;
        state.Translate.X = state.On ? x - grow * Math.Clamp(p, 0, 1) : x;
    }

    private static void Toggle_StateChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle) return;

        ToggleState? state = ToggleStates.TryGetValue(toggle, out ToggleState? known) ? known : BindToggle(toggle);
        if (state?.Knob is null) return;

        state.On = toggle.IsChecked == true;
        state.Position.Target = state.On ? 1 : 0;
        if (!toggle.IsLoaded || Reduced(AnimationAreas.SettingsWindow))
        {
            state.Position.Value = state.Position.Target;
            state.Position.Velocity = 0;
            state.Stretch.Value = state.Stretch.Target = 0;
            state.Stretch.Velocity = 0;
            ApplyToggle(state);
            return;
        }

        state.Stretch.Target = 0;
        state.Stretch.Velocity = MotionTokens.ToggleStretchKick;
        state.Track ??= new SpringTrack(() => ApplyToggle(state), state.Position, state.Stretch);
        SpringRunner.Start(state.Track);
    }

    // ----- Checkbox list -----

    // Длина галочки M 4,10 L 8,14 L 16,5 в единицах толщины линии (≈17,7 px при толщине 2).
    private const double CheckLength = 9;
    private const double CheckDrawDelay = 0.08;

    public static readonly DependencyProperty SpringCheckProperty = DependencyProperty.RegisterAttached(
        "SpringCheck", typeof(bool), typeof(SpringMotion), new PropertyMetadata(false, OnSpringCheckChanged));

    public static bool GetSpringCheck(DependencyObject element) => (bool)element.GetValue(SpringCheckProperty);

    public static void SetSpringCheck(DependencyObject element, bool value) => element.SetValue(SpringCheckProperty, value);

    // Как в компоненте: заливка (fill), отскок коробки (pop, на включении стартует с 0,7 и пинка скоростью), а галочка рисуется
    // по смещению штриха чуть позже (draw).
    private sealed class CheckState
    {
        public Border? Box { get; set; }
        public Border? Fill { get; set; }
        public ShapePath? Mark { get; set; }
        public ScaleTransform BoxScale { get; } = new(1, 1);
        public SpringValue FillSpring { get; } = new();
        public SpringValue Pop { get; } = new();
        public SpringValue Draw { get; } = new();
        public Stopwatch Clock { get; } = new();
        public bool DrawPending { get; set; }
        public SpringTrack? Track { get; set; }
    }

    private static readonly ConditionalWeakTable<ToggleButton, CheckState> CheckStates = new();

    private static void OnSpringCheckChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ToggleButton box) return;

        box.Loaded -= Check_Loaded;
        box.Checked -= Check_StateChanged;
        box.Unchecked -= Check_StateChanged;
        box.Indeterminate -= Check_StateChanged;
        if (e.NewValue is not true) return;

        box.Loaded += Check_Loaded;
        box.Checked += Check_StateChanged;
        box.Unchecked += Check_StateChanged;
        box.Indeterminate += Check_StateChanged;
        if (box.IsLoaded) BindCheck(box);
    }

    private static void Check_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton box) BindCheck(box);
    }

    private static CheckState? BindCheck(ToggleButton box)
    {
        box.ApplyTemplate();
        if (box.Template?.FindName("Box", box) is not Border visual
            || box.Template.FindName("AccentFill", box) is not Border fill
            || box.Template.FindName("Mark", box) is not ShapePath mark)
            return null;

        CheckState state = CheckStates.GetOrCreateValue(box);
        if (ReferenceEquals(state.Box, visual) && ReferenceEquals(state.Mark, mark)) return state;

        state.Box = visual;
        state.Fill = fill;
        state.Mark = mark;
        MotionTokens.CheckFill.ApplyTo(state.FillSpring);
        MotionTokens.CheckPop.ApplyTo(state.Pop);
        MotionTokens.CheckDraw.ApplyTo(state.Draw);
        visual.RenderTransform = state.BoxScale;
        visual.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        fill.RenderTransform = Transform.Identity;
        mark.RenderTransform = Transform.Identity;
        mark.StrokeDashArray = [CheckLength, 100];

        double on = box.IsChecked == true ? 1 : 0;
        state.FillSpring.Value = state.FillSpring.Target = on;
        state.Draw.Value = state.Draw.Target = on;
        state.Pop.Value = state.Pop.Target = 1;
        state.FillSpring.Velocity = state.Draw.Velocity = state.Pop.Velocity = 0;
        ApplyCheck(state);
        return state;
    }

    private static void ApplyCheck(CheckState state)
    {
        if (state.DrawPending && state.Clock.Elapsed.TotalSeconds >= CheckDrawDelay)
        {
            state.DrawPending = false;
            state.Draw.Target = 1;
        }

        if (state.Fill is not null) state.Fill.Opacity = Math.Clamp(state.FillSpring.Value, 0, 1);
        state.BoxScale.ScaleX = state.BoxScale.ScaleY = state.Pop.Value;
        if (state.Mark is null) return;

        double draw = Math.Clamp(state.Draw.Value, 0, 1);
        state.Mark.StrokeDashOffset = CheckLength * (1 - draw);
        state.Mark.Opacity = draw > 0.01 ? 1 : 0;
    }

    private static void Check_StateChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton box) return;

        CheckState? state = CheckStates.TryGetValue(box, out CheckState? known) ? known : BindCheck(box);
        if (state is null) return;

        bool on = box.IsChecked == true;
        state.FillSpring.Target = on ? 1 : 0;
        if (!box.IsLoaded || Reduced(AnimationAreas.SettingsWindow))
        {
            state.FillSpring.Value = state.FillSpring.Target;
            state.Draw.Value = state.Draw.Target = on ? 1 : 0;
            state.Pop.Value = state.Pop.Target = 1;
            state.Pop.Velocity = 0;
            state.DrawPending = false;
            ApplyCheck(state);
            return;
        }

        state.Pop.Value = on ? 0.7 : 0.85;
        state.Pop.Velocity = 5;
        state.Pop.Target = 1;
        if (on)
        {
            state.Clock.Restart();
            state.DrawPending = true;
        }
        else
        {
            state.DrawPending = false;
            state.Draw.Target = 0;
        }

        state.Track ??= new SpringTrack(() => ApplyCheck(state), state.FillSpring, state.Pop, state.Draw)
        {
            KeepAlive = () => state.DrawPending,
        };
        SpringRunner.Start(state.Track);
    }
}
