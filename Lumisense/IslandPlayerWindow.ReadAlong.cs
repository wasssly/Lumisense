using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Lumisense;

// Read along (designbookmark.com/motion): строка песни читается по словам. За читаемым словом едет пилюля, прочитанные слова светлые,
// будущие приглушены. Края пилюли — две пружины, передний быстрый, задний ленивый, поэтому она тянется на ходу; на новой строке
// пилюля раскрывается с левого края слова. В данных нет таймингов слов, поэтому время строки делится между словами по длине.
internal partial class IslandPlayerWindow
{
    private const double ReadAlongPad = 0.16, ReadAlongDwellBase = 0.2, ReadAlongDwellPerChar = 0.035;

    private static readonly Brush ReadAlongBright = Brushes.White;
    private static readonly Brush ReadAlongDim = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255));
    private static readonly Brush ReadAlongNow = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));

    private readonly List<Run> _readWords = [];
    private double[] _readStarts = [];
    private double _readLineStart, _readLineTop = double.NaN;
    private int _readWord = -2;
    private readonly SpringValue _readLeft = new(), _readRight = new(), _readTop = new(), _readFade = new();
    private readonly TranslateTransform _readShift = new();
    private SpringTrack? _readTrack;

    // Подготавливает активную строку: разбивает на слова (Run) и считает, когда читается каждое.
    private void SetupReadAlong(int active)
    {
        ClearReadAlong();
        if (!Settings.IslandReadAlong || active < 0 || active >= _lyrics.Lines.Count) return;

        string[] parts = _lyrics.Lines[active].Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        LyricLine2.Inlines.Clear();
        for (int i = 0; i < parts.Length; i++)
        {
            var run = new Run(parts[i]);
            _readWords.Add(run);
            LyricLine2.Inlines.Add(run);
            if (i < parts.Length - 1) LyricLine2.Inlines.Add(new Run(" "));
        }

        double lineStart = _lyrics.Lines[active].Time.TotalSeconds;
        double lineEnd = active + 1 < _lyrics.Lines.Count ? _lyrics.Lines[active + 1].Time.TotalSeconds : lineStart + 5;
        double duration = Math.Max(0.5, lineEnd - lineStart);
        double[] dwell = [.. parts.Select(part => ReadAlongDwellBase + part.Length * ReadAlongDwellPerChar)];

        // Строка читается за 92% своего времени, но не медленнее обычного темпа в полтора раза.
        double scale = Math.Min(duration * 0.92, dwell.Sum() * 1.5) / dwell.Sum();
        _readStarts = new double[parts.Length];
        double at = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            _readStarts[i] = at;
            at += dwell[i] * scale;
        }

        _readLineStart = lineStart;
        _readWord = -2;
        _readLineTop = double.NaN;
    }

    private void ClearReadAlong()
    {
        _readWords.Clear();
        _readStarts = [];
        _readWord = -2;
        _readLineTop = double.NaN;
        _readFade.Value = _readFade.Target = 0;
        ReadAlongPill.Opacity = 0;
        if (_readTrack is not null) SpringRunner.Stop(_readTrack, finish: false);
    }

    // Вызывается на каждый снимок позиции: переходит к слову, которое читается сейчас.
    private void UpdateReadAlong(double positionSeconds)
    {
        if (_readWords.Count == 0) return;

        double t = positionSeconds - _readLineStart;
        int index = -1;
        for (int i = 0; i < _readStarts.Length && _readStarts[i] <= t; i++) index = i;
        if (index == _readWord) return;

        _readWord = index;
        try
        {
            ShowReadAlongWord();
        }
        catch (Exception ex)
        {
            Logger.Error("Read along: не удалось поставить пилюлю, подсветка слов отключена для этой строки", ex);
            ClearReadAlong();
            if (_lyricsLine >= 0 && _lyricsLine < _lyrics.Lines.Count) LyricLine2.Text = _lyrics.Lines[_lyricsLine].Text;
        }
    }

    private void ShowReadAlongWord()
    {
        for (int i = 0; i < _readWords.Count; i++)
            _readWords[i].Foreground = i < _readWord ? ReadAlongBright : i == _readWord ? ReadAlongNow : ReadAlongDim;

        if (_readWord < 0)
        {
            _readFade.Target = 0;
            RunReadAlong();
            return;
        }

        LyricLine2.UpdateLayout();
        Run word = _readWords[_readWord];
        Rect start = word.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        Rect end = word.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
        if (start.IsEmpty || end.IsEmpty) return;

        // Слой пилюли делит с TextBlock масштаб, поэтому координаты берутся без учёта масштаба: смещение строки в ячейке плюс
        // прямоугольник слова внутри TextBlock. Так пилюля сидит на словах и пока строка ещё сдвигается и растёт.
        double pad = LyricLine2.FontSize * ReadAlongPad;
        Vector offset = VisualTreeHelper.GetOffset(LyricLine2);
        var topLeft = new System.Windows.Point(start.Left - pad + offset.X, start.Top + offset.Y);
        var bottomRight = new System.Windows.Point(end.Right + pad + offset.X, start.Bottom + offset.Y);
        double height = bottomRight.Y - topLeft.Y;
        ReadAlongPill.Height = height;
        ReadAlongPill.CornerRadius = new CornerRadius(height * 0.34);

        bool forward = topLeft.X >= _readLeft.Target;
        bool fresh = double.IsNaN(_readLineTop) || Math.Abs(topLeft.Y - _readLineTop) > 2 || _readFade.Value < 0.05;
        if (fresh || ReduceMotion)
        {
            // Новая строка или первое слово: пилюля раскрывается с левого края слова, а не едет через весь текст.
            _readLeft.Value = _readLeft.Target = topLeft.X;
            _readRight.Value = _readRight.Target = fresh ? topLeft.X : bottomRight.X;
            _readTop.Value = _readTop.Target = topLeft.Y;
            _readLeft.Velocity = _readRight.Velocity = _readTop.Velocity = 0;
        }

        _readLineTop = topLeft.Y;
        MotionTokens.ReadAlongFront.ApplyTo(forward ? _readRight : _readLeft);
        MotionTokens.ReadAlongBack.ApplyTo(forward ? _readLeft : _readRight);
        MotionTokens.ReadAlongLine.ApplyTo(_readTop);
        MotionTokens.ReadAlongFade.ApplyTo(_readFade);
        _readLeft.Target = topLeft.X;
        _readRight.Target = bottomRight.X;
        _readTop.Target = topLeft.Y;
        _readFade.Target = 1;
        if (ReduceMotion)
        {
            _readLeft.Value = _readLeft.Target;
            _readRight.Value = _readRight.Target;
            _readFade.Value = 1;
        }

        RunReadAlong();
    }

    private void RunReadAlong()
    {
        ReadAlongPill.RenderTransform = _readShift;
        _readTrack ??= new SpringTrack(ApplyReadAlong, _readLeft, _readRight, _readTop, _readFade);
        SpringRunner.Start(_readTrack);
    }

    private void ApplyReadAlong()
    {
        ReadAlongPill.Width = Math.Max(0, _readRight.Value - _readLeft.Value);
        _readShift.X = _readLeft.Value;
        _readShift.Y = _readTop.Value;
        ReadAlongPill.Opacity = Math.Clamp(_readFade.Value, 0, 1);
    }
}
