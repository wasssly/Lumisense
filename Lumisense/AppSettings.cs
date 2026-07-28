using System.IO;
using System.Text.Json;

namespace AudioPlayer;

// Одна сохранённая группа плейлиста (папка целиком или набор отдельных файлов) —
// то, что пишется в settings.json и восстанавливается при следующем запуске
public class SavedPlaylistFolder
{
    public string DisplayName { get; set; } = "";
    public string? SourcePath { get; set; }      // null для группы "Отдельные файлы" и для созданных вручную папок
    public bool IsEnabled { get; set; } = true;
    public bool IsExpanded { get; set; } = true;  // развёрнут ли список треков этой группы в UI
    public List<string> Tracks { get; set; } = new();

    // true только у автосоздаваемой группы "Отдельные файлы" — отличает её от папок,
    // созданных вручную через "Новую папку…" (у обеих SourcePath == null)
    public bool IsLooseFilesBucket { get; set; }
}

// Одна настраиваемая глобальная комбинация клавиш (например, Ctrl+Alt+P), через WinAPI
// RegisterHotKey — срабатывает даже когда окно не в фокусе. Клавиша Win отслеживается
// отдельно через Keyboard.IsKeyDown, т.к. Keyboard.Modifiers её не учитывает.
public class HotkeyBinding
{
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public bool Win { get; set; }

    // Имя клавиши из перечисления System.Windows.Input.Key (например, "P", "Right").
    // Пустая строка означает "комбинация не задана" — соответствующий хоткей выключен.
    public string Key { get; set; } = "";

    public bool IsEmpty => string.IsNullOrEmpty(Key);
}

// Настройки приложения, сохраняемые между запусками
public class AppSettings
{
    public string Theme { get; set; } = "Dark";           // "Dark" или "Light"
    public bool AlwaysOnTop { get; set; }                  // Держать окно поверх остальных
    public bool RememberVolume { get; set; } = true;       // Запоминать громкость между запусками
    public double SavedVolume { get; set; } = 0.3;

    // Линейный ползунок (0..1) сильнее всего меняет громкость на верхнем участке — слух
    // воспринимает громкость логарифмически. При включении позиция переводится в децибелы
    // перед применением к устройству (MainWindow.ToOutputVolume). По умолчанию выключено —
    // сохраняем привычное поведение для тех, кто уже пользуется линейной шкалой.
    public bool UseLogarithmicVolume { get; set; }
    public bool MinimizeToTrayOnClose { get; set; } = true; // Сворачивать в трей вместо закрытия

    // Не показывать окно плеера сразу после запуска — только значок в трее. Автозапуск
    // с Windows хранится отдельно, в реестре (см. StartupManager) — эти две настройки
    // независимы: можно запускаться с Windows и сразу показывать окно, а можно наоборот.
    public bool StartHiddenInTray { get; set; }

    // Запоминаем режим отображения плеера между запусками: был ли он свёрнут в мини-плеер
    // на момент закрытия, и была ли видна панель плейлиста в обычном окне.
    public bool WasMiniPlayerOnClose { get; set; }
    public bool IsPlaylistVisible { get; set; } = true;

    // "Square" / "Rectangular" / "Mini" — выбирается через контекстное меню на заголовке
    // "Lumisense". Null, если ещё не сохранялось — сигнал первого запуска, тогда открываем
    // квадратный вид (см. SettingsManager.HasSavedSettingsFile).
    public string? PlayerViewMode { get; set; }

    // Плейлист, сохраняемый между запусками, теперь разбит на группы (папки/отдельные файлы)
    public List<SavedPlaylistFolder> SavedPlaylistFolders { get; set; } = new();

    // Старое плоское поле оставлено только для миграции плейлистов, сохранённых
    // предыдущей версией плеера. Само приложение больше в него не пишет.
    public List<string>? SavedPlaylist { get; set; }

    // Пути избранных треков (сердечко на строке трека) — общий список, не привязанный ни к
    // одной конкретной группе плейлиста. Из него на лету строится виртуальная группа
    // "Избранное" (см. FavoritesManager и MainWindow._favoritesFolder). Порядок сохраняется —
    // это порядок добавления в избранное, тот же, в котором треки показываются в плейлисте
    // "Избранное".
    public List<string> FavoriteTracks { get; set; } = new();

    public string? LastTrackPath { get; set; }              // Путь последнего проигранного трека
    public double LastPositionSeconds { get; set; }          // Позиция в треке на момент закрытия

    // Запоминаем состояние кнопок "Перемешать" и "Повтор" между запусками — так же, как
    // громкость и позицию в треке. RepeatMode хранится строкой (имя значения перечисления
    // MainWindow.RepeatMode: "Off"/"All"/"One") — так безопаснее для settings.json, если
    // порядок значений перечисления когда-нибудь поменяется.
    public bool IsShuffleEnabled { get; set; }
    public string RepeatMode { get; set; } = "Off";

    // Настройки мини-плеера
    public double MiniPlayerOpacity { get; set; } = 1.0;
    public bool MiniPlayerAlwaysOnTop { get; set; } = true;
    public bool MiniPlayerPinned { get; set; }               // Запрещает перетаскивание окна мышью

    // Место на экране, куда пользователь перетащил мини-плеер в последний раз.
    // null означает "ещё ни разу не задавалось" — тогда используется положение по умолчанию.
    public double? MiniPlayerLeft { get; set; }
    public double? MiniPlayerTop { get; set; }

    // Настраиваемые глобальные горячие клавиши. По умолчанию — Ctrl+Alt+<клавиша>,
    // чтобы не конфликтовать с обычным набором текста в других приложениях.
    // Работают в дополнение к физическим мультимедийным клавишам клавиатуры,
    // которые всегда активны и не настраиваются.
    public HotkeyBinding HotkeyPlayPause { get; set; } = new() { Ctrl = true, Alt = true, Key = "P" };
    public HotkeyBinding HotkeyNext { get; set; } = new() { Ctrl = true, Alt = true, Key = "Right" };
    public HotkeyBinding HotkeyPrevious { get; set; } = new() { Ctrl = true, Alt = true, Key = "Left" };
    public HotkeyBinding HotkeyStop { get; set; } = new() { Ctrl = true, Alt = true, Key = "S" };
    public HotkeyBinding HotkeyVolumeUp { get; set; } = new() { Ctrl = true, Alt = true, Key = "Up" };
    public HotkeyBinding HotkeyVolumeDown { get; set; } = new() { Ctrl = true, Alt = true, Key = "Down" };
    public HotkeyBinding HotkeyMute { get; set; } = new() { Ctrl = true, Alt = true, Key = "M" };
    public HotkeyBinding HotkeyShuffle { get; set; } = new() { Ctrl = true, Alt = true, Key = "U" };
    public HotkeyBinding HotkeyRepeat { get; set; } = new() { Ctrl = true, Alt = true, Key = "R" };

    // Без Flags: default (пустая) — намеренно не включена по умолчанию, в отличие от остальных
    // хоткеев. Необратимое действие (хоть и через корзину), пользователь должен назначить
    // комбинацию сам, а не рисковать случайно удалить трек хоткеем по умолчанию.
    public HotkeyBinding HotkeyDeleteTrack { get; set; } = new();

    // ---------- Экспериментальные функции ----------
    // По умолчанию выключены — осознанный выбор пользователя, а не смена привычного
    // поведения без явного согласия.

    // Вместо чисто случайного выбора трека на каждом шаге тасует весь плейлист один раз
    // и проигрывает по порядку — гарантирует, что каждый трек сыграет один раз до повтора
    public bool UseImprovedShuffle { get; set; }

    // Убирает фон у кнопок "Перемешать", "Повтор", "Предыдущий", "Пуск/Пауза", "Следующий",
    // "Стоп" и перехода в мини-плеер в главном окне (см.
    // MainWindow.ApplyPlaybackButtonsVisibility) — кнопки остаются видимыми и кликабельными,
    // просто фон становится прозрачным (сливается с фоном плеера), видна только иконка.
    public bool HidePlaybackButtons { get; set; }

    // Версия, которую пользователь явно отклонил в диалоге "Доступно обновление" (кнопка
    // "Позже") — при следующих запусках с ЭТОЙ ЖЕ версией на GitHub диалог больше не
    // всплывает сам по себе (чтобы не надоедать), но появится снова, как только выйдет
    // версия новее. Ручная проверка кнопкой в настройках всегда показывает результат,
    // независимо от этого поля. См. UpdateChecker.
    public string? SkippedUpdateVersion { get; set; }

    // Откуда качать сам установщик при обновлении — "GitHub" напрямую или одно из зеркал
    // gh-proxy (см. UpdateChecker.DownloadSources/ApplyDownloadSource и переключатель в
    // настройках "О плеере"). Влияет только на скачивание файла установщика — сама проверка
    // версии (обращение к api.github.com) всегда идёт напрямую независимо от этой настройки.
    public string UpdateDownloadSource { get; set; } = "GitHub";

    // ---------- Эквалайзер ----------
    // См. EqualizerSampleProvider — 10 классических ISO-полос графического EQ. Массив может
    // быть короче/длиннее 10 элементов у настроек, сохранённых другой версией плеера (если
    // список полос когда-нибудь изменится) — SettingsWindow и MainWindow при загрузке
    // подстраиваются под фактическую длину EqualizerSampleProvider.BandFrequencies, а не
    // слепо доверяют длине сохранённого массива.
    public bool EqualizerEnabled { get; set; }
    public double[] EqualizerBandGainsDb { get; set; } = new double[10];
}

// Загрузка и сохранение настроек в %AppData%\Lumisense\settings.json
public static class SettingsManager
{
    private static readonly string SettingsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lumisense", "settings.json");

    // true, если файл настроек уже когда-либо сохранялся. Используется, чтобы отличить
    // самый первый запуск плеера (тогда PlayerViewMode ещё не сохранён и мы открываем
    // квадратный вид) от запуска с уже существующими, но старыми настройками (тогда вид
    // плеера подбирается по прежним полям IsPlaylistVisible/WasMiniPlayerOnClose, чтобы
    // ничего не переключилось неожиданно после обновления плеера).
    public static bool HasSavedSettingsFile => File.Exists(SettingsFilePath);

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null)
                {
                    MigrateOldFlatPlaylist(settings);
                    return settings;
                }
            }
        }
        catch
        {
            // Повреждённый или недоступный файл настроек — просто используем значения по умолчанию
        }

        return new AppSettings();
    }

    // Плейлисты, сохранённые старой версией плеера, хранились одним плоским списком путей.
    // Заворачиваем их в единственную группу "Загруженные файлы", чтобы ничего не потерялось.
    private static void MigrateOldFlatPlaylist(AppSettings settings)
    {
        if (settings.SavedPlaylistFolders.Count > 0) return;
        if (settings.SavedPlaylist == null || settings.SavedPlaylist.Count == 0) return;

        settings.SavedPlaylistFolders.Add(new SavedPlaylistFolder
        {
            DisplayName = "Загруженные файлы",
            SourcePath = null,
            IsEnabled = true,
            Tracks = settings.SavedPlaylist.ToList()
        });

        settings.SavedPlaylist = null;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsFilePath);
            if (directory != null)
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFilePath, json);
        }
        catch
        {
            // Нет прав на запись и т.п. — тихо игнорируем, это не критично для работы плеера
        }
    }
}
