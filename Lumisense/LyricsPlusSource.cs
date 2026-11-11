using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Lumisense;

// LyricsPlus (KPoe, сервер lyricsplus.binimum.org и запасной): сводный сервис, который сам перебирает Apple Music, Musixmatch и другие
// каталоги и отдаёт JSON со строками и слогами. Плееру нужны только строки с временем, поэтому из ответа собирается LRC.
// Работает только при включённых дополнительных источниках (AppSettings.UseExtraLyricsSource и UseLyricsPlusLyrics):
// название и исполнитель трека уходят на выбранный сервер; запасной опрашивается, только если основной не дал ответа.
internal static class LyricsPlusSource
{
    public const string SourceName = "LyricsPlus";

    internal static readonly string[] Servers =
    {
        "https://lyricsplus.binimum.org",
        "https://lyricsplus.prjktla.workers.dev",
    };

    private static readonly HttpClient Http = ExtraLyricsHttp.CreateClient();

    public static Task<IReadOnlyList<OnlineLyricsResult>> SearchAsync(string title, string artist, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(title)) return Task.FromResult<IReadOnlyList<OnlineLyricsResult>>(Array.Empty<OnlineLyricsResult>());
        return ExtraLyricsHttp.GuardAsync(SourceName, sourceToken => LoadAsync(title.Trim(), artist, sourceToken), token);
    }

    private static async Task<IReadOnlyList<OnlineLyricsResult>> LoadAsync(string title, string artist, CancellationToken token)
    {
        string cleanArtist = ExtraLyricsHttp.CleanArtist(artist) ?? string.Empty;
        foreach (string server in Servers)
        {
            string? json;
            try
            {
                json = await ExtraLyricsHttp.GetTextAsync(Http, BuildUrl(server, title, artist), SourceName, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            {
                Logger.Info($"LyricsPlus ({server}) не ответил: {ex.Message}");
                continue;
            }

            if (json is null) continue;

            OnlineLyricsResult? result = ParseResponse(json, title, cleanArtist);
            if (result is not null) return new[] { result };
        }

        return Array.Empty<OnlineLyricsResult>();
    }

    internal static string BuildUrl(string server, string title, string artist)
    {
        var builder = new StringBuilder(server.TrimEnd('/')).Append("/v2/lyrics/get?title=").Append(Uri.EscapeDataString(title));
        string? cleanArtist = ExtraLyricsHttp.CleanArtist(artist);
        if (cleanArtist is not null) builder.Append("&artist=").Append(Uri.EscapeDataString(cleanArtist));
        return builder.ToString();
    }

    // Ответ: {"type":"Word"|"Line","metadata":{...},"lyrics":[{"time":мс,"duration":мс,"text":"...","syllabus":[...]}]};
    // строки иногда лежат в data.lyrics или в самом data.
    internal static OnlineLyricsResult? ParseResponse(string json, string requestedTitle, string requestedArtist)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            JsonElement lyrics = FindLyrics(root);
            if (lyrics.ValueKind != JsonValueKind.Array) return null;

            var timed = new List<(double Seconds, string Text)>();
            var plainLines = new List<string>();
            foreach (JsonElement entry in lyrics.EnumerateArray())
            {
                string text = ReadLine(entry);
                if (text.Length == 0) continue;

                plainLines.Add(text);
                double? time = ReadSeconds(entry, "time");
                if (time is not null) timed.Add((time.Value, text));
            }

            if (plainLines.Count == 0) return null;

            string? synced = null, plain = null;
            if (timed.Count >= 2 && timed.Select(line => Math.Round(line.Seconds, 2)).Distinct().Count() >= 2)
                synced = TtmlLyricsParser.BuildLrc(timed);
            else
                plain = string.Join('\n', plainLines);

            JsonElement metadata = root.TryGetProperty("metadata", out JsonElement meta) ? meta : default;
            string title = OnlineLyricsJson.GetString(metadata, "title")?.Trim() is { Length: > 0 } metaTitle ? metaTitle : requestedTitle;
            string artist = OnlineLyricsJson.GetFirstString(metadata, "artist", "artists")?.Trim() is { Length: > 0 } metaArtist ? metaArtist : requestedArtist;
            string album = OnlineLyricsJson.GetString(metadata, "album")?.Trim() ?? string.Empty;

            long id = -(4_000_000_000_000L + LyricifyLyricsSource.StableHash($"{title}|{artist}"));
            return new OnlineLyricsResult(id, title, artist, album, 0, plain, synced, SourceName);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement FindLyrics(JsonElement root)
    {
        if (root.TryGetProperty("lyrics", out JsonElement direct) && direct.ValueKind == JsonValueKind.Array) return direct;
        if (root.TryGetProperty("data", out JsonElement data))
        {
            if (data.ValueKind == JsonValueKind.Array) return data;
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("lyrics", out JsonElement nested)) return nested;
        }

        return default;
    }

    // Текст строки — поле text; если его нет, строка собирается из слогов (фоновые голоса пропускаются).
    private static string ReadLine(JsonElement entry)
    {
        string? text = OnlineLyricsJson.GetString(entry, "text");
        if (!string.IsNullOrWhiteSpace(text)) return text.Trim();

        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("syllabus", out JsonElement syllabus) || syllabus.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var builder = new StringBuilder();
        foreach (JsonElement syllable in syllabus.EnumerateArray())
        {
            if (OnlineLyricsJson.GetBool(syllable, "isBackground")) continue;
            builder.Append(OnlineLyricsJson.GetString(syllable, "text"));
        }

        return builder.ToString().Trim();
    }

    // Целое число — миллисекунды, дробное — секунды (так отдают оба варианта ответа).
    internal static double? ReadSeconds(JsonElement entry, string name)
    {
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty(name, out JsonElement value)) return null;

        double number;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetDouble(out number)) return null;
            return value.TryGetInt64(out _) ? number / 1000 : number;
        }

        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number)
            ? number / 1000
            : null;
    }
}
