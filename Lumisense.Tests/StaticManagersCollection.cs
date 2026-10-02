using Xunit;

namespace Lumisense.Tests;

// FavoritesManager, PlayCountManager и SettingsManager хранят состояние в статических полях,
// поэтому тесты, которые его меняют, не должны выполняться параллельно друг с другом.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StaticManagersCollection
{
    public const string Name = "Static managers";
}
