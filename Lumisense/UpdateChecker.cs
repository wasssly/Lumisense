using System.Collections.Generic;
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
    // Updater.exe внутри, см. подробный комментарий у ExtractUpdatePayload) — то, что
    // распаковывается поверх текущей установки при автообновлении через Updater.exe.
    public string? ZipDownloadUrl { get; init; }

    // Прямая ссылка на .exe-установщик релиза (Inno Setup) — раньше использовался только для
    // самой первой установки, теперь им можно установить (или переустановить/откатить) и через
    // UpdateAvailableWindow: часть релизов может не публиковать ZIP вовсе, а для кого-то
    // обычный установщик просто привычнее автообновления. См. UpdateAvailableWindow —
    // выбор между этими двумя способами показывается, только если у релиза есть оба ассета.
    public string? ExeDownloadUrl { get; init; }

    // Страница релиза на GitHub — на неё ведёт "Подробнее" в диалоге.
    public string? ReleaseNotesUrl { get; init; }

    // Текст описания релиза (Markdown как есть, без рендеринга) — короткая выжимка
    // показывается в диалоге, полностью — по ссылке ReleaseNotesUrl.
    public string? ReleaseNotes { get; init; }

    public string? ErrorMessage { get; init; }
}

// Одна запись из полного списка релизов (см. UpdateChecker.GetAllReleasesAsync) — то же самое,
// что и UpdateCheckResult, но без Status/CurrentVersion (список сразу про несколько релизов —
// "актуальна ли эта версия" каждый элемент списка не знает и не должен, это решает вызывающая
// сторона, сравнивая Version с UpdateChecker.GetCurrentVersion()).
public sealed class ReleaseListItem
{
    public string Version { get; init; } = "";
    public string? ZipDownloadUrl { get; init; }
    public string? ExeDownloadUrl { get; init; }
    public string? ReleaseNotesUrl { get; init; }
    public string? ReleaseNotes { get; init; }
    public System.DateTimeOffset? PublishedAt { get; init; }
    public bool IsPrerelease { get; init; }
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
            string? exeDownloadUrl = FindExeAssetUrl(root);

            string? notes = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() : null;
            string? htmlUrl = root.TryGetProperty("html_url", out var htmlEl) ? htmlEl.GetString() : null;

            bool hasNewer = !string.IsNullOrEmpty(latestVersion) && IsNewer(latestVersion, currentVersion);

            if (hasNewer && downloadUrl == null && exeDownloadUrl == null)
            {
                // Новая версия по тегу есть, но в её Assets нет ни .zip, ни .exe — либо релиз
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
                    ErrorMessage = $"Вышла новая версия {latestVersion}, но в релизе на GitHub не найдено ни ZIP-архива, ни .exe-установщика."
                };
            }

            return new UpdateCheckResult
            {
                Status = hasNewer ? UpdateCheckStatus.UpdateAvailable : UpdateCheckStatus.UpToDate,
                CurrentVersion = currentVersion,
                LatestVersion = string.IsNullOrEmpty(latestVersion) ? null : latestVersion,
                ZipDownloadUrl = downloadUrl,
                ExeDownloadUrl = exeDownloadUrl,
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

    // Полный список релизов репозитория (см. SettingsWindow — страница "О плеере", аккордеон
    // "Все версии") — в отличие от CheckAsync выше, берёт сразу все, чтобы можно было откатиться
    // на более старую версию. per_page=100 — собственный максимум GitHub за один запрос.
    // Черновики не показываем (не предназначены для скачивания), пререлизы показываем, но
    // помечаем.
    public static async Task<(List<ReleaseListItem> Releases, string? ErrorMessage)> GetAllReleasesAsync(CancellationToken ct = default)
    {
        try
        {
            string url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page=100";
            using var response = await Http.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
                return (new List<ReleaseListItem>(), $"GitHub вернул код {(int)response.StatusCode}");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return (new List<ReleaseListItem>(), "Неожиданный ответ GitHub — ожидался список релизов.");

            var releases = new List<ReleaseListItem>();

            foreach (var releaseEl in doc.RootElement.EnumerateArray())
            {
                bool isDraft = releaseEl.TryGetProperty("draft", out var draftEl) && draftEl.GetBoolean();
                if (isDraft) continue;

                string tagName = releaseEl.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
                string version = tagName.TrimStart('v', 'V');
                if (string.IsNullOrEmpty(version)) continue;

                System.DateTimeOffset? publishedAt = null;
                if (releaseEl.TryGetProperty("published_at", out var pubEl) && pubEl.ValueKind == JsonValueKind.String
                    && System.DateTimeOffset.TryParse(pubEl.GetString(), out var parsedDate))
                {
                    publishedAt = parsedDate;
                }

                releases.Add(new ReleaseListItem
                {
                    Version = version,
                    ZipDownloadUrl = FindZipAssetUrl(releaseEl),
                    ExeDownloadUrl = FindExeAssetUrl(releaseEl),
                    ReleaseNotesUrl = releaseEl.TryGetProperty("html_url", out var htmlEl) ? htmlEl.GetString() : null,
                    ReleaseNotes = releaseEl.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() : null,
                    PublishedAt = publishedAt,
                    IsPrerelease = releaseEl.TryGetProperty("prerelease", out var preEl) && preEl.GetBoolean(),
                });
            }

            return (releases, null);
        }
        catch (System.Exception ex)
        {
            return (new List<ReleaseListItem>(), ex.Message);
        }
    }

    // Ищет .zip среди Assets релиза. Если их несколько (маловероятно при том, как релиз
    // собирает workflow, но в принципе возможно для ручных релизов) — предпочитает тот, что
    // явно назван в честь приложения, иначе берёт первый попавшийся: лучше попытаться
    // обновиться не тем архивом, чем безосновательно отказаться при доступном обновлении.
    private static string? FindZipAssetUrl(JsonElement releaseRoot) => FindAssetUrl(releaseRoot, ".zip");

    // То же самое, но для .exe-установщика (Inno Setup) — см. UpdateCheckResult.ExeDownloadUrl.
    private static string? FindExeAssetUrl(JsonElement releaseRoot) => FindAssetUrl(releaseRoot, ".exe");

    private static string? FindAssetUrl(JsonElement releaseRoot, string extension)
    {
        if (!releaseRoot.TryGetProperty("assets", out var assetsEl) || assetsEl.ValueKind != JsonValueKind.Array)
            return null;

        var matchingAssets = assetsEl.EnumerateArray()
            .Where(a => a.TryGetProperty("name", out var n) &&
                        (n.GetString() ?? "").EndsWith(extension, System.StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matchingAssets.Count == 0) return null;

        var best = matchingAssets.FirstOrDefault(a =>
            a.TryGetProperty("name", out var n) &&
            (n.GetString() ?? "").Contains("lumisense", System.StringComparison.OrdinalIgnoreCase));

        if (best.ValueKind != JsonValueKind.Object)
            best = matchingAssets[0];

        return best.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() : null;
    }

    // Версия программы берётся из того же changelog.json, что и карточка "О плеере" в
    // настройках (см. SettingsWindow.RefreshAppVersionText) — единственное место, где она
    // задаётся, чтобы номер нигде не мог разойтись. Публичный — переиспользуется списком всех
    // версий в настройках (см. GetAllReleasesAsync/SettingsWindow), чтобы пометить в нём
    // текущую версию, не выясняя её ещё раз каким-то другим способом.
    public static string GetCurrentVersion()
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

    // Подробности о ходе скачивания — сколько уже получено, сколько всего (если сервер вообще
    // прислал Content-Length — не гарантировано, но GitHub для ассетов релизов отдаёт его
    // почти всегда), доля от 0 до 1 и текущая скорость. См. UpdateAvailableWindow —
    // показывает это пользователю вместо голого процента.
    public sealed class DownloadProgressInfo
    {
        public long BytesReceived { get; init; }
        public long? TotalBytes { get; init; }
        public double Fraction { get; init; }
        public double BytesPerSecond { get; init; }
    }

    // Скачивает ZIP-архив обновления во временную папку сессии, докладывая подробный прогресс
    // (см. DownloadProgressInfo).
    public static Task<string> DownloadUpdateZipAsync(string downloadUrl, string sessionDir, System.IProgress<DownloadProgressInfo>? progress, CancellationToken ct) =>
        DownloadToFileAsync(downloadUrl, Path.Combine(sessionDir, "update.zip"), progress, ct);

    // То же самое, но для .exe-установщика (см. UpdateAvailableWindow — вариант "установить
    // через установщик" вместо автообновления через Updater.exe). Отдельный метод только ради
    // говорящего имени на месте вызова — сама логика скачивания полностью общая, см.
    // DownloadToFileAsync.
    public static Task<string> DownloadUpdateExeAsync(string downloadUrl, string sessionDir, System.IProgress<DownloadProgressInfo>? progress, CancellationToken ct) =>
        DownloadToFileAsync(downloadUrl, Path.Combine(sessionDir, "LumisenseSetup.exe"), progress, ct);

    private static async Task<string> DownloadToFileAsync(string downloadUrl, string destinationPath, System.IProgress<DownloadProgressInfo>? progress, CancellationToken ct)
    {
        using var response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength;

        // Скорость считается не от самого начала скачивания, а с момента последнего отчёта —
        // "средняя скорость за весь файл" на медленном старте (например, пока прогревается
        // TCP-соединение) была бы занижена и потом медленно "разгонялась" бы к реальной, а не
        // отражала бы её сразу после первого же интервала.
        var stopwatch = Stopwatch.StartNew();
        var lastReportElapsed = TimeSpan.Zero;
        long lastReportBytes = 0;

        // Отчёты не на каждый прочитанный кусок (для файла в десятки МБ их были бы сотни —
        // Progress<T> перекладывает каждый Report на UI-поток через Dispatcher, незачем грузить
        // его настолько часто), а не чаще, чем раз в ~150мс — этого более чем достаточно, чтобы
        // цифры на экране выглядели "живыми", а не заметно чаще человеческий глаз всё равно не
        // считывает.
        const double reportIntervalMs = 150;

        await using var httpStream = await response.Content.ReadAsStreamAsync(ct);
        await using (var fileStream = File.Create(destinationPath))
        {
            var buffer = new byte[81920];
            long readTotal = 0;
            int read;
            while ((read = await httpStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                readTotal += read;

                var elapsed = stopwatch.Elapsed;
                if (progress != null && (elapsed - lastReportElapsed).TotalMilliseconds >= reportIntervalMs)
                {
                    double intervalSeconds = (elapsed - lastReportElapsed).TotalSeconds;
                    double bytesPerSecond = intervalSeconds > 0 ? (readTotal - lastReportBytes) / intervalSeconds : 0;

                    progress.Report(new DownloadProgressInfo
                    {
                        BytesReceived = readTotal,
                        TotalBytes = totalBytes,
                        Fraction = totalBytes is > 0 ? (double)readTotal / totalBytes.Value : 0,
                        BytesPerSecond = bytesPerSecond
                    });

                    lastReportElapsed = elapsed;
                    lastReportBytes = readTotal;
                }
            }

            // Финальный отчёт — гарантирует ровно 100% (readTotal == totalBytes) на экране в
            // момент завершения, даже если цикл выше закончился между двумя плановыми отчётами
            // и последнее увиденное пользователем значение было чуть меньше.
            progress?.Report(new DownloadProgressInfo
            {
                BytesReceived = readTotal,
                TotalBytes = totalBytes,
                Fraction = totalBytes is > 0 ? (double)readTotal / totalBytes.Value : 1,
                BytesPerSecond = 0
            });
        }

        return destinationPath;
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

    // Запускает скачанный .exe-установщик и сразу завершает плеер — в отличие от
    // LaunchUpdaterAndExit выше, дальше ничего готовить не нужно: сам установщик (Inno Setup)
    // умеет и закрыть/дождаться запущенный плеер, и заменить файлы, и предложить запустить
    // обновлённую версию по завершении — весь этот сценарий уже реализован в самом инсталляторе
    // для случая первой установки, для обновления он работает точно так же.
    //
    // Verb="runas" — по тем же причинам, что и у Updater'а: установка обычно идёт в Program
    // Files, куда без прав администратора не записать. Сам установщик, скорее всего, и так
    // запросил бы повышение через собственный манифест — но полагаться на это не стоит: не все
    // инсталляторы Inno Setup собираются с privilegesRequired=admin по умолчанию, а без него
    // Process.Start только с UseShellExecute здесь бы тихо запустил его без прав и установка
    // впоследствии могла бы просто отказать в доступе к файлам.
    public static void LaunchInstallerAndExit(string installerExePath)
    {
        var startInfo = new ProcessStartInfo(installerExePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(installerExePath)!
        };

        Process.Start(startInfo);

        System.Windows.Application.Current.Shutdown();
    }
}
