using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Lumisense;

// Better Lyrics (api.betterlyrics.org): TTML из общего кэша; для треков вне кэша сервис отвечает 401, пустой ответ — норма.
// Работает только при включённых UseExtraLyricsSource и UseBetterLyrics: название и исполнитель уходят на сервер.
internal static class BetterLyricsSource
{
    public const string SourceName = "Better Lyrics";

    private const string Root = "https://api.betterlyrics.org";
    private static readonly HttpClient Http = ExtraLyricsHttp.CreateClient();

    public static Task<IReadOnlyList<OnlineLyricsResult>> SearchAsync(string title, string artist, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(title)) return Task.FromResult<IReadOnlyList<OnlineLyricsResult>>(Array.Empty<OnlineLyricsResult>());
        return ExtraLyricsHttp.GuardAsync(SourceName, sourceToken => LoadAsync(title.Trim(), artist, sourceToken), token);
    }

    private static async Task<IReadOnlyList<OnlineLyricsResult>> LoadAsync(string title, string artist, CancellationToken token)
    {
        string url = BuildUrl(title, artist);
        string? json = await ExtraLyricsHttp.GetTextAsync(Http, url, SourceName, token).ConfigureAwait(false);
        if (json is null) return Array.Empty<OnlineLyricsResult>();

        (string? synced, string? plain) = ParseResponse(json);
        if (synced is null && plain is null) return Array.Empty<OnlineLyricsResult>();

        string cleanArtist = ExtraLyricsHttp.CleanArtist(artist) ?? string.Empty;
        long id = -(6_000_000_000_000L + LyricifyLyricsSource.StableHash($"{title}|{cleanArtist}"));
        return new[] { new OnlineLyricsResult(id, title, cleanArtist, string.Empty, 0, plain, synced, SourceName) };
    }

    // Алиасы параметров (s, song, songName) сервер склеивает, поэтому каждый параметр передаётся один раз.
    internal static string BuildUrl(string title, string artist)
    {
        var builder = new StringBuilder(Root).Append("/getLyrics?s=").Append(Uri.EscapeDataString(title));
        string? cleanArtist = ExtraLyricsHttp.CleanArtist(artist);
        if (cleanArtist is not null) builder.Append("&a=").Append(Uri.EscapeDataString(cleanArtist));
        return builder.ToString();
    }

    // Ответ: {"ttml": "<tt ...>"}; вместо объекта допускается и сам TTML, на случай изменения формата.
    internal static (string? Synced, string? Plain) ParseResponse(string body)
    {
        string trimmed = body.TrimStart();
        if (trimmed.StartsWith('<')) return TtmlLyricsParser.Parse(trimmed);

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return TtmlLyricsParser.Parse(OnlineLyricsJson.GetString(document.RootElement, "ttml"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
