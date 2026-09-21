using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Lumisense;

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

// Один сохранённый пресет эквалайзера — имя и 10 значений гейна по полосам. Отдельный класс,
// а не Dictionary, чтобы формат совпадал в settings.json и при экспорте/импорте пресета.
public class EqualizerPreset
{
    public string Name { get; set; } = "";
    public double[] GainsDb { get; set; } = new double[10];
}

// Одна глобальная комбинация клавиш через WinAPI RegisterHotKey — срабатывает даже без фокуса.
// Win отслеживается отдельно через Keyboard.IsKeyDown, т.к. Keyboard.Modifiers её не учитывает.
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
    // Растёт только при несовместимом изменении формата или семантики settings.json; WASAPI endpoint-ID и хоткеи
    // additive (1.18.0 читает основной профиль), поэтому schema остаётся 7.
    public const int CurrentSettingsSchemaVersion = 7;
    public int SettingsSchemaVersion { get; set; } = CurrentSettingsSchemaVersion;

    // Неизвестные поля из более новой сборки не теряются при временном запуске старой или
    // экспериментальной версии. System.Text.Json вернёт их обратно при следующем Save.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ForwardCompatibleProperties { get; set; }

    // "Dark" / "Light" — выбирается в настройках (страница "Оформление").
    public string Theme { get; set; } = "Dark";

    // Меняется вживую (IconPacks.SetCurrent) на всех открытых окнах; это поле — только то,
    // что подхватывается при следующем запуске.
    public string IconPack { get; set; } = IconPacks.Duotone;

    // Значок приложения (окна, трей) — меняется вживую (AppIcons.SetCurrent), см. AppIcons.cs.
    // Не влияет на иконку самого .exe/ярлыка — та зашита в сборку через ApplicationIcon.
    public string AppIcon { get; set; } = AppIcons.Aurora;

    // Язык статического интерфейса: "ru" или "en". На первом запуске установщик может
    // передать свой выбор через одноразовый marker-файл в папке данных приложения.
    public string Language { get; set; } = "ru";

    // Разрешает Theme в светлую/тёмную; если в settings.json остался устаревший "System", не считаем его тёмной,
    // а смотрим реестр Windows (AppsUseLightTheme).
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

    // Акцент интерфейса (Wpf.Ui ApplicationAccentColorManager): "System" — акцент Windows, "Manual" — AccentColorHex
    // (пресет или палитра — результат один), "Cover" — цвет от текущей обложки.
    public string AccentColorMode { get; set; } = "System";
    public string AccentColorHex { get; set; } = "#0078D4";

    // Доступность интерфейса. Масштаб влияет на базовый размер шрифта окон, а режим
    // снижения движения отключает необязательные декоративные переходы.
    public double InterfaceScale { get; set; } = 1.0;
    public bool ReduceMotion { get; set; }

    // Оформление синхронного LRC-текста в панели главного окна. Старые settings.json не
    // содержат эти поля, поэтому значения по умолчанию сохраняют нейтральный читаемый вид.
    public double SyncedLyricsFontSize { get; set; } = 14.0;
    public string SyncedLyricsHighlightEffect { get; set; } = "Glow";

    // Независимо от AccentColorMode подмешивает к основе окна приглушённый цвет обложки: можно оставить системный
    // акцент с цветной основой или взять акцент от обложки без изменения основы.
    public bool CoverBaseFromCover { get; set; }

    // "Mica" (по умолчанию) или "Acrylic" через DWM backdrop Windows 11; только для MainWindow/SettingsWindow/
    // StatisticsWindow — мелкие диалоги открываются слишком быстро, чтобы разница была заметна.
    public string WindowBackdropType { get; set; } = "Mica";

    // "Slider" (по умолчанию) или "Waveform" (форма звука, см. WaveformView/WaveformGenerator); на мини-плеер не
    // влияет — при высоте полосы 4px волна неразличима.
    public string ProgressBarStyle { get; set; } = "Slider";

    public bool AlwaysOnTop { get; set; }                  // Держать окно поверх остальных
    public bool RememberVolume { get; set; } = true;       // Запоминать громкость между запусками
    // Только для чистого профиля: существующий SavedVolume из settings.json не перезаписывается.
    public double SavedVolume { get; set; } = 0.15;

    // Линейный ползунок сильнее всего меняет громкость на верхнем участке (слух логарифмичен): при включении
    // позиция переводится в дБ (MainWindow.ToOutputVolume); по умолчанию выключено, чтобы сохранить привычную шкалу.
    public bool UseLogarithmicVolume { get; set; }

    // ReplayGain по тегам REPLAYGAIN_* (источник тега определяет ATL.NET, см. ReplayGainReader); по умолчанию выключено —
    // автоматическое изменение громкости без явного выбора пользователя нежелательно.
    public bool ReplayGainEnabled { get; set; }

    // Discord Rich Presence использует локальный Discord IPC и публичный Application ID Lumisense; по умолчанию
    // выключено, публикация названия, исполнителя и таймлайна настраивается отдельно.
    public bool DiscordRichPresenceEnabled { get; set; }
    public bool DiscordRichPresenceShowTrackInfo { get; set; } = true;
    public bool DiscordRichPresenceShowTimeline { get; set; } = true;
    // Только вместе с DiscordRichPresenceShowTrackInfo (обложка выдаёт трек не хуже текста); ищется по артисту/названию
    // через открытые API (DiscordCoverArtLookupService), а не берётся из локального файла.
    public bool DiscordRichPresenceShowCoverArt { get; set; } = true;

    // Темп воспроизведения без изменения высоты тона. 1.0 — обычная скорость.
    public double PlaybackSpeed { get; set; } = 1.0;

    // Независимое изменение высоты тона в полутонах. 0 — исходный тон.
    public double PlaybackPitchSemitones { get; set; } = 0.0;

    public bool MinimizeToTrayOnClose { get; set; } = true; // Сворачивать в трей вместо закрытия

    // Не показывать окно сразу после запуска, только значок в трее; автозапуск с Windows хранится отдельно (StartupManager)
    // и не зависит от этой настройки.
    public bool StartHiddenInTray { get; set; }

    // Запоминаем режим отображения плеера между запусками: был ли он свёрнут в мини-плеер
    // на момент закрытия, и была ли видна панель плейлиста в обычном окне.
    public bool WasMiniPlayerOnClose { get; set; }
    public bool IsPlaylistVisible { get; set; } = true;

    // "Square" / "Rectangular" / "Mini" — выбирается в контекстном меню заголовка "Lumisense"; null — первый запуск
    // (открываем квадратный вид, см. SettingsManager.HasSavedSettingsFile).
    public string? PlayerViewMode { get; set; }

    // Плейлист, сохраняемый между запусками, теперь разбит на группы (папки/отдельные файлы)
    public List<SavedPlaylistFolder> SavedPlaylistFolders { get; set; } = new();

    // Автоподхват новых аудиофайлов в добавленных папках; в старых settings.json поля нет, поэтому true сохраняет
    // ожидаемое поведение после обновления.
    public bool AutoRefreshPlaylistFolders { get; set; } = true;

    // Старое плоское поле читается только для миграции плейлистов до SavedPlaylistFolders; после переноса становится
    // null и не сериализуется, так что новые settings.json не хранят устаревшую копию.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? SavedPlaylist { get; set; }

    // Пути избранных — общий список вне групп плейлиста, из него строится виртуальная группа "Избранное".
    // Порядок — добавления (GetOrder); закреплённые (PinnedFavoriteTracks) поднимаются лишь при показе.
    public List<string> FavoriteTracks { get; set; } = new();

    // Подмножество FavoriteTracks, закреплённое наверху "Избранного" (FavoritesManager.TogglePin); треки — только пути.
    public List<string> PinnedFavoriteTracks { get; set; } = new();

    // По умолчанию очередь "Играть следующим" не переживает перезапуск; если включено, SavedQueue хранит её содержимое
    // (см. MainWindow._playbackQueue, событие Changed).
    public bool SaveQueueBetweenRestarts { get; set; } = false;
    public List<string> SavedQueue { get; set; } = new();

    public string? LastTrackPath { get; set; }              // Путь последнего проигранного трека
    public double LastPositionSeconds { get; set; }          // Позиция в треке на момент закрытия

    // Состояние последнего сеанса: нужно только для решения, продолжать ли именно игравший
    // трек после запуска. Это не пользовательская настройка и не переносится профилями.
    public bool WasPlayingOnClose { get; set; }

    // Запрещает автоматический старт звука при открытии приложения. Последний трек и позиция
    // при этом всё равно восстанавливаются на паузе, чтобы пользователь мог продолжить вручную.
    public bool NeverAutoPlayLastTrackOnStartup { get; set; }

    // Состояние "Перемешать" и "Повтор" запоминается между запусками; RepeatMode хранится строкой (имя значения
    // MainWindow.RepeatMode), чтобы смена порядка значений перечисления не ломала settings.json.
    public bool IsShuffleEnabled { get; set; }
    public string RepeatMode { get; set; } = "Off";

    // История шаффла — данные сессии, а не настройка: пройденные треки, индекс и остаток колоды, чтобы "Назад"
    // после перезапуска возвращала к тем же композициям.
    public List<string> ShuffleHistory { get; set; } = new();
    public int ShuffleHistoryIndex { get; set; } = -1;
    public List<string> ShuffleBag { get; set; } = new();

    // Анимация смены обложки при переключении трека (старая улетает, новая влетает, как в iTunes); можно отключить
    // (см. MainWindow.SetAlbumArtTransitionEnabled).
    public bool AlbumArtTransitionEnabled { get; set; } = true;

    // Касание/свайпы на обложке: касание — пуск/пауза, горизонтальный свайп — трек, вертикальный — громкость;
    // при отключении клик снова открывает просмотр обложки.
    public bool AlbumArtGesturesEnabled { get; set; } = true;

    public double MiniPlayerOpacity { get; set; } = 1.0;

    // Ручной режим совместимости с играми/overlay: меньше WPF/DWM-композиции (без декоративных анимаций и теней toast,
    // фон мини-плеера плотный). Выключен по умолчанию, чтобы не менять обычный вид.
    public bool GameOverlayCompatibilityMode { get; set; }
    // Периодическая проверка на полноэкранное окно и известные процессы оверлеев (см.
    // GameOverlayDetectionService); можно отключить, не трогая ручную галочку выше.
    public bool GameOverlayCompatibilityAutoDetect { get; set; } = true;

    // Default — скруглённая обложка; Vinyl — круглая «пластинка», вращается только при воспроизведении;
    // StaticCircle — круглая неподвижная (см. MiniPlayerWindow).
    public string MiniPlayerArtworkStyle { get; set; } = "Default";
    public bool MiniPlayerAlwaysOnTop { get; set; } = true;
    public bool MiniPlayerPinned { get; set; }               // Запрещает перетаскивание окна мышью

    // "Магнитное" прилипание к краям при перетаскивании (WindowSnapHelper); MiniPlayerPinned сильнее — при закреплении
    // перетаскивания нет. У обычного окна такой настройки нет: с системным ui:TitleBar (HTCAPTION) прилипание ненадёжно.
    public bool MiniPlayerSnapToEdges { get; set; } = true;

    // Какая из кнопок (повтор/перемешать) показывается в компактном мини-плеере — места хватает лишь на одну "вторую";
    // "Repeat" по умолчанию сохраняет прежнее поведение.
    public string MiniPlayerSecondaryButton { get; set; } = "Repeat";

    // Вторая строка заголовка мини-плеера: "TitleArtist" (по умолчанию) — исполнитель, "TitleOnly" — строка скрыта,
    // "TitleRemaining" — оставшееся время (см. MiniPlayerWindow.UpdateSecondaryLine).
    public string MiniPlayerInfoMode { get; set; } = "TitleArtist";

    // Устойчивый WASAPI endpoint-ID выбранного устройства (`wasapi:{...}`); пусто — системный endpoint Windows,
    // который после отключения USB/Bluetooth-наушников может уйти на новое устройство. Старые WaveOut-имена мигрируют.
    public string OutputDeviceName { get; set; } = "";

    // "Shared" (по умолчанию) или "Exclusive" — при ошибке инициализации EnsureOutputDevice сам
    // откатывает это значение на "Shared" и просит SettingsWindow обновить ComboBox.
    public string WasapiMode { get; set; } = "Shared";

    // Однократная попытка удалить унаследованный HKLM-ключ контекстного меню уже была — не
    // повторять, даже если пользователь отклонил UAC (см. TryCleanupLegacyHklmWildcardContextMenu).
    public bool HklmWildcardContextMenuCleanupAttempted { get; set; }

    // Подробный технический trace этапов загрузки трека выключен по умолчанию. Он не содержит
    // пути или метаданные, но помогает измерять редкое замедление Next/Previous локально.
    public bool TrackLoadTraceEnabled { get; set; }

    public string LyricsSearchPolicy { get; set; } = "AutoExact";

    // Всплывающее уведомление в углу экрана при смене трека (обложка + название, исчезает
    // само через пару секунд) — см. TrackChangeToastWindow и MainWindow.ShowTrackChangeToast.
    public bool ShowTrackChangeToast { get; set; } = true;

    // Когда показывать карточку: EveryTrackChange — прежнее поведение; PlaybackOnly — не при выборе на паузе;
    // ManualOnly — не при естественном переходе, восстановлении сессии и перезагрузке после внешней правки.
    public string TrackChangeToastPolicy { get; set; } = "EveryTrackChange";

    // Где на экране показывать уведомление — "BottomRight" (по умолчанию, как было всегда),
    // "BottomLeft", "BottomCenter", "TopRight", "TopLeft" или "TopCenter".
    public string TrackChangeToastPosition { get; set; } = "BottomRight";

    // Монитор уведомления: пусто — тот же, что у основного окна (MainWindow.ResolveToastScreen), иначе Screen.DeviceName
    // ("\\.\DISPLAY1"); если монитор пропал, тихо откатываемся на автоматический выбор.
    public string TrackChangeToastMonitor { get; set; } = "";

    // Размер карточки "Small" / "Medium" (по умолчанию) / "Large": высота, обложка, шрифты и отступы текста
    // (TrackChangeToastWindow.ApplySizePreset); не зависит от ширины ниже.
    public string TrackChangeToastSize { get; set; } = "Medium";

    // Ширина карточки в пикселях (SettingsWindow.ToastWidthSlider), независимо от размера: меняет только ширину и
    // сколько текста влезает до многоточия. 300 — ширина пресета "Средний", прежнее поведение до появления ползунка.
    public double TrackChangeToastWidth { get; set; } = 300.0;

    // С какой стороны карточки показывать обложку — "Left" (по умолчанию, как было всегда) или
    // "Right" (см. TrackChangeToastWindow.ApplyLayout).
    public string TrackChangeToastArtSide { get; set; } = "Left";

    // Как выравнивать название/исполнителя в оставшемся месте карточки относительно обложки —
    // "Left" (по умолчанию), "Center" или "Right" (см. ApplyLayout).
    public string TrackChangeToastTextAlignment { get; set; } = "Left";

    // Кнопки мини-плеера при наведении: "Below" (по умолчанию) — окно подрастает вниз, кнопки отдельной строкой;
    // "Overlay" — кнопки поверх обложки и текста, высота окна не меняется (MiniPlayerWindow.ApplyButtonsLayoutMode).
    public string MiniPlayerButtonsLayout { get; set; } = "Below";

    // Показывать ли полосу прогресса в мини-плеере — по умолчанию включено (прежнее
    // поведение). См. MiniPlayerWindow.ApplyProgressBarVisibility.
    public bool MiniPlayerShowProgress { get; set; } = true;

    // Акцентный прогресс вокруг обложки; независим от горизонтальной полосы, можно включить оба индикатора.
    // Выключен по умолчанию, чтобы не менять вид существующих мини-плееров.
    public bool MiniPlayerShowArtworkProgress { get; set; } = false;

    // Цвет контура: "Accent" — акцент из «Оформления», "Fixed" — MiniPlayerArtworkProgressColorHex;
    // Accent — безопасный дефолт и поведение первой версии функции.
    public string MiniPlayerArtworkProgressColorMode { get; set; } = "Accent";
    public string MiniPlayerArtworkProgressColorHex { get; set; } = "#0078D4";

    // Толщина трека и акцентного контура progress вокруг обложки мини-плеера. Значение
    // хранится в DIP и ограничивается SettingsIntegrityService (2–4).
    public double MiniPlayerArtworkProgressThickness { get; set; } = 2.5;

    // Место на экране, куда пользователь перетащил мини-плеер в последний раз.
    // null означает "ещё ни разу не задавалось" — тогда используется положение по умолчанию.
    public double? MiniPlayerLeft { get; set; }
    public double? MiniPlayerTop { get; set; }

    // Последняя позиция окна настроек между сессиями (SettingsWindow.OnLocationChanged/RestoreOrCenterPosition);
    // null — окно ни разу не двигали, открывается по центру экрана владельца.
    public double? SettingsWindowLeft { get; set; }
    public double? SettingsWindowTop { get; set; }

    // Глобальные хоткеи; по умолчанию Ctrl+Alt+<клавиша>, чтобы не конфликтовать с набором текста. Работают в
    // дополнение к физическим мультимедийным клавишам, которые всегда активны и не настраиваются.
    public HotkeyBinding HotkeyPlayPause { get; set; } = new() { Ctrl = true, Alt = true, Key = "P" };
    public HotkeyBinding HotkeyNext { get; set; } = new() { Ctrl = true, Alt = true, Key = "Right" };
    public HotkeyBinding HotkeyPrevious { get; set; } = new() { Ctrl = true, Alt = true, Key = "Left" };
    public HotkeyBinding HotkeyStop { get; set; } = new() { Ctrl = true, Alt = true, Key = "S" };
    public HotkeyBinding HotkeyVolumeUp { get; set; } = new() { Ctrl = true, Alt = true, Key = "Up" };
    public HotkeyBinding HotkeyVolumeDown { get; set; } = new() { Ctrl = true, Alt = true, Key = "Down" };
    public HotkeyBinding HotkeyMute { get; set; } = new() { Ctrl = true, Alt = true, Key = "M" };
    public HotkeyBinding HotkeyShuffle { get; set; } = new() { Ctrl = true, Alt = true, Key = "U" };
    public HotkeyBinding HotkeyRepeat { get; set; } = new() { Ctrl = true, Alt = true, Key = "R" };
    public HotkeyBinding HotkeyToggleFavorite { get; set; } = new() { Ctrl = true, Alt = true, Key = "F" };
    public HotkeyBinding HotkeyToggleLyrics { get; set; } = new() { Ctrl = true, Alt = true, Key = "L" };
    public HotkeyBinding HotkeyToggleMiniPlayer { get; set; } = new() { Ctrl = true, Alt = true, Key = "N" };

    // Перемотка на 5 секунд, как колесо над прогресс-баром (MainWindow.SeekBy); Ctrl+Alt+Shift, чтобы не совпасть
    // с занятыми Ctrl+Alt+Right/Left (следующий/предыдущий трек).
    public HotkeyBinding HotkeySeekForward { get; set; } = new() { Ctrl = true, Alt = true, Shift = true, Key = "Right" };
    public HotkeyBinding HotkeySeekBackward { get; set; } = new() { Ctrl = true, Alt = true, Shift = true, Key = "Left" };

    // Не включена по умолчанию (пустая привязка): удаление необратимо, пользователь назначает комбинацию сам,
    // а не рискует удалить трек хоткеем по умолчанию.
    public HotkeyBinding HotkeyDeleteTrack { get; set; } = new();

    // "Шаффл без повторов": один раз тасует весь плейлист, каждый трек играет один раз до повторов. Имя поля осталось
    // от "Улучшенного шаффла": переименование сбросило бы выбор у тех, у кого уже true.
    public bool UseImprovedShuffle { get; set; }

    // Экспериментальные функции — выключены по умолчанию: выбор пользователя, а не смена поведения без согласия.

    // Убирает фон у кнопок управления главного окна (MainWindow.ApplyPlaybackButtonsVisibility): они остаются
    // видимыми и кликабельными, виден только значок.
    public bool HidePlaybackButtons { get; set; }

    // Версия, отклонённая кнопкой "Позже": диалог не всплывает снова для неё, но появится с более новой версией.
    // Ручная проверка в настройках показывает результат всегда (см. UpdateChecker).
    public string? SkippedUpdateVersion { get; set; }

    // Откуда качать установщик: "GitHub" или зеркало gh-proxy (UpdateChecker.DownloadSources/ApplyDownloadSource);
    // проверка версии (api.github.com) всегда идёт напрямую.
    public string UpdateDownloadSource { get; set; } = "GitHub";

    // 10 ISO-полос графического EQ (EqualizerSampleProvider); при другой длине сохранённого массива SettingsWindow и
    // MainWindow подстраиваются под BandFrequencies, а не доверяют длине массива.
    public bool EqualizerEnabled { get; set; }

    // Bypass временно пропускает сигнал мимо фильтров, но не меняет включение EQ, значения
    // полос или пресеты. После выключения Bypass сохранённая коррекция возвращается сразу.
    public bool EqualizerBypass { get; set; }

    public double[] EqualizerBandGainsDb { get; set; } = new double[10];

    // Шаблон безопасной нормализации имён аудиофайлов, см. FileNameNormalizer. Операция
    // выполняется только вручную по подтверждённому предпросмотру и не запускается при импорте.
    public string FileNameNormalizationTemplate { get; set; } = FileNameNormalizer.DefaultTemplate;

    // Скрытые действия контекстного меню трека; пустой список — полное меню, базовое «Воспроизвести» не отключается,
    // чтобы у меню всегда было понятное основное действие.
    public List<string> DisabledTrackContextMenuActions { get; set; } = new();

    // Скрытые пункты контекстного меню мини-плеера (MiniPlayerContextMenuActions); в отличие от меню трека можно скрыть и
    // "Настройки" (страница есть в основном окне и трее). Пустой список — всё меню.
    public List<string> DisabledMiniPlayerContextMenuActions { get; set; } = new()
    {
        MiniPlayerContextMenuActions.NowPlaying,
        MiniPlayerContextMenuActions.OverlayCompatibility,
        MiniPlayerContextMenuActions.Opacity,
        MiniPlayerContextMenuActions.SnapToEdges,
        MiniPlayerContextMenuActions.ShowProgress,
        MiniPlayerContextMenuActions.ShowArtworkProgress,
        MiniPlayerContextMenuActions.ArtworkStyle,
        MiniPlayerContextMenuActions.ButtonsLayout
    };

    // Именованные наборы EQ пользователя; экспортируются/импортируются как .json (MainWindow.ExportEqualizerPreset/
    // ImportEqualizerPresetFromFile), чтобы делиться настройкой.
    public List<EqualizerPreset> EqualizerPresets { get; set; } = new();

    // Путь → сколько раз трек засчитан прослушанным (PlayCountManager.Increment, при половине трека); счётчик
    // теряется при переименовании файла — тот же компромисс, что у плейлиста с абсолютными путями.
    public Dictionary<string, int> PlayCounts { get; set; } = new();

    // Суммарное реальное время воспроизведения копится по тикам MainWindow.ProgressTimer_Tick (пока трек играет),
    // а не из длительностей файлов, поэтому перемотка вперёд не засчитывает пропущенное.
    public double TotalListenSeconds { get; set; }

    // Момент начала сбора статистики (первое прослушивание после появления этой версии), null — накоплений не было;
    // ISO 8601 ("O"), парсится через DateTime.TryParse, некорректное значение не показываем.
    public string? StatsStartedAt { get; set; }
}

// Загрузка и сохранение настроек в %AppData%\Lumisense\settings.json
public static class SettingsManager
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = 16 };
    private static readonly SemaphoreSlim SaveGate = new(1, 1);
    private static long NextSaveRevision;
    private static long LastWrittenRevision;
    private static string? LastObservedSettingsJson;
    private static int CheckpointInProgress;
    private static readonly string SettingsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lumisense", "settings.json");

    // Резервный снимок создаётся только из состояния с пользовательскими данными и, в отличие от settings.json,
    // защищает не только пути плейлиста, но и избранное, закрепления, счётчики, время и последнее воспроизведение.
    private static readonly string UserDataRecoveryBackupPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lumisense", "settings.user-data-backup.json");

    // Сохраняем прежнее имя параллельно для уже созданных копий и понятной ручной диагностики.
    private static readonly string PlaylistRecoveryBackupPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lumisense", "settings.playlist-backup.json");

    // true, если файл настроек уже сохранялся: отличает первый запуск (открываем квадратный вид) от старых настроек,
    // где вид подбирается по IsPlaylistVisible/WasMiniPlayerOnClose, чтобы после обновления ничего не переключилось.
    public static bool HasSavedSettingsFile => File.Exists(SettingsFilePath);

    public static AppSettings Load()
    {
        if (SettingsIntegrityService.TryLoad(SettingsFilePath, out AppSettings? settings, out string? failure) && settings != null)
        {
            RememberObservedSnapshot(settings);
            return settings;
        }

        if (File.Exists(SettingsFilePath))
            Logger.Warn($"Не удалось прочитать settings.json ({SettingsFilePath}): {failure ?? "неизвестная ошибка"}");

        if (SettingsIntegrityService.TryLoadLatestRecoveryBackup(SettingsFilePath, out AppSettings? recovered) && recovered != null)
        {
            Logger.Warn("Основной settings.json недоступен — восстановлено последнее корректное поколение резервной копии.");
            RememberObservedSnapshot(recovered);
            return recovered;
        }

        var defaults = new AppSettings();
        RememberObservedSnapshot(defaults);
        return defaults;
    }

    // false только при неудаче записи; большинство вызовов результат игнорирует, а Velopack apply использует его
    // как барьер перед плановым перезапуском.
    public static bool Save(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            // Публикуем готовый JSON до I/O: если процесс оборвётся в WriteJsonAtomic,
            // аварийный обработчик сможет сохранить тот же снимок без чтения WPF-состояния.
            Volatile.Write(ref LastObservedSettingsJson, json);
            WriteJsonAtomic(json, Interlocked.Increment(ref NextSaveRevision));
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось сохранить settings.json ({SettingsFilePath}): {ex.Message}");
            return false;
        }
    }

    // Для ProcessExit/Console.CancelKeyPress вне WPF Dispatcher: без сериализации модели и обращения к MainWindow
    // пишем только готовый JSON-снимок, опубликованный при Load/Save/SaveAsync/checkpoint.
    internal static void SaveLastObservedSnapshot()
    {
        string? json = Volatile.Read(ref LastObservedSettingsJson);
        if (string.IsNullOrEmpty(json)) return;

        try
        {
            WriteJsonAtomic(json, Interlocked.Increment(ref NextSaveRevision));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось сохранить аварийный снимок settings.json ({SettingsFilePath}): {ex.Message}");
        }
    }

    // Сериализация — коротко на вызывающем потоке, запись и замена файла — в фоне; SemaphoreSlim не даёт двум
    // автосохранениям поменять результаты местами. Финальный Save() при закрытии остаётся синхронным.
    public static async Task SaveAsync(AppSettings settings)
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(settings, JsonOptions);
            Volatile.Write(ref LastObservedSettingsJson, json);
            var revision = Interlocked.Increment(ref NextSaveRevision);
            await Task.Run(() => WriteJsonAtomic(json, revision)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось асинхронно сохранить settings.json ({SettingsFilePath}): {ex.Message}");
        }
    }

    // Общий checkpoint для изменений только в памяти: сравнение готового JSON позволяет таймеру работать часто
    // (на случай закрытия консоли), не создавая запись на диск без изменений.
    public static async Task SaveIfChangedAsync(AppSettings settings)
    {
        if (Interlocked.CompareExchange(ref CheckpointInProgress, 1, 0) != 0)
            return;

        try
        {
            string json;
            try
            {
                json = JsonSerializer.Serialize(settings, JsonOptions);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Не удалось подготовить snapshot настроек ({SettingsFilePath}): {ex.Message}");
                return;
            }

            if (string.Equals(json, Volatile.Read(ref LastObservedSettingsJson), StringComparison.Ordinal))
                return;

            Volatile.Write(ref LastObservedSettingsJson, json);
            var revision = Interlocked.Increment(ref NextSaveRevision);
            await Task.Run(() => WriteJsonAtomic(json, revision)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось записать checkpoint настроек ({SettingsFilePath}): {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref CheckpointInProgress, 0);
        }
    }

    private static void RememberObservedSnapshot(AppSettings settings)
    {
        try
        {
            Volatile.Write(ref LastObservedSettingsJson, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            Volatile.Write(ref LastObservedSettingsJson, null);
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
                TryBackupUserData(json);
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

    private static void TryBackupUserData(string candidateJson) =>
        SettingsIntegrityService.CreateRecoveryBackups(
            candidateJson,
            SettingsFilePath,
            UserDataRecoveryBackupPath,
            PlaylistRecoveryBackupPath);
}
