using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using static Lumisense.BackgroundTask;

namespace Lumisense;

public partial class SettingsWindow
{
    private void BuildSearchIndex()
    {
        void Add(string label, string pageTitle, string pageKey, FrameworkElement target, string extraKeywords = "")
            => _searchIndex.Add(new SettingsSearchEntry(label, pageTitle, pageKey,
                $"{label} {LocalizationService.ToEnglish(label)} {extraKeywords}".ToLowerInvariant(), target));

        // Элементы ленивых страниц ещё не существуют при сборке индекса, поэтому ищутся по имени при переходе.
        void AddLazy(string label, string pageTitle, string pageKey, string targetName, string extraKeywords = "")
            => _searchIndex.Add(new SettingsSearchEntry(label, pageTitle, pageKey,
                $"{label} {LocalizationService.ToEnglish(label)} {extraKeywords}".ToLowerInvariant(), null, targetName));

        AddLazy("Язык интерфейса", "Оформление", "Appearance", "InterfaceLanguageRadioGroup", "язык русский english language locale локализация");
        AddLazy("Тема", "Оформление", "Appearance", "ThemeRadioGroup", "тёмная светлая цвет тема оформление dark light");
        AddLazy("Навигация в настройках", "Оформление", "Appearance", "NavigationStyleCard", "навигация меню таблетки панель боковая плавающая нижняя разделы названия подписи текст pills sidebar floating navigation labels");
        AddLazy("Акцентный цвет", "Оформление", "Appearance", "AccentModeRadioGroup", "акцент цвет палитра accent color");
        AddLazy("Основа окна", "Оформление", "Appearance", "BackdropRadioGroup", "mica alt acrylic blur акрил размытие блюр подложка фон backdrop более плотная и контрастная основа windows 11 22h2 сильнее прозрачность");
        AddLazy("Цвет основы от текущей обложки", "Оформление", "Appearance", "CoverBaseFromCoverCheckBox", "обложка cover основа фон окно цвет theme");
        AddLazy("Доступность", "Оформление", "Appearance", "AccessibilityCard", "масштаб интерфейса текст размер доступность движение анимация анимации по разделам слайдеры accessibility scale motion animations sliders");
        AddLazy("Анимация смены обложки", "Обложка и Now Playing", "Cover", "AlbumArtTransitionOnRadio", "анимация обложка переход трек itunes слайд карусель соседние предыдущая следующая fly transition carousel album art cover");
        AddLazy("Подпись под названием трека", "Обложка и Now Playing", "Cover", "TrackStateBadgeModeRadioGroup", "подпись состояние трека расширение формат качество битрейт размер файла badge extension format bitrate size flac mp3 и частота свой набор отметить что показывать");
        AddLazy("Жесты на обложке", "Обложка и Now Playing", "Cover", "AlbumArtGesturesCheckBox", "жесты обложка касание свайп пуск пауза громкость следующий предыдущий gesture swipe cover");
        AddLazy("Вид главного окна", "Окно и запуск", "Window", "MainWindowLayoutCard", "оформление новый классический крупная обложка кнопки управление layout modern classic");
        AddLazy("Вид плеера", "Окно и запуск", "Window", "PlayerViewModeCard", "квадратный прямоугольный мини плеер вид размер окна square rectangular mini");
        AddLazy("Поверх всех окон", "Окно и запуск", "Window", "AlwaysOnTopCheckBox", "topmost всегда сверху главное окно");
        AddLazy("Сворачивать в трей при закрытии", "Окно и запуск", "Window", "MinimizeToTrayCheckBox", "трей закрытие свернуть tray");
        AddLazy("Запускать вместе с Windows", "Окно и запуск", "Window", "LaunchOnStartupCheckBox", "автозапуск запуск windows автозагрузка startup");
        AddLazy("Запускать свёрнутым в трей", "Окно и запуск", "Window", "StartHiddenInTrayCheckBox", "запуск свёрнутым трей автозапуск скрыто hidden startup tray");
        AddLazy("Запоминать громкость между запусками", "Воспроизведение", "Playback", "RememberVolumeCheckBox", "громкость запуск volume");
        AddLazy("Логарифмическая регулировка громкости", "Воспроизведение", "Playback", "LogarithmicVolumeCheckBox", "громкость логарифм слух дБ db volume logarithmic");
        AddLazy("Не запускать трек при старте", "Воспроизведение", "Playback", "NeverAutoPlayLastTrackOnStartupCheckBox", "старт запуск продолжить воспроизведение последний трек пауза resume autoplay");
        AddLazy("Автоматически обновлять папки плейлиста", "Воспроизведение", "Playback", "AutoRefreshPlaylistFoldersCheckBox", "папки плейлист новые файлы автообновление слежение watcher refresh auto folders");
        AddLazy("Очистить кэш интернет-обложек", "Библиотека и файлы", "Library", "ClearArtworkCacheButton", "кэш обложка интернет очистить удалить cover cache artwork image");
        AddLazy("Нормализация имён файлов", "Библиотека и файлы", "Library", "NormalizePlaylistFileNamesButton", "нормализация имя файл шаблон переименование artist title album track extension rename");
        AddLazy("Действия контекстного меню трека", "Библиотека и файлы", "Library", "TrackContextFavoriteCheckBox", "контекстное меню правый клик пкм трек плейлист скрыть отключить действия проводник копировать теги свойства удалить найти файл замена недоступный locate relink missing");
        AddLazy("Найти файл", "Библиотека и файлы", "Library", "TrackContextFindFileCheckBox", "контекстное меню заменить недоступный файл путь locate relink missing file");
        AddLazy("Discord Rich Presence", "Интеграции", "Integrations", "DiscordRichPresenceEnabledCheckBox", "discord статус rich presence rpc активность" );
        AddLazy("Подключить Discord", "Интеграции", "Integrations", "ConnectDiscordButton", "discord подключить connection rich presence статус" );
        AddLazy("Открыть журнал Discord", "Интеграции", "Integrations", "OpenDiscordDiagnosticsLogButton", "discord журнал лог диагностика ошибка rich presence" );
        AddLazy("Приватность Discord: название и исполнитель", "Интеграции", "Integrations", "DiscordRichPresenceShowTrackInfoCheckBox", "discord приватность название исполнитель трек" );
        AddLazy("Приватность Discord: таймлайн", "Интеграции", "Integrations", "DiscordRichPresenceShowTimelineCheckBox", "discord приватность время прогресс таймлайн" );
        AddLazy("Приватность Discord: кнопка GitHub", "Интеграции", "Integrations", "DiscordRichPresenceShowButtonsCheckBox", "discord кнопка github ссылки статус button");
        AddLazy("Приватность Discord: кнопка Скачать Lumisense", "Интеграции", "Integrations", "DiscordRichPresenceShowDownloadButtonCheckBox", "discord кнопка скачать download ссылки статус button");
        AddLazy("Приватность Discord: обложка трека", "Интеграции", "Integrations", "DiscordRichPresenceShowCoverArtCheckBox", "discord обложка cover art musicbrainz itunes deezer" );
        AddLazy("Эквалайзер", "Эквалайзер", "Equalizer", "EqualizerEnabledCheckBox", "equalizer эквалайзер частоты полосы бас звук eq");
        AddLazy("Звук для каждого устройства", "Эквалайзер", "Equalizer", "DeviceSoundProfilesCheckBox", "профиль устройство наушники колонки эквалайзер баланс запоминать device profile");
        AddLazy("Авто-преамп эквалайзера", "Эквалайзер", "Equalizer", "EqualizerAutoHeadroomCheckBox", "преамп headroom клиппинг перегрузка громкость эквалайзер eq подъём");
        Add("Монитор острова", "Остров", "Island", IslandMonitorCombo, "остров монитор экран дисплей island monitor display");
        Add("Настройки острова", "Остров", "Island", IslandCustomCard, "остров круг капсула вид свёрнутого только обложка размер отступ размытие эквалайзер обводка скрыть средняя кнопка колесо громкость обложка игры раскрываться сворачиваться перетягивание свайп круглая кнопки избранное повтор текст песни island scale blur equalizer lag тормоза swipe round cover ширина width эквалайзер точки полоски фон свечение матрица уведомление контекстное меню пункты волна перемотка сегменты мерцание backdrop glow stars matrix seek shimmer");
        Add("Вид мини-плеера: карточка или остров", "Мини-плеер", "MiniPlayer", MiniStyleCard, "остров капсула dynamic island монитор экран вид мини плеер island monitor style");
        Add("Прозрачность окна мини-плеера", "Мини-плеер", "MiniPlayer", MiniOpacitySlider, "прозрачность opacity мини плеер");
        Add("Поверх всех окон (мини-плеер)", "Мини-плеер", "MiniPlayer", MiniAlwaysOnTopCheckBox, "topmost мини плеер");
        Add("Закрепить положение (мини-плеер)", "Мини-плеер", "MiniPlayer", MiniPinnedCheckBox, "закрепить перетаскивание pin мини плеер");
        Add("Прилипание к краям экрана (мини-плеер)", "Мини-плеер", "MiniPlayer", MiniSnapToEdgesCheckBox, "прилипание магнит края экран snap edge мини плеер");
        Add("Вторая кнопка в мини-плеере", "Мини-плеер", "MiniPlayer", MiniSecondaryButtonRadioGroup, "вторая кнопка повтор перемешать избранное сердечко favorite shuffle repeat мини плеер");
        Add("Прокручивать длинного исполнителя", "Мини-плеер", "MiniPlayer", MiniArtistMarqueeCheckBox, "исполнитель артист прокрутка бегущая строка marquee мини плеер");
        Add("Отображение обложки (мини-плеер)", "Мини-плеер", "MiniPlayer", MiniPlayerArtworkStyleRadioGroup, "обложка винил пластинка вращение круглая artwork vinyl rotate мини плеер по умолчанию без вращения");
        Add("Показывать полосу прогресса (мини-плеер)", "Мини-плеер", "MiniPlayer", MiniShowProgressCheckBox, "полоса прогресс progress bar скрыть мини плеер");
        Add("Прогресс вокруг обложки (мини-плеер)", "Мини-плеер", "MiniPlayer", MiniShowArtworkProgressCheckBox, "контур скруглённый квадрат прогресс обложка арт мини плеер artwork outline");
        Add("Толщина контура вокруг обложки", "Мини-плеер", "MiniPlayer", MiniArtworkProgressThicknessSlider, "толщина линия контур прогресс обложка мини плеер artwork outline thickness");
        Add("Цвет контура прогресса (мини-плеер)", "Мини-плеер", "MiniPlayer", MiniArtworkProgressColorModeRadioGroup, "акцент фиксированный цвет палитра контур прогресс обложка мини плеер artwork outline color");
        AddLazy("Пуск / пауза", "Горячие клавиши", "Hotkeys", "HotkeyPlayPauseButton", "play pause горячая клавиша");
        AddLazy("Следующий трек", "Горячие клавиши", "Hotkeys", "HotkeyNextButton", "next горячая клавиша");
        AddLazy("Предыдущий трек", "Горячие клавиши", "Hotkeys", "HotkeyPreviousButton", "previous горячая клавиша");
        AddLazy("Стоп", "Горячие клавиши", "Hotkeys", "HotkeyStopButton", "stop горячая клавиша");
        AddLazy("Громкость +", "Горячие клавиши", "Hotkeys", "HotkeyVolumeUpButton", "volume up громкость горячая клавиша");
        AddLazy("Громкость -", "Горячие клавиши", "Hotkeys", "HotkeyVolumeDownButton", "volume down громкость горячая клавиша");
        AddLazy("Без звука", "Горячие клавиши", "Hotkeys", "HotkeyMuteButton", "mute без звука горячая клавиша");
        AddLazy("Перемешать", "Горячие клавиши", "Hotkeys", "HotkeyShuffleButton", "shuffle перемешать горячая клавиша");
        AddLazy("Режим повтора", "Горячие клавиши", "Hotkeys", "HotkeyRepeatButton", "repeat повтор горячая клавиша");
        AddLazy("Избранное: текущий трек", "Горячие клавиши", "Hotkeys", "HotkeyToggleFavoriteButton", "избранное favorite текущий трек переключить горячая клавиша");
        AddLazy("Показать / скрыть текст", "Горячие клавиши", "Hotkeys", "HotkeyToggleLyricsButton", "текст lyrics панель показать скрыть переключить горячая клавиша");
        AddLazy("Переключить мини-плеер", "Горячие клавиши", "Hotkeys", "HotkeyToggleMiniPlayerButton", "мини плеер mini player переключить показать скрыть горячая клавиша");
        AddLazy("Удалить трек с диска", "Горячие клавиши", "Hotkeys", "HotkeyDeleteTrackButton", "delete удалить трек диск горячая клавиша");
        AddLazy("Перемотка вперёд", "Горячие клавиши", "Hotkeys", "HotkeySeekForwardButton", "seek forward вперёд перемотка 5 секунд горячая клавиша");
        AddLazy("Перемотка назад", "Горячие клавиши", "Hotkeys", "HotkeySeekBackwardButton", "seek backward назад перемотка 5 секунд горячая клавиша");
        AddLazy("Сбросить все горячие клавиши", "Горячие клавиши", "Hotkeys", "HotkeysResetAllButton", "сброс сбросить очистить все горячие клавиши reset all hotkeys clear");
        AddLazy("Режим шаффла", "Воспроизведение", "Playback", "ShuffleModeRadioGroup", "шаффл перемешать shuffle режим колода случайный любимые редкие папки альбомы чаще по папкам");
        AddLazy("Баланс левого и правого канала", "Воспроизведение", "Playback", "PlaybackBalanceSlider", "баланс левый правый наушник канал balance left right pan earphone");
        AddLazy("Измерять громкость файлов без тегов", "Воспроизведение", "Playback", "LoudnessScanCheckBox", "громкость измерение анализ replaygain r128 lufs без тегов нормализация");
        AddLazy("Кроссфейд при автопереходе", "Воспроизведение", "Playback", "CrossfadeSlider", "кроссфейд crossfade плавный переход наложение треков");
        AddLazy("Переход между треками без паузы", "Воспроизведение", "Playback", "GaplessCheckBox", "гэплесс gapless без паузы переход трек альбом бесшовный");
        AddLazy("Ширина стереобазы", "Воспроизведение", "Playback", "StereoWidthSlider", "ширина стерео база stereo width моно шире");
        AddLazy("Кроссфид для наушников", "Воспроизведение", "Playback", "CrossfeedSlider", "кроссфид crossfeed наушники стерео голова");
        AddLazy("Тонкомпенсация громкости", "Воспроизведение", "Playback", "LoudnessCompensationSlider", "тонкомпенсация loudness громкость низы тихо");
        AddLazy("Анимация подключения устройства", "Воспроизведение", "Playback", "DeviceConnectStyleCombo", "подключение устройство наушники bluetooth анимация карточка connect device airpods");
        AddLazy("Анимация карточки устройства", "Воспроизведение", "Playback", "DeviceCardAnimationCombo", "анимация карточка устройство волна орбита свечение раскрытие wave orbit glow animation");
        AddLazy("Значок устройства", "Воспроизведение", "Playback", "DeviceKindCombo", "значок иконка устройство наушники колонки вручную icon headphones speakers override");
        AddLazy("Устройство вывода", "Воспроизведение", "Playback", "OutputDeviceCombo", "звук аудио устройство вывод наушники колонки динамики speakers headphones audio output wasapi shared");
        AddLazy("Защита от клиппинга (лимитер)", "Воспроизведение", "Playback", "LimiterCheckBox", "лимитер клиппинг перегрузка искажения limiter clipping громкость пики");
        AddLazy("Скорость меняет тон", "Воспроизведение", "Playback", "PlaybackSpeedChangesPitchCheckBox", "скорость тон винил pitch rate speed ускорить замедлить");
        AddLazy("ReplayGain: альбомный режим", "Воспроизведение", "Playback", "ReplayGainAlbumCheckBox", "replaygain альбом album режим громкость");
        AddLazy("ReplayGain: предусилитель", "Воспроизведение", "Playback", "ReplayGainPreampSlider", "replaygain предусилитель preamp громкость усиление дб");
        AddLazy("ReplayGain", "Воспроизведение", "Playback", "ReplayGainCheckBox", "replaygain громкость выравнивание нормализация gain");
        AddLazy("Каналы: стерео и моно", "Воспроизведение", "Playback", "ChannelModeRadioGroup", "каналы стерео моно mono stereo downmix один наушник channels по умолчанию");
        AddLazy("Уведомление о смене трека", "Уведомления", "Notifications", "TrackChangeToastCheckBox", "уведомление тост смена трека toast notification");
        Add("Полоса воспроизведения", "Полоса и ползунки", "Progress", ProgressStyleWaveformRadio, "полоса воспроизведения прогресс seek слайдер waveform форма звука soundcloud material волна цвет толщина ручка форма концов предпросмотр для каждого окна progress bar color thickness wave thumb per window как в главном окне по умолчанию обычная волнистая линия android slider ползунок с зазором");
        AddLazy("Режим текста песни", "Тексты песен", "Lyrics", "LyricsDisplayPlainRadio", "текст песни синхронный обычный lrc txt переключить режим synced plain lyrics если он есть по умолчанию");
        AddLazy("Показать пример уведомления", "Уведомления", "Notifications", "ToastPreviewButton", "уведомление пример предпросмотр показать toast preview");
        AddLazy("Расположение уведомления", "Уведомления", "Notifications", "ToastPosTopLeftRadio", "уведомление угол расположение позиция монитор экран размер position monitor screen size сверху по центру справа снизу слева");
        AddLazy("Своё положение уведомления", "Уведомления", "Notifications", "ToastPickPositionButton", "уведомление перенести перетащить положение позиция свой custom drag move position toast notification");
        AddLazy("Когда показывать", "Уведомления", "Notifications", "ToastPolicyEveryTrackChangeRadio", "уведомление тост смена трека воспроизведение ручной выбор policy toast notification playback manual только при начале воспроизведения моём выборе");
        AddLazy("Размер уведомления", "Уведомления", "Notifications", "ToastSizeSmallRadio", "размер уведомление тост маленький средний большой size toast notification");
        AddLazy("Выравнивание текста песни", "Тексты песен", "Lyrics", "LyricsAlignLeftRadio", "слева по центру справа выравнивание текст песни lyrics alignment now playing");
        AddLazy("Дополнительный источник текстов", "Тексты песен", "Lyrics", "UseExtraLyricsSourceCheckBox", "текст песни lyrics источник netease qq kugou paxsenix lyricify русские исполнители extra source");
        AddLazy("Фон Now Playing", "Обложка и Now Playing", "Cover", "NowPlayingBackgroundCloudsRadio", "фон now playing размытая обложка один цвет без фона облака цветные шары волны background orbs clouds waves простой тёмный");
        AddLazy("Пресет оформления текста песни", "Тексты песен", "Lyrics", "LyricsPresetCombo", "пресет оформление текст песни караоке apple music классика минимализм сохранить свой preset lyrics style");
        AddLazy("Переход между строками текста", "Тексты песен", "Lyrics", "LyricsTransitionCombo", "переход анимация строк текст песни apple music плавный быстрый волна прокрутка lyrics transition animation");
        AddLazy("Скрывать обложку при тексте песни", "Тексты песен", "Lyrics", "HideCoverInLyricsCheckBox", "обложка скрыть текст песни больше места lyrics cover hide");
        AddLazy("Анимация избранного", "Обложка и Now Playing", "Cover", "FavoriteHeartFillRadio", "сердечко избранное анимация заливка искры кольцо favorite heart animation");
        Add("Размер мини-плеера", "Мини-плеер", "MiniPlayer", MiniPlayerSizePresetRadioGroup, "размер мини плеер компактный классический текущий 1.21 size preset");
        AddLazy("Цвет уведомления", "Уведомления", "Notifications", "ToastColorModeRadioGroup", "цвет уведомления обложка акцент свой окрашивание фон toast color cover accent tint стандартный по теме умолчанию акцентный приложения");
        AddLazy("Прозрачность уведомления", "Уведомления", "Notifications", "ToastOpacitySlider", "прозрачность фона уведомление тост opacity transparency toast");
        AddLazy("Ширина уведомления", "Уведомления", "Notifications", "ToastWidthSlider", "ширина уведомление тост размер width toast notification size");
        AddLazy("Экспортировать настройки", "Профиль", "Profile", "ExportProfileButton", "экспорт настройки профиль lumi файл backup export profile");
        AddLazy("Импортировать настройки", "Профиль", "Profile", "ImportProfileButton", "импорт настройки профиль lumi файл backup import restore profile");
        AddLazy("Кэш текстов песен", "Тексты песен", "Lyrics", "ClearLyricsCacheButton", "текст lyrics кэш очистить память локальный cache lyrics clear");
        AddLazy("Сбросить плеер к исходному состоянию", "Профиль", "Profile", "ResetPlayerButton", "сброс сбросить умолчание reset default настройки factory");
        AddLazy("Вернуть состояние до последнего сброса", "Профиль", "Profile", "RestoreResetSnapshotButton", "восстановить вернуть точка сброса backup restore reset recovery");
        AddLazy("Полный сброс данных", "Профиль", "Profile", "ResetAllDataButton", "сброс удалить очистить стереть настройки плейлисты избранное история пресеты wipe erase all data reset");
        AddLazy("О плеере", "О плеере", "About", "AboutInfoCard", "версия lumisense о программе о плеере");
        AddLazy("Источник загрузки обновлений", "Обновления", "Updates", "UpdateSourceGitHubRadio", "update mirror зеркало gh-proxy обновление скачать источник v4.gh proxy.org только ipv4 v6.gh ipv6 cdn.gh через cdn");
        AddLazy("Все версии", "Обновления", "Updates", "AllVersionsExpanderControl", "версии история версия откат downgrade install version releases обновление скачать установить zip exe установщик");
        AddLazy("Проверить обновления", "Обновления", "Updates", "CheckUpdatesButton", "обновление update github версия проверить");
        AddLazy("Проверить зеркала", "Обновления", "Updates", "ProbeUpdateSourcesButton", "зеркало скорость проверка источник mirror speed probe gh-proxy ghfast");
        AddLazy("Скачать базовый пакет", "Обновления", "Updates", "PrepareVelopackBasePackageButton", "velopack базовый пакет полный пакет компактные обновления delta base package");
        AddLazy("Удалить старую EXE-копию", "Обновления", "Updates", "RemoveLegacyInnoButton", "удалить uninstall деинсталлятор старая exe msi migration cleanup compact updates");
        AddLazy("История изменений", "О плеере", "About", "ChangelogButton", "патчноуты changelog версии история изменений");
        AddLazy("Разработчик", "О плеере", "About", "DeveloperGitHubButton", "разработчик автор github telegram wasssly ссылки контакты аватар");
        AddLazy("Открыть папку с логами", "О плеере", "About", "OpenLogsButton", "логи log ошибка краш crash диагностика");
        AddLazy("Фон у кнопок управления", "Окно и запуск", "Window", "ControlButtonsBackgroundCheckBox", "кнопки плашка подложка прозрачные без фона buttons background plate transparent");
        AddLazy("Вид кнопки Пуск/Пауза", "Окно и запуск", "Window", "PlayButtonStyleRadioGroup", "акцентный фон как у остальных кнопок play pause accent");
        AddLazy("Меню «Ещё» (три точки)", "Окно и запуск", "Window", "MoreMenuStyleRadioGroup", "три точки очередь текст песни меню пружина анимация dynamic menu more");
        AddLazy("Форма кнопки Пуск/Пауза", "Окно и запуск", "Window", "PlayButtonShapeRadioGroup", "квадратный круглый круг квадрат по умолчанию форма кнопки play pause square round circle shape");
        AddLazy("Диагностика производительности", "О плеере", "About", "DiagnosticsButton", "проверка нагрузка процессор память утечки тест фоновое наблюдение за ресурсами watchdog diagnostics performance cpu memory leak");
        AddLazy("Автоматически переводить журнал изменений на английский", "Оформление", "Appearance", "ChangelogAutoTranslateCheckBox", "чейнджлог changelog перевод английский google translate история изменений патчноуты");
        AddLazy("Показывать названия рядом с иконками", "Оформление", "Appearance", "NavShowLabelsCheckBox", "подписи названия иконки навигация labels icons");
        AddLazy("Сохранить пресет текста песни", "Тексты песен", "Lyrics", "LyricsPresetSaveButton", "пресет сохранить удалить preset save delete lyrics style");
        AddLazy("Размер шрифта текста песни", "Тексты песен", "Lyrics", "SyncedLyricsFontSizeSlider", "шрифт размер текст font size lyrics");
        AddLazy("Размер текста в Now Playing", "Тексты песен", "Lyrics", "NowPlayingLyricsFontSizeSlider", "шрифт размер текст песни font size lyrics");
        AddLazy("Эффект активной строки", "Тексты песен", "Lyrics", "SyncedLyricsEffectGlowRadio", "свечение подсветка активная строка glow highlight effect и подложка только полоска слева без эффекта");
        AddLazy("Сила свечения активной строки", "Тексты песен", "Lyrics", "SyncedLyricsGlowStrengthSlider", "свечение glow");
        AddLazy("Увеличивать активную строку", "Тексты песен", "Lyrics", "SyncedLyricsScaleCheckBox", "масштаб увеличение scale zoom");
        AddLazy("Степень увеличения активной строки", "Тексты песен", "Lyrics", "SyncedLyricsScalePercentSlider", "масштаб процент scale zoom");
        AddLazy("Приглушать дальние строки", "Тексты песен", "Lyrics", "SyncedLyricsFadeCheckBox", "прозрачность затемнение dim fade");
        AddLazy("Степень приглушения дальних строк", "Тексты песен", "Lyrics", "SyncedLyricsFadeStrengthSlider", "прозрачность затемнение dim fade");
        AddLazy("Размывать неактивные строки", "Тексты песен", "Lyrics", "SyncedLyricsBlurCheckBox", "размытие blur");
        AddLazy("Сила размытия неактивных строк", "Тексты песен", "Lyrics", "SyncedLyricsBlurStrengthSlider", "размытие blur");
        AddLazy("Видимых строк в Now Playing", "Тексты песен", "Lyrics", "SyncedLyricsVisibleLinesSlider", "количество строк lines count");
        AddLazy("Откуда брать тексты", "Тексты песен", "Lyrics", "LyricsPolicyRadioGroup", "только локальные lrclib интернет автоматически источник local online policy искать точное совпадение по моему запросу");
        AddLazy("NetEase Cloud Music", "Тексты песен", "Lyrics", "UseNetEaseLyricsCheckBox", "тексты источник дополнительный lyrics source netease");
        AddLazy("QQ Music", "Тексты песен", "Lyrics", "UseQqMusicLyricsCheckBox", "тексты источник дополнительный lyrics source qq");
        AddLazy("Kugou", "Тексты песен", "Lyrics", "UseKugouLyricsCheckBox", "тексты источник дополнительный lyrics source");
        AddLazy("Paxsenix (Apple Music)", "Тексты песен", "Lyrics", "UsePaxsenixLyricsCheckBox", "тексты источник дополнительный lyrics source apple music");
        AddLazy("Better Lyrics", "Тексты песен", "Lyrics", "UseBetterLyricsCheckBox", "тексты источник дополнительный lyrics source");
        AddLazy("Binimum", "Тексты песен", "Lyrics", "UseBinimumLyricsCheckBox", "тексты источник дополнительный lyrics source");
        AddLazy("LyricsPlus", "Тексты песен", "Lyrics", "UseLyricsPlusLyricsCheckBox", "тексты источник дополнительный lyrics source");
        AddLazy("Состав подписи под названием трека", "Обложка и Now Playing", "Cover", "BadgePartStateCheckBox", "состояние расширение битрейт частота дискретизации размер файла badge bitrate sample rate extension file size");
        AddLazy("Режим WASAPI", "Воспроизведение", "Playback", "WasapiModeCombo", "wasapi exclusive shared эксклюзивный общий");
        AddLazy("Размер буфера WASAPI", "Воспроизведение", "Playback", "WasapiLatencyCombo", "задержка latency buffer wasapi");
        AddLazy("Повышенный приоритет плеера", "Воспроизведение", "Playback", "RaisePlaybackPriorityCheckBox", "priority процесс приоритет");
        AddLazy("Копировать аудиодиагностику", "Воспроизведение", "Playback", "CopyAudioDiagnosticsButton", "диагностика устройство audio diagnostics");
        AddLazy("Подробный trace подготовки трека", "Воспроизведение", "Playback", "TrackLoadTraceCheckBox", "trace журнал отладка логи");
        AddLazy("Сохранять очередь «Играть следующим» между запусками", "Воспроизведение", "Playback", "SaveQueueBetweenRestartsCheckBox", "очередь queue play next");
        AddLazy("Не смешивать треки одного альбома", "Воспроизведение", "Playback", "CrossfadeSkipSameAlbumCheckBox", "кроссфейд crossfade album альбом");
        Add("Область настройки полосы прогресса", "Полоса и ползунки", "Progress", ProgressScopeRadioGroup, "одинаковая для всех окон отдельная для каждого scope shared per window своя окна по умолчанию");
        Add("Окно, для которого настраивается полоса", "Полоса и ползунки", "Progress", ProgressSurfaceMainRadio, "главное окно мини-плеер now playing window surface");
        Add("Цвет полосы прогресса", "Полоса и ползунки", "Progress", ProgressColorRadioGroup, "акцент обложка свой цвет accent cover custom color от обложки текущего трека");
        Add("Толщина дорожки прогресса", "Полоса и ползунки", "Progress", ProgressThicknessSlider, "толщина thickness");
        Add("Размер ручки прогресса", "Полоса и ползунки", "Progress", ProgressThumbSlider, "ручка thumb handle size");
        Add("Форма концов полосы прогресса", "Полоса и ползунки", "Progress", ProgressShapeRoundRadio, "скруглённые прямые round square");
        Add("Высота волны прогресса", "Полоса и ползунки", "Progress", ProgressWaveAmplitudeSlider, "волна wave amplitude");
        Add("Длина волны прогресса", "Полоса и ползунки", "Progress", ProgressWaveLengthSlider, "волна wave length");
        Add("Скорость волны прогресса", "Полоса и ползунки", "Progress", ProgressWaveSpeedSlider, "волна wave speed");
        Add("Волна бежит и на паузе", "Полоса и ползунки", "Progress", ProgressWaveAlwaysCheckBox, "волна пауза wave pause");
        Add("Ручка на волне", "Полоса и ползунки", "Progress", ProgressWaveHandleCheckBox, "волна ручка wave handle");
        Add("Сделать контур волнистым", "Полоса и ползунки", "Progress", ProgressRingMakeWavyButton, "волна контур ring wavy");
        Add("Вид столбиков прогресса", "Полоса и ползунки", "Progress", ProgressWaveformWaveRadio, "тонкие толстые столбики waveform bars скруглённые");
        Add("Зазор между столбиками прогресса", "Полоса и ползунки", "Progress", ProgressWaveformGapSlider, "столбики waveform gap");
        Add("Прозрачность непроигранной части", "Полоса и ползунки", "Progress", ProgressWaveformOpacitySlider, "прозрачность opacity waveform");
        Add("Показывать время в точке под курсором", "Полоса и ползунки", "Progress", ProgressHoverTimeCheckBox, "наведение время hover time");
        Add("Подсвечивать участок до курсора", "Полоса и ползунки", "Progress", ProgressHoverHighlightCheckBox, "наведение подсветка hover highlight");
        Add("Сбросить вид полосы", "Полоса и ползунки", "Progress", ProgressAppearanceResetButton, "сброс reset");
        Add("Стиль ползунков", "Полоса и ползунки", "Progress", SliderStyleRadioGroup, "ползунок слайдер громкость обычный material slider volume тонкая ручка с зазором как в android");
        AddLazy("Играть следующим", "Библиотека и файлы", "Library", "TrackContextPlayNextCheckBox", "контекстное меню трека context menu очередь queue play next");
        AddLazy("Добавить в очередь", "Библиотека и файлы", "Library", "TrackContextAddToQueueCheckBox", "контекстное меню трека context menu очередь queue");
        AddLazy("Показать в проводнике", "Библиотека и файлы", "Library", "TrackContextShowInExplorerCheckBox", "контекстное меню трека context menu explorer папка файл show in explorer");
        AddLazy("Копировать имя трека", "Библиотека и файлы", "Library", "TrackContextCopyNameCheckBox", "контекстное меню трека context menu copy name");
        AddLazy("Копировать путь к файлу", "Библиотека и файлы", "Library", "TrackContextCopyPathCheckBox", "контекстное меню трека context menu copy path");
        AddLazy("Копировать файл", "Библиотека и файлы", "Library", "TrackContextCopyFileCheckBox", "контекстное меню трека context menu copy file");
        AddLazy("Сохранить обработанную копию", "Библиотека и файлы", "Library", "TrackContextExportProcessedCopyCheckBox", "контекстное меню трека context menu export processed copy экспорт эквалайзер");
        AddLazy("Свойства", "Библиотека и файлы", "Library", "TrackContextPropertiesCheckBox", "контекстное меню трека context menu properties информация");
        AddLazy("Изменить теги", "Библиотека и файлы", "Library", "TrackContextEditTagsCheckBox", "контекстное меню трека context menu теги tags редактор tag editor");
        AddLazy("Нормализовать имя файла", "Библиотека и файлы", "Library", "TrackContextNormalizeFileNameCheckBox", "контекстное меню трека context menu имена файлов normalize file name");
        AddLazy("Убрать из плейлиста", "Библиотека и файлы", "Library", "TrackContextRemoveFromPlaylistCheckBox", "контекстное меню трека context menu remove from playlist");
        AddLazy("Удалить с диска", "Библиотека и файлы", "Library", "TrackContextDeleteFromDiskCheckBox", "контекстное меню трека context menu delete from disk корзина");
        AddLazy("Шаблон нормализации имён файлов", "Библиотека и файлы", "Library", "FileNameNormalizationTemplateTextBox", "шаблон template исполнитель название имена файлов");
        AddLazy("Положение обложки в уведомлении", "Уведомления", "Notifications", "ToastArtLeftRadio", "слева справа cover position left right");
        AddLazy("Выравнивание текста в уведомлении", "Уведомления", "Notifications", "ToastTextLeftRadio", "слева по центру справа text alignment");
        AddLazy("Прижать обложку к тексту", "Уведомления", "Notifications", "ToastArtNextToTextCheckBox", "обложка текст cover text");
        AddLazy("Монитор уведомления", "Уведомления", "Notifications", "ToastMonitorCombo", "монитор экран monitor screen");
        AddLazy("Сила окрашивания уведомления", "Уведомления", "Notifications", "ToastColorStrengthSlider", "цвет окрашивание color tint");
        AddLazy("Полосы эквалайзера", "Эквалайзер", "Equalizer", "EqBand0Slider", "31 62 125 250 500 1k 2k 4k 8k 16k гц hz частоты усиление bands gain");
        AddLazy("Пресеты эквалайзера", "Эквалайзер", "Equalizer", "EqualizerPresetComboBox", "применить сохранить удалить поделиться импортировать экспорт preset apply save delete share import export");
        AddLazy("Сбросить настройки эквалайзера", "Эквалайзер", "Equalizer", "EqualizerResetButton", "сбросить всё reset flat");
        Add("Включать автоматически при обнаружении игры или оверлея", "Мини-плеер", "MiniPlayer", GameOverlayAutoDetectCheckBox, "игра оверлей совместимость game overlay compatibility");
        Add("Держать режим совместимости с играми включённым всегда", "Мини-плеер", "MiniPlayer", GameOverlayCompatibilityCheckBox, "игра оверлей совместимость game overlay compatibility always");
        Add("Действия контекстного меню мини-плеера", "Мини-плеер", "MiniPlayer", MiniContextSettingsCheckBox, "контекстное меню настройки now playing закрепить поверх окон совместимость вторая кнопка скорость тон прозрачность прилипание полоса прогресса расположение кнопок context menu actions полный экран с играми и оверлеем выбор второй кнопки");
        Add("Расположение кнопок (мини-плеер)", "Мини-плеер", "MiniPlayer", MiniButtonsLayoutRadioGroup, "снизу сбоку кнопки управление buttons below на месте обложки и названия размер окна не меняется");
        Add("Стиль контура прогресса вокруг обложки", "Мини-плеер", "MiniPlayer", MiniPlayerArtworkProgressStyleRadioGroup, "обычный волнистый wavy outline material волнистая линия как в android slider ползунок с зазором");
        Add("Что показывать под названием (мини-плеер)", "Мини-плеер", "MiniPlayer", MiniInfoModeRadioGroup, "исполнитель альбом artist album ничего только название трека оставшееся время");
        Add("Вид свёрнутого острова", "Остров", "Island", IslandShapeCombo, "капсула круг notch nook вырез capsule circle shape");
        Add("Notch Nook: раскрывать при наведении курсора", "Остров", "Island", IslandNotchHoverOpenCheckBox, "вырез наведение hover notch");
        Add("Размер острова", "Остров", "Island", IslandScaleSlider, "масштаб scale size");
        Add("Ширина острова", "Остров", "Island", IslandWidthSlider, "width");
        Add("Отступ острова от верхнего края", "Остров", "Island", IslandTopGapSlider, "отступ сверху gap top");
        Add("Скрытие острова по средней кнопке мыши", "Остров", "Island", IslandHideSlider, "скрыть средняя кнопка колесо hide middle click");
        Add("Сворачивание острова после ухода курсора", "Остров", "Island", IslandCollapseSlider, "таймер задержка collapse delay");
        Add("Раскрываться при смене трека", "Остров", "Island", IslandTrackToastCheckBox, "смена трека уведомление track change");
        Add("Не показывать уведомление о смене трека в виде «Остров»", "Остров", "Island", IslandHideSystemToastCheckBox, "уведомление toast");
        Add("Сворачиваться, когда курсор ушёл", "Остров", "Island", IslandAutoCollapseCheckBox, "collapse cursor");
        Add("Эквалайзер в свёрнутом виде", "Остров", "Island", IslandEqualizerCheckBox, "полоски equalizer bars");
        Add("Вместо эквалайзера показывать устройство вывода", "Остров", "Island", IslandCompactDeviceCheckBox, "устройство значок device icon");
        Add("Обводка цветом акцента во время игры", "Остров", "Island", IslandAccentRimCheckBox, "акцент обводка игра accent rim");
        Add("Полоса прогресса трека вокруг свёрнутого острова", "Остров", "Island", IslandProgressRimCheckBox, "контур прогресс ring progress");
        Add("Кольцо прогресса рядом с островом", "Остров", "Island", IslandRingSideCombo, "кольцо круг прогресс слева справа ring progress side");
        Add("Размытие при смене вида", "Остров", "Island", IslandBlurTransitionsCheckBox, "blur transitions анимация");
        Add("Клик по обложке открывает её крупно", "Остров", "Island", IslandCoverPreviewCheckBox, "обложка просмотр cover preview");
        Add("Показывать громкость, когда её меняют не на острове", "Остров", "Island", IslandExternalVolumeCheckBox, "громкость volume");
        Add("Не раскрываться при смене трека в играх", "Остров", "Island", IslandQuietInGamesCheckBox, "игры games quiet");
        Add("Перетягивание острова: влево — назад, вправо — вперёд", "Остров", "Island", IslandSwipeInvertedCheckBox, "свайп swipe перетягивание");
        Add("Круглая обложка", "Остров", "Island", IslandRoundCoverCheckBox, "round cover");
        Add("Принимать перетаскиваемые файлы и папки", "Остров", "Island", IslandAcceptDropsCheckBox, "drag drop перетаскивание файлы");
        Add("Левая кнопка раскрытого острова", "Остров", "Island", IslandLeftButtonCombo, "кнопка button left");
        Add("Правая кнопка раскрытого острова", "Остров", "Island", IslandRightButtonCombo, "кнопка button right");
        Add("Эквалайзер свёрнутого острова", "Остров", "Island", IslandEqualizerStyleCombo, "полоски equalizer style");
        Add("Фон раскрытого острова", "Остров", "Island", IslandBackdropCombo, "фон backdrop background");
        Add("Полоса перемотки раскрытого острова", "Остров", "Island", IslandSeekStyleCombo, "перемотка seek bar");
        Add("Мерцание, пока ищется текст песни", "Остров", "Island", IslandLyricsShimmerCheckBox, "текст песни поиск lyrics shimmer");
        Add("Название трека катается по буквам", "Остров", "Island", IslandWordRollCheckBox, "word roll анимация название трек буквы");
        Add("Строки песни в свёрнутом острове катаются по буквам", "Остров", "Island", IslandLyricsWordRollCheckBox, "word roll анимация строка песни текст буквы свёрнутый");
        Add("Подсветка слов в тексте песни (бета)", "Остров", "Island", IslandReadAlongCheckBox, "read along слова подсветка пилюля текст песни караоке");
        Add("Размытие движения при растяжении", "Остров", "Island", IslandMotionBlurCheckBox, "motion blur растяжение");
        Add("Действия контекстного меню острова", "Остров", "Island", IslandMenuPlayPauseCheckBox, "контекстное меню пауза следующий предыдущий скрыть развернуть now playing скорость тон совместимость настройки context menu actions");
        AddLazy("Масштаб интерфейса", "Оформление", "Appearance", "InterfaceScaleSlider", "размер текст scale interface size");
        AddLazy("Все анимации", "Оформление", "Appearance", "AnimationsMasterCheckBox", "движение анимация слайдеры переходы обложка текст песни полоса мини-плеер бегущая строка остров меню трея сердечко индикаторы загрузки motion animations reduce");
        AddLazy("Now Playing: фон и переходы", "Оформление", "Appearance", "AnimationNowPlayingCheckBox", "анимация фон now playing animation background");
        AddLazy("Показывать дополнительные кнопки по бокам от кнопок управления", "Окно и запуск", "Window", "ModernShowSideButtonsCheckBox", "кнопки слоты управление extra buttons slots");
        AddLazy("Подложка у кнопок управления (вид «Стекло»)", "Окно и запуск", "Window", "GlassControlsBackgroundCheckBox", "стекло glass подложка controls background");
        AddLazy("Выравнивание обложки в виде «Компакт»", "Окно и запуск", "Window", "CompactCoverAlignmentCombo", "компакт обложка выравнивание compact cover alignment");
    }

    private void SettingsSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => RefreshSearchResults();

    private void RefreshSearchResults()
    {
        string query = SettingsSearchBox.Text.Trim();

        if (query.Length == 0)
        {
            NavCategoriesPanel.Visibility = Visibility.Visible;
            SearchResultsHost.Visibility = Visibility.Collapsed;
            UpdateSearchOverlayVisibility();
            return;
        }

        NavCategoriesPanel.Visibility = Visibility.Collapsed;
        SearchResultsHost.Visibility = Visibility.Visible;
        UpdateSearchOverlayVisibility();

        string queryLower = query.ToLowerInvariant();
        _searchResults.Clear();
        foreach (var entry in _searchIndex)
        {
            if (entry.Keywords.Contains(queryLower, StringComparison.Ordinal))
                _searchResults.Add(entry);
        }

        bool hasResults = _searchResults.Count > 0;
        SearchResultsList.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
        SearchEmptyState.Visibility = hasResults ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SearchResultItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SettingsSearchEntry entry }) return;

        System.Windows.Controls.RadioButton navButton = entry.PageKey switch
        {
            "Appearance" => NavAppearance,
            "Window" => NavWindow,
            "Playback" => NavPlayback,
            "Progress" => NavProgress,
            "Library" => NavLibrary,
            "Cover" => NavCover,
            "Lyrics" => NavLyrics,
            "Integrations" => NavIntegrations,
            "Notifications" => NavNotifications,
            "Equalizer" => NavEqualizer,
            "MiniPlayer" => NavMiniPlayer,
            "Island" => NavIsland,
            "Hotkeys" => NavHotkeys,
            "Profile" => NavProfile,
            "Updates" => NavUpdates,
            "About" => NavAbout,
            _ => NavAppearance
        };
        navButton.IsChecked = true;
        EnsurePageBuilt(entry.PageKey);
        FrameworkElement? target = entry.Target ?? ResolveLazyTarget(entry);
        if (target is null) return;

        // Возвращаемся к обычному виду навигации — поиск своё дело сделал
        SettingsSearchBox.Text = string.Empty;

        // Ждём, пока страница станет видимой и разложится по месту, и только потом
        // прокручиваем к нужному элементу и подсвечиваем его
        Dispatcher.InvokeAsync(() =>
        {
            // Часть результатов поиска лежит в свёрнутых Expander (FluentExpanderStyle в App.xaml): BringIntoView и подсветка
            // скрытого элемента ничего не покажут, поэтому заранее разворачиваем все Expander-предки.
            ExpandAncestorExpanders(target);

            // Раскрытие аккордеона меняет раскладку: ждём ещё один цикл, иначе прокрутка и подсветка считались бы
            // по устаревшим координатам.
            Dispatcher.InvokeAsync(() =>
            {
                FrameworkElement shown = NearestVisible(target);
                shown.BringIntoView();
                SearchHighlightAdorner.Flash(shown);
            }, DispatcherPriority.Loaded);
        }, DispatcherPriority.Loaded);
    }

    // Часть настроек видна только при определённом выборе (параметры волны при стиле «Волна»): у скрытого элемента
    // подсветка ничего не покажет, поэтому берётся ближайший видимый предок, то есть карточка с переключателем.
    private static FrameworkElement NearestVisible(FrameworkElement target)
    {
        DependencyObject? current = target;
        while (current is not null)
        {
            if (current is FrameworkElement { IsVisible: true } visible) return visible;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return target;
    }

    private static void ExpandAncestorExpanders(DependencyObject element)
    {
        var current = System.Windows.Media.VisualTreeHelper.GetParent(element);
        while (current != null)
        {
            if (current is System.Windows.Controls.Expander expander) expander.IsExpanded = true;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
    }
}
