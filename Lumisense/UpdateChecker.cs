using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AudioPlayer;

public enum UpdateCheckStatus { UpdateAvailable, UpToDate, Error }

// Результат обращения к GitHub — см. UpdateChecker.CheckAsync
public sealed class UpdateCheckResult
{
    public UpdateCheckStatus Status { get; init; }
    public string CurrentVersion { get; init; } = "";
    public string? LatestVersion { get; init; }

    // Прямая ссылка на ZIP-ассет релиза (например Lumisense.zip — весь плеер целиком, plus
    // Updater.exe внутри, см. подробный комментарий у ExtractUpdatePayload) — то, что реально
    // скачивается и распаковывается при обновлении. Установщик (.exe, Inno Setup) при
    // обновлении больше не используется, только при самой первой установке.
    public string? DownloadUrl { get; init; }

    // Страница релиза на GitHub — на неё ведёт "Подробнее" в диалоге.
    public string? ReleaseNotesUrl { get; init; }

    // Текст описания релиза (Markdown как есть, без рендеринга) — короткая выжимка
    // показывается в диалоге, полностью — по ссылке ReleaseNotesUrl.
    public string? ReleaseNotes { get; init; }

    public string? ErrorMessage { get; init; }
}

// Проверка обновлений через GitHub Releases API без токена — публичных запросов заведомо
// мало для лимита 60/час на IP. Используется и при тихой проверке на старте, и по кнопке
// в настройках.
//
// Ожидает, что релиз содержит ZIP-ассет (например Lumisense.zip) — архив с опубликованными
// файлами плеера (dotnet publish, self-contained win-x64) и Updater.exe внутри. При
// обнаружении обновления приложение скачивает и распаковывает этот архив во временную папку,
// затем передаёт её Updater'у — маленькому отдельному приложению (проект Updater/), которое
// дожидается закрытия плеера, копирует новые файлы поверх старой установки, запускает
// обновлённый Lumisense.exe и подчищает за собой временные файлы (см. Updater/Program.cs).
// Установщик (Installer/Lumisense.iss, Inno Setup) остаётся только для самой первой установки
// "с нуля" — во время обновления уже установленной копии он не запускается.
//
// ВАЖНО: RepoOwner/RepoName должны указывать на реальный репозиторий с релизами.
public static class UpdateChecker
{
    private const string RepoOwner = "wasssly";
    private const string RepoName = "Lumisense";

    // Имя exe-файла плеера и вспомогательного обновлятора внутри ZIP-архива релиза — должны
    // совпадать с AssemblyName в Lumisense.csproj / Updater.csproj и с тем, что реально
    // упаковывает .github/workflows/release.yml.
    public const string PlayerExeName = "Lumisense.exe";
    private const string UpdaterExeName = "Updater.exe";

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

            string? downloadUrl = FindZipAssetUrl(root);

            string? notes = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() : null;
            string? htmlUrl = root.TryGetProperty("html_url", out var htmlEl) ? htmlEl.GetString() : null;

            bool hasNewer = !string.IsNullOrEmpty(latestVersion) && IsNewer(latestVersion, currentVersion);

            if (hasNewer && downloadUrl == null)
            {
                // Новая версия по тегу есть, но в её Assets нет ни одного .zip — либо релиз
                // ещё собирается (workflow не успел прикрепить файлы), либо релиз собран
                // неправильно. Молчать тут нельзя: без этого сообщения обновление выглядело
                // бы просто как "нет обновлений", хотя на самом деле оно есть, но его нечем
                // скачать — см. требование "если ZIP отсутствует, должно отображаться понятное
                // сообщение об ошибке".
                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.Error,
                    CurrentVersion = currentVersion,
                    LatestVersion = latestVersion,
                    ErrorMessage = $"Вышла новая версия {latestVersion}, но в релизе на GitHub не найден ZIP-архив с обновлением."
                };
            }

            return new UpdateCheckResult
            {
                Status = hasNewer ? UpdateCheckStatus.UpdateAvailable : UpdateCheckStatus.UpToDate,
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

    // Ищет .zip среди Assets релиза. Если их несколько (маловероятно при том, как релиз
    // собирает workflow, но в принципе возможно для ручных релизов) — предпочитает тот, что
    // явно назван в честь приложения, иначе берёт первый попавшийся: лучше попытаться
    // обновиться не тем архивом, чем безосновательно отказаться при доступном обновлении.
    private static string? FindZipAssetUrl(JsonElement releaseRoot)
    {
        if (!releaseRoot.TryGetProperty("assets", out var assetsEl) || assetsEl.ValueKind != JsonValueKind.Array)
            return null;

        var zipAssets = assetsEl.EnumerateArray()
            .Where(a => a.TryGetProperty("name", out var n) &&
                        (n.GetString() ?? "").EndsWith(".zip", System.StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (zipAssets.Count == 0) return null;

        var best = zipAssets.FirstOrDefault(a =>
            a.TryGetProperty("name", out var n) &&
            (n.GetString() ?? "").Contains("lumisense", System.StringComparison.OrdinalIgnoreCase));

        if (best.ValueKind != JsonValueKind.Object)
            best = zipAssets[0];

        return best.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() : null;
    }

    // Версия программы берётся из того же changelog.json, что и карточка "О плеере" в
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

    // Отдельная папка на каждую попытку обновления (случайное имя) — чтобы скачивание/
    // распаковка одного обновления никогда не пересекались с остатками другого (например,
    // если предыдущая попытка не удалась и её временные файлы почему-то не подчистились).
    public static string CreateUpdateSession() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Lumisense_Update", System.Guid.NewGuid().ToString("N"))).FullName;

    // Скачивает ZIP-архив обновления во временную папку сессии, докладывая прогресс от 0 до 1
    public static async Task<string> DownloadUpdateZipAsync(string downloadUrl, string sessionDir, System.IProgress<double>? progress, CancellationToken ct)
    {
        string zipPath = Path.Combine(sessionDir, "update.zip");

        using var response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength;

        await using var httpStream = await response.Content.ReadAsStreamAsync(ct);
        await using (var fileStream = File.Create(zipPath))
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

        return zipPath;
    }

    // Распаковывает скачанный архив и возвращает путь к папке, где реально лежит
    // Lumisense.exe — т.е. к "корню" новой версии, который и нужно будет скопировать поверх
    // старой установки. Архив может быть упакован и плоско (все файлы сразу в корне ZIP), и с
    // одной оборачивающей папкой (например, если он собран как "zip -r Lumisense.zip
    // publish/") — оба варианта поддержаны без дополнительных настроек, см. FindPayloadRoot.
    public static string ExtractUpdatePayload(string zipPath, string sessionDir)
    {
        string extractDir = Path.Combine(sessionDir, "extracted");
        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

        return FindPayloadRoot(extractDir)
            ?? throw new FileNotFoundException(
                $"В скачанном архиве не найден файл {PlayerExeName} — обновление повреждено или собрано неправильно.");
    }

    // Ищет папку, содержащую PlayerExeName: сначала сам extractDir, затем (если архив был с
    // оборачивающей папкой) — его подпапки на один уровень вглубь. Глубже не ищем: реальные
    // релизы так не паковались бы, а более "умный" поиск рискует случайно найти какой-нибудь
    // левый Lumisense.exe не в том месте.
    private static string? FindPayloadRoot(string extractDir)
    {
        if (File.Exists(Path.Combine(extractDir, PlayerExeName)))
            return extractDir;

        foreach (string subDir in Directory.GetDirectories(extractDir))
        {
            if (File.Exists(Path.Combine(subDir, PlayerExeName)))
                return subDir;
        }

        return null;
    }

    // Копирует Updater.exe из распакованного обновления в отдельную временную папку вне
    // sessionDir и возвращает путь к этой копии. Так важно делать по двум причинам:
    //  1) после запуска Updater плеер удаляет всю sessionDir (архив + распакованные файлы) как
    //     временный мусор — если бы Updater исполнялся прямо оттуда, он не смог бы удалить
    //     собственную же папку, пока сам работает из неё;
    //  2) на всякий случай не полагаемся, что Updater из новой версии совместим именно с этой
    //     сессией распаковки — копия изолирована и переживёт удаление sessionDir.
    public static string PrepareUpdaterRunner(string payloadRoot)
    {
        string sourceUpdaterExe = Path.Combine(payloadRoot, UpdaterExeName);
        if (!File.Exists(sourceUpdaterExe))
            throw new FileNotFoundException(
                $"В архиве обновления отсутствует {UpdaterExeName} — обновление повреждено или собрано неправильно.");

        string runnerDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "Lumisense_Update", System.Guid.NewGuid().ToString("N") + "-updater")).FullName;

        string runnerExePath = Path.Combine(runnerDir, UpdaterExeName);
        File.Copy(sourceUpdaterExe, runnerExePath, overwrite: true);
        return runnerExePath;
    }

    // Запускает подготовленную (см. PrepareUpdaterRunner) копию Updater'а и сразу завершает
    // текущий процесс — дальше всё делает он (см. подробный комментарий в Updater/Program.cs):
    // дожидается полного выхода плеера по PID, копирует новые файлы поверх старой установки в
    // installDir, запускает обновлённый Lumisense.exe и удаляет временные файлы sessionDir.
    //
    // Verb="runas" — Updater'у нужны права администратора для записи в installDir (обычно
    // Program Files), поэтому запуск сразу запрашивает повышение через UAC (тот же диалог,
    // который раньше показывал сам установщик). Если пользователь отклонит запрос, Process.Start
    // бросит исключение — оно намеренно не проглатывается здесь, чтобы вызывающий код
    // (UpdateAvailableWindow) мог показать понятную ошибку и НЕ завершать плеер, оставив
    // пользователя с рабочим приложением вместо зависшего процесса обновления.
    public static void LaunchUpdaterAndExit(string updaterExePath, string payloadRoot, string sessionDir)
    {
        string installDir = System.AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        int playerPid = System.Environment.ProcessId;

        var startInfo = new ProcessStartInfo(updaterExePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(updaterExePath)!
        };

        startInfo.ArgumentList.Add(playerPid.ToString());
        startInfo.ArgumentList.Add(payloadRoot);
        startInfo.ArgumentList.Add(installDir);
        startInfo.ArgumentList.Add(PlayerExeName);
        startInfo.ArgumentList.Add(sessionDir);

        Process.Start(startInfo);

        System.Windows.Application.Current.Shutdown();
    }
}
