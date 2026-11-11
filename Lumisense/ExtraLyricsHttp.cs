using System.Net.Http;

namespace Lumisense;

// Общие мелочи новых источников текстов (Better Lyrics, Binimum, LyricsPlus): клиент с единым User-Agent, предел времени
// на источник и превращение любого сбоя в пустой список, чтобы основной поиск по LRCLIB не страдал.
internal static class ExtraLyricsHttp
{
    public static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(8);

    // Предел размера ответа: чужой сервер не должен заставлять приложение читать мегабайты ради одного текста.
    public const int MaxResponseCharacters = 2_000_000;

    public static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        LyricsNetworkIdentity.Apply(client);
        return client;
    }

    public static async Task<IReadOnlyList<OnlineLyricsResult>> GuardAsync(
        string sourceName,
        Func<CancellationToken, Task<IReadOnlyList<OnlineLyricsResult>>> work,
        CancellationToken userToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(userToken);
        timeout.CancelAfter(SourceTimeout);
        try
        {
            return await work(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (userToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            Logger.Info($"Источник текстов {sourceName} не ответил за {SourceTimeout.TotalSeconds:0} с");
            return Array.Empty<OnlineLyricsResult>();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Источник текстов {sourceName} недоступен: {ex.Message}");
            return Array.Empty<OnlineLyricsResult>();
        }
    }

    // GET с проверкой кода ответа: 401/404/429 у этих сервисов означают «нет текста» или «лимит», а не сбой.
    public static async Task<string?> GetTextAsync(HttpClient http, string url, string sourceName, CancellationToken token)
    {
        using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // 401 (нужен ключ) и 404 (нет текста) — обычный ответ на большинство треков, и писать его на каждую смену трека незачем.
            if (response.StatusCode is not (System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.NotFound))
                Logger.Info($"Источник текстов {sourceName}: ответ {(int)response.StatusCode}");
            return null;
        }

        string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        return body.Length > MaxResponseCharacters ? null : body;
    }

    public static string BuildTitleQuery(string title, string artist) =>
        string.IsNullOrWhiteSpace(artist) || artist == "—" ? title.Trim() : $"{title} {artist}".Trim();

    public static string? CleanArtist(string artist) =>
        string.IsNullOrWhiteSpace(artist) || artist == "—" ? null : artist.Trim();
}
