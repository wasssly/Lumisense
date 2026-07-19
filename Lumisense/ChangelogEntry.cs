namespace AudioPlayer;

/// <summary>
/// Одна версия в списке изменений. Загружается из Changelog/changelog.json (лежит рядом
/// с .exe — см. ChangelogLoader), но поля Version и IsCurrent в самом JSON не пишутся —
/// они вычисляются в ChangelogLoader.AssignComputedFields: Version — по смыслу изменений
/// записи в стиле semver ("1.0.0", "1.1.0", "2.0.0", ...; см. BumpForChanges), а IsCurrent
/// выставляется у записи с самой свежей датой.
/// </summary>
public class ChangelogEntry
{
    public string Version { get; set; } = "";
    public string Date { get; set; } = "";
    public List<ChangeItem> Changes { get; set; } = new();

    /// <summary>Необязательная картинка версии — либо ссылка (http/https), либо имя файла
    /// (или относительный путь) внутри папки Changelog, рядом с changelog.json. Разрешается
    /// в реальный путь для отображения в ChangelogEntryViewModel.ImageSource.</summary>
    public string? Image { get; set; }

    public bool IsCurrent { get; set; }
}

/// <summary>Одна строка в списке изменений версии — текст и тип (см. ChangeTypeCatalog:
/// "added", "changed", "fixed" или "removed"). Неизвестный или пустой Type трактуется как
/// "changed" — см. ChangeTypeCatalog.Resolve.</summary>
public class ChangeItem
{
    public string Type { get; set; } = "changed";
    public string Text { get; set; } = "";

    /// <summary>Необязательная картинка ИМЕННО этого пункта (не всей версии) — тот же формат,
    /// что и ChangelogEntry.Image: либо ссылка (http/https), либо имя файла (или относительный
    /// путь) внутри папки Changelog. Разрешается в ChangeItemViewModel.ImageSource.</summary>
    public string? Image { get; set; }
}
