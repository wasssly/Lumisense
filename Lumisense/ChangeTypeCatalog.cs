using System.Windows.Media;

namespace AudioPlayer;

/// <summary>
/// Справочник типов строк в списке изменений: подпись по-русски, иконка (см. Icons/svg) и
/// цвет маркировки. Ключи в JSON (changelog.json) — латиницей и в нижнем регистре: "added",
/// "changed", "fixed", "removed" — как раз то, что попадает в ChangeItem.Type.
/// </summary>
public static class ChangeTypeCatalog
{
    public sealed record Info(string Key, string Label, string IconKey, Color Color);

    public static readonly Info Added = new("added", "Добавлено", "IconAdd", Color.FromRgb(0x22, 0xC5, 0x5E));
    public static readonly Info Changed = new("changed", "Изменено", "IconEdit", Color.FromRgb(0x3B, 0x82, 0xF6));
    public static readonly Info Fixed = new("fixed", "Исправлено", "IconWrench", Color.FromRgb(0xF5, 0x9E, 0x0B));
    public static readonly Info Removed = new("removed", "Удалено", "IconDelete", Color.FromRgb(0xEF, 0x44, 0x44));

    /// <summary>Все типы в порядке, в котором они везде показываются — фильтры, точки-маркеры и т.д.</summary>
    public static readonly IReadOnlyList<Info> All = new[] { Added, Changed, Fixed, Removed };

    /// <summary>Ключ (в любом регистре) → описание типа. Неизвестный или пустой ключ — как "Изменено",
    /// чтобы старые/нестандартные записи не ломали отображение, а просто выглядели нейтрально.</summary>
    public static Info Resolve(string? key) => key?.Trim().ToLowerInvariant() switch
    {
        "added" => Added,
        "changed" => Changed,
        "fixed" => Fixed,
        "removed" => Removed,
        _ => Changed
    };
}
