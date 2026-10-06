using System.Windows.Media;

namespace Lumisense;

// Ключи в changelog.json — латиницей в нижнем регистре, ровно как в ChangeItem.Type
public static class ChangeTypeCatalog
{
    public sealed record Info(string Key, string SourceLabel, string IconKey, Color Color)
    {
        public string Label => LocalizationService.Translate(SourceLabel);
    }

    public static readonly Info Added = new("added", "Добавлено", "IconAdd", Color.FromRgb(0x22, 0xC5, 0x5E));
    public static readonly Info Changed = new("changed", "Изменено", "IconEdit", Color.FromRgb(0x3B, 0x82, 0xF6));
    public static readonly Info Fixed = new("fixed", "Исправлено", "IconWrench", Color.FromRgb(0xF5, 0x9E, 0x0B));
    public static readonly Info Removed = new("removed", "Удалено", "IconDelete", Color.FromRgb(0xEF, 0x44, 0x44));
    public static readonly Info Improved = new("improved", "Улучшено", "IconRefresh", Color.FromRgb(0x8B, 0x5C, 0xF6));
    public static readonly Info Security = new("security", "Безопасность", "IconInfo", Color.FromRgb(0x14, 0xB8, 0xA6));

    public static readonly IReadOnlyList<Info> All = new[] { Added, Changed, Improved, Fixed, Removed, Security };

    // неизвестный/пустой ключ падает на "Изменено", чтобы не ломать отображение
    public static Info Resolve(string? key) => key?.Trim().ToLowerInvariant() switch
    {
        "added" => Added,
        "changed" => Changed,
        "fixed" => Fixed,
        "removed" => Removed,
        "improved" => Improved,
        "security" => Security,
        _ => Changed
    };
}
