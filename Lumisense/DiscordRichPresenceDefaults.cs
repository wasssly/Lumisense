namespace AudioPlayer;

// Публичный идентификатор приложения Discord Rich Presence для Lumisense. Он не является
// токеном или секретом: Discord использует его для выбора зарегистрированного приложения при
// локальном IPC-подключении. Меняется только владельцем приложения при необходимости миграции.
public static class DiscordRichPresenceDefaults
{
    public const string ApplicationId = "1539323116508024912";
    public const string GitHubRepositoryUrl = "https://github.com/wasssly/Lumisense";
    public const string DownloadUrl = "https://github.com/wasssly/Lumisense/releases/latest";
}
