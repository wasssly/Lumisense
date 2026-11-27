using System.Windows;
using System.Windows.Automation;

namespace Lumisense;

// Имена для экранного диктора у элементов без текстовой подписи (слайдеры, списки, полосы прогресса). Русская строка
// переводится по тем же правилам, что и остальной интерфейс; кнопки с подсказкой получают имя из ToolTip прямо в XAML.
internal static class AutomationNames
{
    public static void Set(DependencyObject element, string russianName) =>
        AutomationProperties.SetName(element, LocalizationService.Translate(russianName));
}
