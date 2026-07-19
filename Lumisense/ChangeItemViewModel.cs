using System.Windows.Media;

namespace AudioPlayer;

/// <summary>Обёртка над ChangeItem для UI: помимо текста сразу несёт готовые для биндинга
/// подпись типа, ключ иконки и цвет — see ChangeTypeCatalog.</summary>
public sealed class ChangeItemViewModel
{
    public string Text { get; }
    public string TypeKey { get; }
    public string TypeLabel { get; }
    public string IconKey { get; }
    public SolidColorBrush TypeBrush { get; }

    /// <summary>Необязательная картинка этого конкретного пункта (не всей версии) — см.
    /// ChangeItem.Image / ChangelogImageResolver.Resolve.</summary>
    public string? ImageSource { get; }

    public bool HasImage => !string.IsNullOrWhiteSpace(ImageSource);

    public ChangeItemViewModel(ChangeItem source)
    {
        var info = ChangeTypeCatalog.Resolve(source.Type);

        Text = source.Text;
        TypeKey = info.Key;
        TypeLabel = info.Label;
        IconKey = info.IconKey;
        ImageSource = ChangelogImageResolver.Resolve(source.Image);

        TypeBrush = new SolidColorBrush(info.Color);
        TypeBrush.Freeze();
    }
}
