using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Lumisense.Tests;

public sealed class UpdateFeedSourcesTests
{
    private const string Official = "https://github.com/wasssly/Lumisense/releases/latest/download/";

    [Fact]
    public void Build_PutsTheOfficialFeedFirstAndAddsTrustedMirrorsOnce()
    {
        var urls = UpdateFeedSources.Build(Official, new[] { "https://updates.example.org/lumisense", "https://updates.example.org/lumisense/" });

        Assert.Equal(new[] { Official, "https://updates.example.org/lumisense/" }, urls);
    }

    [Theory]
    [InlineData("http://updates.example.org/feed/")]
    [InlineData("https://user:pass@updates.example.org/feed/")]
    [InlineData("https://127.0.0.1/feed/")]
    [InlineData("https://gh-proxy.org/https://github.com/wasssly/Lumisense/releases/latest/download/")]
    [InlineData("https://ghfast.top/feed/")]
    [InlineData("not a url")]
    public void IsTrustedMirrorUrl_RejectsInsecureAndPublicProxyAddresses(string url) =>
        Assert.False(UpdateFeedSources.IsTrustedMirrorUrl(url));

    [Fact]
    public void IsTrustedMirrorUrl_AcceptsAnHttpsDomain() =>
        Assert.True(UpdateFeedSources.IsTrustedMirrorUrl("https://updates.example.org/lumisense/"));
}

public sealed class UpdateFeedRaceTests
{
    private static readonly string[] Feeds = { "https://a.example/", "https://b.example/" };

    [Fact]
    public async Task CheckAsync_PrefersUpToDateOverAFailedFeed()
    {
        var update = VelopackProbeResult.Failed("stub", UpdateFailureKind.Network);
        UpdateFeedRaceResult result = await UpdateFeedRace.CheckAsync(
            Feeds,
            (url, token) => url == Feeds[0]
                ? Task.FromResult(VelopackProbeResult.UpToDate())
                : Task.FromResult(update),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        // Оба ответа без обновления: побеждает «актуально», ошибка не перебивает его.
        Assert.Equal(VelopackProbeStatus.UpToDate, result.Result.Status);
        Assert.Equal(Feeds[0], result.FeedUrl);
    }

    [Fact]
    public async Task CheckAsync_ReturnsOfficialFailureWhenEveryFeedFails()
    {
        UpdateFeedRaceResult result = await UpdateFeedRace.CheckAsync(
            Feeds,
            (url, token) => Task.FromResult(VelopackProbeResult.Failed(url, UpdateFailureKind.Network)),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(VelopackProbeStatus.Error, result.Result.Status);
        Assert.Equal(Feeds[0], result.FeedUrl);
    }

    [Fact]
    public async Task CheckAsync_TurnsAnExceptionIntoAFailure()
    {
        UpdateFeedRaceResult result = await UpdateFeedRace.CheckAsync(
            Feeds,
            (url, token) => url == Feeds[0]
                ? throw new System.Net.Http.HttpRequestException("blocked")
                : Task.FromResult(VelopackProbeResult.UpToDate()),
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        Assert.Equal(VelopackProbeStatus.UpToDate, result.Result.Status);
        Assert.Equal(Feeds[1], result.FeedUrl);
    }

    [Fact]
    public async Task CheckAsync_UsesTheOnlyFeedDirectly()
    {
        UpdateFeedRaceResult result = await UpdateFeedRace.CheckAsync(
            new[] { Feeds[0] },
            (url, token) => Task.FromResult(VelopackProbeResult.UpToDate()),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(Feeds[0], result.FeedUrl);
    }
}

public sealed class StartupUpdateCheckTests
{
    private static UpdateCheckResult Error(UpdateFailureKind kind = UpdateFailureKind.Network) =>
        new() { Status = UpdateCheckStatus.Error, FailureKind = kind };

    [Fact]
    public async Task RunAsync_RetriesAfterEachConfiguredDelayUntilItSucceeds()
    {
        int attempts = 0;
        var waits = new List<TimeSpan>();

        UpdateCheckResult result = await StartupUpdateCheck.RunAsync(
            token => Task.FromResult(++attempts < 3 ? Error() : new UpdateCheckResult { Status = UpdateCheckStatus.UpToDate }),
            new[] { TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5) },
            (wait, token) =>
            {
                waits.Add(wait);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Equal(3, attempts);
        Assert.Equal(new[] { TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5) }, waits);
    }

    [Fact]
    public async Task RunAsync_GivesUpAfterTheLastRetryAndReturnsTheError()
    {
        int attempts = 0;

        UpdateCheckResult result = await StartupUpdateCheck.RunAsync(
            token =>
            {
                attempts++;
                return Task.FromResult(Error());
            },
            new[] { TimeSpan.FromSeconds(1) },
            (wait, token) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.Error, result.Status);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task RunAsync_DoesNotRetryAMissingChecksum()
    {
        int attempts = 0;

        await StartupUpdateCheck.RunAsync(
            token =>
            {
                attempts++;
                return Task.FromResult(Error(UpdateFailureKind.MissingInstallerChecksum));
            },
            new[] { TimeSpan.FromSeconds(1) },
            (wait, token) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(1, attempts);
    }
}

public sealed class UpdateCheckHealthTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ShouldShowFailureHint_WaitsAWeekOfFailures()
    {
        Assert.False(UpdateCheckHealth.ShouldShowFailureHint(Now, Now.AddDays(-6), null));
        Assert.True(UpdateCheckHealth.ShouldShowFailureHint(Now, Now.AddDays(-7), null));
        Assert.False(UpdateCheckHealth.ShouldShowFailureHint(Now, null, null));
    }

    [Fact]
    public void ShouldShowFailureHint_ShowsAtMostOncePerDay()
    {
        Assert.False(UpdateCheckHealth.ShouldShowFailureHint(Now, Now.AddDays(-30), Now.AddHours(-23)));
        Assert.True(UpdateCheckHealth.ShouldShowFailureHint(Now, Now.AddDays(-30), Now.AddHours(-25)));
    }

    [Fact]
    public void Parse_RoundTripsFormat()
    {
        Assert.Equal(Now, UpdateCheckHealth.Parse(UpdateCheckHealth.Format(Now)));
        Assert.Null(UpdateCheckHealth.Parse("garbage"));
        Assert.Null(UpdateCheckHealth.Parse(null));
    }
}
