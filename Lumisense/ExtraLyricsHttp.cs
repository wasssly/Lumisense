using System.IO;
using System.Net.Http;
using System.Text;

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

        // Лимит проверяется до чтения всего ответа: иначе огромное тело сначала целиком попало бы в память.
        if (response.Content.Headers.ContentLength > MaxResponseCharacters * 4L) return null;

        await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var body = new StringBuilder();
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            body.Append(buffer, 0, read);
            if (body.Length > MaxResponseCharacters) return null;
        }

        return body.ToString();
    }

    public static string BuildTitleQuery(string title, string artist) =>
        string.IsNullOrWhiteSpace(artist) || artist == "—" ? title.Trim() : $"{title} {artist}".Trim();

    public static string? CleanArtist(string artist) =>
        string.IsNullOrWhiteSpace(artist) || artist == "—" ? null : artist.Trim();
}
