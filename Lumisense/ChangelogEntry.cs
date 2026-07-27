namespace AudioPlayer;

// Version и IsCurrent в JSON не хранятся, их выставляет ChangelogLoader.AssignComputedFields
// (Version — semver по смыслу изменений, IsCurrent — у записи с самой свежей датой)
public class ChangelogEntry
{
    public string Version { get; set; } = "";
    public string Date { get; set; } = "";
    public List<ChangeItem> Changes { get; set; } = new();
    public string? Image { get; set; }
    public bool IsCurrent { get; set; }
}

// Type — "added"/"changed"/"fixed"/"removed", см. ChangeTypeCatalog
public class ChangeItem
{
    public string Type { get; set; } = "changed";
    public string Text { get; set; } = "";
    public string? Image { get; set; }
}
