namespace Lumisense;

// Откуда читается feed обновлений Velopack (releases.win.json). Feed определяет, какие пакеты и с какими хешами будут
// скачаны, поэтому в список попадают только официальный GitHub и зеркала, которыми управляет владелец проекта.
internal static class UpdateFeedSources
{
    // Зеркала feed на собственном домене (например, S3-совместимое хранилище или VPS), https и тот же каталог файлов, что у
    // GitHub Release. Пока пусто: проверка идёт только по GitHub. Публичные прокси вроде gh-proxy сюда добавлять нельзя
    // (IsTrustedMirrorUrl их не пропустит): сторонний прокси мог бы подменить и feed, и пакеты.
    internal static readonly string[] TrustedMirrorFeedUrls = [];

    // Подстроки хостов публичных прокси GitHub; feed с них не принимается, даже если адрес случайно попал в список.
    private static readonly string[] PublicProxyMarkers = ["gh-proxy", "ghproxy", "ghfast", "kkgithub", "gitclone"];

    public static IReadOnlyList<string> FeedUrls { get; } =
        Build(VelopackUpdateService.PublicReleaseFeedUrl, TrustedMirrorFeedUrls);

    // Официальный адрес всегда первый: при равных результатах он главнее зеркал.
    internal static IReadOnlyList<string> Build(string officialUrl, IEnumerable<string> mirrorUrls)
    {
        var urls = new List<string> { WithTrailingSlash(officialUrl) };
        foreach (string mirror in mirrorUrls)
        {
            string normalized = WithTrailingSlash(mirror);
            if (IsTrustedMirrorUrl(normalized) && !urls.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                urls.Add(normalized);
        }

        return urls;
    }

    internal static bool IsTrustedMirrorUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || uri.IsLoopback) return false;
        if (uri.HostNameType != UriHostNameType.Dns) return false;

        return !PublicProxyMarkers.Any(marker => uri.Host.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static string WithTrailingSlash(string url) => url.EndsWith('/') ? url : url + "/";
}
