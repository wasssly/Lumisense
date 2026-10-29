using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class ResourceScenarioRunnerTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Measure = TimeSpan.FromMilliseconds(1200);

    private sealed class FakeHost : IDiagnosticsHost
    {
        public List<string> Calls { get; } = new();
        public bool HasTrack { get; set; } = true;
        public bool IsPlaying { get; set; }
        public bool IsMiniMode { get; set; }
        public bool IsMainWindowVisible { get; set; } = true;
        public bool IsNowPlayingOpen { get; set; }

        public void SetPlaying(bool playing)
        {
            Calls.Add("play=" + playing);
            IsPlaying = playing;
        }

        public void ShowMainWindow()
        {
            Calls.Add("main");
            IsMiniMode = false;
            IsMainWindowVisible = true;
        }

        public void ShowMiniPlayer()
        {
            Calls.Add("mini");
            IsMiniMode = true;
            IsMainWindowVisible = false;
        }

        public void HideToTray()
        {
            Calls.Add("tray");
            IsMiniMode = false;
            IsMainWindowVisible = false;
        }

        public void SetNowPlayingOpen(bool open)
        {
            Calls.Add("np=" + open);
            IsNowPlayingOpen = open;
        }

        public void NextTrack() => Calls.Add("next");
    }

    private static ResourceScenario Scenario(string key) => ResourceScenarios.All.First(item => item.Key == key);

    [Fact]
    public async Task WithoutATrack_NothingIsTouched()
    {
        var host = new FakeHost { HasTrack = false };

        ScenarioResult result = await ResourceScenarioRunner.RunAsync(Scenario("main-playing"), host, null, CancellationToken.None, Settle, Measure);

        Assert.False(result.Started);
        Assert.NotNull(result.FailureReason);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task TrayScenario_AppliesTheModeAndRestoresTheOriginalState()
    {
        var host = new FakeHost { IsPlaying = false, IsMiniMode = true, IsMainWindowVisible = false };

        ScenarioResult result = await ResourceScenarioRunner.RunAsync(Scenario("tray-playing"), host, null, CancellationToken.None, Settle, Measure);

        Assert.True(result.Started);
        Assert.NotEmpty(result.Samples);
        Assert.Equal(new[] { "play=True", "np=False", "tray" }, host.Calls.Take(3));
        Assert.True(host.IsMiniMode);
        Assert.False(host.IsPlaying);
        Assert.False(host.IsNowPlayingOpen);
    }

    [Fact]
    public async Task NowPlayingScenario_OpensTheWindowAndClosesItAfterwards()
    {
        var host = new FakeHost { IsPlaying = true };

        await ResourceScenarioRunner.RunAsync(Scenario("nowplaying-playing"), host, null, CancellationToken.None, Settle, Measure);

        Assert.Contains("np=True", host.Calls);
        Assert.True(host.IsMainWindowVisible);
        Assert.False(host.IsMiniMode);
        Assert.True(host.IsPlaying);
        Assert.False(host.IsNowPlayingOpen);
    }

    [Fact]
    public async Task CancelledDuringPreparation_StillRestoresThePlayer()
    {
        var host = new FakeHost { IsPlaying = true, IsMainWindowVisible = true };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ResourceScenarioRunner.RunAsync(Scenario("tray-paused"), host, null, cts.Token, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)));

        Assert.True(host.IsPlaying);
        Assert.True(host.IsMainWindowVisible);
    }

    [Fact]
    public void ScenarioKeysAreUnique() =>
        Assert.Equal(ResourceScenarios.All.Count, ResourceScenarios.All.Select(item => item.Key).Distinct().Count());
}
