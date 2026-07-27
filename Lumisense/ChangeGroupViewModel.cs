using System.Windows.Media;

namespace AudioPlayer;

// Группа строк одного типа внутри версии ("Добавлено", "Исправлено" и т.п.)
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
