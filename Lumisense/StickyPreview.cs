using System;
using System.Windows;
using System.Windows.Media;

namespace Lumisense;

// Предпросмотр остаётся на экране, пока виден хотя бы кусок его карточки: сдвигается вниз вместе с прокруткой, но не дальше
// нижнего края карточки (за её пределы настройки уже другие, предпросмотр им не нужен).
internal static class StickyPreview
{
    public static void Update(FrameworkElement scroll, FrameworkElement card, FrameworkElement panel, TranslateTransform translate)
    {
        if (!card.IsVisible || !panel.IsVisible || panel.ActualHeight <= 0)
        {
            translate.Y = 0;
            return;
        }

        try
        {
            Point panelTop = panel.TransformToAncestor(scroll).Transform(new Point(0, 0));
            Point cardTop = card.TransformToAncestor(scroll).Transform(new Point(0, 0));

            const double topGap = 8.0;
            const double bottomPadding = 16.0;
            double naturalTop = panelTop.Y - translate.Y;
            double cardBottom = cardTop.Y + card.ActualHeight;
            double maxOffset = cardBottom - bottomPadding - (naturalTop + panel.ActualHeight);
            double wanted = topGap - naturalTop;

            translate.Y = Math.Max(0.0, Math.Min(wanted, maxOffset));
        }
        catch (InvalidOperationException)
        {
            translate.Y = 0;
        }
    }
}
