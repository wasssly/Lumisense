using System.Windows.Media;

namespace AudioPlayer;

/// <summary>Все изменения одного типа внутри версии (например, все "Добавлено") — новая
/// деталь-панель ChangelogWindow группирует строки по типу вместо плоского списка, так
/// разница между тем, что добавили/поправили/убрали, видна сразу, а не только по цвету
/// иконки у каждой отдельной строки.</summary>
public sealed class ChangeGroupViewModel
{
    public string Label { get; }
    public string IconKey { get; }
    public SolidColorBrush Brush { get; }
    public IReadOnlyList<ChangeItemViewModel> Items { get; }
    public int Count => Items.Count;

    public ChangeGroupViewModel(ChangeTypeCatalog.Info info, IReadOnlyList<ChangeItemViewModel> items)
    {
        Label = info.Label;
        IconKey = info.IconKey;
        Items = items;

        Brush = new SolidColorBrush(info.Color);
        Brush.Freeze();
    }
}
