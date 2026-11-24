using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lumisense;

// Word roll (designbookmark.com/motion): при смене текста буквы старого слова по очереди уезжают вверх, а буквы нового выезжают
// снизу. Слой кладётся поверх обычного TextBlock в одну ячейку Grid: TextBlock остаётся источником шрифта, размера и запасным
// вариантом, когда слой выключен. Слишком длинный текст перед показом обрезается многоточием по ширине строки (Fit).
internal sealed class WordRollLayer : Grid
{
    private const double OutStagger = 0.022;
    private const double InFirstDelay = 0.06;
    private const double InStagger = 0.028;

    private sealed class Letter
    {
        public Letter(TextBlock block, double startValue, double target, double delay)
        {
            Block = block;
            Delay = delay;
            Spring.Value = startValue;
            Spring.Target = startValue;
            FinalTarget = target;
            MotionTokens.WordRollLetter.ApplyTo(Spring);
            block.RenderTransform = Shift;
        }

        public TextBlock Block { get; }
        public double Delay { get; }
        public double FinalTarget { get; }
        public bool Started { get; set; }
        public SpringValue Spring { get; } = new();
        public TranslateTransform Shift { get; } = new();
    }

    private sealed class Word
    {
        public Word(StackPanel panel, List<Letter> letters)
        {
            Panel = panel;
            Letters = letters;
        }

        public StackPanel Panel { get; }
        public List<Letter> Letters { get; }
    }

    private Word? _current;
    private SpringTrack? _track;
    private Action? _finishTrack;

    public WordRollLayer()
    {
        IsHitTestVisible = false;

        // Буквы выезжают из-под полосы и уходят за неё: срезаем только по вертикали, по горизонтали остров может расти.
        SizeChanged += (_, e) => Clip = new RectangleGeometry(new Rect(-4000, 0, e.NewSize.Width + 8000, e.NewSize.Height));
    }

    // true, пока текст показывает слой, а не сам TextBlock.
    public bool Active => _current is not null;

    // Текст, который показывает слой (возможно, обрезанный), и исходный текст, из которого он получен.
    public string Text { get; private set; } = "";

    public string FullText { get; private set; } = "";

    // Возвращает текст для показа слоем: целиком, если он влезает в availableWidth, иначе обрезанный многоточием.
    // null — если слой применить нельзя (пусто, нет ширины или не влезает даже одна буква).
    public static string? Fit(string text, TextBlock source, double availableWidth)
    {
        if (string.IsNullOrEmpty(text) || availableWidth <= 0) return null;

        string probeText = text + "…";
        StackPanel probe = BuildPanel(probeText, source, HorizontalAlignment.Left, out _,
            startValue: 0, target: 0, delayOf: _ => 0);
        probe.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));

        double[] widths = [.. probe.Children.OfType<TextBlock>().Select(block => block.DesiredSize.Width)];
        double ellipsis = widths[^1];
        double total = widths.Take(text.Length).Sum();
        if (total <= availableWidth) return text;

        double used = 0;
        int count = 0;
        while (count < text.Length && used + widths[count] + ellipsis <= availableWidth)
            used += widths[count++];

        string cut = text[..count].TrimEnd();
        return cut.Length == 0 ? null : cut + "…";
    }

    public void Clear()
    {
        _finishTrack?.Invoke();
        Children.Clear();
        _current = null;
        Text = "";
        FullText = "";
    }

    // previous — текст, который сейчас показывает TextBlock (если слой ещё не работал): его буквы тоже уедут вверх.
    public void Show(string fullText, string text, TextBlock source, string? previous, bool animate)
    {
        _finishTrack?.Invoke();

        Word? outgoing = _current;
        if (outgoing is null && animate && !string.IsNullOrEmpty(previous))
        {
            StackPanel panel = BuildPanel(previous, source, AlignmentOf(source), out List<Letter> letters, startValue: 0, target: -1, delayOf: k => k * OutStagger);
            Children.Add(panel);
            outgoing = new Word(panel, letters);
        }
        else if (outgoing is not null)
        {
            foreach (Letter letter in outgoing.Letters) letter.Spring.Value = letter.Spring.Target = 0;
            RebindOutgoing(outgoing);
        }

        double enter = animate ? 1 : 0;
        StackPanel incomingPanel = BuildPanel(text, source, AlignmentOf(source), out List<Letter> incomingLetters,
            startValue: enter, target: 0, delayOf: k => InFirstDelay + k * InStagger);
        Children.Add(incomingPanel);
        var incoming = new Word(incomingPanel, incomingLetters);
        _current = incoming;
        Text = text;
        FullText = fullText;

        double height = source.ActualHeight > 0 ? source.ActualHeight : source.FontSize * 1.3;
        var all = new List<Letter>(incoming.Letters);
        if (outgoing is not null) all.AddRange(outgoing.Letters);
        Stopwatch clock = Stopwatch.StartNew();

        void Apply()
        {
            double elapsed = clock.Elapsed.TotalSeconds;
            foreach (Letter letter in all)
            {
                if (!letter.Started && elapsed >= letter.Delay)
                {
                    letter.Started = true;
                    letter.Spring.Target = letter.FinalTarget;
                }

                double y = letter.Spring.Value;
                letter.Shift.Y = y * height * 1.05;
                letter.Block.Opacity = Math.Clamp(1 - Math.Abs(y) * 0.9, 0, 1);
            }
        }

        void Done()
        {
            if (outgoing is not null) Children.Remove(outgoing.Panel);
            _track = null;
            _finishTrack = null;
        }

        var track = new SpringTrack(Apply, [.. all.Select(letter => letter.Spring)])
        {
            KeepAlive = () => all.Exists(letter => !letter.Started),
            Settled = Done,
        };
        _track = track;
        _finishTrack = () =>
        {
            SpringRunner.Stop(track, finish: true);
            if (ReferenceEquals(_track, track)) _track = null;
        };

        Apply();
        if (animate) SpringRunner.Start(track);
        else Done();
    }

    // Буквы уходящего слова при повторном показе стартуют с места и получают свои задержки заново.
    private static void RebindOutgoing(Word outgoing)
    {
        for (int k = 0; k < outgoing.Letters.Count; k++)
        {
            Letter old = outgoing.Letters[k];
            var letter = new Letter(old.Block, 0, -1, k * OutStagger);
            outgoing.Letters[k] = letter;
        }
    }

    private static HorizontalAlignment AlignmentOf(TextBlock source) => source.TextAlignment switch
    {
        TextAlignment.Center => HorizontalAlignment.Center,
        TextAlignment.Right => HorizontalAlignment.Right,
        _ => HorizontalAlignment.Left,
    };

    private static StackPanel BuildPanel(string text, TextBlock source, HorizontalAlignment alignment, out List<Letter> letters,
        double startValue, double target, Func<int, double> delayOf)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Top,
        };
        letters = [];
        int k = 0;
        foreach (char ch in text)
        {
            var block = new TextBlock
            {
                Text = ch.ToString(),
                FontFamily = source.FontFamily,
                FontSize = source.FontSize,
                FontWeight = source.FontWeight,
                FontStyle = source.FontStyle,
                FontStretch = source.FontStretch,
                Foreground = source.Foreground,
            };
            panel.Children.Add(block);
            letters.Add(new Letter(block, startValue, target, delayOf(k)));
            k++;
        }

        return panel;
    }
}
