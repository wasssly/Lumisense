using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using ShapePath = System.Windows.Shapes.Path;

namespace Lumisense;

// Присоединяемые поведения пружинных компонентов designbookmark.com/motion: Toggle switch, Checkbox list и Spring dropdown.
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

    // ----- Spring dropdown -----

    // Шаблон ComboBox остаётся стандартным шаблоном WPF-UI (он гарантированно открывается): анимируются его части — Popup, DropDownBorder
    // и строки. Подсветка-пилюля вставляется в сетку под строками при первом открытии.
    private const double TickLength = 9.4;
    private const double RowRise = 14;
    private const double ValueRise = 14;
    private const double RowStagger = 0.045;
    private const double RowFirstDelay = 0.05;

    public static readonly DependencyProperty SpringDropdownProperty = DependencyProperty.RegisterAttached(
        "SpringDropdown", typeof(bool), typeof(SpringMotion), new PropertyMetadata(false, OnSpringDropdownChanged));

    public static bool GetSpringDropdown(DependencyObject element) => (bool)element.GetValue(SpringDropdownProperty);

    public static void SetSpringDropdown(DependencyObject element, bool value) => element.SetValue(SpringDropdownProperty, value);

    private sealed class DropdownRow
    {
        public DropdownRow(ComboBoxItem item, ShapePath? tick, bool selected, double startAt)
        {
            Item = item;
            Tick = tick;
            Selected = selected;
            StartAt = startAt;
        }

        public ComboBoxItem Item { get; }
        public ShapePath? Tick { get; }
        public bool Selected { get; }
        public double StartAt { get; }
        public bool Started { get; set; }
        public SpringValue Slide { get; } = new();
        public SpringValue Draw { get; } = new();
        public TranslateTransform Shift { get; } = new();
        public EventHandler? Highlighted { get; set; }
    }

    private sealed class DropdownState
    {
        public Popup? Popup { get; set; }
        public FrameworkElement? Host { get; set; }
        public Panel? HighlightHost { get; set; }
        public Border? Highlight { get; set; }
        public ContentPresenter? Site { get; set; }

        public TranslateTransform SiteShift { get; } = new();
        public TranslateTransform HighlightShift { get; } = new();
        public ScaleTransform HostScale { get; } = new(1, 1);
        public RectangleGeometry Clip { get; } = new() { RadiusX = 12, RadiusY = 12 };

        public SpringValue Height { get; } = new();
        public SpringValue Fade { get; } = new();
        public SpringValue Value { get; } = new();
        public SpringValue HighlightTop { get; } = new();
        public SpringValue HighlightBottom { get; } = new();
        public SpringValue HighlightFade { get; } = new();

        public List<DropdownRow> Rows { get; } = [];
        public Stopwatch Clock { get; } = new();
        public bool MenuActive { get; set; }
        public double MenuWidth { get; set; }
        public double MenuHeight { get; set; }
        public int Hover { get; set; } = -1;
        public SpringTrack? Track { get; set; }
    }

    private static readonly ConditionalWeakTable<System.Windows.Controls.ComboBox, DropdownState> DropdownStates = new();

    private static void OnSpringDropdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not System.Windows.Controls.ComboBox combo) return;

        combo.DropDownOpened -= Combo_DropDownOpened;
        combo.DropDownClosed -= Combo_DropDownClosed;
        combo.SelectionChanged -= Combo_SelectionChanged;
        combo.DropDownOpened -= Combo_CloseOnScrollOpened;
        combo.DropDownClosed -= Combo_CloseOnScrollClosed;
        if (e.NewValue is not true) return;

        combo.DropDownOpened += Combo_DropDownOpened;
        combo.DropDownClosed += Combo_DropDownClosed;
        combo.SelectionChanged += Combo_SelectionChanged;
        combo.DropDownOpened += Combo_CloseOnScrollOpened;
        combo.DropDownClosed += Combo_CloseOnScrollClosed;
    }

    // Открытый список закрывается, когда страницу под ним прокручивают: иначе всплывающее окно ехало бы вслед за полем.
    private sealed class ScrollWatch
    {
        public List<(ScrollViewer Viewer, ScrollChangedEventHandler Handler)> Hooks { get; } = [];
    }

    private static readonly ConditionalWeakTable<System.Windows.Controls.ComboBox, ScrollWatch> ScrollWatches = new();

    private static void Combo_CloseOnScrollOpened(object? sender, EventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox combo) return;

        ScrollWatch watch = ScrollWatches.GetOrCreateValue(combo);
        UnhookScroll(watch);
        for (DependencyObject? node = VisualTreeHelper.GetParent(combo); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is not ScrollViewer viewer) continue;

            ScrollChangedEventHandler handler = (_, args) =>
            {
                // События внутреннего ScrollViewer самого списка сюда тоже всплывают: берём только прокрутку страницы.
                if (ReferenceEquals(args.OriginalSource, viewer) && args.VerticalChange != 0) combo.IsDropDownOpen = false;
            };
            viewer.ScrollChanged += handler;
            watch.Hooks.Add((viewer, handler));
        }
    }

    private static void Combo_CloseOnScrollClosed(object? sender, EventArgs e)
    {
        if (sender is System.Windows.Controls.ComboBox combo && ScrollWatches.TryGetValue(combo, out ScrollWatch? watch)) UnhookScroll(watch);
    }

    private static void UnhookScroll(ScrollWatch watch)
    {
        foreach ((ScrollViewer viewer, ScrollChangedEventHandler handler) in watch.Hooks) viewer.ScrollChanged -= handler;
        watch.Hooks.Clear();
    }

    // Находит части стандартного шаблона WPF-UI. Если шаблон другой (части не нашлись), анимации просто нет, а список работает как обычно.
    private static DropdownState? BindDropdown(System.Windows.Controls.ComboBox combo)
    {
        combo.ApplyTemplate();
        ControlTemplate? template = combo.Template;
        if (template?.FindName("Popup", combo) is not Popup { Child: FrameworkElement host } popup
            || template.FindName("DropDownBorder", combo) is not Border { Child: Panel highlightHost })
            return null;

        DropdownState state = DropdownStates.GetOrCreateValue(combo);
        if (ReferenceEquals(state.Host, host)) return state;

        state.Popup = popup;
        state.Host = host;
        state.HighlightHost = highlightHost;
        state.Site = template.FindName("PART_ContentPresenter", combo) as ContentPresenter;

        MotionTokens.DropdownHeight.ApplyTo(state.Height);
        MotionTokens.DropdownFade.ApplyTo(state.Fade);
        MotionTokens.DropdownValue.ApplyTo(state.Value);
        MotionTokens.DropdownHighlightFade.ApplyTo(state.HighlightFade);
        MotionTokens.DropdownHighlightLead.ApplyTo(state.HighlightTop);
        MotionTokens.DropdownHighlightLead.ApplyTo(state.HighlightBottom);
        state.Value.Value = state.Value.Target = 1;

        var highlight = new Border
        {
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(8),
            IsHitTestVisible = false,
            Opacity = 0,
            Height = 0,
            RenderTransform = state.HighlightShift,
        };
        highlight.SetResourceReference(Border.BackgroundProperty, "ControlFillColorTertiaryBrush");
        highlightHost.Children.Insert(0, highlight);
        state.Highlight = highlight;

        if (state.Site is not null) state.Site.RenderTransform = state.SiteShift;
        host.RenderTransformOrigin = new System.Windows.Point(0.5, 0);
        return state;
    }

    private static void RunDropdown(DropdownState state)
    {
        state.Track ??= new SpringTrack(
            () => ApplyDropdown(state),
            state.Height,
            state.Fade,
            state.Value,
            state.HighlightTop,
            state.HighlightBottom,
            state.HighlightFade)
        {
            KeepAlive = () => state.MenuActive && state.Rows.Exists(row => !row.Started),
        };
        SpringRunner.Start(state.Track);
    }

    private static void ApplyDropdown(DropdownState state)
    {
        // Новое значение выезжает в поле снизу (прежнее просто гаснет под ним).
        double r = state.Value.Value;
        if (state.Site is not null)
        {
            state.Site.Opacity = Math.Clamp(r, 0, 1);
            state.SiteShift.Y = (1 - r) * ValueRise;
        }

        if (!state.MenuActive || state.Host is null) return;

        double height = Math.Max(0, state.Height.Value);
        state.Host.Opacity = Math.Clamp(state.Fade.Value, 0, 1);
        state.Clip.Rect = new Rect(0, 0, state.MenuWidth, height);
        state.HostScale.ScaleX = 0.9 + 0.1 * Math.Clamp(height / state.MenuHeight, 0, 1.06);

        double elapsed = state.Clock.Elapsed.TotalSeconds;
        foreach (DropdownRow row in state.Rows)
        {
            if (!row.Started && elapsed >= row.StartAt)
            {
                row.Started = true;
                row.Slide.Target = 1;
                if (row.Selected) row.Draw.Target = 1;
            }

            row.Item.Opacity = Math.Clamp(row.Slide.Value, 0, 1);
            row.Shift.Y = (row.Slide.Value - 1) * RowRise;
            if (row.Tick is null) continue;

            double draw = Math.Clamp(row.Draw.Value, 0, 1);
            row.Tick.StrokeDashOffset = TickLength * (1 - draw);
            row.Tick.Opacity = draw > 0.01 ? 1 : 0;
        }

        if (state.Highlight is null) return;

        const double minHeight = 12;
        double top = Math.Min(state.HighlightTop.Value, state.HighlightBottom.Value - minHeight);
        double bottom = Math.Max(state.HighlightBottom.Value, top + minHeight);
        state.Highlight.Height = bottom - top;
        state.HighlightShift.Y = top;
        state.Highlight.Opacity = Math.Clamp(state.HighlightFade.Value, 0, 1) * Math.Clamp(state.Fade.Value, 0, 1);
    }

    private static void Combo_DropDownOpened(object? sender, EventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox combo || Reduced(AnimationAreas.SettingsWindow)) return;

        DropdownState? state = null;
        try
        {
            state = BindDropdown(combo);
            if (state?.Host is null) return;

            // До первой раскладки меню скрыто, иначе оно мелькнуло бы целиком. Страховка ниже вернёт его в любом случае.
            state.Host.Opacity = 0;
            DropdownState bound = state;
            combo.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => BeginMenu(combo, bound)));

            var guard = new DispatcherTimer(DispatcherPriority.Background, combo.Dispatcher) { Interval = TimeSpan.FromMilliseconds(400) };
            guard.Tick += (_, _) =>
            {
                guard.Stop();
                if (!bound.MenuActive) RestoreMenu(bound);
            };
            guard.Start();
        }
        catch (Exception ex)
        {
            Logger.Error("Spring dropdown: не удалось запустить анимацию открытия, список показан без неё", ex);
            if (state is not null) RestoreMenu(state);
        }
    }

    // Возвращает меню в обычный вид: список не останется невидимым, что бы ни случилось с анимацией.
    private static void RestoreMenu(DropdownState state)
    {
        state.MenuActive = false;
        EndMenuRows(state);
        if (state.Host is not null)
        {
            state.Host.ClearValue(UIElement.OpacityProperty);
            state.Host.ClearValue(UIElement.ClipProperty);
            state.Host.ClearValue(UIElement.RenderTransformProperty);
        }

        if (state.Highlight is not null) state.Highlight.Opacity = 0;
    }

    private static void BeginMenu(System.Windows.Controls.ComboBox combo, DropdownState state)
    {
        try
        {
            if (!BeginMenuCore(combo, state)) RestoreMenu(state);
        }
        catch (Exception ex)
        {
            Logger.Error("Spring dropdown: ошибка при раскрытии меню, список показан без анимации", ex);
            RestoreMenu(state);
        }
    }

    private static bool BeginMenuCore(System.Windows.Controls.ComboBox combo, DropdownState state)
    {
        if (!combo.IsDropDownOpen || state.Host is null || state.Highlight is null || state.HighlightHost is null) return false;

        state.Host.UpdateLayout();
        state.MenuWidth = state.Host.ActualWidth;
        state.MenuHeight = state.Host.ActualHeight;
        if (state.MenuWidth <= 0 || state.MenuHeight <= 0) return false;

        EndMenuRows(state);
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.ItemContainerGenerator.ContainerFromIndex(i) is not ComboBoxItem item) continue;

            item.ApplyTemplate();
            var tick = item.Template?.FindName("Tick", item) as ShapePath;
            var row = new DropdownRow(item, tick, i == combo.SelectedIndex, RowFirstDelay + Math.Min(i, 10) * RowStagger);
            MotionTokens.DropdownRow.ApplyTo(row.Slide);
            MotionTokens.DropdownTick.ApplyTo(row.Draw);
            row.Slide.Value = row.Slide.Target = 0;
            row.Draw.Value = row.Draw.Target = 0;
            item.RenderTransform = row.Shift;
            item.Opacity = 0;
            if (tick is not null) tick.StrokeDashArray = [TickLength, 100];

            int index = i;
            EventHandler highlighted = (_, _) =>
            {
                if (row.Item.IsHighlighted) MoveHighlight(state, row.Item, index, snap: false);
            };
            row.Highlighted = highlighted;
            DependencyPropertyDescriptor.FromProperty(ComboBoxItem.IsHighlightedProperty, typeof(ComboBoxItem))
                ?.AddValueChanged(item, highlighted);
            state.Rows.Add(row);
        }

        // Пружины строк создаются при каждом открытии, поэтому трек пересобирается вместе с ними: иначе строки так и остались бы
        // прозрачными (их пружины никто не двигал бы).
        if (state.Track is not null) SpringRunner.Stop(state.Track, finish: false);
        state.Track = new SpringTrack(
            () => ApplyDropdown(state),
            [
                state.Height, state.Fade, state.Value, state.HighlightTop, state.HighlightBottom, state.HighlightFade,
                .. state.Rows.SelectMany(row => new[] { row.Slide, row.Draw }),
            ])
        {
            KeepAlive = () => state.MenuActive && state.Rows.Exists(row => !row.Started),
        };

        state.Host.RenderTransform = state.HostScale;
        state.Host.Clip = state.Clip;
        state.Height.Value = 0;
        state.Height.Velocity = 0;
        state.Height.Target = state.MenuHeight;
        state.Fade.Value = 0;
        state.Fade.Velocity = 0;
        state.Fade.Target = 1;
        state.Clock.Restart();
        state.MenuActive = true;

        DropdownRow? selected = state.Rows.Find(row => row.Selected);
        state.Hover = -1;
        if (selected is not null) MoveHighlight(state, selected.Item, state.Rows.IndexOf(selected), snap: true);
        else state.HighlightFade.Target = 0;

        RunDropdown(state);
        return true;
    }

    private static void MoveHighlight(DropdownState state, ComboBoxItem item, int index, bool snap)
    {
        if (state.HighlightHost is null || state.Highlight is null || (!state.MenuActive && !snap)) return;

        System.Windows.Point origin = item.TranslatePoint(new System.Windows.Point(0, 0), state.HighlightHost);
        double top = origin.Y;
        double bottom = top + item.ActualHeight;
        state.Highlight.Width = item.ActualWidth;
        state.Highlight.Margin = new Thickness(origin.X, 0, 0, 0);
        if (snap || state.Hover < 0)
        {
            state.HighlightTop.Value = state.HighlightTop.Target = top;
            state.HighlightBottom.Value = state.HighlightBottom.Target = bottom;
            state.HighlightTop.Velocity = state.HighlightBottom.Velocity = 0;
        }
        else
        {
            // Край по ходу движения быстрый, задний отстаёт: подсветка растягивается между строками.
            bool down = index > state.Hover;
            (down ? MotionTokens.DropdownHighlightLead : MotionTokens.DropdownHighlightTrail).ApplyTo(state.HighlightBottom);
            (down ? MotionTokens.DropdownHighlightTrail : MotionTokens.DropdownHighlightLead).ApplyTo(state.HighlightTop);
            state.HighlightTop.Target = top;
            state.HighlightBottom.Target = bottom;
        }

        state.Hover = index;
        state.HighlightFade.Target = 1;
        RunDropdown(state);
    }

    private static void EndMenuRows(DropdownState state)
    {
        var descriptor = DependencyPropertyDescriptor.FromProperty(ComboBoxItem.IsHighlightedProperty, typeof(ComboBoxItem));
        foreach (DropdownRow row in state.Rows)
        {
            if (row.Highlighted is not null) descriptor?.RemoveValueChanged(row.Item, row.Highlighted);
            row.Item.ClearValue(UIElement.OpacityProperty);
            row.Item.ClearValue(UIElement.RenderTransformProperty);
            if (row.Tick is null) continue;

            row.Tick.ClearValue(ShapePath.StrokeDashOffsetProperty);
            row.Tick.ClearValue(UIElement.OpacityProperty);
        }

        state.Rows.Clear();
    }

    private static void Combo_DropDownClosed(object? sender, EventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox combo || !DropdownStates.TryGetValue(combo, out DropdownState? state)) return;

        RestoreMenu(state);
        state.HighlightFade.Value = state.HighlightFade.Target = 0;
        state.Hover = -1;
    }

    private static void Combo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox { IsLoaded: true } combo
            || e.RemovedItems.Count == 0
            || Reduced(AnimationAreas.SettingsWindow))
            return;

        DropdownState? state = BindDropdown(combo);
        if (state?.Site is null) return;

        state.Value.Value = 0;
        state.Value.Velocity = 0;
        state.Value.Target = 1;
        RunDropdown(state);
    }
}
