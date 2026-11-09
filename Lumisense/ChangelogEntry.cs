namespace Lumisense;

// Version и IsCurrent выставляет ChangelogLoader; необязательное "version" в JSON задаёт номер явно.
public class ChangelogEntry
{
    public string Version { get; set; } = "";
    public string Date { get; set; } = "";
    public List<ChangeItem> Changes { get; set; } = new();
    public string? Image { get; set; }
    public bool IsCurrent { get; set; }
}

// Type — "added"/"changed"/"improved"/"fixed"/"removed"/"security", см. ChangeTypeCatalog
public class ChangeItem
{
    public string Type { get; set; } = "changed";
    public string Text { get; set; } = "";
    public string? Image { get; set; }
}
