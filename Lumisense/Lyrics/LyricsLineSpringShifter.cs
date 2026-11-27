using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lumisense;

// Построчный пружинный сдвиг для перехода «Как в Apple Music»: список прокручивается сразу, а строки догоняют его пружиной,
// ниже активной — с задержкой (волна). Новая смена посреди движения продолжает его с текущей скоростью.
public sealed class LyricsLineSpringShifter
{
    private const double Stiffness = 170;
    private const double DampingRatio = 0.8;

    // Окно строк вокруг активной, которые двигаются; остальные вне экрана, и их анимировать незачем.
    private const int LinesAbove = 10;
    private const int LinesBelow = 14;

    private sealed class Line
    {
        public required TranslateTransform Transform { get; init; }
        public required SpringValue Spring { get; init; }
        public double DelaySeconds { get; set; }
    }

    private readonly Dictionary<FrameworkElement, Line> _lines = new();
    private TimeSpan _lastFrame = TimeSpan.MinValue;
    private bool _running;

    // Строки списка вокруг активной в виде пар (контейнер, число строк ниже активной; для строк выше активной — 0).
    public static List<(FrameworkElement Element, int LinesBelowActive)> CollectLines(ItemsControl list, int activeIndex, int itemCount)
    {
        var result = new List<(FrameworkElement, int)>();
        int first = Math.Max(0, activeIndex - LinesAbove);
        int last = Math.Min(itemCount - 1, activeIndex + LinesBelow);
        for (int i = first; i <= last; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement element)
                result.Add((element, Math.Max(0, i - activeIndex)));
        }

        return result;
    }

    // Текущее смещение контейнера от пружины: нужно, чтобы отделить его от места строки в раскладке при расчёте прокрутки.
    public double CurrentShift(FrameworkElement element) =>
        _lines.TryGetValue(element, out Line? line) ? line.Spring.Value : 0;

    // delta — на сколько пикселей список прокрутился мгновенно: строки визуально остаются на прежних местах и едут к новым.
    public void Shift(IEnumerable<(FrameworkElement Element, int LinesBelowActive)> lines, double delta, string transitionStyle)
    {
        foreach ((FrameworkElement element, int linesBelow) in lines)
        {
            if (!_lines.TryGetValue(element, out Line? line))
            {
                var transform = new TranslateTransform();
                element.RenderTransform = transform;
                line = new Line { Transform = transform, Spring = new SpringValue { Stiffness = Stiffness, DampingRatio = DampingRatio } };
                _lines[element] = line;
            }

            line.Spring.Value += delta;
            line.Spring.Target = 0;
            line.DelaySeconds = LyricsTransitionStyle.LineDelay(transitionStyle, linesBelow).TotalSeconds;
            line.Transform.Y = line.Spring.Value;
        }

        if (_lines.Count > 0) Start();
    }

    // Возвращает все строки на свои места без анимации (смена трека, режима, вида перехода).
    public void Cancel()
    {
        foreach (Line line in _lines.Values)
            line.Transform.Y = 0;

        _lines.Clear();
        Stop();
    }

    private void Start()
    {
        if (_running) return;

        _running = true;
        _lastFrame = TimeSpan.MinValue;
        CompositionTarget.Rendering += OnRendering;
    }

    private void Stop()
    {
        if (!_running) return;

        CompositionTarget.Rendering -= OnRendering;
        _running = false;
        _lastFrame = TimeSpan.MinValue;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        TimeSpan now = ((RenderingEventArgs)e).RenderingTime;
        double delta = _lastFrame == TimeSpan.MinValue ? 1.0 / 60 : (now - _lastFrame).TotalSeconds;
        // Событие может прийти дважды за один кадр с тем же временем.
        if (_lastFrame != TimeSpan.MinValue && delta <= 0) return;
        _lastFrame = now;

        List<FrameworkElement>? settled = null;
        foreach ((FrameworkElement element, Line line) in _lines)
        {
            if (line.DelaySeconds > 0)
            {
                line.DelaySeconds -= delta;
                continue;
            }

            bool moving = line.Spring.Step(delta);
            line.Transform.Y = line.Spring.Value;
            if (!moving)
                (settled ??= new List<FrameworkElement>()).Add(element);
        }

        if (settled is not null)
        {
            foreach (FrameworkElement element in settled)
                _lines.Remove(element);
        }

        if (_lines.Count == 0) Stop();
    }
}
