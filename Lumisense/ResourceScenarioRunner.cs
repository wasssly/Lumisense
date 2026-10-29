namespace Lumisense;

internal enum ScenarioWindow { Main, MiniPlayer, Tray, NowPlaying }

internal sealed record ResourceScenario(string Key, string Title, bool Playing, ScenarioWindow Window);

internal static class ResourceScenarios
{
    // Те же сценарии, что в tools/Measure-LumisenseRuntime.ps1 и docs/PERFORMANCE_BASELINE.md.
    public static IReadOnlyList<ResourceScenario> All { get; } = new[]
    {
        new ResourceScenario("main-playing", "Играет, главное окно открыто", true, ScenarioWindow.Main),
        new ResourceScenario("tray-playing", "Играет, окно скрыто в трее", true, ScenarioWindow.Tray),
        new ResourceScenario("mini-playing", "Играет, мини-плеер", true, ScenarioWindow.MiniPlayer),
        new ResourceScenario("nowplaying-playing", "Играет, открыто окно «Сейчас играет»", true, ScenarioWindow.NowPlaying),
        new ResourceScenario("main-paused", "Пауза, главное окно открыто", false, ScenarioWindow.Main),
        new ResourceScenario("tray-paused", "Пауза, окно скрыто в трее", false, ScenarioWindow.Tray),
        new ResourceScenario("mini-paused", "Пауза, мини-плеер", false, ScenarioWindow.MiniPlayer)
    };
}

internal sealed record ScenarioResult(
    bool Started,
    IReadOnlyList<ResourceSample> Samples,
    int InputDetections,
    string? FailureReason);

// Сценарный замер: плеер сам ставит воспроизведение и режим окна, ждёт, пока всё устоится, замеряет минуту и возвращает всё как было.
internal static class ResourceScenarioRunner
{
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan MeasureTime = TimeSpan.FromSeconds(60);

    private sealed record SavedState(bool Playing, bool Mini, bool MainVisible, bool NowPlaying);

    public static async Task<ScenarioResult> RunAsync(
        ResourceScenario scenario,
        IDiagnosticsHost host,
        IProgress<(double Fraction, ResourceSample Last)>? progress,
        CancellationToken token,
        TimeSpan? settleTime = null,
        TimeSpan? measureTime = null)
    {
        if (!host.HasTrack)
            return new ScenarioResult(false, Array.Empty<ResourceSample>(), 0, LocalizationService.Translate("Сначала откройте трек: без трека замерять нечего."));

        var saved = new SavedState(host.IsPlaying, host.IsMiniMode, host.IsMainWindowVisible, host.IsNowPlayingOpen);
        try
        {
            Apply(scenario, host);

            // Окна и воспроизведение устаиваются: без прогрева замер захватил бы сам переход.
            await Task.Delay(settleTime ?? SettleTime, token);

            var session = new ResourceSession
            {
                Title = scenario.Title,
                Duration = measureTime ?? MeasureTime,
                SampleInterval = TimeSpan.FromSeconds(1),
                LoadMode = true,
                TrackUserInput = true
            };
            IReadOnlyList<ResourceSample> samples = await session.RunAsync(null, progress, token);
            return new ScenarioResult(true, samples, session.InputDetections, null);
        }
        finally
        {
            Restore(saved, host);
        }
    }

    private static void Apply(ResourceScenario scenario, IDiagnosticsHost host)
    {
        host.SetPlaying(scenario.Playing);
        switch (scenario.Window)
        {
            case ScenarioWindow.Main:
                host.SetNowPlayingOpen(false);
                host.ShowMainWindow();
                break;
            case ScenarioWindow.MiniPlayer:
                host.SetNowPlayingOpen(false);
                host.ShowMiniPlayer();
                break;
            case ScenarioWindow.Tray:
                host.SetNowPlayingOpen(false);
                host.HideToTray();
                break;
            default:
                host.ShowMainWindow();
                host.SetNowPlayingOpen(true);
                break;
        }
    }

    private static void Restore(SavedState saved, IDiagnosticsHost host)
    {
        host.SetNowPlayingOpen(saved.NowPlaying);
        if (saved.Mini)
            host.ShowMiniPlayer();
        else if (saved.MainVisible)
            host.ShowMainWindow();
        else
            host.HideToTray();

        host.SetPlaying(saved.Playing);
    }
}
