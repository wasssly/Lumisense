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

    // Треки папки-источника, которые пользователь убрал из плейлиста: пересканирование их не возвращает.
    public List<string> ExcludedTracks { get; set; } = new();

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

    // Та же комбинация: все модификаторы совпадают, клавиша сравнивается без учёта регистра.
    // Пустая комбинация ни с чем не совпадает (две не заданные функции — не конфликт).
    public bool SameCombinationAs(HotkeyBinding? other) =>
        other is not null && !IsEmpty && !other.IsEmpty &&
        Ctrl == other.Ctrl && Alt == other.Alt && Shift == other.Shift && Win == other.Win &&
        string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase);
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
    public string AppIcon { get; set; } = AppIcons.Default;

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
    // "Glow" | "GlowPill" | "Pill" | "Bar" | "None" (см. LyricsHighlightStyle).
    public string SyncedLyricsHighlightEffect { get; set; } = "Glow";
    // Дополнительные приёмы выделения строки; применяются в Now Playing.
    public bool SyncedLyricsScaleActiveLine { get; set; }
    public bool SyncedLyricsFadeDistantLines { get; set; }
    // Степень приглушения дальних строк, 0–100 %.
    public int SyncedLyricsFadeStrength { get; set; } = 50;
    // Насколько увеличивается активная строка, 2–15 %.
    public int SyncedLyricsScalePercent { get; set; } = 5;
    // Сколько строк видно в Now Playing: 0 — все, иначе нечётное 3–15.
    public int SyncedLyricsVisibleLines { get; set; }
    // Сила свечения активной строки, 0–100 % (в Now Playing и в панели текста главного окна).
    public int SyncedLyricsGlowStrength { get; set; } = LyricsHighlightStyle.DefaultGlowStrength;
    // Размытие неактивных строк в Now Playing и его сила (радиус дальних строк, px).
    public bool SyncedLyricsBlurInactive { get; set; }
    public int SyncedLyricsBlurStrength { get; set; } = LyricsHighlightStyle.DefaultBlurStrength;
    // Размер шрифта текста песни в Now Playing, px (синхронный и обычный текст).
    public int NowPlayingLyricsFontSize { get; set; } = LyricsHighlightStyle.DefaultNowPlayingFontSize;

    // Выравнивание текста песни в панели главного окна и в Now Playing: "Left" | "Center" | "Right".
    public string LyricsTextAlignment { get; set; } = "Left";

    // Анимация сердечка при добавлении в избранное: "None" | "Sparks" | "Fill" | "Ring".
    public string FavoriteHeartAnimation { get; set; } = "Fill";

    // Фон Now Playing: "Clouds" (облака из палитры обложки) | "Orbs" (мягкие цветные шары на орбитах) | "Waves" (размытые волны).
    public string NowPlayingBackground { get; set; } = "Clouds";

    // Вид навигации в окне настроек: "Sidebar" (боковая панель) | "Pills" (таблетки сверху) |
    // "FloatingBar" (плавающая нижняя панель).
    public string SettingsNavigationStyle { get; set; } = "Sidebar";

    // Плавающая нижняя панель: показывать названия пунктов под иконками (иначе только иконки с подсказками).
    public bool SettingsNavigationShowLabels { get; set; }

    // Как показывать названия в плавающей панели: "Below" (под иконками у всех пунктов) | "Selected" (рядом с иконкой только у
    // выбранного пункта). Действует только при SettingsNavigationShowLabels.
    public string SettingsNavigationLabelMode { get; set; } = "Below";

    // Дополнительный источник текстов песен (NetEase через Lyricify.Lyrics.Helper); по умолчанию выключен.
    public bool UseExtraLyricsSource { get; set; }

    // Размер мини-плеера: "Compact" | "Current" | "Classic" (по умолчанию размер версии 1.21.0).
    public string MiniPlayerSizePreset { get; set; } = "Classic";

    // Вид мини-плеера: "Classic" (плавающая карточка) | "Island" (капсула у верхнего края экрана, раскрывается по клику).
    public string MiniPlayerStyle { get; set; } = "Classic";

    // Монитор острова: пусто — основной экран, иначе Screen.DeviceName; если монитор пропал, остров уходит на основной.
    public string IslandMonitor { get; set; } = "";

    // Настройки острова: масштаб 80–120 %, отступ от верхнего края 0–40 px. Размытия в прозрачном окне считаются на процессоре,
    // поэтому по умолчанию выключены; скрытие по средней кнопке — 3–60 с.
    public int IslandScalePercent { get; set; } = 100;
    public int IslandTopGap { get; set; } = 6;
    public bool IslandTrackToast { get; set; } = true;
    public bool IslandAutoCollapse { get; set; } = true;
    public bool IslandShowEqualizer { get; set; } = true;
    public bool IslandAccentRim { get; set; } = true;
    public bool IslandBlurTransitions { get; set; }
    public int IslandHideSeconds { get; set; } = 10;
    // Через сколько секунд после ухода курсора раскрытый остров сворачивается (1–15).
    public int IslandCollapseSeconds { get; set; } = 2;
    public bool IslandMotionBlur { get; set; }
    public bool IslandCoverPreview { get; set; } = true;
    public bool IslandExternalVolume { get; set; } = true;
    public bool IslandQuietInGames { get; set; } = true;
    // Перетягивание острова: по умолчанию влево — следующий трек; включённый флажок меняет направления местами.
    public bool IslandSwipeInverted { get; set; }
    // Функция боковых кнопок раскрытого острова: "Favorite" | "Repeat" | "Lyrics".
    public string IslandLeftButton { get; set; } = "Favorite";
    public string IslandRightButton { get; set; } = "Lyrics";
    public bool IslandRoundCover { get; set; }
    // Ширина свёрнутого и раскрытого острова в процентах от обычной (80–130).
    public int IslandWidthPercent { get; set; } = 100;
    // Не показывать уведомление о смене трека в углу экрана, пока мини-плеер в виде «Остров»: остров показывает его сам.
    public bool IslandHideSystemToast { get; set; }
    // Скрытые пункты контекстного меню острова: PlayPause, Next, Previous, Hide, Expand, NowPlaying, Speed, Pitch, Overlay, Settings.
    // По умолчанию выключены скорость, тон и совместимость с оверлеем.
    public List<string> DisabledIslandMenuActions { get; set; } = new() { "Speed", "Pitch", "Overlay" };
    // Вид эквалайзера свёрнутого острова: "Bars" (полоски) | "Dots" (точки).
    public string IslandEqualizerStyle { get; set; } = "Bars";
    // Фон раскрытого острова: "None" | "Glow" | "Matrix" | "Wave".
    public string IslandBackdrop { get; set; } = "Glow";
    // Полоса перемотки раскрытого острова: "Plain" (сплошная) | "Segments" (сегменты).
    public string IslandSeekStyle { get; set; } = "Plain";
    public bool IslandLyricsShimmer { get; set; } = true;

    // Прятать обложку, пока открыта панель «Текст песни», чтобы тексту хватало места.
    public bool HideCoverInLyricsPanel { get; set; } = true;

    // Независимо от AccentColorMode подмешивает к основе окна приглушённый цвет обложки: можно оставить системный
    // акцент с цветной основой или взять акцент от обложки без изменения основы.
    public bool CoverBaseFromCover { get; set; }

    // "Mica" (по умолчанию), "MicaAlt", "Acrylic" через DWM backdrop Windows 11 или "Blur" — обычное размытие (DwmBlurBackdrop)
    // (WindowBackdropOptions); только для MainWindow/SettingsWindow/
    // StatisticsWindow — мелкие диалоги открываются слишком быстро, чтобы разница была заметна.
    public string WindowBackdropType { get; set; } = "Mica";

    // "Slider" (по умолчанию), "Waveform" (форма звука, см. WaveformView/WaveformGenerator), "Material"
    // (волнистая линия, см. MaterialProgressView) или "MaterialSlider" (ползунок Material, см. MaterialSliderVisual).
    // Относится к главному окну и Now Playing (там Waveform заменяется обычной полосой); мини-плеер настраивается отдельно.
    public string ProgressBarStyle { get; set; } = "Slider";

    // "Default" (по умолчанию) или "Material" (тонкая ручка и зазор, см. MaterialSliderVisual) — вид всех ползунков
    // в приложении, включая вертикальные в эквалайзере.
    public string SliderStyle { get; set; } = "Default";

    public bool AlwaysOnTop { get; set; }                  // Держать окно поверх остальных
    public bool RememberVolume { get; set; } = true;       // Запоминать громкость между запусками
    // Только для чистого профиля: существующий SavedVolume из settings.json не перезаписывается.
    public double SavedVolume { get; set; } = 0.15;

    // Линейный ползунок сильнее всего меняет громкость на верхнем участке (слух логарифмичен): при включении
    // позиция переводится в дБ (TrackPreparationService.ComputeAudioFileVolume); по умолчанию выключено, чтобы сохранить привычную шкалу.
    public bool UseLogarithmicVolume { get; set; }

    // ReplayGain по тегам REPLAYGAIN_* (источник тега определяет ATL.NET, см. ReplayGainReader); по умолчанию выключено —
    // автоматическое изменение громкости без явного выбора пользователя нежелательно.
    public bool ReplayGainEnabled { get; set; }

    // "Stereo" (по умолчанию, звук как в файле) или "Mono" (каналы сводятся в один, см. ChannelModeSampleProvider).
    public string PlaybackChannelMode { get; set; } = "Stereo";

    // Что показывает подпись под названием трека на главном экране: "State" (по умолчанию, состояние воспроизведения),
    // "Extension" (формат файла), "Quality" (битрейт и частота дискретизации), "FileSize" или "Custom" — набор из частей ниже,
    // объединённых через « · ». Загрузка и ошибка всегда показываются как состояние (MainWindow.UpdateTrackUserStatePresentation).
    public string TrackStateBadgeMode { get; set; } = "State";

    // Части режима "Custom". Если ни одна не подходит (ничего не отмечено или у файла нет данных), остаётся состояние.
    public bool TrackStateBadgeShowState { get; set; } = true;
    public bool TrackStateBadgeShowExtension { get; set; } = true;
    public bool TrackStateBadgeShowBitrate { get; set; }
    public bool TrackStateBadgeShowSampleRate { get; set; }
    public bool TrackStateBadgeShowFileSize { get; set; }

    // Как показывать текст песни, когда у трека есть синхронный (LRC): "Synced" (по умолчанию) или "Plain" — тот же текст
    // без привязки ко времени (LyricsService.ApplyDisplayMode). У трека только с обычным текстом ничего не меняется.
    public string LyricsDisplayMode { get; set; } = "Synced";

    // Полоса воспроизведения (вид и ProgressBarAppearance): "Shared" — одна на все окна (стиль ProgressBarStyle и профиль
    // ProgressAppearanceMain), "PerWindow" — у главного окна, Now Playing и мини-плеера своя. По умолчанию "PerWindow": так и работало
    // раньше (у мини-плеера свой стиль, Now Playing повторяет главное окно), а профили у всех окон одинаковые по умолчанию.
    public string ProgressAppearanceScope { get; set; } = "PerWindow";

    // Стиль полосы Now Playing при ProgressAppearanceScope == "PerWindow": "" — как в главном окне (по умолчанию), иначе
    // "Slider", "Material" или "MaterialSlider" (Waveform там недоступен). См. ProgressStyleResolver.
    public string NowPlayingProgressStyle { get; set; } = "";
    public ProgressBarAppearance ProgressAppearanceMain { get; set; } = new();
    public ProgressBarAppearance ProgressAppearanceNowPlaying { get; set; } = new();
    public ProgressBarAppearance ProgressAppearanceMini { get; set; } = new();

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

    // Стиль анимации смены обложки: "Slide" (старая улетает, новая влетает) | "Carousel" (лента из предыдущей, текущей и следующей).
    public string AlbumArtTransitionStyle { get; set; } = "Slide";

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
    // Бегущая строка для исполнителя в мини-плеере. По умолчанию выключена, чтобы не добавлять
    // постоянную анимацию пользователям, которым достаточно статичной второй строки.
    public bool MiniPlayerArtistMarquee { get; set; }

    // Устойчивый WASAPI endpoint-ID выбранного устройства (`wasapi:{...}`); пусто — системный endpoint Windows,
    // который после отключения USB/Bluetooth-наушников может уйти на новое устройство. Старые WaveOut-имена мигрируют.
    public string OutputDeviceName { get; set; } = "";

    // "Shared" (по умолчанию) или "Exclusive" — при ошибке инициализации EnsureOutputDevice сам
    // откатывает это значение на "Shared" и просит SettingsWindow обновить ComboBox.
    public string WasapiMode { get; set; } = "Shared";

    // Размер буфера вывода WASAPI, мс: 60 (малая задержка), 100 (по умолчанию) или 200 (максимальная устойчивость к щелчкам,
    // когда процессор загружен). Настройка машины: в профиль не переносится. Применяется при следующем открытии вывода.
    public int WasapiLatencyMilliseconds { get; set; } = 100;

    // Повышенный приоритет процесса: при загрузке процессора под 100% плеер получает его раньше фоновых программ и сборщик
    // мусора не ждёт «голодные» потоки, из-за чего звук щёлкал. Настройка машины: в профиль не переносится.
    public bool RaisePlaybackPriority { get; set; } = true;

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
    // "BottomLeft", "BottomCenter", "TopRight", "TopLeft", "TopCenter" или "Custom".
    public string TrackChangeToastPosition { get; set; } = "BottomRight";

    // Для TrackChangeToastPosition == "Custom": доля свободной области монитора по горизонтали и вертикали (0 — левый/верхний
    // край, 1 — правый/нижний), чтобы выбранное место не зависело от разрешения и масштаба.
    public double TrackChangeToastCustomX { get; set; }
    public double TrackChangeToastCustomY { get; set; }

    // Цвет карточки: "Default" (по теме, как было), "Cover" (цвет текущей обложки на момент показа), "Accent" (акцент приложения)
    // или "Custom" (TrackChangeToastColorHex). Strength — доля цвета в фоне, 0.2..1.0; текст подбирается по яркости фона
    // (ToastColorCalculator).
    public string TrackChangeToastColorMode { get; set; } = "Default";
    public string TrackChangeToastColorHex { get; set; } = "#0078D4";
    public double TrackChangeToastColorStrength { get; set; } = 0.7;

    // Монитор уведомления: пусто — тот же, что у основного окна (MainWindow.ResolveToastScreen), иначе Screen.DeviceName
    // ("\\.\DISPLAY1"); если монитор пропал, тихо откатываемся на автоматический выбор.
    public string TrackChangeToastMonitor { get; set; } = "";

    // Размер карточки "Small" / "Medium" (по умолчанию) / "Large": высота, обложка, шрифты и отступы текста
    // (TrackChangeToastWindow.ApplySizePreset); не зависит от ширины ниже.
    public string TrackChangeToastSize { get; set; } = "Medium";

    // Ширина карточки в пикселях (SettingsWindow.ToastWidthSlider), независимо от размера: меняет только ширину и
    // сколько текста влезает до многоточия. 300 — ширина пресета "Средний", прежнее поведение до появления ползунка.
    public double TrackChangeToastWidth { get; set; } = 300.0;

    // Непрозрачность фона уведомления (0.3–1.0); текст и обложка всегда непрозрачны.
    public double TrackChangeToastOpacity { get; set; } = 1.0;

    // С какой стороны карточки показывать обложку — "Left" (по умолчанию, как было всегда) или
    // "Right" (см. TrackChangeToastWindow.ApplyLayout).
    public string TrackChangeToastArtSide { get; set; } = "Left";

    // Как выравнивать название/исполнителя в оставшемся месте карточки относительно обложки —
    // "Left" (по умолчанию), "Center" или "Right" (см. ApplyLayout).
    public string TrackChangeToastTextAlignment { get; set; } = "Left";

    // При выравнивании текста "Center": обложка и текст центрируются вместе, обложка стоит вплотную к тексту.
    public bool TrackChangeToastArtNextToText { get; set; }

    // Кнопки мини-плеера при наведении: "Below" (по умолчанию) — окно подрастает вниз, кнопки отдельной строкой;
    // "Overlay" — кнопки поверх обложки и текста, высота окна не меняется (MiniPlayerWindow.ApplyButtonsLayoutMode).
    public string MiniPlayerButtonsLayout { get; set; } = "Below";

    // Показывать ли полосу прогресса в мини-плеере — по умолчанию включено (прежнее
    // поведение). См. MiniPlayerWindow.ApplyProgressBarVisibility.
    public bool MiniPlayerShowProgress { get; set; } = true;

    // Акцентный прогресс вокруг обложки; независим от горизонтальной полосы, можно включить оба индикатора.
    // Выключен по умолчанию, чтобы не менять вид существующих мини-плееров.
    public bool MiniPlayerShowArtworkProgress { get; set; } = false;

    // Вид полосы прогресса мини-плеера: "Default", "Material" (волна) или "MaterialSlider" (ползунок Material).
    public string MiniPlayerProgressStyle { get; set; } = "Default";

    // Вид кольца вокруг обложки: "Default", "Material" (волна вдоль контура) или "MaterialSlider" (ручка вдоль контура).
    public string MiniPlayerArtworkProgressStyle { get; set; } = "Default";

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

    // Как выбирается следующий трек при включённом шаффле (ShuffleModes): "Deck" — колода без повторов (по умолчанию),
    // "Random", "Favorites", "Rare", "Folders". Старое булево UseImprovedShuffle убрано: оно не влияло на выбор трека.
    public string ShuffleMode { get; set; } = ShuffleModes.Deck;

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

    // «Больше не спрашивать» в предупреждении о зеркале загрузки (SettingsWindow.ConfirmMirrorUse).
    public bool UpdateMirrorWarningSuppressed { get; set; }

    // Здоровье проверки обновлений на старте (UpdateCheckHealth, строки ISO 8601 UTC): когда последняя проверка удалась,
    // с какого момента проверки подряд не удаются и когда в последний раз показывалась подсказка про ручную загрузку.
    public string? LastSuccessfulUpdateCheckUtc { get; set; }
    public string? UpdateCheckFailingSinceUtc { get; set; }
    public string? UpdateCheckHintShownUtc { get; set; }

    // Обращение к пользователям (CommunityDialog): показывается при каждом запуске, пока не отмечено «Не показывать».
    // Имя не совпадает со старым CommunityDialogShown: тот флаг ставился при любом закрытии, и его значение намеренно не переносится.
    public bool CommunityDialogDismissed { get; set; }

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

    // Checkpoint срабатывает раз в секунду на UI-потоке, а settings.json содержит весь плейлист, избранное и счётчики: полная
    // сериализация (с отступами) на большой библиотеке давала мегабайты строк в куче больших объектов каждую секунду.
    // Поэтому проверка изменений идёт по лёгкому снимку (все поля, кроме больших коллекций) и отпечатку самих коллекций;
    // полный JSON строится, только когда что-то изменилось.
    private static string? LastProbeJson;
    private static string? LastProbeObserved;
    private static ulong LastBulkFingerprint;

    private static readonly HashSet<string> BulkPropertyNames = new(StringComparer.Ordinal)
    {
        nameof(AppSettings.SavedPlaylistFolders), nameof(AppSettings.SavedPlaylist), nameof(AppSettings.FavoriteTracks),
        nameof(AppSettings.PinnedFavoriteTracks), nameof(AppSettings.SavedQueue), nameof(AppSettings.ShuffleHistory),
        nameof(AppSettings.ShuffleBag), nameof(AppSettings.PlayCounts)
    };

    private static readonly JsonSerializerOptions ChangeProbeOptions = new()
    {
        MaxDepth = 16,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver
        {
            Modifiers = { ExcludeBulkProperties }
        }
    };

    private static void ExcludeBulkProperties(System.Text.Json.Serialization.Metadata.JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type != typeof(AppSettings)) return;

        foreach (var property in typeInfo.Properties)
        {
            if (BulkPropertyNames.Contains(property.Name))
                property.ShouldSerialize = static (_, _) => false;
        }
    }

    private static ulong MixHash(ulong hash, int value) => (hash ^ (uint)value) * 1099511628211UL;

    private static ulong MixStrings(ulong hash, List<string>? items)
    {
        if (items is null) return MixHash(hash, -1);

        hash = MixHash(hash, items.Count);
        foreach (string item in items)
            hash = MixHash(hash, item?.GetHashCode() ?? 0);
        return hash;
    }

    // Отпечаток больших коллекций: порядок списков учитывается, словаря счётчиков — нет (сумма не зависит от порядка).
    private static ulong ComputeBulkFingerprint(AppSettings settings)
    {
        ulong hash = 14695981039346656037UL;
        hash = MixHash(hash, settings.SavedPlaylistFolders.Count);
        foreach (SavedPlaylistFolder folder in settings.SavedPlaylistFolders)
        {
            hash = MixHash(hash, folder.DisplayName?.GetHashCode() ?? 0);
            hash = MixHash(hash, folder.SourcePath?.GetHashCode() ?? 0);
            hash = MixHash(hash, (folder.IsEnabled ? 1 : 0) | (folder.IsExpanded ? 2 : 0) | (folder.IsLooseFilesBucket ? 4 : 0));
            hash = MixStrings(hash, folder.Tracks);
            hash = MixStrings(hash, folder.ExcludedTracks);
        }

        hash = MixStrings(hash, settings.SavedPlaylist);
        hash = MixStrings(hash, settings.FavoriteTracks);
        hash = MixStrings(hash, settings.PinnedFavoriteTracks);
        hash = MixStrings(hash, settings.SavedQueue);
        hash = MixStrings(hash, settings.ShuffleHistory);
        hash = MixStrings(hash, settings.ShuffleBag);

        ulong counts = 0;
        foreach (KeyValuePair<string, int> pair in settings.PlayCounts)
            counts += (ulong)((pair.Key?.GetHashCode() ?? 0) * 397 ^ pair.Value) * 2654435761UL;
        return MixHash(MixHash(hash, settings.PlayCounts.Count), (int)counts ^ (int)(counts >> 32));
    }

    // Резервные копии нужны от порчи файла, а не от каждого изменения: во время работы они обновляются не чаще раза в несколько минут.
    // Первая копия процесса, выход, обновление и аварийный снимок пишут копию всегда; неизменившийся снимок повторно не пишется.
    private static readonly long BackupIntervalMilliseconds = (long)TimeSpan.FromMinutes(5).TotalMilliseconds;
    private static long LastBackupTick = long.MinValue;
    private static string? LastBackedUpJson;
    private static readonly string DefaultStorageDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumisense");

    // Пути не readonly только ради тестов: UseStorageDirectoryForTests переключает их на временную папку.
    private static string SettingsFilePath = Path.Combine(DefaultStorageDirectory, "settings.json");

    // Резервный снимок создаётся только из состояния с пользовательскими данными и, в отличие от settings.json,
    // защищает не только пути плейлиста, но и избранное, закрепления, счётчики, время и последнее воспроизведение.
    private static string UserDataRecoveryBackupPath = Path.Combine(DefaultStorageDirectory, "settings.user-data-backup.json");

    // Сохраняем прежнее имя параллельно для уже созданных копий и понятной ручной диагностики.
    private static string PlaylistRecoveryBackupPath = Path.Combine(DefaultStorageDirectory, "settings.playlist-backup.json");

    // Только для тестов: переносит settings.json и его резервные копии в указанную папку (null возвращает %AppData%\Lumisense)
    // и сбрасывает внутреннее состояние сохранения. В приложении не вызывается.
    internal static void UseStorageDirectoryForTests(string? directory)
    {
        string target = directory ?? DefaultStorageDirectory;
        SettingsFilePath = Path.Combine(target, "settings.json");
        UserDataRecoveryBackupPath = Path.Combine(target, "settings.user-data-backup.json");
        PlaylistRecoveryBackupPath = Path.Combine(target, "settings.playlist-backup.json");
        Interlocked.Exchange(ref NextSaveRevision, 0);
        Interlocked.Exchange(ref LastWrittenRevision, 0);
        Interlocked.Exchange(ref CheckpointInProgress, 0);
        Volatile.Write(ref LastObservedSettingsJson, null);
        LastProbeJson = null;
        LastProbeObserved = null;
        LastBulkFingerprint = 0;
        LastBackupTick = long.MinValue;
        LastBackedUpJson = null;
    }

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
    // forceBackup: выход, обновление и другие моменты, когда резервная копия должна содержать самое свежее состояние.
    public static bool Save(AppSettings settings, bool forceBackup = false)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            // Публикуем готовый JSON до I/O: если процесс оборвётся в WriteJsonAtomic,
            // аварийный обработчик сможет сохранить тот же снимок без чтения WPF-состояния.
            Volatile.Write(ref LastObservedSettingsJson, json);
            WriteJsonAtomic(json, Interlocked.Increment(ref NextSaveRevision), forceBackup);
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
            WriteJsonAtomic(json, Interlocked.Increment(ref NextSaveRevision), forceBackup: true);
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
            string probe;
            ulong bulk;
            string? observed = Volatile.Read(ref LastObservedSettingsJson);
            try
            {
                probe = JsonSerializer.Serialize(settings, ChangeProbeOptions);
                bulk = ComputeBulkFingerprint(settings);

                // Ничего не менялось с прошлой проверки, и сохранённый снимок тот же (любой Save подменяет строку): полный JSON не нужен.
                if (observed is not null && ReferenceEquals(observed, LastProbeObserved) &&
                    bulk == LastBulkFingerprint && string.Equals(probe, LastProbeJson, StringComparison.Ordinal))
                    return;

                json = JsonSerializer.Serialize(settings, JsonOptions);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Не удалось подготовить snapshot настроек ({SettingsFilePath}): {ex.Message}");
                return;
            }

            LastProbeJson = probe;
            LastBulkFingerprint = bulk;

            if (string.Equals(json, observed, StringComparison.Ordinal))
            {
                LastProbeObserved = observed;
                return;
            }

            LastProbeObserved = json;
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

    private static void WriteJsonAtomic(string json, long revision, bool forceBackup = false)
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
                TryBackupUserData(json, forceBackup);
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

    // Вызывается под SaveGate, поэтому статическое состояние троттлинга не нуждается в отдельной блокировке.
    private static void TryBackupUserData(string candidateJson, bool force)
    {
        if (string.Equals(candidateJson, LastBackedUpJson, StringComparison.Ordinal)) return;

        long now = Environment.TickCount64;
        bool firstBackup = LastBackupTick == long.MinValue;
        if (!force && !firstBackup && now - LastBackupTick < BackupIntervalMilliseconds) return;

        // Разбор JSON дороже проверок выше, поэтому идёт последним; без пользовательских данных копия не нужна и интервал не стартует.
        if (!SettingsIntegrityService.HasUserData(candidateJson)) return;

        LastBackupTick = now;
        LastBackedUpJson = candidateJson;
        SettingsIntegrityService.CreateRecoveryBackups(
            candidateJson,
            SettingsFilePath,
            UserDataRecoveryBackupPath,
            PlaylistRecoveryBackupPath);
    }
}
