using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Lumisense;

// Число, у которого меняются только отличающиеся цифры: старая уезжает вверх или вниз, новая выезжает с противоположной стороны.
// Идея и тайминги — из DynamicIsland (Digits.cs); размытие убрано, чтобы не гонять эффекты в прозрачном окне.
internal sealed class RollingDigits : ContentControl
{
    private readonly StackPanel _cells = new() { Orientation = Orientation.Horizontal };
    private readonly List<TextBlock?> _glyphs = [];
    private string _text = "";
    private bool _rollingDown = true;

    public RollingDigits()
    {
        Focusable = false;
        IsTabStop = false;
        UseLayoutRounding = false;
        Content = _cells;
    }

    public string Text
    {
        get => _text;
        set
        {
            value ??= "";
            if (value == _text) return;

            _rollingDown = IsDecrease(_text, value) ?? _rollingDown;
            _text = value;
            Render(value, IsVisible && Opacity > 0.05);
        }
    }

    private static bool? IsDecrease(string was, string next)
    {
        if (!was.Any(char.IsAsciiDigit) || !next.Any(char.IsAsciiDigit)) return null;

        string a = string.Concat(was.Where(char.IsAsciiDigit)).TrimStart('0');
        string b = string.Concat(next.Where(char.IsAsciiDigit)).TrimStart('0');
        int order = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
        return order == 0 ? null : order > 0;
    }

    private double TravelDistance => Math.Round(FontSize * 0.5);

    private void Render(string text, bool animate)
    {
        while (_glyphs.Count > text.Length)
        {
            _cells.Children.RemoveAt(0);
            _glyphs.RemoveAt(0);
        }
        while (_glyphs.Count < text.Length)
        {
            _cells.Children.Insert(0, new Grid());
            _glyphs.Insert(0, null);
        }

        for (int i = 0; i < text.Length; i++)
        {
            string symbol = text[i].ToString();
            TextBlock? old = _glyphs[i];
            if (old?.Text == symbol) continue;

            var cell = (Grid)_cells.Children[i];
            var next = new TextBlock { Text = symbol, RenderTransform = new TranslateTransform() };
            Typography.SetNumeralAlignment(next, FontNumeralAlignment.Tabular);
            _glyphs[i] = next;

            if (!animate) cell.Children.Clear();
            cell.Children.Add(next);
            if (!animate) continue;

            if (old is not null) AnimateOut(cell, old);
            AnimateIn(next);
        }
    }

    private void AnimateOut(Grid cell, TextBlock glyph)
    {
        ((TranslateTransform)glyph.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(_rollingDown ? TravelDistance : -TravelDistance, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            });

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) => cell.Children.Remove(glyph);
        glyph.BeginAnimation(OpacityProperty, fade);
    }

    private void AnimateIn(TextBlock glyph)
    {
        ((TranslateTransform)glyph.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(_rollingDown ? -TravelDistance : TravelDistance, 0, TimeSpan.FromMilliseconds(380))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        glyph.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
    }
}
