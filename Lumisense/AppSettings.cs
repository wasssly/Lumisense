using System.IO;
using System.Text.Json;

namespace AudioPlayer;

/// <summary>
/// Одна сохранённая группа плейлиста (папка целиком или набор отдельных файлов) —
/// то, что пишется в settings.json и восстанавливается при следующем запуске.
/// </summary>
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

/// <summary>
/// Одна настраиваемая глобальная комбинация клавиш (например, Ctrl+Alt+P).
/// Работает через WinAPI RegisterHotKey — срабатывает даже когда окно не в фокусе.
/// Поддерживаются модификаторы Ctrl/Alt/Shift/Win (клавиша Win отслеживается отдельно
/// через Keyboard.IsKeyDown, т.к. Keyboard.Modifiers её не учитывает).
/// </summary>
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

/// <summary>
/// Настройки приложения, сохраняемые между запусками.
/// </summary>
public class AppSettings
{
    public string Theme { get; set; } = "Dark";           // "Dark" или "Light"
    public bool AlwaysOnTop { get; set; }                  // Держать окно поверх остальных
    public bool RememberVolume { get; set; } = true;       // Запоминать громкость между запусками
    public double SavedVolume { get; set; } = 0.3;

    // Логарифмическая (на слух — линейная) регулировка громкости: обычный линейный ползунок
    // (0..1) сильнее всего меняет громкость на верхнем участке, а тихие значения почти не
    // отличаются друг от друга — человеческий слух воспринимает громкость логарифмически.
    // При включении позиция ползунка (0..1) переводится в децибелы перед применением к
    // выходному устройству (см. MainWindow.ToOutputVolume), а не подаётся как множитель
    // амплитуды напрямую. Выключено по умолчанию — сохраняем прежнее поведение для тех,
    // кто уже привык к линейной шкале.
    public bool UseLogarithmicVolume { get; set; }
    public bool MinimizeToTrayOnClose { get; set; } = true; // Сворачивать в трей вместо закрытия

    // Запоминаем режим отображения плеера между запусками: был ли он свёрнут в мини-плеер
    // на момент закрытия, и была ли видна панель плейлиста в обычном окне.
    public bool WasMiniPlayerOnClose { get; set; }
    public bool IsPlaylistVisible { get; set; } = true;

    // Вид плеера, выбранный через контекстное меню по клику на заголовок "Lumisense" в
    // левом верхнем углу: "Square" (обычный/квадратный, без плейлиста), "Rectangular"
    // (с плейлистом — как было раньше по умолчанию) или "Mini" (мини-плеер). Оставлено
    // null, если ещё ни разу не сохранялось — это и есть сигнал "первый запуск" (см.
    // SettingsManager.HasSavedSettingsFile и MainWindow_Loaded), при котором открываем
    // именно квадратный вид.
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

    // Без Flags: default (пустая) — намеренно НЕ включена по умолчанию, в отличие от
    // остальных хоткеев. Это необратимое (хоть и с подтверждением и через корзину, см.
    // MainWindow.DeleteTrackFromDisk) действие — пользователь должен сам осознанно назначить
    // для него комбинацию в настройках, а не рисковать случайно удалить трек хоткеем,
    // выбранным разработчиком по умолчанию.
    public HotkeyBinding HotkeyDeleteTrack { get; set; } = new();

    // ---------- Экспериментальные функции ----------
    // Отдельная группа настроек на своей странице ("Экспериментальное"): необязательные
    // изменения поведения, которые в перспективе могут стать поведением по умолчанию, но
    // пока включаются вручную и по умолчанию выключены — как и HotkeyDeleteTrack выше,
    // это осознанный выбор пользователя, а не то, что должно менять привычное поведение
    // без явного согласия.

    // См. MainWindow.GetNextShuffleTrack: вместо чисто случайного выбора трека на каждом
    // шаге (старое поведение) тасует весь плейлист один раз и проигрывает по порядку —
    // гарантирует, что каждый трек сыграет ровно один раз, прежде чем какой-либо повторится.
    public bool UseImprovedShuffle { get; set; }

    // Версия, которую пользователь явно отклонил в диалоге "Доступно обновление" (кнопка
    // "Позже") — при следующих запусках с ЭТОЙ ЖЕ версией на GitHub диалог больше не
    // всплывает сам по себе (чтобы не надоедать), но появится снова, как только выйдет
    // версия новее. Ручная проверка кнопкой в настройках всегда показывает результат,
    // независимо от этого поля. См. UpdateChecker.
    public string? SkippedUpdateVersion { get; set; }
}

/// <summary>
/// Загрузка и сохранение настроек в %AppData%\Lumisense\settings.json
/// </summary>
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
