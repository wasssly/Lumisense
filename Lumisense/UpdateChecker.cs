using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Velopack;

namespace AudioPlayer;

public enum UpdateCheckStatus { UpdateAvailable, UpToDate, Error }

// Способ установки выбран строго по фактической модели установки, а не по версии приложения.
// Старый Inno Setup продолжает работать с полным EXE и SHA-256; только Velopack-managed
// установка может скачивать full/delta .nupkg через UpdateManager.
public enum UpdateDeliveryKind { LegacyInnoSetup, Velopack }

// Причина ошибки передаётся из сетевого слоя без локализованного текста. UI формирует
// понятное RU/EN-сообщение в UpdateFailureExperience, а TechnicalDetail остаётся для журнала.
public enum UpdateFailureKind { None, HttpStatus, InvalidResponse, MissingInstallerChecksum, Network }

// Результат обращения к GitHub — см. UpdateChecker.CheckAsync
public sealed class UpdateCheckResult
{
    public UpdateCheckStatus Status { get; init; }
    public UpdateDeliveryKind DeliveryKind { get; init; } = UpdateDeliveryKind.LegacyInnoSetup;
    public string CurrentVersion { get; init; } = "";
    public string? LatestVersion { get; init; }

    // Прямая ссылка на .exe-ассет релиза (сам установщик Lumisense_Setup.exe — см.
    // Installer/Lumisense.iss) — то, что реально скачивается и запускается.
    public string? DownloadUrl { get; init; }

    // SHA-256 из поля digest у GitHub Release asset. Установщик не запускается, пока
    // вычисленная при скачивании сумма не совпадёт с опубликованной.
    public string? InstallerSha256 { get; init; }

    // Доступно только в migration-релизах: legacy EXE-копия может по явному согласию
    // пользователя скачать этот MSI и перейти на Velopack. Никакой автозамены нет.
    public string? MsiDownloadUrl { get; init; }
    public string? MsiSha256 { get; init; }

    // Страница релиза на GitHub — на неё ведёт "Подробнее" в диалоге.
    public string? ReleaseNotesUrl { get; init; }

    // Текст описания релиза (Markdown как есть, без рендеринга) — короткая выжимка
    // показывается в диалоге, полностью — по ссылке ReleaseNotesUrl.
    public string? ReleaseNotes { get; init; }

    // Заполняется только для настоящей Velopack-установки. Не сериализуется и не используется
    // legacy Inno Setup-кодом, поэтому старые сценарии отката/переустановки остаются прежними.
    public UpdateInfo? VelopackUpdate { get; init; }

    public UpdateFailureKind FailureKind { get; init; }
    public int? HttpStatusCode { get; init; }
    public string? TechnicalDetail { get; init; }
}

// Результат загрузки списка опубликованных релизов для настроек и Changelog.
public sealed class ReleaseListResult
{
    public List<ReleaseListItem> Releases { get; init; } = new();
    public UpdateFailureKind FailureKind { get; init; }
    public int? HttpStatusCode { get; init; }
    public string? TechnicalDetail { get; init; }
    public bool IsSuccess => FailureKind == UpdateFailureKind.None;
}

// Подробность скачивания установщика для отображения в окне обновления.
public sealed class DownloadProgressInfo
{
    public long BytesReceived { get; init; }
    public long? TotalBytes { get; init; }
    public double Fraction { get; init; }
    public double BytesPerSecond { get; init; }
}

// Одна запись из полного списка релизов для страницы «Все версии».
// В EXE-only схеме хранится только ссылка на Inno Setup установщик.
public sealed class ReleaseListItem
{
    public string Version { get; init; } = "";
    public string? ExeDownloadUrl { get; init; }
    public string? ExeSha256 { get; init; }
    public string? ReleaseNotesUrl { get; init; }
    public string? ReleaseNotes { get; init; }
    public System.DateTimeOffset? PublishedAt { get; init; }
    public bool IsPrerelease { get; init; }
}

// Проверка обновлений через GitHub Releases API без токена — публичных запросов заведомо
// мало для лимита 60/час на IP. Используется и при тихой проверке на старте, и по кнопке
// в настройках.
//
// Ожидает, что релиз содержит один .exe-ассет — установщик Lumisense_Setup.exe (Installer/
// Lumisense.iss). Inno Setup сам обнаружит уже установленную копию и обновит её на месте,
// отдельный "автообновляльщик" не нужен.
//
// ВАЖНО: RepoOwner/RepoName должны указывать на реальный репозиторий с релизами.
public static class UpdateChecker
{
    private const string RepoOwner = "wasssly";
    private const string RepoName = "Lumisense";

    private const long MaxInstallerBytes = 250L * 1024 * 1024;
    private static readonly HashSet<string> TrustedDownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "objects.githubusercontent.com", "gh-proxy.org", "v4.gh-proxy.org",
        "v6.gh-proxy.org", "cdn.gh-proxy.org"
    };

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = System.TimeSpan.FromSeconds(10) };
        // GitHub API отклоняет запросы без User-Agent
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Lumisense-AudioPlayer", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        string currentVersion = GetCurrentVersion();

        // Никакой эвристики по номеру версии: delta-путь допустим лишь если UpdateManager
        // подтверждает реальную Velopack-установку с package store и Update.exe.
        var velopack = new VelopackUpdateService();
        if (velopack.IsManagedInstall)
        {
            VelopackProbeResult probe = await velopack.CheckAsync(ct);
            return probe.Status switch
            {
                VelopackProbeStatus.UpdateAvailable when probe.Update is not null => new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.UpdateAvailable,
                    DeliveryKind = UpdateDeliveryKind.Velopack,
                    CurrentVersion = currentVersion,
                    LatestVersion = probe.Update.TargetFullRelease.Version.ToString(),
                    ReleaseNotes = probe.Update.TargetFullRelease.NotesMarkdown,
                    ReleaseNotesUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases/tag/v{probe.Update.TargetFullRelease.Version}",
                    VelopackUpdate = probe.Update
                },
                VelopackProbeStatus.UpToDate => new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.UpToDate,
                    DeliveryKind = UpdateDeliveryKind.Velopack,
                    CurrentVersion = currentVersion
                },
                _ => new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.Error,
                    DeliveryKind = UpdateDeliveryKind.Velopack,
                    CurrentVersion = currentVersion,
                    FailureKind = UpdateFailureKind.Network,
                    TechnicalDetail = probe.TechnicalDetail
                }
            };
        }

        try
        {
            string url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            using var response = await Http.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.Error,
                    CurrentVersion = currentVersion,
                    FailureKind = UpdateFailureKind.HttpStatus,
                    HttpStatusCode = (int)response.StatusCode
                };
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            string tagName = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
            string latestVersion = tagName.TrimStart('v', 'V');

            var installer = FindInstallerAsset(root);
            var msi = FindMsiAsset(root);
            string? downloadUrl = installer.DownloadUrl;
            string? installerSha256 = installer.Sha256;

            string? notes = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() : null;
            string? htmlUrl = root.TryGetProperty("html_url", out var htmlEl) ? htmlEl.GetString() : null;

            bool hasNewer = !string.IsNullOrEmpty(latestVersion) && IsNewer(latestVersion, currentVersion);

            if (hasNewer && downloadUrl != null && installerSha256 == null)
            {
                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.Error,
                    CurrentVersion = currentVersion,
                    LatestVersion = latestVersion,
                    FailureKind = UpdateFailureKind.MissingInstallerChecksum
                };
            }

            return new UpdateCheckResult
            {
                Status = hasNewer && downloadUrl != null && installerSha256 != null ? UpdateCheckStatus.UpdateAvailable : UpdateCheckStatus.UpToDate,
                CurrentVersion = currentVersion,
                LatestVersion = string.IsNullOrEmpty(latestVersion) ? null : latestVersion,
                DownloadUrl = downloadUrl,
                InstallerSha256 = installerSha256,
                MsiDownloadUrl = msi.DownloadUrl,
                MsiSha256 = msi.Sha256,
                ReleaseNotesUrl = htmlUrl,
                ReleaseNotes = notes
            };
        }
        catch (System.Exception ex)
        {
            // Нет сети, таймаут, репозиторий/релиз ещё не существует и т.п. — не критично.
            // Техническая подробность нужна для диагностики, но в UI показывается локализованная
            // причина UpdateFailureKind, а не текст исключения.
            Logger.Warn($"Не удалось проверить обновление: {ex.Message}");
            return new UpdateCheckResult
            {
                Status = UpdateCheckStatus.Error,
                CurrentVersion = currentVersion,
                FailureKind = UpdateFailureKind.Network,
                TechnicalDetail = ex.Message
            };
        }
    }

    // Полный список опубликованных релизов для выбора версии и отката.
    public static async Task<ReleaseListResult> GetAllReleasesAsync(CancellationToken ct = default)
    {
        try
        {
            string url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page=100";
            using var response = await Http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return new ReleaseListResult
                {
                    FailureKind = UpdateFailureKind.HttpStatus,
                    HttpStatusCode = (int)response.StatusCode
                };

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return new ReleaseListResult { FailureKind = UpdateFailureKind.InvalidResponse };

            var releases = new List<ReleaseListItem>();
            foreach (var releaseEl in doc.RootElement.EnumerateArray())
            {
                if (releaseEl.TryGetProperty("draft", out var draftEl) && draftEl.GetBoolean()) continue;
                string tagName = releaseEl.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
                string version = tagName.TrimStart('v', 'V');
                if (string.IsNullOrEmpty(version)) continue;

                System.DateTimeOffset? publishedAt = null;
                if (releaseEl.TryGetProperty("published_at", out var pubEl) && pubEl.ValueKind == JsonValueKind.String
                    && System.DateTimeOffset.TryParse(pubEl.GetString(), out var parsedDate))
                    publishedAt = parsedDate;

                var installer = FindInstallerAsset(releaseEl);
                releases.Add(new ReleaseListItem
                {
                    Version = version,
                    ExeDownloadUrl = installer.DownloadUrl,
                    ExeSha256 = installer.Sha256,
                    ReleaseNotesUrl = releaseEl.TryGetProperty("html_url", out var htmlEl) ? htmlEl.GetString() : null,
                    ReleaseNotes = releaseEl.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() : null,
                    PublishedAt = publishedAt,
                    IsPrerelease = releaseEl.TryGetProperty("prerelease", out var preEl) && preEl.GetBoolean(),
                });
            }

            return new ReleaseListResult { Releases = releases };
        }
        catch (System.Exception ex)
        {
            Logger.Warn($"Не удалось загрузить список релизов: {ex.Message}");
            return new ReleaseListResult
            {
                FailureKind = UpdateFailureKind.Network,
                TechnicalDetail = ex.Message
            };
        }
    }

    // В migration-релизе рядом с Inno EXE может лежать Velopack Setup.exe. Legacy путь
    // намеренно принимает только привычный Lumisense_Setup.exe, а не первый попавшийся .exe.
    private static (string? DownloadUrl, string? Sha256) FindInstallerAsset(JsonElement releaseRoot) =>
        FindAssetByExactName(releaseRoot, "Lumisense_Setup.exe");

    // Принимаем только ожидаемый MSI из release workflow, чтобы случайный или будущий
    // дополнительный .msi asset не мог быть предложен legacy-копии как путь миграции.
    private static (string? DownloadUrl, string? Sha256) FindMsiAsset(JsonElement releaseRoot) =>
        FindAssetByExactName(releaseRoot, "Wasssly.Lumisense-win.msi");

    private static (string? DownloadUrl, string? Sha256) FindAssetByExactName(JsonElement releaseRoot, string expectedName)
    {
        if (!releaseRoot.TryGetProperty("assets", out var assetsEl) || assetsEl.ValueKind != JsonValueKind.Array)
            return (null, null);

        var asset = assetsEl.EnumerateArray().FirstOrDefault(a =>
            a.TryGetProperty("name", out var n) &&
            string.Equals(n.GetString(), expectedName, StringComparison.OrdinalIgnoreCase));
        return asset.ValueKind == JsonValueKind.Object ? GetAssetDownload(asset) : (null, null);
    }

    private static (string? DownloadUrl, string? Sha256) GetAssetDownload(JsonElement asset)
    {
        string? downloadUrl = asset.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() : null;
        return (downloadUrl, GetAssetSha256(asset));
    }

    private static string? GetAssetSha256(JsonElement asset)
    {
        if (!asset.TryGetProperty("digest", out var digestEl) || digestEl.ValueKind != JsonValueKind.String ||
            !TryParseSha256(digestEl.GetString(), out var hash))
            return null;

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // Версия берётся из assembly metadata, которую CI сверяет с тегом релиза. Это устраняет
    // прежнюю зависимость update-механизма от эвристически вычисляемой версии changelog.
    public static string GetCurrentVersion()
    {
        var assembly = typeof(UpdateChecker).Assembly;
        string? informationalVersion = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()
            ?.InformationalVersion;

        if (SemanticVersion.TryParse(informationalVersion, out var semanticVersion))
            return semanticVersion.ToString();

        var assemblyVersion = assembly.GetName().Version;
        if (assemblyVersion != null && assemblyVersion.Major >= 0 && assemblyVersion.Minor >= 0)
        {
            int patch = assemblyVersion.Build >= 0 ? assemblyVersion.Build : 0;
            return $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{patch}";
        }

        // Fallback нужен только для локального запуска устаревшей/самодельной сборки без
        // assembly version; он не участвует в опубликованных CI-релизах.
        var entries = ChangelogLoader.Load();
        var current = entries.FirstOrDefault(e => e.IsCurrent) ?? entries.FirstOrDefault();
        return current?.Version ?? "0.0.0";
    }

    private static bool IsNewer(string latest, string current)
    {
        if (!SemanticVersion.TryParse(latest, out var latestVersion) ||
            !SemanticVersion.TryParse(current, out var currentVersion))
        {
            Logger.Warn($"Пропущена проверка обновления с недопустимой SemVer-версией: latest='{latest}', current='{current}'.");
            return false;
        }

        return latestVersion.CompareTo(currentVersion) > 0;
    }

    // gh-proxy — сторонний прокси для github.com/githubusercontent.com на случай, если сам
    // GitHub недоступен напрямую или скачивается медленно. Домены — разные точки входа одного
    // сервиса, какая быстрее зависит от провайдера и региона, поэтому даём выбрать самому.
    public static readonly (string Key, string DisplayName)[] DownloadSources =
    {
        ("GitHub", "GitHub (напрямую)"),
        ("GhProxy", "gh-proxy.org (зеркало)"),
        ("GhProxyV4", "v4.gh-proxy.org (зеркало, IPv4)"),
        ("GhProxyV6", "v6.gh-proxy.org (зеркало, IPv6)"),
        ("GhProxyCdn", "cdn.gh-proxy.org (зеркало, CDN)"),
    };

    // Подменяем ссылку на зеркало только перед скачиванием — CheckAsync (api.github.com) всегда
    // идёт напрямую, эти прокси обычно рассчитаны на github.com/codeload, а не на api.*
    public static string ApplyDownloadSource(string githubUrl, string source) => source switch
    {
        "GhProxy" => $"https://gh-proxy.org/{githubUrl}",
        "GhProxyV4" => $"https://v4.gh-proxy.org/{githubUrl}",
        "GhProxyV6" => $"https://v6.gh-proxy.org/{githubUrl}",
        "GhProxyCdn" => $"https://cdn.gh-proxy.org/{githubUrl}",
        _ => githubUrl
    };

    // Скачивает установщик во временную папку и сообщает размер, процент и скорость.
    public static Task<string> DownloadInstallerAsync(
        string downloadUrl,
        string expectedSha256,
        System.IProgress<DownloadProgressInfo>? progress,
        CancellationToken ct) =>
        DownloadReleaseAssetAsync(downloadUrl, expectedSha256, ".exe", progress, ct);

    public static Task<string> DownloadMsiAsync(
        string downloadUrl,
        string expectedSha256,
        System.IProgress<DownloadProgressInfo>? progress,
        CancellationToken ct) =>
        DownloadReleaseAssetAsync(downloadUrl, expectedSha256, ".msi", progress, ct);

    private static async Task<string> DownloadReleaseAssetAsync(
        string downloadUrl,
        string expectedSha256,
        string extension,
        System.IProgress<DownloadProgressInfo>? progress,
        CancellationToken ct)
    {
        if (!TryValidateDownloadUrl(downloadUrl, out var uri))
            throw new InvalidOperationException("Источник обновления не входит в список доверенных HTTPS-адресов.");
        if (!TryParseSha256(expectedSha256, out var expectedHash))
            throw new InvalidDataException("Контрольная сумма SHA-256 установщика отсутствует или имеет недопустимый формат.");

        string tempPath = Path.Combine(Path.GetTempPath(), $"Lumisense_Update_{Guid.NewGuid():N}.part");
        bool completed = false;
        try
        {
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            if (totalBytes is > MaxInstallerBytes)
                throw new InvalidDataException("Размер установщика превышает допустимый лимит.");

            await using var httpStream = await response.Content.ReadAsStreamAsync(ct);
            await using (var fileStream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                long readTotal = 0;
                long lastReportBytes = 0;
                var lastReportAt = stopwatch.Elapsed;
                int read;

                while ((read = await httpStream.ReadAsync(buffer, ct)) > 0)
                {
                    readTotal += read;
                    sha256.AppendData(buffer, 0, read);
                    if (readTotal > MaxInstallerBytes)
                        throw new InvalidDataException("Размер установщика превышает допустимый лимит.");

                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);

                    var now = stopwatch.Elapsed;
                    if ((now - lastReportAt).TotalMilliseconds >= 150 || (totalBytes is > 0 && readTotal == totalBytes.Value))
                    {
                        double seconds = (now - lastReportAt).TotalSeconds;
                        double speed = seconds > 0 ? (readTotal - lastReportBytes) / seconds : 0;
                        progress?.Report(new DownloadProgressInfo
                        {
                            BytesReceived = readTotal,
                            TotalBytes = totalBytes,
                            Fraction = totalBytes is > 0 ? (double)readTotal / totalBytes.Value : 0,
                            BytesPerSecond = speed
                        });
                        lastReportBytes = readTotal;
                        lastReportAt = now;
                    }
                }

                progress?.Report(new DownloadProgressInfo
                {
                    BytesReceived = readTotal,
                    TotalBytes = totalBytes,
                    Fraction = totalBytes is > 0 ? 1 : 0,
                    BytesPerSecond = 0
                });
            }

            if (!CryptographicOperations.FixedTimeEquals(sha256.GetHashAndReset(), expectedHash))
                throw new InvalidDataException("Контрольная сумма скачанного установщика не совпадает с опубликованной в GitHub Release.");

            string finalPath = Path.ChangeExtension(tempPath, extension);
            File.Move(tempPath, finalPath);
            completed = true;
            return finalPath;
        }
        finally
        {
            if (!completed)
                TryDelete(tempPath);
        }
    }

    internal static bool IsTrustedReleaseNotesUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
               uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
               uri.AbsolutePath.StartsWith("/wasssly/Lumisense/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryValidateDownloadUrl(string value, out Uri uri)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri!) || uri.Scheme != Uri.UriSchemeHttps ||
            !TrustedDownloadHosts.Contains(uri.Host))
            return false;

        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return uri.AbsolutePath.StartsWith("/wasssly/Lumisense/releases/download/", StringComparison.OrdinalIgnoreCase);

        return uri.Host.EndsWith("gh-proxy.org", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseSha256(string? value, out byte[] hash)
    {
        hash = Array.Empty<byte>();
        const string prefix = "sha256:";
        if (string.IsNullOrWhiteSpace(value)) return false;

        string hex = value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? value[prefix.Length..] : value;
        if (hex.Length != 64) return false;

        try
        {
            hash = Convert.FromHexString(hex);
            return hash.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* cleanup is best-effort after cancellation/failure */ }
    }

    // Запускает установщик через оболочку (Inno Setup сам запросит права администратора)
    // и завершает текущий процесс, чтобы установщик мог перезаписать используемые им файлы.
    // Перед запуском файл проверяется повторно: путь мог быть изменён между скачиванием и стартом.
    public static void LaunchInstallerAndExit(string installerPath, string expectedSha256)
    {
        VerifyDownloadedAssetHash(installerPath, expectedSha256);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installerPath)
        {
            UseShellExecute = true
        });

        System.Windows.Application.Current.Shutdown();
    }

    // MSI PerMachine всегда требует явного подтверждения Windows/UAC. Запускаем его только
    // после нажатия пользователя в диалоге migration и повторной SHA-256-проверки файла.
    public static void LaunchMsiAndExit(string msiPath, string expectedSha256)
    {
        VerifyDownloadedAssetHash(msiPath, expectedSha256);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("msiexec.exe", $"/i \"{msiPath}\"")
        {
            UseShellExecute = true,
            Verb = "runas"
        });

        System.Windows.Application.Current.Shutdown();
    }

    private static void VerifyDownloadedAssetHash(string assetPath, string expectedSha256)
    {
        if (!File.Exists(assetPath) || !TryParseSha256(expectedSha256, out var expectedHash))
            throw new InvalidDataException("Загруженный установщик не прошёл проверку SHA-256.");

        using var stream = File.OpenRead(assetPath);
        byte[] actualHash = SHA256.HashData(stream);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            throw new InvalidDataException("Загруженный установщик не прошёл проверку SHA-256.");
    }
}
