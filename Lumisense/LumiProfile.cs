using System.IO;
using System.Reflection;
using System.Text.Json;

namespace AudioPlayer;

// Формат файла для переноса профиля плеера на другой компьютер (.lumi) — обычный JSON с
// другим расширением, тот же подход, что и у settings.json (см. SettingsManager.Load/Save).
// Каждая секция независима и необязательна — что выбрал пользователь при экспорте (см.
// ProfileTransferWindow), то и заполнено; остальное остаётся null, а НЕ пустым списком/
// объектом, чтобы при импорте можно было отличить "секции не было в файле" от "секция была,
// но пустая" — LumiProfileIO.ApplySettingsSection и импорт плейлиста/избранного в
// SettingsWindow полагаются именно на это различие.
public class LumiProfile
{
    public int FormatVersion { get; set; } = 1;
    public DateTime ExportedAtUtc { get; set; } = DateTime.UtcNow;

    // Только "предпочтения" — тема, акцент, эквалайзер, хоткеи, мини-плеер и т.п. Плейлист и
    // избранное сюда НЕ попадают, хотя физически это тоже поля AppSettings (SavedPlaylistFolders/
    // FavoriteTracks) — они экспортируются отдельными секциями ниже, независимо выбираемыми.
    // Сессионные/локальные для конкретного компьютера поля (последний трек и позиция,
    // статистика прослушиваний, состояние окна на момент закрытия) тоже сюда не попадают —
    // см. LumiProfileIO.CloneSettingsForExport.
    public AppSettings? Settings { get; set; }

    public List<SavedPlaylistFolder>? Playlist { get; set; }

    public List<string>? Favorites { get; set; }
}

public static class LumiProfileIO
{
    public const string FileExtension = ".lumi";
    public const string FileFilter = "Профиль Lumisense (*.lumi)|*.lumi";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Поля AppSettings, которые НЕ переносятся секцией "Настройки" — они либо принадлежат
    // отдельным секциям (плейлист/избранное), либо это чисто сессионные/локальные для
    // конкретного компьютера данные, которым нет смысла путешествовать между машинами (и
    // которые опасно затирать чужими значениями при импорте на уже используемый профиль).
    private static readonly HashSet<string> SettingsSectionExcludedProperties = new()
    {
        nameof(AppSettings.SavedPlaylistFolders),
        nameof(AppSettings.SavedPlaylist),
        nameof(AppSettings.FavoriteTracks),
        nameof(AppSettings.LastTrackPath),
        nameof(AppSettings.LastPositionSeconds),
        nameof(AppSettings.WasMiniPlayerOnClose),
        nameof(AppSettings.PlayCounts),
    };

    public static void Export(string filePath, AppSettings? liveSettings,
        List<SavedPlaylistFolder>? playlist, List<string>? favorites)
    {
        var profile = new LumiProfile
        {
            Settings = liveSettings == null ? null : CloneSettingsForExport(liveSettings),
            Playlist = playlist,
            Favorites = favorites
        };

        File.WriteAllText(filePath, JsonSerializer.Serialize(profile, JsonOptions));
    }

    // JSON-рандтрип, а не ручное копирование полей — простой и надёжный способ полного клона
    // без риска случайно расшарить те же самые вложенные списки/объекты с живыми настройками
    // приложения (при экспорте не хотим, чтобы дальнейшее изменение экспортированного клона
    // как-то повлияло на реальные настройки, и наоборот).
    private static AppSettings CloneSettingsForExport(AppSettings source)
    {
        var clone = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(source, JsonOptions), JsonOptions)!;

        clone.SavedPlaylistFolders = new List<SavedPlaylistFolder>();
        clone.SavedPlaylist = null;
        clone.FavoriteTracks = new List<string>();
        clone.LastTrackPath = null;
        clone.LastPositionSeconds = 0;
        clone.WasMiniPlayerOnClose = false;
        clone.PlayCounts = new Dictionary<string, int>();

        return clone;
    }

    // null — файл не читается (не .lumi, повреждён, не JSON и т.п.). Сами по себе null-секции
    // внутри успешно прочитанного профиля — это не ошибка, а нормальный результат выборочного
    // экспорта (см. LumiProfile).
    public static LumiProfile? TryReadFile(string filePath)
    {
        try
        {
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<LumiProfile>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    // Копирует поля "предпочтений" (все, кроме SettingsSectionExcludedProperties) из imported
    // поверх live — рефлексией по всем публичным read/write свойствам AppSettings, а не
    // десятками ручных присваиваний, чтобы не забыть перенести какое-нибудь новое поле,
    // добавленное позже. Плейлист/избранное/сессионные поля live-объекта этим не затрагиваются.
    public static void ApplySettingsSection(AppSettings imported, AppSettings live)
    {
        foreach (PropertyInfo prop in typeof(AppSettings).GetProperties())
        {
            if (!prop.CanRead || !prop.CanWrite) continue;
            if (SettingsSectionExcludedProperties.Contains(prop.Name)) continue;

            prop.SetValue(live, prop.GetValue(imported));
        }
    }
}
