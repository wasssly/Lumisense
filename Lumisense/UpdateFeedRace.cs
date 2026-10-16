namespace Lumisense;

internal readonly record struct UpdateFeedRaceResult(string FeedUrl, VelopackProbeResult Result);

// Проверяет обновление по всем адресам feed одновременно и берёт первый содержательный ответ: у медленного или
// заблокированного источника старт не ждёт полного таймаута. Свежее побеждает: если источник ответил «актуально», другим
// даётся короткая отсрочка, чтобы устаревшее зеркало не скрыло новую версию.
internal static class UpdateFeedRace
{
    public static readonly TimeSpan DefaultUpToDateGrace = TimeSpan.FromSeconds(3);

    public static async Task<UpdateFeedRaceResult> CheckAsync(
        IReadOnlyList<string> feedUrls,
        Func<string, CancellationToken, Task<VelopackProbeResult>> probe,
        TimeSpan upToDateGrace,
        CancellationToken cancellationToken)
    {
        if (feedUrls.Count == 0) throw new ArgumentException("Нужен хотя бы один адрес feed.", nameof(feedUrls));
        if (feedUrls.Count == 1) return await RunAsync(feedUrls[0], probe, cancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = feedUrls.Select(url => RunAsync(url, probe, cts.Token)).ToList();
        var failures = new List<UpdateFeedRaceResult>();
        UpdateFeedRaceResult? upToDate = null;
        Task? grace = null;

        while (pending.Count > 0)
        {
            var waiting = new List<Task>(pending);
            if (grace is not null) waiting.Add(grace);

            Task done = await Task.WhenAny(waiting);
            if (ReferenceEquals(done, grace)) break;

            var finished = (Task<UpdateFeedRaceResult>)done;
            pending.Remove(finished);
            UpdateFeedRaceResult result = finished.Result;

            switch (result.Result.Status)
            {
                case VelopackProbeStatus.UpdateAvailable:
                    cts.Cancel();
                    return result;
                case VelopackProbeStatus.UpToDate:
                    upToDate ??= result;
                    grace ??= Task.Delay(upToDateGrace, cts.Token);
                    break;
                default:
                    failures.Add(result);
                    break;
            }
        }

        cts.Cancel();
        if (upToDate is { } actual) return actual;

        // Все источники не ответили: показываем ошибку официального адреса, он первый в списке.
        return failures.FirstOrDefault(failure => failure.FeedUrl == feedUrls[0]) is { FeedUrl: not null } official
            ? official
            : failures[0];
    }

    private static async Task<UpdateFeedRaceResult> RunAsync(
        string feedUrl,
        Func<string, CancellationToken, Task<VelopackProbeResult>> probe,
        CancellationToken cancellationToken)
    {
        try
        {
            return new UpdateFeedRaceResult(feedUrl, await probe(feedUrl, cancellationToken));
        }
        catch (Exception ex)
        {
            return new UpdateFeedRaceResult(feedUrl, VelopackProbeResult.Failed(ex.Message, VelopackUpdateService.ClassifyFailure(ex)));
        }
    }
}
