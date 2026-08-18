using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

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

// Один сохранённый пресет эквалайзера — имя и 10 значений гейна по полосам, тот же
// набор, что и AppSettings.EqualizerBandGainsDb. Отдельный класс (а не просто
// Dictionary<string, double[]>), чтобы формат совпадал что при хранении в settings.json,
// что при экспорте/импорте в отдельный .json-файл для "поделиться пресетом".
public class EqualizerPreset
{
    public string Name { get; set; } = "";
    public double[] GainsDb { get; set; } = new double[10];
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
    // "Dark" / "Light" — выбирается в настройках (страница "Оформление").
    public string Theme { get; set; } = "Dark";

    // Разрешает значение Theme в фактическую светлую/тёмную. На случай, если в settings.json
    // осталось "System" от более ранней версии (был такой вариант в настройках, убрали) —
    // не падаем и не считаем его тёмной по умолчанию, а на всякий случай всё равно смотрим
    // реестр Windows (AppsUseLightTheme — тот же флаг, которым сама Windows определяет, в
    // каком виде рисовать свои приложения), раз уж это ничего не стоит.
    public bool IsLightThemeResolved() => Theme switch
    {
        "Light" => true,
        "Dark" => false,
        _ => IsSystemThemeLight()
    };

    private static bool IsSystemThemeLight()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            // Ключа может не быть на совсем старых сборках Windows — тогда просто остаёмся
            // на тёмной, это и так дефолт приложения.
            return false;
        }
    }

    // Акцентный цвет интерфейса (кнопки/переключатели/выделения — всё, что рисуется через
    // Wpf.Ui.Appearance.ApplicationAccentColorManager). "System" — берём акцент из настроек
    // персонализации Windows (тот самый цвет, которым подсвечены системные "Пуск"/плитки).
    // "Manual" — используем AccentColorHex, независимо от того, выбран ли он одним из
    // пресетов в настройках или через палитру: результат в обоих случаях — обычный hex-цвет.
    // "System" — акцент Windows, "Manual" — AccentColorHex, "Cover" — цвет от текущей обложки.
    public string AccentColorMode { get; set; } = "System";
    public string AccentColorHex { get; set; } = "#0078D4";

    // Независимо от AccentColorMode добавляет к основе главного окна приглушённый цвет
    // текущей обложки. Поэтому можно, например, оставить системный акцент и включить
    // цветную основу, либо взять акцент от обложки без изменения основы окна.
    public bool CoverBaseFromCover { get; set; }

    // Подложка окна — "Mica" (по умолчанию) или "Acrylic", оба через системный DWM backdrop
    // Windows 11. Применяется только к MainWindow/SettingsWindow/StatisticsWindow — мелкие
    // диалоговые окна (свойства трека, поиск обложки) открываются и закрываются слишком быстро,
    // чтобы разница была заметна.
    public string WindowBackdropType { get; set; } = "Mica";

    // Вид полосы воспроизведения на главном окне — "Slider" (по умолчанию, как было всегда:
    // обычная сплошная полоса-прогресс) или "Waveform" (форма звука по всей длине трека, как в
    // SoundCloud — см. WaveformView/WaveformGenerator). На мини-плеер не влияет: там полоса
    // всего 4px высотой, полноценная форма волны на такой высоте всё равно неразличима.
    public string ProgressBarStyle { get; set; } = "Slider";

    public bool AlwaysOnTop { get; set; }                  // Держать окно поверх остальных
    public bool RememberVolume { get; set; } = true;       // Запоминать громкость между запусками
    public double SavedVolume { get; set; } = 0.3;

    // Линейный ползунок (0..1) сильнее всего меняет громкость на верхнем участке — слух
    // воспринимает громкость логарифмически. При включении позиция переводится в децибелы
    // перед применением к устройству (MainWindow.ToOutputVolume). По умолчанию выключено —
    // сохраняем привычное поведение для тех, кто уже пользуется линейной шкалой.
    public bool UseLogarithmicVolume { get; set; }

    // ReplayGain — выравнивание субъективной громкости между треками по тегам REPLAYGAIN_*
    // (ID3v2 TXXX/Vorbis comments/APE — откуда именно, решает сама TagLibSharp в зависимости
    // от формата файла, см. ReplayGainReader). По умолчанию выключено: у треков без этих тегов
    // ничего не меняется, но включать что-либо, способное менять громкость воспроизведения без
    // явного действия пользователя, по умолчанию — не лучшая идея, пусть будет осознанным выбором.
    public bool ReplayGainEnabled { get; set; }

    // Темп воспроизведения без изменения высоты тона. 1.0 — обычная скорость.
    public double PlaybackSpeed { get; set; } = 1.0;

    // Независимое изменение высоты тона в полутонах. 0 — исходный тон.
    public double PlaybackPitchSemitones { get; set; } = 0.0;

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

    // Пути избранных треков — общий список, не привязанный к группе плейлиста. Из него на лету
    // строится виртуальная группа "Избранное" (см. FavoritesManager). Порядок — это порядок
    // добавления (FavoritesManager.GetOrder), а не порядок показа: закреплённые треки (см.
    // PinnedFavoriteTracks) при показе поднимаются наверх, не меняя сам этот список.
    public List<string> FavoriteTracks { get; set; } = new();

    // Подмножество FavoriteTracks — какие треки закреплены наверху "Избранного" (см.
    // FavoritesManager.TogglePin). Отдельный список, а не флаг на треке — треки тут только
    // пути к файлам.
    public List<string> PinnedFavoriteTracks { get; set; } = new();

    public string? LastTrackPath { get; set; }              // Путь последнего проигранного трека
    public double LastPositionSeconds { get; set; }          // Позиция в треке на момент закрытия

    // Запоминаем состояние кнопок "Перемешать" и "Повтор" между запусками — так же, как
    // громкость и позицию в треке. RepeatMode хранится строкой (имя значения перечисления
    // MainWindow.RepeatMode: "Off"/"All"/"One") — так безопаснее для settings.json, если
    // порядок значений перечисления когда-нибудь поменяется.
    public bool IsShuffleEnabled { get; set; }
    public string RepeatMode { get; set; } = "Off";

    // Анимация смены обложки (старая "улетает" в сторону, новая "влетает" с
    // противоположной — как в iTunes) при переключении трека. Можно выключить в
    // настройках, если анимация мешает или не нравится — см. MainWindow.SetAlbumArtTransitionEnabled.
    public bool AlbumArtTransitionEnabled { get; set; } = true;

    // Настройки мини-плеера
    public double MiniPlayerOpacity { get; set; } = 1.0;
    public bool MiniPlayerAlwaysOnTop { get; set; } = true;
    public bool MiniPlayerPinned { get; set; }               // Запрещает перетаскивание окна мышью

    // "Магнитное" прилипание к краям экрана при перетаскивании — см. WindowSnapHelper, по
    // умолчанию включено. MiniPlayerPinned сильнее: если положение закреплено, перетаскивания
    // нет вообще. У обычного окна плеера такой настройки нет — оно тащится через системный
    // ui:TitleBar (HTCAPTION), и подобное прилипание там на практике оказалось ненадёжным.
    public bool MiniPlayerSnapToEdges { get; set; } = true;

    // Какая из двух кнопок (повтор/перемешать) показывается в компактном мини-плеере — там
    // места хватает только под одну "вторую" кнопку рядом с play/pause и next/prev (в отличие
    // от основного окна, где показаны обе). "Repeat" или "Shuffle", по умолчанию "Repeat" —
    // так сохраняется прежнее поведение мини-плеера для тех, кто уже им пользуется.
    public string MiniPlayerSecondaryButton { get; set; } = "Repeat";

    // Что показывать во второй строке заголовка мини-плеера (под названием трека — оно само
    // видно всегда, вне зависимости от этого режима). "TitleArtist" (по умолчанию, прежнее
    // поведение) — исполнитель. "TitleOnly" — вторая строка вообще скрыта, только название.
    // "TitleRemaining" — оставшееся время трека вместо исполнителя, обновляется вместе с
    // прогресс-баром (см. MiniPlayerWindow.OnProgressChanged/UpdateSecondaryLine).
    public string MiniPlayerInfoMode { get; set; } = "TitleArtist";

    // Всплывающее уведомление в углу экрана при смене трека (обложка + название, исчезает
    // само через пару секунд) — см. TrackChangeToastWindow и MainWindow.ShowTrackChangeToast.
    public bool ShowTrackChangeToast { get; set; } = true;

    // Где на экране показывать уведомление — "BottomRight" (по умолчанию, как было всегда),
    // "BottomLeft", "BottomCenter", "TopRight", "TopLeft" или "TopCenter".
    public string TrackChangeToastPosition { get; set; } = "BottomRight";

    // На каком мониторе показывать уведомление, если их несколько. Пусто — "автоматически":
    // тот же монитор, на котором сейчас находится основное окно плеера (см.
    // MainWindow.ResolveToastScreen); иначе — Screen.DeviceName конкретного монитора (вида
    // "\\.\DISPLAY1"). Если сохранённый монитор отключили (или это уже другой компьютер) —
    // тихо откатываемся на автоматический выбор, а не падаем и не показываем уведомление
    // за пределами экрана.
    public string TrackChangeToastMonitor { get; set; } = "";

    // Размер карточки — "Small" / "Medium" (по умолчанию) / "Large" — задаёт высоту, размер
    // обложки, размер шрифтов и внутренние отступы текстовой колонки (см.
    // TrackChangeToastWindow.ApplySizePreset). Независим от ширины ниже: размер меняет "рост"
    // карточки и то, насколько крупно всё внутри неё, ширина — только сколько карточка
    // занимает по горизонтали.
    public string TrackChangeToastSize { get; set; } = "Medium";

    // Ширина карточки уведомления в пикселях — отдельная настройка от размера выше (см.
    // SettingsWindow.ToastWidthSlider): меняет ТОЛЬКО ширину карточки (и то, сколько текста
    // помещается в строку до многоточия), высота/обложка/шрифты остаются такими, какими их
    // задаёт TrackChangeToastSize. 300 — ширина пресета "Средний", прежнее поведение по
    // умолчанию до появления этого ползунка.
    public double TrackChangeToastWidth { get; set; } = 300.0;

    // Расположение кнопок управления в мини-плеере при наведении курсора. "Below" (по
    // умолчанию) — прежнее поведение: окно подрастает вниз и кнопки появляются отдельной
    // строкой под прогресс-баром. "Overlay" — новый вариант для тех, кому не нужен рост
    // окна: кнопки появляются на месте обложки и названия/исполнителя (та же строка), а
    // сама обложка с текстом на это время просто прячется — высота окна не меняется.
    // См. MiniPlayerWindow.ApplyButtonsLayoutMode.
    public string MiniPlayerButtonsLayout { get; set; } = "Below";

    // Показывать ли полосу прогресса в мини-плеере — по умолчанию включено (прежнее
    // поведение). См. MiniPlayerWindow.ApplyProgressBarVisibility.
    public bool MiniPlayerShowProgress { get; set; } = true;

    // Показывать ли акцентный прогресс вокруг обложки в мини-плеере. Независим от обычной
    // горизонтальной полосы: можно оставить один из индикаторов или включить оба.
    // По умолчанию выключен, чтобы не менять привычный вид уже существующих мини-плееров.
    public bool MiniPlayerShowArtworkProgress { get; set; } = false;

    // Источник цвета контура прогресса обложки: "Accent" — текущий акцент из раздела
    // «Оформление», "Fixed" — отдельный цвет из MiniPlayerArtworkProgressColorHex.
    // Accent — безопасный дефолт и сохраняет поведение первой версии функции.
    public string MiniPlayerArtworkProgressColorMode { get; set; } = "Accent";
    public string MiniPlayerArtworkProgressColorHex { get; set; } = "#0078D4";

    // Место на экране, куда пользователь перетащил мини-плеер в последний раз.
    // null означает "ещё ни разу не задавалось" — тогда используется положение по умолчанию.
    public double? MiniPlayerLeft { get; set; }
    public double? MiniPlayerTop { get; set; }

    // Место на экране, куда пользователь перетащил окно настроек в последний раз — сохраняется
    // между сессиями (см. SettingsWindow.OnLocationChanged/RestoreOrCenterPosition). null
    // означает "пользователь ни разу не двигал окно" — тогда оно открывается по центру экрана
    // (владельца), как и раньше.
    public double? SettingsWindowLeft { get; set; }
    public double? SettingsWindowTop { get; set; }

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

    // Перемотка на несколько секунд вперёд/назад — тот же шаг (5 секунд), что и колесо мыши
    // над прогресс-баром (см. MainWindow.SeekBy). По умолчанию — та же схема Ctrl+Alt, что и
    // у остальных хоткеев, плюс Shift, чтобы не совпасть с уже занятыми Ctrl+Alt+Right/Left
    // (следующий/предыдущий трек).
    public HotkeyBinding HotkeySeekForward { get; set; } = new() { Ctrl = true, Alt = true, Shift = true, Key = "Right" };
    public HotkeyBinding HotkeySeekBackward { get; set; } = new() { Ctrl = true, Alt = true, Shift = true, Key = "Left" };

    // Без Flags: default (пустая) — намеренно не включена по умолчанию, в отличие от остальных
    // хоткеев. Необратимое действие (хоть и через корзину), пользователь должен назначить
    // комбинацию сам, а не рисковать случайно удалить трек хоткеем по умолчанию.
    public HotkeyBinding HotkeyDeleteTrack { get; set; } = new();

    // "Шаффл без повторов" (страница настроек "Воспроизведение") — вместо чисто случайного
    // выбора трека на каждом шаге тасует весь плейлист один раз и проигрывает по порядку из
    // этой тасовки — гарантирует, что каждый трек сыграет один раз, прежде чем какой-либо
    // трек повторится. Имя поля осталось с тех пор, когда настройка называлась "Улучшенный
    // шаффл" и жила на странице "Экспериментальное" — переименовывать поле ради названия в
    // интерфейсе смысла нет, только сбросило бы выбор тем, у кого уже стоит true.
    public bool UseImprovedShuffle { get; set; }

    // ---------- Экспериментальные функции ----------
    // По умолчанию выключены — осознанный выбор пользователя, а не смена привычного
    // поведения без явного согласия.

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

    // Именованные наборы значений эквалайзера, сохранённые пользователем — переключаются
    // и, при необходимости, экспортируются/импортируются как отдельный .json-файл (см.
    // MainWindow.ExportEqualizerPreset/ImportEqualizerPresetFromFile), чтобы поделиться
    // настройкой EQ с кем-то ещё.
    public List<EqualizerPreset> EqualizerPresets { get; set; } = new();

    // ---------- Счётчик прослушиваний ----------
    // Путь к файлу → сколько раз трек реально засчитан как прослушанный (см.
    // PlayCountManager.Increment, вызывается из MainWindow.ProgressTimer_Tick при достижении
    // половины трека). Не переживший переименование/перемещение файла счётчик просто
    // "теряется" вместе с самим ключом — это тот же компромисс, что и у остального
    // плейлиста, который тоже хранится по абсолютным путям.
    public Dictionary<string, int> PlayCounts { get; set; } = new();

    // ---------- Статистика прослушивания (см. StatisticsWindow) ----------
    // Суммарное время реального воспроизведения — накапливается в MainWindow.ProgressTimer_Tick
    // на длительность каждого тика (пока трек действительно играет, а не на паузе), а не
    // вычисляется из длительностей файлов: так, например, перемотка вперёд не "досчитывает"
    // пропущенный кусок как прослушанный.
    public double TotalListenSeconds { get; set; }

    // Момент, с которого вообще собирается статистика (первый когда-либо прослушанный трек
    // после появления этой версии) — null, пока ни разу не было ни одного накопления.
    // Формат — ISO 8601 (DateTime.ToString("O")), как и у остальных дат в этом файле, если бы
    // они тут были; парсится через DateTime.TryParse при отображении, некорректное/отсутствующее
    // значение просто не показываем.
    public string? StatsStartedAt { get; set; }
}

// Загрузка и сохранение настроек в %AppData%\Lumisense\settings.json
public static class SettingsManager
{
    private const long MaxSettingsBytes = 8L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = 16 };
    private static readonly SemaphoreSlim SaveGate = new(1, 1);
    private static long NextSaveRevision;
    private static long LastWrittenRevision;
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
            if (File.Exists(SettingsFilePath) && new FileInfo(SettingsFilePath).Length <= MaxSettingsBytes)
            {
                var json = File.ReadAllText(SettingsFilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings != null)
                {
                    // До разделения настроек режим AccentColorMode=Cover одновременно красил
                    // акцент и основу окна. Сохраняем это ожидаемое поведение для старых файлов,
                    // в которых новый независимый флаг ещё отсутствует.
                    if (!json.Contains("\"CoverBaseFromCover\"", StringComparison.Ordinal)
                        && settings.AccentColorMode == "Cover")
                    {
                        settings.CoverBaseFromCover = true;
                    }

                    MigrateOldFlatPlaylist(settings);
                    return settings;
                }
            }
        }
        catch
        {
            // Повреждённый или недоступный файл настроек — просто используем значения по умолчанию
            Logger.Warn($"Не удалось прочитать settings.json ({SettingsFilePath}) — используются значения по умолчанию");
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
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            WriteJsonAtomic(json, Interlocked.Increment(ref NextSaveRevision));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось сохранить settings.json ({SettingsFilePath}): {ex.Message}");
        }
    }

    // Сериализация snapshot остаётся короткой операцией на вызывающем потоке, а файловая запись
    // и замена файла выполняются в фоне. SemaphoreSlim не даёт двум автосохранениям поменять
    // местами результаты. Финальный Save() при закрытии остаётся синхронным.
    public static async Task SaveAsync(AppSettings settings)
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(settings, JsonOptions);
            var revision = Interlocked.Increment(ref NextSaveRevision);
            await Task.Run(() => WriteJsonAtomic(json, revision)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось асинхронно сохранить settings.json ({SettingsFilePath}): {ex.Message}");
        }
    }

    private static void WriteJsonAtomic(string json, long revision)
    {
        var directory = Path.GetDirectoryName(SettingsFilePath);
        if (directory != null)
            Directory.CreateDirectory(directory);

        SaveGate.Wait();
        try
        {
            if (revision < LastWrittenRevision) return;

            var tempPath = SettingsFilePath + $".{revision}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, SettingsFilePath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                catch { /* best-effort cleanup after an I/O failure */ }
            }
            LastWrittenRevision = revision;
        }
        finally
        {
            SaveGate.Release();
        }
    }
}
