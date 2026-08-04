using System.IO;
using System.Reflection;
using System.Text.Json;

namespace AudioPlayer;

// Формат файла для переноса настроек плеера на другой компьютер (.lumi) — обычный JSON с
// другим расширением, тот же подход, что и у settings.json (см. SettingsManager.Load/Save).
public class LumiProfile
{
    public int FormatVersion { get; set; } = 1;
    public DateTime ExportedAtUtc { get; set; } = DateTime.UtcNow;

    // Только "предпочтения" — тема, акцент, эквалайзер, хоткеи, мини-плеер и т.п. Плейлист и
    // избранное сюда намеренно не входят (перенос профиля ограничен только настройками),
    // сессионные/локальные для конкретного компьютера поля (последний трек и позиция,
    // статистика прослушиваний, состояние окна на момент закрытия) — тоже не входят, см.
    // LumiProfileIO.CloneSettingsForExport.
    public AppSettings Settings { get; set; } = new();
}

public static class LumiProfileIO
{
    public const string FileExtension = ".lumi";
    public const string FileFilter = "Профиль Lumisense (*.lumi)|*.lumi";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Поля AppSettings, которые НЕ переносятся при экспорте/импорте — плейлист и избранное
    // (перенос профиля ограничен только настройками, см. LumiProfile), а также чисто
    // сессионные/локальные для конкретного компьютера данные, которым нет смысла
    // путешествовать между машинами (и которые опасно затирать чужими значениями при импорте
    // на уже используемый профиль).
    private static readonly HashSet<string> ExcludedProperties = new()
    {
        nameof(AppSettings.SavedPlaylistFolders),
        nameof(AppSettings.SavedPlaylist),
        nameof(AppSettings.FavoriteTracks),
        nameof(AppSettings.LastTrackPath),
        nameof(AppSettings.LastPositionSeconds),
        nameof(AppSettings.WasMiniPlayerOnClose),
        nameof(AppSettings.PlayCounts),
    };

    public static void Export(string filePath, AppSettings liveSettings)
    {
        var profile = new LumiProfile { Settings = CloneSettingsForExport(liveSettings) };
        File.WriteAllText(filePath, JsonSerializer.Serialize(profile, JsonOptions));
    }

    // JSON-рандтрип, а не ручное копирование полей — простой и надёжный способ полного клона
    // без риска случайно расшарить те же самые вложенные списки/объекты с живыми настройками
    // приложения.
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

    // null — файл не читается (не .lumi, повреждён, не JSON и т.п.).
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

    // Копирует поля "предпочтений" (все, кроме ExcludedProperties) из imported поверх live —
    // рефлексией по всем публичным read/write свойствам AppSettings, а не десятками ручных
    // присваиваний, чтобы не забыть перенести какое-нибудь новое поле, добавленное позже.
    public static void Apply(AppSettings imported, AppSettings live)
    {
        foreach (PropertyInfo prop in typeof(AppSettings).GetProperties())
        {
            if (!prop.CanRead || !prop.CanWrite) continue;
            if (ExcludedProperties.Contains(prop.Name)) continue;

            prop.SetValue(live, prop.GetValue(imported));
        }
    }
}
