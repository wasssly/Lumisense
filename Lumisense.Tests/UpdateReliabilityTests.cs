using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Lumisense.Tests;

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
