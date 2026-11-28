namespace Lumisense;

// Смена языка интерфейса вместе с сохранением в настройки: сам LocalizationService о настройках не знает.
internal static class LanguageSettings
{
    public static void Change(AppSettings settings, string language)
    {
        LocalizationService.ChangeLanguage(language);
        settings.Language = LocalizationService.CurrentLanguage;
        _ = SettingsManager.SaveAsync(settings);
    }
}
