using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Lumisense;

// Страница настроек, которая разбирается при первом показе, а не при создании окна.
internal interface ILazySettingsPage
{
    void MoveContentTo(Panel host);

    FrameworkElement? FindElement(string name);
}

internal static class LazySettingsPage
{
    // Элементы переезжают в хост окна, а сама страница остаётся владельцем их имён и обработчиков.
    public static void MoveChildren(Panel source, Panel host)
    {
        var children = source.Children.Cast<UIElement>().ToList();
        source.Children.Clear();
        foreach (UIElement child in children) host.Children.Add(child);
    }
}
