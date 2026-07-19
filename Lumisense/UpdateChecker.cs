using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AudioPlayer;

public enum UpdateCheckStatus { UpdateAvailable, UpToDate, Error }

/// <summary>Результат обращения к GitHub — см. UpdateChecker.CheckAsync.</summary>
public sealed class UpdateCheckResult
{
    public UpdateCheckStatus Status { get; init; }
    public string CurrentVersion { get; init; } = "";
    public string? LatestVersion { get; init; }

    // Прямая ссылка на .exe-ассет релиза (сам установщик Lumisense_Setup.exe — см.
    // Installer/Lumisense.iss) — то, что реально скачивается и запускается.
    public string? DownloadUrl { get; init; }

    // Страница релиза на GitHub — на неё ведёт "Подробнее" в диалоге.
    public string? ReleaseNotesUrl { get; init; }

    // Текст описания релиза (Markdown как есть, без рендеринга) — короткая выжимка
    // показывается в диалоге, полностью — по ссылке ReleaseNotesUrl.
    public string? ReleaseNotes { get; init; }

    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Проверка обновлений через GitHub Releases API (без токена — обычные публичные запросы,
/// которых заведомо мало для лимита в 60/час на IP). Используется и при тихой проверке на
/// старте (см. MainWindow), и по кнопке "Проверить обновления" в настройках.
///
/// Ожидает, что релиз на GitHub содержит один .exe-ассет — тот самый установщик
/// Lumisense_Setup.exe, собранный Installer/Lumisense.iss. Пользователю предлагается
/// скачать и запустить именно его: Inno Setup сам обнаружит уже установленную копию (тот же
/// AppId) и обновит её на месте, а не поставит рядом вторую — так что никакого отдельного
/// "автообновляльщика" не нужно, достаточно переиспользовать обычный установщик.
///
/// ВАЖНО: RepoOwner/RepoName нужно подставить под реальный репозиторий на GitHub, где будут
/// публиковаться релизы (Release → тег вида "v1.5.0" + прикреплённый Lumisense_Setup.exe).
/// </summary>
public static class UpdateChecker
{
    private const string RepoOwner = "wasssly";
    private const string RepoName = "Lumisense";

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
                    ErrorMessage = $"GitHub вернул код {(int)response.StatusCode}"
                };
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            string tagName = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
            string latestVersion = tagName.TrimStart('v', 'V');

            string? downloadUrl = null;
            if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
            {
                var exeAsset = assetsEl.EnumerateArray().FirstOrDefault(a =>
                    a.TryGetProperty("name", out var n) &&
                    (n.GetString() ?? "").EndsWith(".exe", System.StringComparison.OrdinalIgnoreCase));

                if (exeAsset.ValueKind == JsonValueKind.Object &&
                    exeAsset.TryGetProperty("browser_download_url", out var urlEl))
                {
                    downloadUrl = urlEl.GetString();
                }
            }

            string? notes = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() : null;
            string? htmlUrl = root.TryGetProperty("html_url", out var htmlEl) ? htmlEl.GetString() : null;

            bool hasNewer = !string.IsNullOrEmpty(latestVersion) && IsNewer(latestVersion, currentVersion);

            return new UpdateCheckResult
            {
                Status = hasNewer && downloadUrl != null ? UpdateCheckStatus.UpdateAvailable : UpdateCheckStatus.UpToDate,
                CurrentVersion = currentVersion,
                LatestVersion = string.IsNullOrEmpty(latestVersion) ? null : latestVersion,
                DownloadUrl = downloadUrl,
                ReleaseNotesUrl = htmlUrl,
                ReleaseNotes = notes
            };
        }
        catch (System.Exception ex)
        {
            // Нет сети, таймаут, репозиторий/релиз ещё не существует и т.п. — не критично,
            // просто молча (при тихой проверке на старте) или с сообщением (по кнопке)
            // сообщаем, что проверить не удалось.
            return new UpdateCheckResult
            {
                Status = UpdateCheckStatus.Error,
                CurrentVersion = currentVersion,
                ErrorMessage = ex.Message
            };
        }
    }

    // Версия программы берётся из того же changelog.json, что и карточка "О программе" в
    // настройках (см. SettingsWindow.RefreshAppVersionText) — единственное место, где она
    // задаётся, чтобы номер нигде не мог разойтись.
    private static string GetCurrentVersion()
    {
        var entries = ChangelogLoader.Load();
        var current = entries.FirstOrDefault(e => e.IsCurrent) ?? entries.FirstOrDefault();
        return current?.Version ?? "0.0.0";
    }

    private static bool IsNewer(string latest, string current)
    {
        if (System.Version.TryParse(NormalizeForVersion(latest), out var lv) &&
            System.Version.TryParse(NormalizeForVersion(current), out var cv))
        {
            return lv > cv;
        }

        // Не удалось распарсить как X.Y.Z (например, тег вида "beta") — на всякий случай не
        // считаем это обновлением молча, но и не ломаемся: просто сравниваем как строки.
        return !string.Equals(latest, current, System.StringComparison.OrdinalIgnoreCase);
    }

    // System.Version требует минимум два компонента ("major.minor") — на случай, если где-то
    // указана всего одна цифра версии.
    private static string NormalizeForVersion(string v)
    {
        var parts = v.Split('.');
        return parts.Length switch
        {
            0 => "0.0",
            1 => $"{v}.0",
            _ => v
        };
    }

    /// <summary>Скачивает установщик во временную папку, докладывая прогресс от 0 до 1.</summary>
    public static async Task<string> DownloadInstallerAsync(string downloadUrl, System.IProgress<double>? progress, CancellationToken ct)
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "Lumisense_Setup.exe");

        using var response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength;

        await using var httpStream = await response.Content.ReadAsStreamAsync(ct);
        await using (var fileStream = File.Create(tempPath))
        {
            var buffer = new byte[81920];
            long readTotal = 0;
            int read;
            while ((read = await httpStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                readTotal += read;
                if (totalBytes is > 0)
                    progress?.Report((double)readTotal / totalBytes.Value);
            }
        }

        return tempPath;
    }

    /// <summary>
    /// Запускает скачанный установщик (через оболочку — Inno Setup сам запросит права
    /// администратора, см. PrivilegesRequired=admin в Lumisense.iss) и завершает текущий
    /// процесс плеера, чтобы установщик мог перезаписать файлы, которые сейчас использует
    /// запущенный Lumisense.exe.
    /// </summary>
    public static void LaunchInstallerAndExit(string installerPath)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installerPath)
        {
            UseShellExecute = true
        });

        System.Windows.Application.Current.Shutdown();
    }
}
