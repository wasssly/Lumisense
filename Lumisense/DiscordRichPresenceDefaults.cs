namespace Lumisense;

// Публичный Application ID для Discord Rich Presence — не токен и не секрет, Discord
// выбирает по нему зарегистрированное приложение при локальном IPC.
public static class DiscordRichPresenceDefaults
{
    public const string ApplicationId = "1539323116508024912";
    public const string GitHubRepositoryUrl = "https://github.com/wasssly/Lumisense";
    public const string DownloadUrl = "https://github.com/wasssly/Lumisense/releases/latest";
    // Значок по умолчанию (Spotlight) для карточки без обложки: Discord берёт большое изображение по URL, а значок из портала разработчика остаётся прежним.
    public const string DefaultIconUrl = "https://raw.githubusercontent.com/wasssly/Lumisense/main/Lumisense/Icons/app/lumisense-spotlight-512.png";
}
