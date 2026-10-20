using Xunit;

namespace Lumisense.Tests;

public sealed class SkippedReleasesTests
{
    private static ReleaseListItem Release(string version, string? notes = "notes", bool prerelease = false) =>
        new() { Version = version, ReleaseNotes = notes, IsPrerelease = prerelease };

    [Fact]
    public void SelectSkippedReleases_ReturnsVersionsAfterCurrentUpToLatest_NewestFirst()
    {
        var releases = new[]
        {
            Release("1.20.0"), Release("1.23.0"), Release("1.21.0"), Release("1.22.0"), Release("1.24.0"),
        };

        var skipped = UpdateChecker.SelectSkippedReleases(releases, "1.21.0", "1.23.0");

        Assert.Equal(new[] { "1.23.0", "1.22.0" }, skipped.Select(release => release.Version));
    }

    [Fact]
    public void SelectSkippedReleases_SortsNumericallyNotAsText()
    {
        var releases = new[] { Release("1.9.0"), Release("1.10.0"), Release("1.100.0") };

        var skipped = UpdateChecker.SelectSkippedReleases(releases, "1.8.0", "1.100.0");

        Assert.Equal(new[] { "1.100.0", "1.10.0", "1.9.0" }, skipped.Select(release => release.Version));
    }

    [Fact]
    public void SelectSkippedReleases_SkipsPrereleasesAndReleasesWithoutNotes()
    {
        var releases = new[]
        {
            Release("1.22.0", prerelease: true), Release("1.23.0", notes: " "), Release("1.24.0"),
        };

        var skipped = UpdateChecker.SelectSkippedReleases(releases, "1.21.0", "1.24.0");

        Assert.Equal(new[] { "1.24.0" }, skipped.Select(release => release.Version));
    }

    [Theory]
    [InlineData("", "1.2.0")]
    [InlineData("1.1.0", "not-a-version")]
    public void SelectSkippedReleases_ReturnsEmptyForUnparsableVersions(string current, string latest)
    {
        Assert.Empty(UpdateChecker.SelectSkippedReleases(new[] { Release("1.2.0") }, current, latest));
    }
}
