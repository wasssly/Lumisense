using System.Net.Http;
using System.Net.Http.Headers;

namespace AudioPlayer;

// Единый идентификатор для всех сетевых запросов LyricsService. Версия берётся из assembly
// metadata через UpdateChecker, поэтому release workflow меняет User-Agent автоматически.
internal static class LyricsNetworkIdentity
{
    private const string ProjectUrl = "https://github.com/wasssly/Lumisense";

    public static string UserAgentValue => $"Lumisense/{UpdateChecker.GetCurrentVersion()} (+{ProjectUrl})";

    public static void Apply(HttpClient client)
    {
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentValue);
    }
}
