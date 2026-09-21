using DiscordRPC;

namespace Lumisense;

// Изолирует Discord IPC от аудиопотока и WPF: сбои Discord/библиотеки не должны мешать
// воспроизведению. Rich Presence обновляется только при смене трека/состояния или перемотке.
public sealed class DiscordRichPresenceManager : IDisposable
{
    private const int DiscordTextMaxLength = 128;
    private const double SeekRefreshThresholdSeconds = 2.5;
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(10);

    private readonly object _sync = new();
    private DiscordRpcClient? _client;
    private string? _applicationId;
    private string? _lastTitle;
    private string? _lastArtist;
    private bool _lastPlaying;
    private bool _lastShowTrackInfo;
    private bool _lastShowTimeline;
    private double _lastPublishedPositionSeconds;
    private double _lastPublishedTotalSeconds;
    private DateTime _lastPublishedAtUtc;
    private DateTime _nextConnectionAttemptUtc;
    private bool _disposed;
    // Тот же AppSettings, что MainWindow передаёт в Update — нужен только RefreshCoverArtAsync,
    // чтобы переотправить presence с актуальными флагами приватности.
    private AppSettings? _lastSettings;

    // Растёт при каждой смене трека. RefreshCoverArtAsync сверяет версию перед применением
    // результата — если трек сменился ещё раз, пока лукап летал по сети, URL отбрасывается.
    private long _coverLookupVersion;
    private bool _coverLookupStartedForCurrentTrack;
    private string? _currentCoverUrl;

    public void Update(AppSettings settings, string? title, string? artist, bool isPlaying,
        double currentSeconds, double totalSeconds, bool hasTrack, bool force = false)
    {
        if (_disposed) return;

        if (!settings.DiscordRichPresenceEnabled || !hasTrack)
        {
            ClearAndDispose();
            return;
        }

        try
        {
            var client = EnsureClient(DiscordRichPresenceDefaults.ApplicationId);
            if (client == null) return;

            currentSeconds = Math.Max(0, currentSeconds);
            totalSeconds = Math.Max(0, totalSeconds);
            string normalizedTitle = NormalizeText(title);
            string normalizedArtist = NormalizeText(artist);

            // Отдельно от metadataChanged: play/pause и смена настроек приватности не должны
            // сбрасывать уже найденную обложку и запускать повторный лукап по сети.
            bool trackChanged = !string.Equals(normalizedTitle, _lastTitle, StringComparison.Ordinal) ||
                                !string.Equals(normalizedArtist, _lastArtist, StringComparison.Ordinal);

            bool metadataChanged = trackChanged ||
                                   isPlaying != _lastPlaying ||
                                   settings.DiscordRichPresenceShowTrackInfo != _lastShowTrackInfo ||
                                   settings.DiscordRichPresenceShowTimeline != _lastShowTimeline;

            // Discord сам отсчитывает Timestamps. Повторно отправляем presence только после
            // заметной перемотки, когда фактическая позиция расходится с ожидаемым ходом времени.
            double expectedPosition = _lastPublishedPositionSeconds;
            if (_lastPlaying && isPlaying && _lastPublishedAtUtc != default)
                expectedPosition += (DateTime.UtcNow - _lastPublishedAtUtc).TotalSeconds;
            bool seeked = Math.Abs(currentSeconds - expectedPosition) >= SeekRefreshThresholdSeconds;

            if (!force && !metadataChanged && !seeked) return;

            if (trackChanged)
            {
                _coverLookupVersion++;
                _coverLookupStartedForCurrentTrack = false;
                _currentCoverUrl = null;
            }

            bool wantsCoverArt = settings.DiscordRichPresenceShowTrackInfo && settings.DiscordRichPresenceShowCoverArt;
            if (!wantsCoverArt)
            {
                // Настройку могли выключить, пока обложка уже была найдена и показана —
                // не оставляем зависший URL от предыдущего состояния.
                _currentCoverUrl = null;
            }
            else if (!_coverLookupStartedForCurrentTrack)
            {
                _coverLookupStartedForCurrentTrack = true;
                _ = RefreshCoverArtAsync(normalizedArtist, normalizedTitle, _coverLookupVersion);
            }

            _lastSettings = settings;

            var presence = BuildPresence(settings, normalizedTitle, normalizedArtist, isPlaying, currentSeconds, totalSeconds,
                wantsCoverArt ? _currentCoverUrl : null);
            client.SetPresence(presence);
            DiscordRichPresenceLogger.Info($"Presence отправлен: playing={isPlaying}, timeline={settings.DiscordRichPresenceShowTimeline && isPlaying && totalSeconds > 0}, trackInfo={settings.DiscordRichPresenceShowTrackInfo}, cover={presence.Assets != null}, buttons={presence.Buttons?.Length ?? 0}.");

            _lastTitle = normalizedTitle;
            _lastArtist = normalizedArtist;
            _lastPlaying = isPlaying;
            _lastShowTrackInfo = settings.DiscordRichPresenceShowTrackInfo;
            _lastShowTimeline = settings.DiscordRichPresenceShowTimeline;
            _lastPublishedPositionSeconds = currentSeconds;
            _lastPublishedTotalSeconds = totalSeconds;
            _lastPublishedAtUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            // IPC является необязательной локальной интеграцией. Не создаём MessageBox и не
            // блокируем UI, но сохраняем диагностическую запись для редких проблем библиотеки.
            Logger.Warn($"Discord Rich Presence недоступен: {ex.Message}");
            DiscordRichPresenceLogger.Error("Ошибка отправки или формирования Rich Presence", ex);
            ResetBrokenClient();
        }
    }

    public void ClearAndDispose()
    {
        lock (_sync)
        {
            if (_client == null) return;

            try { _client.SetPresence(null!); }
            catch { /* Discord мог быть закрыт раньше Lumisense. */ }
            try { _client.Dispose(); }
            catch { /* Освобождение IPC не должно нарушать закрытие плеера. */ }
            DiscordRichPresenceLogger.Info("Presence очищен, Discord IPC-клиент освобождён.");

            _client = null;
            _applicationId = null;
            _nextConnectionAttemptUtc = default;
            ResetPublishedState();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        ClearAndDispose();
        _disposed = true;
    }

    private DiscordRpcClient? EnsureClient(string applicationId)
    {
        lock (_sync)
        {
            if (_client != null && string.Equals(_applicationId, applicationId, StringComparison.Ordinal) && _client.IsInitialized)
                return _client;

            if (DateTime.UtcNow < _nextConnectionAttemptUtc)
                return null;

            if (_client != null)
            {
                try { _client.SetPresence(null!); }
                catch { }
                try { _client.Dispose(); }
                catch { }
                _client = null;
            }

            // Конструктор с одним ID сканирует стандартные Discord IPC pipes. ShutdownOnly
            // гарантирует корректное снятие активности, когда приложение закроется штатно.
            var client = new DiscordRpcClient(applicationId)
            {
                ShutdownOnly = true,
                SkipIdenticalPresence = true
            };
            ConfigureClientDiagnostics(client);
            DiscordRichPresenceLogger.Info("Запуск подключения к Discord IPC.");
            if (!client.Initialize())
            {
                DiscordRichPresenceLogger.Warn("Discord IPC недоступен: клиент Discord не запущен или соединение отклонено.");
                client.Dispose();
                _nextConnectionAttemptUtc = DateTime.UtcNow.Add(ReconnectDelay);
                return null;
            }

            _client = client;
            _applicationId = applicationId;
            _nextConnectionAttemptUtc = default;
            ResetPublishedState();
            DiscordRichPresenceLogger.Info("Discord IPC подключён; ожидание события READY.");
            return _client;
        }
    }

    private static void ConfigureClientDiagnostics(DiscordRpcClient client)
    {
        client.OnConnectionEstablished += (_, message) => DiscordRichPresenceLogger.Info($"Discord IPC-соединение установлено: {message}.");
        client.OnReady += (_, message) => DiscordRichPresenceLogger.Info($"Discord подтвердил READY для Rich Presence: {message}.");
        client.OnPresenceUpdate += (_, message) => DiscordRichPresenceLogger.Info($"Discord подтвердил обновление Rich Presence: {message}.");
        client.OnConnectionFailed += (_, message) => DiscordRichPresenceLogger.Warn($"Discord IPC не смог установить соединение: {message}.");
        client.OnClose += (_, message) => DiscordRichPresenceLogger.Warn($"Discord IPC-соединение закрыто клиентом Discord: {message}.");
        client.OnError += (_, message) => DiscordRichPresenceLogger.Warn($"Discord вернул ошибку для Rich Presence: {message}.");
    }

    private void ResetBrokenClient()
    {
        lock (_sync)
        {
            try { _client?.Dispose(); }
            catch { }
            _client = null;
            _applicationId = null;
            _nextConnectionAttemptUtc = DateTime.UtcNow.Add(ReconnectDelay);
            ResetPublishedState();
            DiscordRichPresenceLogger.Warn("Discord IPC-клиент сброшен после ошибки; следующая попытка будет не раньше чем через 10 секунд.");
        }
    }

    private static RichPresence BuildPresence(AppSettings settings, string title, string artist,
        bool isPlaying, double currentSeconds, double totalSeconds, string? coverUrl)
    {
        string activityState = isPlaying ? "Воспроизводится" : "На паузе";
        var presence = new RichPresence
        {
            Details = settings.DiscordRichPresenceShowTrackInfo
                ? title
                : "Lumisense",
            State = settings.DiscordRichPresenceShowTrackInfo && !string.IsNullOrWhiteSpace(artist)
                ? artist
                : activityState,
            Type = ActivityType.Listening,
            // Две пользовательские кнопки (репозиторий и актуальный релиз) уходят вместе с Presence и видны зрителям.
            Buttons = new[]
            {
                new Button { Label = "GitHub", Url = DiscordRichPresenceDefaults.GitHubRepositoryUrl },
                new Button { Label = "Скачать Lumisense", Url = DiscordRichPresenceDefaults.DownloadUrl }
            }
        };

        // LargeImageText дублировал бы Details/State при наведении на обложку, поэтому пустой,
        // пока трек виден текстом; заполняется только когда трек скрыт настройками приватности.
        if (!string.IsNullOrWhiteSpace(coverUrl))
        {
            presence.Assets = new Assets
            {
                LargeImageKey = coverUrl,
                LargeImageText = settings.DiscordRichPresenceShowTrackInfo ? null : "Lumisense"
            };
        }

        // Таймлайн скрывается на паузе: иначе Discord продолжал бы отсчёт и показывал неверное
        // оставшееся время. Для играющего трека задаём абсолютные timestamps от UTC.
        if (settings.DiscordRichPresenceShowTimeline && isPlaying && totalSeconds > 0)
        {
            var now = DateTime.UtcNow;
            presence.Timestamps = new Timestamps(
                now.AddSeconds(-currentSeconds),
                now.AddSeconds(Math.Max(0, totalSeconds - currentSeconds)));
        }

        return presence;
    }

    // Поиск обложки может занять до нескольких секунд; не блокирует Update. Переотправляет
    // presence с картинкой, только если трек к этому моменту не сменился (version совпадает).
    private async Task RefreshCoverArtAsync(string artist, string title, long version)
    {
        string? url;
        try
        {
            url = await DiscordCoverArtLookupService.TryGetCoverUrlAsync(artist, title, CancellationToken.None);
        }
        catch
        {
            url = null;
        }

        lock (_sync)
        {
            if (_disposed || _client is null || version != _coverLookupVersion || _lastSettings is null)
                return;

            _currentCoverUrl = url;

            bool wantsCoverArt = _lastSettings.DiscordRichPresenceShowTrackInfo && _lastSettings.DiscordRichPresenceShowCoverArt;
            if (!wantsCoverArt) return; // настройку выключили, пока лукап летал — не показываем найденное

            var presence = BuildPresence(_lastSettings, _lastTitle ?? title, _lastArtist ?? artist,
                _lastPlaying, _lastPublishedPositionSeconds, _lastPublishedTotalSeconds, url);
            try
            {
                _client.SetPresence(presence);
                DiscordRichPresenceLogger.Info($"Presence обновлён после поиска обложки: найдена={url != null}.");
            }
            catch (Exception ex)
            {
                DiscordRichPresenceLogger.Error("Не удалось отправить Rich Presence с найденной обложкой", ex);
            }
        }
    }

    private static string NormalizeText(string? value)
    {
        string text = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return "Неизвестная композиция";
        return text.Length <= DiscordTextMaxLength ? text : text[..DiscordTextMaxLength];
    }

    private void ResetPublishedState()
    {
        _lastTitle = null;
        _lastArtist = null;
        _lastPlaying = false;
        _lastShowTrackInfo = false;
        _lastShowTimeline = false;
        _lastPublishedPositionSeconds = 0;
        _lastPublishedTotalSeconds = 0;
        _lastPublishedAtUtc = default;
        _lastSettings = null;
        _coverLookupVersion++;
        _coverLookupStartedForCurrentTrack = false;
        _currentCoverUrl = null;
    }
}
