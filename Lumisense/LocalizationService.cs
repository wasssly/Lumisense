using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Controls.Primitives;

namespace AudioPlayer;

// Локализация интерфейса без изменения существующих XAML-разметок. Все статические русские
// подписи собраны в словарь, а при создании и смене языка переводятся на уровне визуального
// дерева. Это сохраняет работающими стили WPF-UI и уже существующие связи данных.
public static class LocalizationService
{
    public const string Russian = "ru";
    public const string English = "en";

    private static readonly Dictionary<string, string> EnglishByRussian = new(StringComparer.Ordinal)
    {
        ["Язык интерфейса"] = "Interface language",
        ["Выберите язык Lumisense. Изменение применяется сразу ко всем открытым окнам."] = "Choose the language for Lumisense. The change is applied immediately to all open windows.",
        ["Найдено: {0} из {1}"] = "Found: {0} of {1}",
        ["Версий в истории: {0}"] = "Versions in history: {0}",
        ["Список изменений"] = "Changelog",
        ["Релизы Lumisense"] = "Lumisense Releases",
        ["Добавлено"] = "Added",
        ["Изменено"] = "Changed",
        ["Исправлено"] = "Fixed",
        ["Удалено"] = "Removed",
        ["Версия"] = "Version",
        ["Кол-во"] = "Count",
        ["Направление сортировки"] = "Sort direction",
        ["Версия "] = "Version ",
        ["Ничего не найдено"] = "No results found",
        ["Текущая версия"] = "Current version",
        ["Перейти к этому типу изменений"] = "Go to this change type",
        ["Открыть релиз на GitHub"] = "Open release on GitHub",
        ["Для этой версии пока нет описания изменений."] = "There are no release notes for this version yet.",
        ["Прокрутить вверх"] = "Scroll up",
        ["Версия не выбрана"] = "No version selected",
        ["Свойства обложки"] = "Cover properties",
        ["Изображение"] = "Image",
        ["Формат"] = "Format",
        ["Размеры"] = "Dimensions",
        ["Размер файла"] = "File size",
        ["Разрешение (DPI)"] = "Resolution (DPI)",
        ["Источник"] = "Source",
        ["Трек"] = "Track",
        ["Тип обложки"] = "Cover type",
        ["Поиск обложки"] = "Cover search",
        ["Искать"] = "Search",
        ["Отмена"] = "Cancel",
        ["Введите исполнителя и название и нажмите «Искать»"] = "Enter the artist and title and click 'Search'",
        ["Источники обложек — открытые каталоги iTunes и Deezer (без регистрации и ключей)"] = "Cover sources — public iTunes and Deezer catalogs (no registration or keys)",
        ["Закрыть"] = "Close",
        ["Обложка"] = "Cover",
        ["Воспроизвести"] = "Play",
        ["Добавить/убрать из избранного"] = "Add/remove from favorites",
        ["Закрепить/открепить в избранном"] = "Pin/unpin in favorites",
        ["Показать в проводнике"] = "Show in Explorer",
        ["Копировать имя трека"] = "Copy track name",
        ["Копировать путь к файлу"] = "Copy file path",
        ["Копировать файл"] = "Copy file",
        ["Свойства"] = "Properties",
        ["Изменить теги"] = "Edit tags",
        ["Нормализовать имя файла…"] = "Normalize filename…",
        ["Убрать из плейлиста"] = "Remove from playlist",
        ["Удалить трек с диска…"] = "Delete track from disk…",
        ["Сколько раз прослушан этот трек"] = "Play count for this track",
        ["Включить/выключить эту папку в проигрывании"] = "Enable/disable this folder for playback",
        ["Свернуть/развернуть список треков"] = "Collapse/expand track list",
        ["Добавить файлы в эту папку"] = "Add files to this folder",
        ["Проверить папку на новые треки"] = "Check folder for new tracks",
        ["Убрать эту папку из плейлиста"] = "Remove this folder from the playlist",
        ["Вид плеера"] = "Player view",
        ["Обычный (квадратный)"] = "Normal (square)",
        ["Прямоугольный"] = "Rectangular",
        ["Now Playing (полный экран)"] = "Now Playing (full screen)",
        ["Мини-плеер"] = "Mini player",
        ["Открыть обложку"] = "Open album cover",
        ["Скачать изображение…"] = "Download image…",
        ["Копировать изображение"] = "Copy image",
        ["Файл не выбран"] = "No file selected",
        ["Перемешать"] = "Shuffle",
        ["Повтор: выключен"] = "Repeat: Off",
        ["Предыдущий"] = "Previous",
        ["Пуск/Пауза"] = "Play/Pause",
        ["Следующий"] = "Next",
        ["Стоп"] = "Stop",
        ["Свернуть в мини-плеер"] = "Minimize to mini player",
        ["Скорость воспроизведения"] = "Playback speed",
        ["Без звука"] = "Muted",
        ["Скорость"] = "Speed",
        ["Тон"] = "Tone",
        ["Скрыть плейлист"] = "Hide playlist",
        ["Плейлист"] = "Playlist",
        ["Избранное"] = "Favorites",
        ["Добавить"] = "Add",
        ["Добавить файлы или папку"] = "Add files or a folder",
        ["Файлы…"] = "Files…",
        ["Папку…"] = "Folder…",
        ["Новую папку…"] = "New Folder…",
        ["Создать пустую папку в плейлисте и добавлять в неё файлы вручную"] = "Create an empty folder in the playlist and add files to it manually",
        ["Очистить плейлист"] = "Clear playlist",
        ["Статистика"] = "Statistics",
        ["Настройки"] = "Settings",
        ["Отпустите, чтобы добавить в плейлист"] = "Release to add to playlist",
        ["Закрепить"] = "Pin",
        ["Поверх окон"] = "Always on Top",
        ["Вторая кнопка"] = "Secondary Button",
        ["Повтор"] = "Repeat",
        ["Прозрачность"] = "Opacity",
        ["Закрыть полноэкранный режим (Esc)"] = "Exit Full Screen (Esc)",
        ["Пуск / пауза"] = "Play / Pause",
        ["Показать панель текста"] = "Show Lyrics Panel",
        ["Нажмите, чтобы перейти к выбранному месту трека"] = "Click to jump to the selected track position",
        ["Текст песни"] = "Lyrics",
        ["Синхронный текст"] = "Live Lyrics",
        ["Размер шрифта"] = "Font size",
        ["Эффект активной строки"] = "Active line effect",
        ["Без эффекта"] = "No effect",
        ["Лёгкое увеличение"] = "Subtle scale",
        ["Мягкое свечение"] = "Soft glow",
        ["Свечение и увеличение"] = "Glow and scale",
        ["Активная строка остаётся белой, неактивные — приглушённо-серыми. Настройки применяются сразу в панели текста главного окна."] = "The active line stays white and inactive lines are muted gray. Changes apply immediately in the main window lyrics panel.",
        ["Текст не найден"] = "Lyrics not found",
        ["Загружаем текст…"] = "Loading lyrics…",
        ["Для этого трека пока нет локального текста, текста из тега или подходящего результата поиска."] = "No local lyrics, tag lyrics, or matching search result is available for this track.",
        ["Нет текста"] = "No lyrics",
        ["Открыть встроенный поиск текста"] = "Open built-in lyrics search",
        ["Загрузить .lrc или .txt"] = "Load .lrc or .txt",
        ["Скрыть панель текста"] = "Hide lyrics panel",
        ["Текст песни не найден"] = "Lyrics not found",
        ["Ищем текст автоматически. При необходимости можно загрузить файл .lrc / .txt."] = "Searching for lyrics automatically. You can upload a .lrc / .txt file if needed.",
        ["Результаты поиска"] = "Search results",
        ["Назад к тексту"] = "Back to lyrics",
        ["Исполнитель — название или название трека"] = "Artist — title or track name",
        ["Нет подходящего варианта?"] = "No suitable option?",
        ["Открыть поиск на Genius"] = "Open search on Genius",
        ["Вставить текст"] = "Paste text",
        ["Сохранить текст песни из буфера обмена"] = "Save lyrics from clipboard",
        ["Оформление"] = "Appearance",
        ["Окно и запуск"] = "Window and startup",
        ["Воспроизведение"] = "Playback",
        ["Интеграции"] = "Integrations",
        ["Уведомления"] = "Notifications",
        ["Эквалайзер"] = "Equalizer",
        ["Горячие клавиши"] = "Keyboard Shortcuts",
        ["Профиль"] = "Profile",
        ["Обновления"] = "Updates",
        ["О плеере"] = "About the Player",
        ["Тема"] = "Theme",
        ["Тёмная"] = "Dark",
        ["Светлая"] = "Light",
        ["Акцентный цвет"] = "Accent Color",
        ["Системный (как в Windows)"] = "System (like Windows)",
        ["От текущей обложки"] = "From current wallpaper",
        ["Свой"] = "Custom",
        ["Свой цвет через палитру"] = "Custom color from palette",
        ["Основа окна"] = "Window base",
        ["Mica (лёгкое размытие, по умолчанию)"] = "Mica (light blur, default)",
        ["Acrylic (сильнее размытие и прозрачность)"] = "Acrylic (stronger blur and transparency)",
        ["Цвет основы от текущей обложки"] = "Base color from current wallpaper",
        ["Этот вариант независим от акцентного цвета: можно включить цвет основы от обложки отдельно или совместить его с любым акцентом."] = "This option is independent of the accent color: you can enable the base color from the wallpaper separately or combine it with any accent.",
        ["Mica/Acrylic применяется к главному окну, окну настроек и статистике."] = "Mica/Acrylic applies to the main window, the Settings window, and Statistics.",
        ["Анимация смены обложки"] = "Wallpaper change animation",
        ["Без анимации"] = "No animation",
        ["С анимацией"] = "With animation",
        ["При переключении трека старая обложка «улетает» в сторону, а новая «влетает» с противоположной — как в iTunes."] = "When changing tracks, the old cover 'flies' off to the side and the new one 'flies in' from the opposite side — like in iTunes.",
        ["Включить жесты на обложке"] = "Enable gestures on the cover",
        ["Касание — пуск/пауза; свайп влево или вправо — смена трека; свайп вверх или вниз — громкость. При отключении клик снова открывает обложку."] = "Tap — play/pause; swipe left or right — change track; swipe up or down — volume. When disabled, a click will open the cover again.",
        ["Как выглядит и какого размера главное окно плеера"] = "Appearance and size of the main player window",
        ["Квадратный"] = "Square",
        ["Поверх всех окон"] = "Always on top",
        ["Сворачивать в трей при закрытии"] = "Minimize to tray on close",
        ["Запускать вместе с Windows"] = "Start with Windows",
        ["Запускать свёрнутым в трей"] = "Start minimized to tray",
        ["Окно плеера не появляется при запуске — сразу только значок в трее. Работает и без автозапуска с Windows: например, вместе с ярлыком в папке автозагрузки, если предпочитаете такой способ."] = "The player window doesn't appear at startup — only the tray icon. This also works without Windows autostart: for example, using a shortcut in the Startup folder if you prefer that method.",
        ["Запоминать громкость между запусками"] = "Remember volume between launches",
        ["Логарифмическая регулировка громкости"] = "Logarithmic volume control",
        ["Ползунок громкости меняет уровень так, как его воспринимает слух: заметный на глаз ход в начале и в конце шкалы звучит одинаково плавно, без резкого скачка на малой громкости."] = "The volume slider adjusts levels as perceived by hearing: visually noticeable movement at the start and end of the scale feels equally smooth, without sudden jumps at low volume.",
        ["Никогда не запускать трек при старте"] = "Never start track at startup",
        ["По умолчанию Lumisense продолжает трек с сохранённой позиции, только если он играл при закрытии приложения. С этой настройкой трек всё равно откроется, но останется на паузе."] = "By default, Lumisense resumes a track from its saved position only if it was playing when the app was closed. With this setting enabled, the track will still open but remain paused.",
        ["Кэш интернет-обложек"] = "Online cover cache",
        ["Повторно открытые варианты обложек берутся из локальной папки, чтобы не скачивать изображения заново."] = "Previously opened cover variants are loaded from a local folder so images don’t need to be downloaded again.",
        ["Очистить кэш обложек"] = "Clear cover cache",
        ["Нормализация имён файлов"] = "Filename normalization",
        ["Переименовывает файлы в исходных папках по тегам. Перед изменением Lumisense покажет примеры и пропустит занятые имена или текущий трек."] = "Renames files in their source folders according to tags. Before making changes, Lumisense will show examples and skip names that are already taken or the current track.",
        ["Шаблон"] = "Template",
        ["Токены: {Artist}, {Title}, {Album}, {Track}, {Extension}. Расширение исходного файла сохраняется автоматически."] = "Tokens: {Artist}, {Title}, {Album}, {Track}, {Extension}. The original file extension is preserved automatically.",
        ["Показать предпросмотр и нормализовать"] = "Show preview and normalize",
        ["Действия контекстного меню трека"] = "Track context menu actions",
        ["Снимите галочки, чтобы скрыть ненужные действия при нажатии правой кнопкой по треку в плейлисте. «Воспроизвести» остаётся доступным всегда."] = "Uncheck items to hide unwanted actions when right-clicking a track in the playlist. 'Play' remains always available.",
        ["Нормализовать имя файла"] = "Normalize filename",
        ["Удалить с диска"] = "Delete from disk",
        ["Шаффл без повторов"] = "Shuffle without repeats",
        ["Вместо чисто случайного выбора трека на каждом шаге сначала тасуется весь плейлист целиком, а треки проигрываются по порядку из этой тасовки — так один и тот же трек не может повториться, пока не сыграют все остальные."] = "Instead of choosing a random track at each step, the entire playlist is shuffled first and tracks are played in order from that shuffle — this ensures the same track won't repeat until all others have played.",
        ["Полоса воспроизведения"] = "Playback bar",
        ["Обычная (по умолчанию)"] = "Normal (default)",
        ["Waveform (форма звука, как в SoundCloud)"] = "Waveform (audio waveform, like on SoundCloud)",
        ["Форма звука считается один раз при загрузке трека и держится в памяти, пока плеер открыт — на длинных файлах (FLAC/WAV) первый расчёт может занять секунду-другую."] = "The waveform is computed once when the track is loaded and kept in memory while the player is open — for long files (FLAC/WAV) the initial calculation may take a second or two.",
        ["Выравнивает субъективную громкость между треками по тегам REPLAYGAIN_TRACK_GAIN, если они есть в файле — без этого более тихо смастеренные треки в одном плейлисте с громкими звучат заметно тише. У треков без таких тегов ничего не меняется."] = "Equalizes perceived loudness between tracks using the REPLAYGAIN_TRACK_GAIN tags, if present — without this, quieter-mastered tracks will sound noticeably quieter when mixed with louder ones in the same playlist. Tracks without such tags are unchanged.",
        ["Подключение внешних сервисов, параметры приватности и диагностика."] = "External service connections, privacy, and diagnostics.",
        ["Показывает активность Lumisense в профиле Discord через локальное соединение. Discord и обмен статусами должны быть включены в самом клиенте Discord."] = "Shows Lumisense activity on your Discord profile via a local connection. Discord and status sharing must be enabled in the Discord client.",
        ["Подключить Discord"] = "Connect to Discord",
        ["Открыть журнал Discord"] = "Open Discord log",
        ["Журнал сохраняет события подключения, отправки статуса и ошибки Discord Rich Presence — он поможет при диагностике проблем интеграции."] = "The log records connection events, status updates, and Discord Rich Presence errors — it helps diagnose integration issues.",
        ["Приватность"] = "Privacy",
        ["Показывать название и исполнителя"] = "Show title and artist",
        ["Показывать таймлайн воспроизведения"] = "Show playback timeline",
        ["Локальные файлы и обложки не загружаются в Discord. Обложка может отображаться только как заранее добавленный asset приложения Lumisense."] = "Local files and cover art are not uploaded to Discord. Cover art can only appear as a pre-added asset of the Lumisense application.",
        ["Уведомление о смене трека"] = "Track change notification",
        ["Маленькая карточка с обложкой и названием в углу экрана — появляется на пару секунд при каждом переключении трека и пропадает сама."] = "A small card with cover art and title in the corner of the screen — appears for a few seconds on each track change and then disappears.",
        ["Расположение уведомления"] = "Notification position",
        ["Сверху слева"] = "Top left",
        ["Сверху по центру"] = "Top center",
        ["Сверху справа"] = "Top right",
        ["Снизу слева"] = "Bottom left",
        ["Снизу по центру"] = "Bottom center",
        ["Снизу справа"] = "Bottom right",
        ["Размер"] = "Size",
        ["Маленький"] = "Small",
        ["Средний"] = "Medium",
        ["Большой"] = "Large",
        ["Ширина"] = "Width",
        ["Монитор"] = "Monitor",
        ["Включить эквалайзер"] = "Enable equalizer",
        ["10 полос графического эквалайзера — регулировки применяются сразу же, без перезапуска трека."] = "10-band graphic equalizer — adjustments apply immediately without restarting the track.",
        ["EQ Bypass (временно отключить обработку)"] = "EQ Bypass (temporarily disable processing)",
        ["Пропускает сигнал мимо фильтров, не меняя настройки полос. Удобно для быстрого сравнения звука с EQ и без."] = "Passes the signal around the filters without changing band settings. Useful for quickly comparing sound with and without the EQ.",
        ["0 дБ"] = "0 dB",
        ["1к"] = "1k",
        ["2к"] = "2k",
        ["4к"] = "4k",
        ["8к"] = "8k",
        ["16к"] = "16k",
        ["Сбросить всё"] = "Reset All",
        ["Пресеты"] = "Presets",
        ["Сохрани текущие настройки полос как пресет, переключайся между ними или поделись файлом пресета с кем-то ещё."] = "Save current band settings as a preset, switch between them, or share a preset file with others.",
        ["Применить"] = "Apply",
        ["Сохранить как пресет…"] = "Save as Preset…",
        ["Удалить"] = "Delete",
        ["Поделиться…"] = "Share…",
        ["Сохранить выбранный пресет в файл, чтобы переслать его кому-то ещё"] = "Save the selected preset to a file to send to someone else",
        ["Импортировать…"] = "Import…",
        ["Добавить пресет из файла, полученного от кого-то ещё"] = "Add a preset from a file received from someone else",
        ["Прозрачность окна"] = "Window transparency",
        ["Поверх всех окон (мини-плеер)"] = "Always on Top (Mini Player)",
        ["Закрепить положение (запретить перетаскивание)"] = "Lock position (prevent dragging)",
        ["Прилипание к краям экрана"] = "Snap to screen edges",
        ["Вторая кнопка в мини-плеере"] = "Second button in Mini Player",
        ["Расположение кнопок управления"] = "Control button placement",
        ["Снизу (окно немного подрастает при наведении)"] = "Bottom (window slightly expands on hover)",
        ["На месте обложки и названия (размер окна не меняется)"] = "In place of the cover and title (window size doesn't change)",
        ["Отображение обложки"] = "Cover display",
        ["По умолчанию"] = "Default",
        ["Винил (медленное вращение во время воспроизведения)"] = "Vinyl (slow rotation during playback)",
        ["Виниловый режим делает обложку круглой и приостанавливает её вращение вместе с треком."] = "Vinyl mode makes the cover round and pauses its rotation along with the track.",
        ["Показывать полосу прогресса"] = "Show progress bar",
        ["Окно мини-плеера становится немного компактнее, если полоса не нужна. Перемотка кликом по обложке/названию по-прежнему недоступна — только сама полоса."] = "The mini-player window becomes slightly more compact if the bar isn't needed. Seeking by clicking the cover/title is still unavailable — only the bar.",
        ["Показывать прогресс вокруг обложки"] = "Show progress around cover",
        ["Тонкий акцентный контур повторяет скруглённую форму обложки и показывает прогресс текущего трека. Его можно включить отдельно от обычной полосы прогресса."] = "A thin accent outline follows the rounded shape of the cover and shows the current track's progress. It can be enabled separately from the regular progress bar.",
        ["Цвет контура"] = "Outline color",
        ["Акцент из вкладки «Оформление»"] = "Accent from the Appearance tab",
        ["Фиксированный цвет"] = "Fixed color",
        ["Вторая строка в мини-плеере"] = "Second line in mini-player",
        ["Исполнитель"] = "Artist",
        ["Ничего (только название трека)"] = "Nothing (track name only)",
        ["Оставшееся время трека"] = "Remaining track time",
        ["Работают из любого окна, даже когда плеер свёрнут. Нажмите на комбинацию и введите новую — Esc отменяет запись."] = "They work from any window, even when the player is minimized. Click the shortcut and enter a new one — Esc cancels.",
        ["Очистить"] = "Clear",
        ["Следующий трек"] = "Next track",
        ["Предыдущий трек"] = "Previous track",
        ["Громкость +"] = "Volume +",
        ["Громкость -"] = "Volume -",
        ["Режим повтора"] = "Repeat mode",
        ["Перемотка вперёд (5 сек)"] = "Skip forward (5 sec)",
        ["Перемотка назад (5 сек)"] = "Skip backward (5 sec)",
        ["Удалить трек с диска"] = "Delete track from disk",
        ["Перенос настроек плеера на другой компьютер одним файлом — тема, акцентный цвет, эквалайзер, горячие клавиши и остальные переключатели на этих страницах. Плейлист, избранное и история прослушиваний сюда не входят."] = "Transfer player settings to another computer in a single file — theme, accent color, equalizer, hotkeys, and other switches from these pages. Playlists, favorites, and listening history are not included.",
        ["Экспортировать настройки…"] = "Export settings…",
        ["Сохранить в один .lumi-файл"] = "Save to a single .lumi file",
        ["Импортировать настройки…"] = "Import settings…",
        ["Из .lumi-файла, экспортированного на другом компьютере"] = "From a .lumi file exported on another computer",
        ["Сброс"] = "Reset",
        ["Возвращает тему, акцент, подложку окна, вид и размер плеера, громкость, шафл/повтор, горячие клавиши, мини-плеер и остальные переключатели к значениям по умолчанию. Плейлист, избранное, история прослушиваний, статистика и сохранённые пресеты эквалайзера не затрагиваются."] = "Resets theme, accent, window background, player view and size, volume, shuffle/repeat, hotkeys, mini-player and other toggles to their default values. Playlists, favorites, listening history, statistics and saved equalizer presets are not affected.",
        ["Сбросить плеер к исходному состоянию"] = "Reset player to default state",
        ["Настройки внешнего вида и поведения — к значениям по умолчанию"] = "Appearance and behavior settings — reset to defaults",
        ["Полный сброс данных"] = "Reset all data",
        ["Очистить настройки, плейлисты, избранное, историю и пресеты"] = "Clear settings, playlists, favorites, history, and presets",
        ["Проверить обновления"] = "Check for updates",
        ["Сверить версию с GitHub"] = "Verify version on GitHub",
        ["Источник загрузки обновлений"] = "Update download source",
        ["Откуда скачивать обновление. По умолчанию — напрямую с GitHub; если он у вас недоступен или скачивается очень медленно, можно выбрать одно из зеркал gh-proxy. На саму проверку версии эта настройка не влияет."] = "Where to download updates from. By default, updates are downloaded directly from GitHub; if GitHub is unavailable or very slow for you, you can choose one of the gh-proxy mirrors. This setting does not affect version checking.",
        ["GitHub (напрямую)"] = "GitHub (direct)",
        ["gh-proxy.org (зеркало)"] = "gh-proxy.org (mirror)",
        ["v4.gh-proxy.org (зеркало, только IPv4)"] = "v4.gh-proxy.org (mirror, IPv4 only)",
        ["v6.gh-proxy.org (зеркало, только IPv6)"] = "v6.gh-proxy.org (mirror, IPv6 only)",
        ["cdn.gh-proxy.org (зеркало, через CDN)"] = "cdn.gh-proxy.org (mirror, via CDN)",
        ["Все версии"] = "All versions",
        ["Полный список версий плеера с GitHub — можно поставить не только последнюю, но и любую другую, в том числе более старую."] = "Full list of player versions from GitHub — you can install not only the latest but any other, including older releases.",
        ["Загружаем список версий…"] = "Loading version list…",
        ["Lumisense — настраиваемый аудиоплеер для Windows 11 в стиле Fluent Design. Он помогает удобно управлять музыкальной библиотекой, настраивать звучание и адаптировать интерфейс под себя — от мини-плеера и горячих клавиш до темы, зависящей от текущей обложки."] = "Lumisense — a customizable audio player for Windows 11 with Fluent Design. It helps you manage your music library, fine-tune sound, and adapt the interface to your needs — from a mini player and hotkeys to a theme that matches the current album art.",
        ["Собрано на .NET 8, WPF-UI и NAudio."] = "Built on .NET 8, WPF UI and NAudio.",
        ["Разработчик"] = "Developer",
        ["Открыть папку с логами"] = "Open logs folder",
        ["Пригодится, если плеер упал или повёл себя странно"] = "Useful if the player crashed or behaved strangely.",
        ["Открытый исходный код"] = "Open source",
        ["Открыть репозиторий Lumisense на GitHub"] = "Open Lumisense repository on GitHub",
        ["Что нового в текущей версии"] = "What's new in this version",
        ["прослушиваний всего"] = "total plays",
        ["разных треков"] = "different tracks",
        ["прослушано"] = "listened",
        ["Топ исполнителей"] = "Top artists",
        ["Топ треков"] = "Top tracks",
        ["Сбросить прослушивания"] = "Reset listen counts",
        ["Считаем статистику…"] = "Calculating statistics…",
        ["Пока нет прослушанных треков"] = "No tracks listened yet",
        ["Статистика появится, как только что-нибудь будет дослушано хотя бы до половины"] = "Statistics will appear once at least one track has been listened to halfway",
        ["Введите значение:"] = "Enter value:",
        ["Создать"] = "Create",
        ["Файл"] = "File",
        ["Имя файла"] = "File name",
        ["Папка"] = "Folder",
        ["Прослушиваний"] = "Plays",
        ["Аудио"] = "Audio",
        ["Длительность"] = "Duration",
        ["Битрейт"] = "Bitrate",
        ["Частота дискретизации"] = "Sample rate",
        ["Каналы"] = "Channels",
        ["Даты"] = "Dates",
        ["Создан"] = "Created",
        ["Изменён"] = "Modified",
        ["Изменение тегов"] = "Edit tags",
        ["Скопировать название трека"] = "Copy track title",
        ["Нажмите, чтобы выбрать изображение"] = "Click to select an image",
        ["Изменить…"] = "Change…",
        ["Найти в интернете…"] = "Search the web…",
        ["Поиск обложки по исполнителю и названию трека"] = "Search for cover by artist and track title",
        ["Удалить обложку"] = "Remove cover",
        ["Название"] = "Title",
        ["Альбом"] = "Album",
        ["Год"] = "Year",
        ["Трек №"] = "Track No.",
        ["Жанр"] = "Genre",
        ["Комментарий"] = "Comment",
        ["Сохранить"] = "Save",
        ["Доступно обновление"] = "Update available",
        ["Версия 1.1.0 (у вас 1.0.0)"] = "Version 1.1.0 (you have 1.0.0)",
        ["Что нового:"] = "What's new:",
        ["Позже"] = "Later",
        ["Подробнее"] = "Learn more",
        ["Скачать и установить"] = "Download and install",
        ["Поиск по версиям и типу"] = "Search by versions and type",
        ["Исполнитель и название трека"] = "Artist and track title",
        ["Поиск по плейлисту"] = "Search by playlist",
        ["{Binding Duration, StringFormat={}{0:0} сек.}"] = "{Binding Duration, StringFormat={}{0:0} sec.}",
        ["Поиск настроек"] = "Search settings",
        ["Русский"] = "Russian",
        ["Например: {Artist} - {Title}{Extension}"] = "For example: {Artist} - {Title}{Extension}",
        ["Не указано в файле"] = "Not specified in file",
        ["Не указан"] = "Not specified",
        ["Не указан в файле"] = "Not specified in file",
        ["Необработанное исключение (AppDomain, приложение сейчас завершится)"] = "Unhandled exception (AppDomain, application will now terminate)",
        ["Необработанное исключение в UI-потоке"] = "Unhandled exception in UI thread",
        ["Что-то пошло не так, но плеер попробует продолжить работу.\\n\\nПодробности сохранены в лог-файл, его можно найти в настройках (страница \\\"Обновления\\\") или в папке %AppData%\\Lumisense\\logs.\\n\\n{args.Exception.Message}"] = "Something went wrong, but the player will try to continue.\\n\\nDetails have been saved to the log file; you can find it in Settings (the \\\"Updates\\\" page) or in the %AppData%\\Lumisense\\logs folder.\\n\\n{args.Exception.Message}",
        ["Lumisense — внутренняя ошибка"] = "Lumisense — Internal Error",
        ["Необработанное исключение в фоновой задаче (fire-and-forget)"] = "Unhandled exception in background task (fire-and-forget)",
        ["Lumisense запускается — версия ОС {Environment.OSVersion}, .NET {Environment.Version}, 64-бит: {Environment.Is64BitProcess}"] = "Lumisense is starting — OS version {Environment.OSVersion}, .NET {Environment.Version}, 64-bit: {Environment.Is64BitProcess}",
        ["Плеер уже запущен — переключаю вид у уже открытого экземпляра и завершаюсь (это не ошибка)."] = "Player is already running — switching the view in the already-open instance and exiting (this is not an error).",
        ["Не удалось просигналить уже запущенному экземпляру: {ex.Message}"] = "Failed to signal the already-running instance: {ex.Message}",
        ["Не удалось создать главное окно — плеер не может запуститься"] = "Failed to create the main window — the player cannot start.",
        ["Lumisense не удалось запуститься.\\n\\nПодробности сохранены в лог-файл (%AppData%\\Lumisense\\logs).\\n\\n{ex.Message}"] = "Lumisense failed to start.\\n\\nDetails have been saved to the log file (%AppData%\\Lumisense\\logs).\\n\\n{ex.Message}",
        ["Lumisense — ошибка запуска"] = "Lumisense — Startup Error",
        ["Главное окно создано и показано — запуск завершён успешно."] = "Main window created and shown — startup completed successfully.",
        ["Lumisense завершается (код выхода {e.ApplicationExitCode})"] = "Lumisense is exiting (exit code {e.ApplicationExitCode})",
        ["{art.PixelWidth} × {art.PixelHeight} пикс."] = "{art.PixelWidth} × {art.PixelHeight} px",
        ["Неизвестно"] = "Unknown",
        ["Обложка альбома (лицевая)"] = "Album cover (front)",
        ["Обложка альбома (обратная)"] = "Album cover (back)",
        ["Фото исполнителя"] = "Artist photo",
        ["Носитель (диск/кассета)"] = "Media (disc/cassette)",
        ["Иллюстрация"] = "Illustration",
        ["{bytes / mb:0.0} МБ"] = "{bytes / mb:0.0} MB",
        ["{bytes / kb:0.0} КБ"] = "{bytes / kb:0.0} KB",
        ["{bytes} байт"] = "{bytes} bytes",
        ["Поиск отменён"] = "Search canceled",
        ["Ищем…"] = "Searching…",
        ["Ничего не найдено. Попробуйте изменить запрос."] = "No results found. Try changing your search.",
        ["Не удалось выполнить поиск: {ex.Message}"] = "Failed to perform search: {ex.Message}",
        ["Не удалось загрузить обложку:\\n{ex.Message}"] = "Failed to load cover:\\n{ex.Message}",
        ["Ошибка загрузки"] = "Load error",
        ["Источник изображения не входит в список доверенных HTTPS-доменов."] = "The image source is not in the list of trusted HTTPS domains.",
        ["Сервер вернул данные, которые не являются поддерживаемым изображением."] = "The server returned data that is not a supported image.",
        ["Ответ превышает допустимый размер."] = "The response exceeds the allowed size.",
        ["Отдельные файлы"] = "Separate files",
        ["Не удалось освободить подготовленный AudioFileReader"] = "Failed to dispose prepared AudioFileReader",
        ["Ошибка в фоновой операции \\\"{operationName}\\\""] = "Error in background operation \\\"{operationName}\\\"",
        ["Не удалось зарегистрировать глобальные горячие клавиши — возможно, какая-то из комбинаций уже занята другим приложением"] = "Failed to register global hotkeys — one of the combinations may already be in use by another application",
        ["Не удалось включить интеграцию с Now Playing (SMTC)"] = "Failed to enable Now Playing (SMTC) integration",
        ["Не удалось создать значок в трее"] = "Failed to create tray icon",
        ["Не удалось построить waveform для файла: {filePath}"] = "Failed to build waveform for file: {filePath}",
        ["Не удалось извлечь цвет из обложки: {ex.Message}"] = "Failed to extract color from cover: {ex.Message}",
        ["Изображение PNG (*.png)|*.png"] = "PNG Image (*.png)|*.png",
        ["Изображение BMP (*.bmp)|*.bmp"] = "BMP Image (*.bmp)|*.bmp",
        ["Изображение GIF (*.gif)|*.gif"] = "GIF Image (*.gif)|*.gif",
        ["Изображение WebP (*.webp)|*.webp"] = "WebP Image (*.webp)|*.webp",
        ["Изображение JPEG (*.jpg)|*.jpg"] = "JPEG Image (*.jpg)|*.jpg",
        ["Сохранить обложку"] = "Save cover",
        ["Не удалось сохранить изображение:\\n{ex.Message}"] = "Failed to save image:\\n{ex.Message}",
        ["Ошибка"] = "Error",
        ["Не удалось скопировать изображение:\\n{ex.Message}"] = "Failed to copy image:\\n{ex.Message}",
        ["Показать плейлист"] = "Show playlist",
        ["В перетащенных папках не найдено поддерживаемых аудиофайлов."] = "No supported audio files were found in the dragged folders.",
        ["Среди перетащенного не найдено ни поддерживаемых аудиофайлов, ни папок."] = "No supported audio files or folders were found among the dragged items.",
        ["Аудиофайлы (*.mp3;*.wav;*.wma;*.flac;*.m4a;*.aac;*.ogg)|*.mp3;*.wav;*.wma;*.flac;*.m4a;*.aac;*.ogg|Все файлы (*.*)|*.*"] = "Audio files (*.mp3;*.wav;*.wma;*.flac;*.m4a;*.aac;*.ogg)|*.mp3;*.wav;*.wma;*.flac;*.m4a;*.aac;*.ogg|All files (*.*)|*.*",
        ["Выберите аудиофайлы"] = "Select audio files",
        ["Новая папка"] = "New Folder",
        ["Название папки:"] = "Folder name:",
        ["Добавить файлы в «{folder.DisplayName}»"] = "Add files to «{folder.DisplayName}»",
        ["Выберите папку с музыкой"] = "Select music folder",
        ["В выбранной папке не найдено поддерживаемых аудиофайлов."] = "No supported audio files were found in the selected folder.",
        ["Слишком длинный путь при сканировании {folderPath}: {ex.Message}"] = "Path too long while scanning {folderPath}: {ex.Message}",
        ["Не удалось просканировать папку {folderPath}: {ex.Message}"] = "Failed to scan folder {folderPath}: {ex.Message}",
        ["Нет доступных файлов для нормализации."] = "No files available for normalization.",
        ["Нормализация имён"] = "Normalize names",
        ["нет файлов, подходящих для переименования"] = "No files suitable for renaming.",
        ["уже соответствует шаблону"] = "Already matches the template.",
        ["Имя файла уже соответствует выбранному шаблону. Переименование не требуется."] = "The file name already matches the selected template. Renaming is not required.",
        ["Ни один файл не будет переименован: {reason}."] = "No files will be renamed: {reason}.",
        ["\\n\\nПропущено: {skipped} (уже соответствует шаблону, конфликтует или играет сейчас)."] = "\\n\\nSkipped: {skipped} (already matches the template, conflicts, or is currently playing).",
        ["Переименовать файлов: {candidates.Count}.\\n\\n{examples}{skippedText}\\n\\n"] = "Files to rename: {candidates.Count}.\\n\\n{examples}{skippedText}\\n\\n",
        ["Файлы останутся в исходных папках; изменятся только имена. Продолжить?"] = "Files will remain in their original folders; only names will change. Continue?",
        ["Новых треков в этой папке не найдено."] = "No new tracks found in this folder.",
        ["Очистить весь плейлист?\\n\\nВсе папки и файлы будут убраны из списка (сами файлы на диске не затрагиваются)."] = "Clear the entire playlist?\\n\\nAll folders and files will be removed from the list (the files on disk will not be affected).",
        ["Очистка плейлиста"] = "Clear playlist",
        ["Имя файла нормализовано. Переименовано: {result.RenamedCount}; пропущено: {result.SkippedCount}.{errors}"] = "Filename normalized. Renamed: {result.RenamedCount}; skipped: {result.SkippedCount}.{errors}",
        ["Не удалось нормализовать имя файла:\\n{ex.Message}"] = "Failed to normalize file name:\\n{ex.Message}",
        ["Удалить файл «{trackName}» с диска?\\n\\nФайл будет перемещён в корзину, а трек — убран из всех плейлистов."] = "Delete file \"{trackName}\" from disk?\\n\\nThe file will be moved to the Recycle Bin and the track removed from all playlists.",
        ["Удаление трека с диска"] = "Delete track from disk",
        ["Не удалось удалить файл:\\n{filePath}\\n\\n{ex.Message}"] = "Failed to delete file:\\n{filePath}\\n\\n{ex.Message}",
        ["Ошибка удаления"] = "Deletion error",
        ["Не удалось открыть аудиофайл: {filePath}"] = "Failed to open audio file: {filePath}",
        ["Не удалось открыть файл:\\n{filePath}\\n\\n{ex.Message}"] = "Failed to open file:\\n{filePath}\\n\\n{ex.Message}",
        ["Ошибка воспроизведения"] = "Playback error",
        ["Не удалось прочитать metadata или embedded cover для файла {filePath}: {ex.Message}"] = "Failed to read metadata or embedded cover for file {filePath}: {ex.Message}",
        ["Ошибка обработки завершения воспроизведения в Dispatcher callback"] = "Error handling playback completion in Dispatcher callback",
        ["Не удалось поставить PlaybackStopped callback в Dispatcher"] = "Failed to post PlaybackStopped callback to Dispatcher",
        ["Не удалось поставить воспроизведение на паузу"] = "Failed to pause playback",
        ["Не удалось запустить воспроизведение"] = "Failed to start playback",
        ["Не удалось запустить воспроизведение — возможно, устройство вывода звука недоступно.\\n\\n{ex.Message}"] = "Failed to start playback — the audio output device may be unavailable.\\n\\n{ex.Message}",
        ["Не удалось освободить WaveOutEvent"] = "Failed to release WaveOutEvent",
        ["Не удалось корректно остановить устройство вывода"] = "Failed to properly stop the output device",
        ["Не удалось освободить AudioFileReader"] = "Failed to release AudioFileReader",
        ["Повтор: весь плейлист"] = "Repeat: entire playlist",
        ["Повтор: один трек"] = "Repeat: single track",
        ["Не удалось обновить ReplayGain для файла: {path}"] = "Failed to update ReplayGain for file: {path}",
        ["-{remaining:mm\\:ss} осталось"] = "-{remaining:mm\\:ss} left",
        ["Версия {current.Version}"] = "Version {current.Version}",
        ["Проверяем…"] = "Checking…",
        ["Доступна версия {result.LatestVersion}"] = "Version {result.LatestVersion} is available",
        ["У вас последняя версия ({result.CurrentVersion})"] = "You have the latest version ({result.CurrentVersion})",
        ["Не удалось проверить: {result.ErrorMessage}"] = "Could not check: {result.ErrorMessage}",
        ["Не удалось загрузить список версий: {errorMessage}"] = "Failed to load version list: {errorMessage}",
        ["На GitHub пока нет ни одного опубликованного релиза."] = "No published releases on GitHub yet.",
        [" · текущая версия"] = " · current version",
        [" · пререлиз"] = " · prerelease",
        ["Дата публикации неизвестна"] = "Release date unknown",
        ["Пререлиз"] = "Prerelease",
        ["В релизе нет .exe-установщика"] = "No .exe installer is available in this release",
        ["В релизе отсутствует SHA-256 установщика"] = "The installer SHA-256 is missing from this release",
        [" · в релизе нет .exe-установщика"] = " · no .exe installer in this release",
        ["Переустановить"] = "Reinstall",
        ["Установить"] = "Install",
        ["язык русский english language locale локализация"] = "language Russian English language locale localization",
        ["тёмная светлая цвет тема оформление dark light"] = "Dark / Light color theme",
        ["акцент цвет палитра accent color"] = "Accent color palette",
        ["mica acrylic blur акрил размытие блюр подложка фон backdrop"] = "Backdrop: Mica, Acrylic, Blur",
        ["обложка cover основа фон окно цвет theme"] = "Cover (window background) theme",
        ["анимация обложка переход трек itunes слайд fly transition album art cover"] = "Album art transition animation (iTunes track cover): slide, fly",
        ["Жесты на обложке"] = "Cover gestures",
        ["жесты обложка касание свайп пуск пауза громкость следующий предыдущий gesture swipe cover"] = "Cover gestures: tap, swipe — play/pause, volume, next/previous",
        ["квадратный прямоугольный мини плеер вид размер окна square rectangular mini"] = "Mini player shape: square or rectangular",
        ["topmost всегда сверху главное окно"] = "Topmost — always on top (main window)",
        ["трей закрытие свернуть tray"] = "Tray / Close / Minimize",
        ["автозапуск запуск windows автозагрузка startup"] = "Run at Windows startup",
        ["запуск свёрнутым трей автозапуск скрыто hidden startup tray"] = "Start hidden (minimized to tray on startup)",
        ["громкость запуск volume"] = "Startup volume",
        ["громкость логарифм слух дБ db volume logarithmic"] = "Logarithmic volume (dB)",
        ["Не запускать трек при старте"] = "Do not play track on startup",
        ["старт запуск продолжить воспроизведение последний трек пауза resume autoplay"] = "Resume playback of last track on startup",
        ["Очистить кэш интернет-обложек"] = "Clear online cover cache",
        ["кэш обложка интернет очистить удалить cover cache artwork image"] = "Clear cover art cache",
        ["нормализация имя файл шаблон переименование artist title album track extension rename"] = "Filename normalization template (rename)",
        ["контекстное меню правый клик пкм трек плейлист скрыть отключить действия проводник копировать теги свойства удалить"] = "Context menu (right-click) — track/playlist: Hide, Disable, Open in Explorer, Copy Tags, Properties, Delete",
        ["discord статус rich presence rpc активность"] = "Discord status (Rich Presence/RPC)",
        ["discord подключить connection rich presence статус"] = "Connect to Discord (Rich Presence)",
        ["discord журнал лог диагностика ошибка rich presence"] = "Discord log/diagnostics (Rich Presence)",
        ["Приватность Discord: название и исполнитель"] = "Discord privacy: track title and artist",
        ["discord приватность название исполнитель трек"] = "Discord privacy: track title and artist",
        ["Приватность Discord: таймлайн"] = "Discord privacy: timeline",
        ["discord приватность время прогресс таймлайн"] = "Discord privacy: time/progress timeline",
        ["equalizer эквалайзер частоты полосы бас звук eq"] = "Equalizer (EQ): frequency bands (bass)",
        ["bypass обход эквалайзер eq временно сравнение фильтры"] = "Bypass (EQ): temporarily bypass equalizer for comparison",
        ["Прозрачность окна мини-плеера"] = "Mini-player window transparency",
        ["прозрачность opacity мини плеер"] = "Mini-player opacity",
        ["topmost мини плеер"] = "Topmost (mini-player)",
        ["Закрепить положение (мини-плеер)"] = "Lock position (mini-player)",
        ["закрепить перетаскивание pin мини плеер"] = "Pin mini-player (disable dragging)",
        ["Прилипание к краям экрана (мини-плеер)"] = "Snap to screen edges (mini-player)",
        ["прилипание магнит края экран snap edge мини плеер"] = "Snap to edge (mini-player)",
        ["вторая кнопка повтор перемешать избранное сердечко favorite shuffle repeat мини плеер"] = "Secondary button: repeat, shuffle, favorite (heart) — mini-player",
        ["Отображение обложки (мини-плеер)"] = "Show cover (mini-player)",
        ["обложка винил пластинка вращение круглая artwork vinyl rotate мини плеер"] = "Vinyl cover: rotating round artwork (mini-player)",
        ["Показывать полосу прогресса (мини-плеер)"] = "Show progress bar (mini-player)",
        ["полоса прогресс progress bar скрыть мини плеер"] = "Hide progress bar (mini-player)",
        ["Прогресс вокруг обложки (мини-плеер)"] = "Progress around cover (mini-player)",
        ["контур скруглённый квадрат прогресс обложка арт мини плеер artwork outline"] = "Rounded square outline for cover progress (mini-player)",
        ["Цвет контура прогресса (мини-плеер)"] = "Progress outline color (mini-player)",
        ["акцент фиксированный цвет палитра контур прогресс обложка мини плеер artwork outline color"] = "Accent/fixed color palette for progress outline (mini-player)",
        ["play pause горячая клавиша"] = "Play/pause hotkey",
        ["next горячая клавиша"] = "Next hotkey",
        ["previous горячая клавиша"] = "Previous hotkey",
        ["stop горячая клавиша"] = "stop hotkey",
        ["volume up громкость горячая клавиша"] = "volume up hotkey",
        ["volume down громкость горячая клавиша"] = "volume down hotkey",
        ["mute без звука горячая клавиша"] = "mute hotkey",
        ["shuffle перемешать горячая клавиша"] = "shuffle hotkey",
        ["repeat повтор горячая клавиша"] = "repeat hotkey",
        ["delete удалить трек диск горячая клавиша"] = "delete track/disk hotkey",
        ["шаффл перемешать shuffle bag колода без повторов"] = "shuffle bag (no repeats)",
        ["waveform форма звука soundcloud полоса прогресс seek слайдер"] = "waveform (SoundCloud) progress/seek slider",
        ["replaygain громкость выравнивание нормализация gain"] = "ReplayGain volume normalization",
        ["уведомление тост смена трека toast notification"] = "track change toast notification",
        ["уведомление угол расположение позиция монитор экран размер position monitor screen size"] = "notification corner/position (monitor/screen size)",
        ["Размер уведомления"] = "Notification size",
        ["размер уведомление тост маленький средний большой size toast notification"] = "Toast notification size: small, medium, large",
        ["Ширина уведомления"] = "Notification width",
        ["ширина уведомление тост размер width toast notification size"] = "Toast notification width",
        ["Экспортировать настройки"] = "Export settings",
        ["экспорт настройки профиль lumi файл backup export profile"] = "Export settings: profile, lumi, file, backup",
        ["Импортировать настройки"] = "Import settings",
        ["импорт настройки профиль lumi файл backup import restore profile"] = "Import settings: profile, lumi, file, backup, restore",
        ["сброс сбросить умолчание reset default настройки factory"] = "Reset to factory defaults",
        ["версия lumisense о программе о плеере"] = "lumisense version, About, About player",
        ["update mirror зеркало gh-proxy обновление скачать источник"] = "update mirror, gh-proxy, update, download, source",
        ["версии история версия откат downgrade install version releases обновление скачать установить zip exe установщик"] = "versions, history, version rollback, downgrade, install version, releases, update, download, install, zip, exe, installer",
        ["обновление update github версия проверить"] = "Check for updates on GitHub",
        ["патчноуты changelog версии история изменений"] = "Patch notes (changelog / version history)",
        ["разработчик автор github telegram wasssly ссылки контакты аватар"] = "Developer / Author — GitHub, Telegram, wasssly — Links, Contacts, Avatar",
        ["логи log ошибка краш crash диагностика"] = "Logs (error, crash, diagnostics)",
        ["Удалить локально сохранённые интернет-обложки?\\n\\nПри следующем поиске нужные изображения будут скачаны заново."] = "Delete locally saved online covers?\\n\\nThey will be downloaded again during the next search.",
        ["Очистить кэш обложек?"] = "Clear artwork cache?",
        ["Кэш обложек уже пуст."] = "Artwork cache is already empty.",
        ["Удалено файлов: {result.DeletedFiles}; освобождено: {FormatArtworkCacheSize(result.FreedBytes)}."] = "Deleted files: {result.DeletedFiles}; freed: {FormatArtworkCacheSize(result.FreedBytes)}.",
        [" Не удалось удалить файлов: {result.FailedFiles}."] = "Could not delete files: {result.FailedFiles}.",
        ["Не удалось очистить кэш: {ex.Message}"] = "Failed to clear cache: {ex.Message}",
        ["{bytes} Б"] = "{bytes} B",
        ["{bytes / 1024.0:0.#} КБ"] = "{bytes / 1024.0:0.#} KB",
        ["{bytes / (1024.0 * 1024.0):0.#} МБ"] = "{bytes / (1024.0 * 1024.0):0.#} MB",
        ["Пользователь включил Discord Rich Presence из настроек."] = "User enabled Discord Rich Presence in settings.",
        ["Пользователь открыл журнал диагностики Discord из настроек."] = "User opened the Discord diagnostics log from settings.",
        ["Не удалось открыть журнал Discord:\\n{ex.Message}"] = "Failed to open the Discord log:\\n{ex.Message}",
        ["Обновить подключение Discord"] = "Refresh Discord connection",
        ["Discord Rich Presence включён. При начале воспроизведения Lumisense обновит ваш статус."] = "Discord Rich Presence is enabled. When playback starts, Lumisense will update your status.",
        ["Нажмите «Подключить Discord», чтобы включить Rich Presence с официальным приложением Lumisense."] = "Click “Connect to Discord” to enable Rich Presence with the official Lumisense app.",
        ["Автоматически (тот же монитор, что и окно плеера)"] = "Automatically (same monitor as the player window)",
        ["Монитор {i + 1} — {s.Bounds.Width}×{s.Bounds.Height}"] = "Monitor {i + 1} — {s.Bounds.Width}×{s.Bounds.Height}",
        [" (основной)"] = " (primary)",
        [")}{gainDb:0.#} дБ"] = ")}{gainDb:0.#} dB",
        ["Сохранить пресет"] = "Save preset",
        ["Название пресета:"] = "Preset name:",
        ["Удалить пресет \\\"{preset.Name}\\\"?"] = "Delete preset \\\"{preset.Name}\\\"?",
        ["Удаление пресета"] = "Delete preset",
        ["Поделиться пресетом эквалайзера"] = "Share equalizer preset",
        ["Пресет эквалайзера (*.json)|*.json"] = "Equalizer preset (*.json)|*.json",
        ["Не удалось сохранить пресет:\\n{ex.Message}"] = "Failed to save preset:\\n{ex.Message}",
        ["Импортировать пресет эквалайзера"] = "Import equalizer preset",
        ["Пресет эквалайзера (*.json)|*.json|Все файлы (*.*)|*.*"] = "Equalizer preset (*.json)|*.json|All files (*.*)|*.*",
        ["Не удалось прочитать пресет — файл повреждён или это не пресет Lumisense."] = "Failed to read preset — the file is corrupted or it's not a Lumisense preset.",
        ["Подготавливается предпросмотр файлов…"] = "Preparing file previews…",
        ["Нормализация отменена."] = "Normalization canceled.",
        [" Ошибок: {result.Errors.Count}."] = " Errors: {result.Errors.Count}.",
        ["Готово. Переименовано: {result.RenamedCount}; пропущено: {result.SkippedCount}.{errors}"] = "Done. Renamed: {result.RenamedCount}; skipped: {result.SkippedCount}.{errors}",
        ["Не удалось нормализовать имена: {ex.Message}"] = "Failed to normalize names: {ex.Message}",
        ["Настройки сохранены."] = "Settings saved.",
        ["Экспорт завершён"] = "Export completed",
        ["Не удалось сохранить файл:\\n{ex.Message}"] = "Failed to save file:\\n{ex.Message}",
        ["Ошибка экспорта"] = "Export error",
        ["Не удалось прочитать этот файл — он повреждён или это не .lumi-профиль."] = "Could not read this file — it is corrupted or not a .lumi profile.",
        ["Ошибка импорта"] = "Import error",
        ["Настройки импортированы.\\n\\nЧасть из них (хоткеи, эквалайзер, поведение трея и мини-плеера) применится полностью после перезапуска плеера."] = "Settings imported.\\n\\nSome of them (hotkeys, equalizer, tray and mini-player behavior) will take full effect after restarting the player.",
        ["Импорт завершён"] = "Import completed",
        ["Сбросить тему, акцент, подложку окна, вид и размер плеера, громкость, шафл/повтор, горячие клавиши, мини-плеер и остальные настройки к значениям по умолчанию?\\n\\n"] = "Reset theme, accent, window backdrop, player appearance and size, volume, shuffle/repeat, hotkeys, mini-player and other settings to their defaults?\\n\\n",
        ["Плейлист, избранное, история прослушиваний, статистика и сохранённые пресеты эквалайзера затронуты не будут."] = "Playlists, favorites, listening history, statistics and saved equalizer presets will not be affected.",
        ["Сбросить плеер?"] = "Reset the player?",
        ["Плеер сброшен к исходным настройкам.\\n\\nЧасть из них (хоткеи, эквалайзер, поведение трея и мини-плеера, размер и положение окна) применится полностью после перезапуска плеера."] = "The player has been reset to default settings.\\n\\nSome settings (hotkeys, equalizer, tray and mini-player behavior, window size and position) will fully take effect after restarting the player.",
        ["Сброс завершён"] = "Reset complete",
        ["Будут удалены настройки, сохранённые плейлисты, избранное, история прослушиваний, статистика и пресеты эквалайзера.\\n\\n"] = "Settings, saved playlists, favorites, listening history, statistics, and equalizer presets will be deleted.\\n\\n",
        ["Аудиофайлы на диске не удаляются. Продолжить?"] = "Audio files on disk will not be deleted. Continue?",
        ["Это действие нельзя отменить. Выполнить полный сброс сейчас?"] = "This action cannot be undone. Perform a full reset now?",
        ["Подтвердите полный сброс"] = "Confirm full reset",
        ["Данные очищены. Для полного применения стандартных настроек перезапустите Lumisense."] = "Data cleared. Restart Lumisense to fully apply default settings.",
        ["Нажмите комбинацию…"] = "Press the key combination…",
        ["Нужен Ctrl/Alt/Shift/Win…"] = "Requires Ctrl/Alt/Shift/Win…",
        ["Не задано"] = "Not set",
        ["Неизвестный исполнитель"] = "Unknown artist",
        ["Статистика собирается с {since:d MMMM yyyy}"] = "Statistics collected since {since:d MMMM yyyy}",
        ["Статистика собирается с {0}"] = "Statistics collected since {0}",
        ["{(int)span.TotalDays} дн {span.Hours} ч"] = "{(int)span.TotalDays} d {span.Hours} h",
        ["{(int)span.TotalHours} ч {span.Minutes} мин"] = "{(int)span.TotalHours} h {span.Minutes} min",
        ["{(int)span.TotalMinutes} мин {span.Seconds} сек"] = "{(int)span.TotalMinutes} min {span.Seconds} sec",
        ["{(int)span.TotalSeconds} сек"] = "{(int)span.TotalSeconds} sec",
        ["прослушивание"] = "play",
        ["прослушивания"] = "plays",
        ["прослушиваний"] = "plays",
        ["Сбросить счётчики прослушиваний по всем трекам?\\n\\nЭто обнулит \\\"Прослушано треков\\\", "] = "Reset play counters for all tracks?\\n\\nThis will reset \\\"Tracks played\\\", ",
        ["\\\"Разных треков\\\" и оба топ-списка. Суммарное время прослушивания не изменится. "] = "\\\"Distinct tracks\\\" and both top lists. Total listening time will not change. ",
        ["Отменить это действие нельзя."] = "This action cannot be undone.",
        ["Сброс прослушиваний"] = "Reset play counts",
        ["Сбросить всю статистику прослушивания?\\n\\nСчётчики прослушиваний по всем трекам и суммарное "] = "Reset all listening statistics?\\n\\nPlay counts for all tracks and the total ",
        ["время обнулятся. Сами файлы и плейлист не затрагиваются. Отменить это действие нельзя."] = "listening time will be reset. The files and playlists themselves are not affected. This action cannot be undone.",
        ["Сброс статистики"] = "Reset statistics",
        ["{tagFile.Properties.AudioBitrate} кбит/с"] = "{tagFile.Properties.AudioBitrate} kbps",
        ["{tagFile.Properties.AudioSampleRate} Гц"] = "{tagFile.Properties.AudioSampleRate} Hz",
        ["Моно"] = "Mono",
        ["Стерео"] = "Stereo",
        ["ещё не проигрывался"] = "Not played yet",
        ["Не удалось прочитать теги — возможно, файл сейчас воспроизводится. "] = "Failed to read tags — the file may be playing right now.",
        ["Остановите воспроизведение этого трека и откройте окно заново, чтобы увидеть текущие значения."] = "Stop playback of this track and reopen the window to see the current values.",
        ["Не удалось прочитать теги: {ex.Message}"] = "Failed to read tags: {ex.Message}",
        ["Не удалось применить найденную обложку: {ex.Message}"] = "Failed to apply found cover art: {ex.Message}",
        ["Выберите обложку"] = "Choose cover",
        ["Изображения (*.jpg;*.jpeg;*.png;*.bmp;*.gif)|*.jpg;*.jpeg;*.png;*.bmp;*.gif|Все файлы (*.*)|*.*"] = "Images (*.jpg;*.jpeg;*.png;*.bmp;*.gif)|*.jpg;*.jpeg;*.png;*.bmp;*.gif|All files (*.*)|*.*",
        ["Не удалось загрузить изображение: {ex.Message}"] = "Failed to load image: {ex.Message}",
        ["Не удалось сохранить: {ex.Message}"] = "Failed to save: {ex.Message}",
        ["Версия {result.LatestVersion} (у вас {result.CurrentVersion})"] = "Version {result.LatestVersion} (you have {result.CurrentVersion})",
        ["Не удалось найти .exe-установщик в этом релизе."] = "Could not find an .exe installer in this release.",
        ["Не удалось скачать установщик: {ex.Message}"] = "Failed to download installer: {ex.Message}",
        ["Скачивание…"] = "Downloading…",
        ["Скачивается {received} из {FormatBytes(info.TotalBytes!.Value)} ({info.Fraction:P0})"] = "Downloading {received} of {FormatBytes(info.TotalBytes!.Value)} ({info.Fraction:P0})",
        ["Скачивается {received}"] = "Downloading {received}",
        [" — {FormatBytes((long)info.BytesPerSecond)}/с"] = " — {FormatBytes((long)info.BytesPerSecond)}/s",
        ["{bytes / mb:F1} МБ"] = "{bytes / mb:F1} MB",
        ["{bytes / kb:F0} КБ"] = "{bytes / kb:F0} KB",
        ["Запуск установщика…"] = "Launching installer…",
        ["Ищем текст…"] = "Searching lyrics…",
        ["Нужен выбор варианта"] = "Selection required",
        ["Поиск временно ограничен"] = "Search temporarily limited",
        ["Пауза"] = "Pause",
        ["Проверьте запрос и нажмите «Искать» или Enter."] = "Check the query and press \"Search\" or Enter.",
        ["Сначала выберите или запустите трек."] = "Select or play a track first.",
        ["Поиск текста"] = "Search lyrics",
        ["Введите название трека или «исполнитель — название»."] = "Enter the track name or \"artist — title\".",
        ["Ищу текст…"] = "Searching for lyrics…",
        ["Во встроенной базе совпадения не найдены. Измените запрос или откройте Genius ниже."] = "No matches found in the built-in database. Change the query or open Genius below.",
        ["Найдено вариантов: {0}. Дважды кликните нужный вариант, чтобы сохранить его рядом с аудиофайлом."] = "Found {0} options. Double-click the one to save it next to the audio file.",
        [" Попробуйте снова через {0} сек."] = " Try again in {0} sec.",
        [" Попробуйте снова немного позже."] = " Try again a little later.",
        ["Сервис временно ограничил запросы."] = "The service has temporarily limited requests.",
        ["Не удалось выполнить поиск. Проверьте подключение к интернету."] = "Search failed. Check your internet connection.",
        ["Сначала скопируйте текст песни в буфер обмена."] = "Copy the song text to the clipboard first.",
        ["Не удалось прочитать буфер обмена. Скопируйте текст ещё раз."] = "Could not read the clipboard. Copy the text again.",
        ["В буфере нет текста песни."] = "No song text in the clipboard.",
        ["Скопированный текст больше 2 МБ и не был сохранён."] = "The copied text is larger than 2 MB and was not saved.",
        ["Не удалось сохранить текст: {ex.Message}"] = "Failed to save text: {ex.Message}",
        ["Введите запрос, чтобы открыть внешний поиск."] = "Enter a query to open external search.",
        ["Не удалось открыть браузер для внешнего поиска."] = "Could not open the browser for external search.",
        ["Сохраняю выбранный текст рядом с аудиофайлом…"] = "Saving the selected lyrics next to the audio file…",
        ["Загрузка текста"] = "Loading lyrics",
        ["Выберите текст песни"] = "Select song lyrics",
        ["Синхронный текст LRC (*.lrc)|*.lrc|Обычный текст (*.txt)|*.txt"] = "Synchronized LRC lyrics (*.lrc)|*.lrc|Plain text (*.txt)|*.txt",
        ["Файл текста больше 2 МБ и не был добавлен."] = "The text file is larger than 2 MB and was not added.",
        ["Не удалось добавить текст песни.\\n\\n{ex.Message}"] = "Failed to add song lyrics.\\n\\n{ex.Message}",
        ["Первоначальная версия плеера на C# + WPF + WPF-UI + NAudio с Fluent-дизайном под Windows 11 (Mica-фон, тёмная тема, скруглённые кнопки)"] = "Initial version of the player in C# + WPF + WPF-UI + NAudio with a Windows 11 Fluent design (Mica background, dark theme, rounded buttons)",
        ["Открытие файлов (mp3/wav/wma/flac) через диалог, с поддержкой множественного выбора"] = "Open files (mp3/wav/wma/flac) via dialog, with multi-select support",
        ["Плейлист с переключением треков по двойному клику"] = "Playlist with track switching on double-click",
        ["Пуск/Пауза/Стоп/Предыдущий/Следующий, перемотка по прогресс-бару, регулировка громкости"] = "Play/Pause/Stop/Previous/Next, seek via progress bar, volume control",
        ["Автопереход к следующему треку"] = "Auto-advance to next track",
        ["Кнопка shuffle"] = "Shuffle button",
        ["Добавление папок в плейлист"] = "Add folders to playlist",
        ["Добавлен мини-плеер и кнопка в плеере для перехода в этот режим"] = "Mini-player added and a button in the player to switch to this mode",
        ["При клике по ползунку громкости или ползунку трека переключение было некорректным"] = "Clicking the volume or track slider caused incorrect behavior",
        ["Добавлено зажатие и перетаскивание по ползунку трека и громкости"] = "Added click-and-drag for track and volume sliders",
        ["Добавлена кнопка повтора"] = "Repeat button added",
        ["Добавлена обложка трека в мини-плеер"] = "Track cover added to mini-player",
        ["Добавлены кнопка настроек и сами Настройки: переключение темы программы на Светлая и Темная; Поверх всех окон; Запоминание громкости между запусками"] = "Added Settings button and Settings: theme switch (Light/Dark); Always on top; remember volume between launches",
        ["Добавлено скрытие плеера в трей при закрытии окна"] = "Added hiding the player to the tray on window close",
        ["В настройки добавлены глобальные горячие клавиши"] = "Added global hotkeys in Settings",
        ["Добавлено Now Playing / SMTC"] = "Added Now Playing / SMTC",
        ["Добавлено автосохранение плейлиста"] = "Added playlist autosave",
        ["Добавлено сохранение последнего трека и его позицию"] = "Added saving the last track and its position",
        ["В настройки добавлены настройки мини-плеера: Прозрачность окна, Поверх всех окон, Закрепить положение"] = "Added mini-player settings in Settings: window opacity, Always on top, lock position",
        ["Добавлено визуальное разделение в настройках"] = "Added visual dividers in Settings",
        ["Мини-плеер теперь имеет фиксированный размер"] = "Mini-player now has a fixed size",
        ["В мини-плеере скрыт заголовок плеера"] = "Mini-player header hidden",
        ["Перетаскивание мини-плеера переделано из-за скрытия заголовка"] = "Mini-player dragging redesigned because the header is hidden",
        ["Уменьшен размер мини-плеера"] = "Reduced mini-player size",
        ["Теперь мини-плеер отображает только обложку и ползунок трека, а при наведении курсором на него снизу появляются основные кнопки"] = "The mini player now shows only the cover and track slider; main buttons appear below when hovering over it",
        ["Теперь при открытии окна настроек можно пользоваться плеером"] = "You can now use the player while the settings window is open",
        ["Клик в любую точку по ползунку трека теперь ставит точку куда нужно и сразу же перематывает аудио туда же"] = "Clicking anywhere on the track slider now sets the position and immediately seeks the audio there",
        ["Исправлена настройка прозрачности мини-плеера"] = "Fixed the mini-player transparency setting",
        ["Добавлено отслеживание и запоминание позиции мини-плеера"] = "Added tracking and saving of the mini-player position",
        ["Добавлено группирование добавленых папок"] = "Added grouping for added folders",
        ["Добавлено включение/выключение папок в плейлисте"] = "Added enable/disable for folders in the playlist",
        ["Добавлено сворачивание/разворачивание папки"] = "Added folder collapse/expand",
        ["Трек не прерывается при удалении папки"] = "Playback is not interrupted when deleting a folder",
        ["Добавлена настройка горячих клавиш под себя"] = "Added customizable hotkey settings",
        ["Замена некоторых иконок на иконки из библиотеки Fluent"] = "Replaced some icons with icons from the Fluent library",
        ["Окно настроек увеличено"] = "The settings window has been enlarged",
        ["Ползунок прозрачности мини-плеера изменен по принципу логики таким же, как и ползунок громкости"] = "Mini-player transparency slider changed to use the same logic as the volume slider",
        ["Попытка исправить отображение некоторых иконок"] = "Attempt to fix the display of some icons",
        ["Добавлены новые горячие клавиши"] = "Added new keyboard shortcuts",
        ["Снова попытка исправить отображение некоторых иконок"] = "Another attempt to fix the display of some icons",
        ["Исправлены комбинации горячих клавиш"] = "Fixed keyboard shortcut combinations",
        ["Исправлен скролл плейлиста"] = "Fixed playlist scrolling",
        ["Добавлена кнопка Развернуть для плеера"] = "Added Expand button to the player",
        ["Все иконки заменены с Fluent на SVG"] = "All icons switched from Fluent to SVG",
        ["Скролл плейлиста переделан с нуля"] = "Playlist scrolling rebuilt from scratch",
        ["Добавлено выключение звука по значку громкости"] = "Added mute toggle via the volume icon",
        ["Добавлено контекстное меню для трека при клике ПКМ по нему"] = "Added context menu for tracks on right-click",
        ["В конец окна настроек добавлена кнопка Патчноуты, а также само окно"] = "Added Patch Notes button and the patch notes window to the end of the settings window",
        ["Добавлена кнопка скрытия всего плейлиста"] = "Added button to hide the entire playlist",
        ["Заменил все иконки на новые"] = "Replaced all icons with new ones",
        ["При переключении трек в состоянии паузы трек теперь не запускается автоматически"] = "Switching tracks while paused no longer starts playback automatically",
        ["Изменено окно настроек, добавлена панель навигации слева: Оформление, Окно, Воспроизведение, Мини-плеер, Горячие клавиши, разделитель, О программе"] = "Settings window redesigned; added left navigation panel: Appearance, Window, Playback, Mini-player, Hotkeys, Separator, About",
        ["Исправлены отображение проблемных иконок"] = "Fixed display of problematic icons",
        ["Исправлена позиция трека при переключении в состоянии паузы"] = "Fixed track position when switching tracks while paused",
        ["Добавлено окно обложки при нажатии на него, а также добавлено приближение обложки"] = "Added cover popup when clicking it, plus cover zoom",
        ["Добавлен поиск в окне настроек"] = "Added search to the settings window",
        ["Список изменений переделан полностью с нуля"] = "Changelog completely rebuilt from scratch",
        ["В список изменений добавлены типы изменений, поиск по типу изменений, сортировка по дате/версии, иконка для каждого типа и цветовая маркировка"] = "Changelog now includes change types, search by change type, sorting by date/version, an icon for each type, and color-coding",
        ["Добавлена иконка для плеера"] = "Added icon for the player",
        ["Добавлено прокрутка громкости колесиком мыши по ползунку громкости"] = "Added volume adjustment by mouse wheel over the volume slider",
        ["Добавлена возможность создать папку в плейлисте, которая будет отображаться только в плеере"] = "Added ability to create a playlist folder that is shown only in the player",
        ["Добавлено разделение пунктов в списке изменений"] = "Added item separators in the changelog list",
        ["Добавлено запоминание режима плеера: обычный или мини-плеер"] = "Added remembering of player mode (normal or mini-player)",
        ["Добавлено запоминание состояние плейлиста: скрыт/виден плейлист, свернута/развернута папка"] = "Added remembering of playlist state: playlist hidden/visible and folder collapsed/expanded",
        ["Дизайн окна Список изменений переделан"] = "Redesigned the Changelog window",
        ["Также в блок настроек О программе добавлена иконка плеера"] = "Added player icon to the About settings section",
        ["Немного изменено окно Список изменений"] = "Slightly updated the Changelog window",
        ["Изменен шаг громкости с 5% на 2%"] = "Changed volume step from 5% to 2%",
        ["Изменена логика подсчета версии списка изменений"] = "Changed logic for calculating the changelog version",
        ["Добавлено отображение картинок в списке изменений"] = "Added display of images in the Changelog",
        ["Добавлен адаптивный полноэкранный режим"] = "Added adaptive fullscreen mode",
        ["Добавлен полноэкранный визуализатор при нажатии на кнопку"] = "Added fullscreen visualizer activated by a button",
        ["Теперь при открытии окна Список изменений окно Настройки закрывается, а при закрытии окна Список изменений снова открывается окно Настройки"] = "Now opening the Changelog closes the Settings window, and closing the Changelog reopens the Settings window",
        ["Добавлено прилипание мини-плеера к краям экрана"] = "Added mini-player snap to screen edges",
        ["Добавлено меню при клике ПКМ по мини-плееру"] = "Added context menu on right-click of mini-player",
        ["Добавлены новые действия в контекстное меню трека: Копировать файл и Свойства"] = "Added new actions to track context menu: Copy File and Properties",
        ["Добавлена иконка плеера в трее"] = "Added player icon to tray",
        ["Скролл списка версий переделан полностью с нуля"] = "Version list scrolling rewritten from scratch",
        ["В Списке изменений теперь вместо Версии используется Количество изменений"] = "Changelog now shows Change Count instead of Version",
        ["Изменена тема с светлой на темную в контекстном меню трея"] = "Changed theme from light to dark in tray context menu",
        ["Исправлено сжатие интерфейса плеера в обычном виде"] = "Fixed player UI compression in normal view",
        ["Исправлен краш плеера при нажатии на кнопку скрытия плейлиста"] = "Fixed player crash when clicking the hide playlist button",
        ["Исправлено некорректное отображение по дате и версии в списке версий"] = "Fixed incorrect display for date and version in the version list",
        ["Удален Полноэкранный визуализатор"] = "Removed fullscreen visualizer",
        ["В контекстное меню трека добавлена кнопка Изменение тегов"] = "Added an \"Edit Tags\" button to the track context menu",
        ["Добавлена подсветка текущего трека, а также перемещение в плейлисте при перелючении треков"] = "Added highlight for the current track and auto-scrolling in the playlist when switching tracks",
        ["Добавлено запоминание шафла и повтора"] = "Added remembering of shuffle and repeat settings",
        ["Добавлены разделители между треками"] = "Added separators between tracks",
        ["Переделано контекстное меню трея"] = "Redesigned tray context menu",
        ["Изменена подсветка трека"] = "Updated track highlighting",
        ["Изменена логика расчета версии"] = "Changed version calculation logic",
        ["Исправлено отображение свойств у трека и добавлено свое окно"] = "Fixed track properties display and added a dedicated window",
        ["Добавлено визуальное разделение треков через Zebra striping"] = "Added visual separation of tracks using zebra striping",
        ["В окно редактирования тегов добавлено добавление и удаление обложки"] = "Added add/remove cover art in the tag editor window",
        ["В окно редактивания тегов добавлена кнопка копирования названия трека"] = "Added a button to copy the track title in the tag editor window",
        ["Добавлено контекстное меню при нажатии на Lumisense в окне плеера"] = "Added context menu when clicking Lumisense in the player window",
        ["Добавлен новый вид плеера"] = "New player view added",
        ["В настройки добавлен выбор вида окна"] = "Window view selection added to Settings",
        ["Добавлен инсталлер"] = "Installer added",
        ["В контекстное меню трека добавлено действие удаления трека с диска"] = "Added 'Delete track from disk' action to track context menu",
        ["[Бета] В коно редактирования тегов добавлена кнопка поиска обложки в интернете"] = "[Beta] Added 'Search cover online' button to tag editor window",
        ["В обычном виде плеера заполнены поля слева и справа от плейлиста"] = "Populated fields left and right of the playlist in the regular player view",
        ["Теперь при открытии обложки окно открывается на весь экран"] = "Now the cover opens in full screen",
        ["Теперь иконки при активном состоянии белого цвета"] = "Icons are now white when active",
        ["Исправлен баг при переключении треков назад при включенном шаффле"] = "Fixed bug when switching to previous tracks with shuffle enabled",
        ["Исправлен баг с автопереключением треков при Повторе всего плейлиста"] = "Fixed bug with auto track switching when 'Repeat entire playlist' is enabled",
        ["Исправлен баг с отображением повторяющихся номеров треков"] = "Fixed bug displaying duplicate track numbers",
        ["Исправлен баг с невлезающими номерами треков"] = "Fixed bug with track numbers not fitting",
        ["Добавлено контекстное меню при клике ПКМ по обложке"] = "Added context menu on right-click of the cover art",
        ["Добавлена логирифмическая регулировка громкости в настройки"] = "Added logarithmic volume control to settings",
        ["Добавлена бегущая строка названия трека в мини-плеере"] = "Added scrolling track title in mini-player",
        ["Добавлен всплывающий индикатор процентов громкости в мини-плеер"] = "Added pop-up volume percentage indicator in mini-player",
        ["Добавлена кнопка Очистить плейлист"] = "Added \"Clear Playlist\" button",
        ["[Бета] Добавлен новый источник для поиска обложек"] = "[Beta] Added a new source for cover searches",
        ["Добавлена кнопка отмены для поиска обложек"] = "Added cancel button for cover search",
        ["Теперь при зажатии горячих кнопок «Следующий», «Предыдущий», «Громкость +» и «Громкость −» действие повторяется автоматически с той же скоростью, что и набор текста на клавиатуре"] = "Now when holding the hotkeys «Next», «Previous», «Volume +» and «Volume −», the action auto-repeats at the same rate as the keyboard repeat",
        ["Теперь при закрытии окна Списка изменений открываются настройки с панелью О программе"] = "Now when closing the Changelog window, Settings opens to the About panel",
        ["Громкость при первом запуске изменена с 70% на 30%"] = "Initial volume on first launch changed from 70% to 30%",
        ["Скорректирован размер окна при открытии обложки"] = "Adjusted window size when opening cover art",
        ["Исправлен ползунок скролла плейлиста"] = "Fixed playlist scroll slider",
        ["Исправлены некоторые ошибки сборки"] = "Fixed some build errors",
        ["Исправлен баг при появлении прямоугольного окна плеера, когда плеер был в окне мини-плеера и был перезапущен"] = "Fixed a bug that caused a rectangular player window to appear when the player was in mini-player mode and restarted",
        ["Исправлен баг с бегущей строкой"] = "Fixed a bug with the scrolling text",
        ["При активном мини-плеере теперь показывается иконка в трее"] = "Now shows an icon in the system tray when the mini-player is active",
        ["Код загружен на GitHub (wasssly/Lumisense)"] = "Source code uploaded to GitHub (wasssly/Lumisense)",
        ["Добавлено в настройки в вкладку О программе кнопка проверки обновления"] = "Added a 'Check for updates' button to the About tab in Settings",
        ["Выпущен первый релиз версии 1.8.0"] = "Released version 1.8.0 (first release)",
        ["Добавлена кнопка проверки папок плейлиста на наличие новых треков в нем"] = "Added a button to check playlist folders for new tracks",
        ["Изменен подход к чейнджлогам в папке программы"] = "Changed how changelogs are handled in the program folder",
        ["В мини-плеере изменен показ процентов громкости при изменении ее через скролл колесом по мини-плееру или через горячие клавиши"] = "Changed how volume percentage is displayed in the mini-player when adjusted with the mouse wheel over the mini-player or with hotkeys",
        ["Оптимизирован запуск плеера"] = "Optimized player startup",
        ["Исправлен баг с Логарифмической громкостью"] = "Fixed a bug with Logarithmic volume",
        ["Добавлена кнопка повтора в окно мини-плеера"] = "Added repeat button to the mini-player window",
        ["В установщик добавлена возможность отключить создание ярлыка на рабочем столе"] = "Added option in the installer to disable creating a desktop shortcut",
        ["Добавлена горячая клавиша удаления трека с диска"] = "Added keyboard shortcut to delete a track from disk",
        ["Добавлена кнопка Избранное в заголовке плейлиста"] = "Added Favorites button in the playlist header",
        ["Добавлено сердечко на каждом треке для добавления трека в Избранное"] = "Added a heart icon on each track to add it to Favorites",
        ["В настройках название панели О программе переименовано в О плеере"] = "In Settings, the 'About' panel was renamed to 'About the player'",
        ["Список изменений теперь привязан к версии, а не к дате - новые записи будут выходить без даты"] = "Changelog is now tied to versions instead of dates; new entries will have no date",
        ["Обновлен README и перемещен в корень репозитория"] = "README updated and moved to the repository root",
        ["Фон мини-плеера теперь подстраивается под цвет текущей обложки, если ее нет, то использует нейтральный цвет темы"] = "Mini-player background now adapts to the current cover's color; if none, it uses the theme's neutral color",
        ["Оптимизирован переход с плейлиста в избранное и обратно"] = "Optimized transition from playlist to Favorites and back",
        ["Оптимизирован клик по сердечку"] = "Optimized clicking the heart icon",
        ["Плеер при запуске всегда открывал один и тот же трек вместо последнего прослушанного, теперь трек и позиция сохраняется чаще"] = "Player previously always opened the same track on startup instead of the last played; now track and position are saved more frequently",
        ["Исправлен баг с файлом .github/workflows/release.yml"] = "Fixed bug with file .github/workflows/release.yml",
        ["Добавлена новая логика шаффла через включение по чекбоксу в настройках"] = "Added new shuffle logic via checkbox in Settings",
        ["В настройки добавлена новая панель Экспериментальное"] = "Added new Experimental panel to Settings",
        ["Добавлено переключение с мини-плеера на обычное окно плеера по клику на ярлык, когда мини-плеер активный"] = "Added switching from mini-player to regular player window by clicking the icon when mini-player is active",
        ["Под кнопкой Проверить обновления добавлен новый блок Источник загрузки обновлений"] = "Added new 'Update download source' block under the Check for updates button",
        ["Добавлены скругленные углы у плейлиста при прокрутке"] = "Added rounded corners to the playlist when scrolling",
        ["Запуск плеера через исходники теперь показывает в консоли вывод при разных случаях"] = "Launching the player from source now shows console output in various cases",
        ["Добавлен полноценный 10-полосный эквалайзер"] = "Added a full 10-band equalizer",
        ["Добавлен поиск по плейлисту"] = "Added playlist search",
        ["Добавлен слайдер в контекстное меню мини-плеера при клике ПКМ по нему"] = "Added a slider to the mini-player context menu when right-clicking it",
        ["Добавлена возможность менять позицию ползунка на слайдере эквалайзера при помощи скролла колесиком мыши"] = "Added ability to change the equalizer slider position using the mouse wheel scroll",
        ["Добавлена новая экспериментальная настройка «Скрыть фон у кнопок управления воспроизведением»"] = "Added new experimental setting \"Hide background of playback control buttons\"",
        ["В деинсталлятор добавлена возможность удалить файлы настроек и пользовательские данные"] = "Added option in the uninstaller to remove configuration files and user data",
        ["Во вкладку О плеере добавлена информация о разработчике"] = "Added developer information to the About player tab",
        ["Добавлена возможность включить автозапуск плеера при запуске системы, а также запуск в свёрнутом виде"] = "Added option to enable player autostart on system startup and to start minimized",
        ["Изменен параметр прозрачности мини-плеера"] = "Changed mini-player transparency setting",
        ["Квадратный вид плера по умолчанию"] = "Square player view set as default",
        ["Изменен дизайн контекстного меню трея"] = "Changed tray context-menu design",
        ["Переработан фон мини-плеера"] = "Redesigned mini-player background",
        ["Исправлен некоторый текст и иконки"] = "Fixed some text and icons",
        ["Изменен фокус плеера за окном настроек"] = "Changed player focus when behind the settings window",
        ["Индикатор процентов громкости на мини-плеере перемещен в более центральное положение"] = "Volume percentage indicator on the mini-player moved to a more central position",
        ["Углы у изображений в списке изменений теперь скруглены"] = "Corners of images in the changelog are now rounded",
        ["Изменено описание плеера на вкладке О плеере"] = "Updated player description on the About player tab",
        ["Полностью переделана панель плейлиста на один настоящий виртуализирующий ListView с плоским списком и включённой VirtualizingPanel"] = "Playlist panel rewritten as a single real virtualizing ListView with a flat list and VirtualizingPanel enabled",
        ["Упращено контекстное меню в плейлисте"] = "Playlist context menu simplified",
        ["Переписана подсветка текущего трека на ScrollIntoView"] = "Current track highlighting rewritten to use ScrollIntoView",
        ["Переделан порядок расположения элементов во вкладке О плеере"] = "Order of items in the About tab rearranged",
        ["Некоторые элементы во вкладке О плеере переделаны под аккордеон"] = "Some elements in the About tab converted to accordion sections",
        ["Исправлена баг с мини-плеером - скрывался за остальными окнами при активной настройке «Поверх окон» через долгое время"] = "Fixed bug where the mini-player would hide behind other windows over time when 'Always on top' was enabled",
        ["Исправено зависание после ускоренного запуска плеера"] = "Fixed freezing after fast player startup",
        ["Исправлено налезание процентов громкости на прогресс-бар трека"] = "Fixed volume percentage overlapping the track progress bar",
        ["Исправлен краш при запуске через исходники"] = "Fixed crash when launching from source",
        ["Попытка исправить зависание плеера при запуске"] = "Attempt to fix player hang on startup",
        ["Исправлен баг в окне поиска обложки"] = "Fixed bug in the cover art search window",
        ["Исправлены ошибки имен"] = "Fixed naming errors",
        ["Исправлено отображение слайдеров в эквалайзере"] = "Fixed equalizer slider display",
        ["Исправлена проблема с дерганием слайдера эквалайзера влево и вправо"] = "Fixed equalizer slider jumping left and right",
        ["Исправлена проблема с автоматическим повторным обходом папок на диске при запуске плеера"] = "Fixed automatic repeated scanning of disk folders at player startup",
        ["Исправлен баг папок после виртуализирующего ListView"] = "Fixed folder bug caused by virtualizing ListView",
        ["Убран тихий авто-пересканинг папок"] = "Removed silent auto-rescan of folders",
        ["Внешний ScrollViewer убран полностью"] = "External ScrollViewer removed completely",
        ["Удалены лишние элементы старой реализации мини-плеера"] = "Removed redundant elements from old mini-player implementation",
        ["Исключены устаревшие реализации, заменённые новой архитектурой"] = "Removed deprecated implementations replaced by the new architecture",
        ["В контекстное меню мини-плеера добавлена кнопка Настройки"] = "Added Settings button to mini-player context menu",
        ["Добавлена перемотка трека колесиком мыши по прогресс-бару с шагом в 5 секунд"] = "Added track seek by mouse wheel on the progress bar in 5-second steps",
        ["Добавлено сохранение и импорт/экспорт пресетов для эквалайзера"] = "Added saving and import/export of equalizer presets",
        ["Добавлен счетчик прослушиваний"] = "Added play count",
        ["Добавлена настройка отображения кнопки в мини-плеере: Повтор или Перемешать"] = "Added setting to display button in mini-player: Repeat or Shuffle",
        ["Добавлен новый вид кнопок управления мини-плеера"] = "Added a new style of mini-player control buttons",
        ["Добавлена кнопка Развернуть для окна Настроек"] = "Added an Expand button for the Settings window",
        ["Добавлены новые горячие клавиши: Перемотка вперед/назад на 5 секунд "] = "Added new keyboard shortcuts: seek forward/backward by 5 seconds",
        ["Добавлена кнопка Статистика в основном окне плеера с отдельным окном"] = "Added a Statistics button in the main player window (opens in a separate window)",
        ["В контекстное меню трека добавлено Копировать имя трека"] = "Added Copy Track Name to the track context menu",
        ["Добавлена настройка изменения содержимого мини-плеера"] = "Added setting to change mini-player content",
        ["Добавлено всплывающее уведомление о смене трека и настройки для него"] = "Added a popup notification for track changes and associated settings",
        ["Добавлена карточка выбора темы"] = "Added a theme selection card",
        ["Добавлена настройка акцентного цвета"] = "Added accent color setting",
        ["Добавлен выбор основы окна"] = "Added window base selection",
        ["Добавлено множественное выделение треков в плейлисте (Ctrl+клик, Shift+клик, Ctrl+A) и удаление выделенного клавишей Delete - с отменой по Ctrl+Z"] = "Added multiple selection of tracks in the playlist (Ctrl+click, Shift+click, Ctrl+A) and deletion of selected items with the Delete key — undo via Ctrl+Z",
        ["Добавлен Экспорт/импорт профиля в один .lumi-файл (страница «Экспериментальное»)"] = "Added profile Export/Import to a single .lumi file (Experimental page)",
        ["Теперь при клике на ярлык при активном окне плеера сворачивает в мини-плеер и повторном клике обратно"] = "Now clicking the icon while the player window is active collapses it into the mini-player, and clicking again restores it",
        ["Улучшенный шаффл перенесен с вкладки Экспериментальное во вкладку Воспроизведение и переименован в Шаффл без повторов"] = "Improved shuffle moved from the Experimental tab to the Playback tab and renamed to Shuffle without repeats",
        ["Теперь для выделенных треков цвет иконки сердечка фиксированный темный, а для невыделенных как раньше"] = "Now the heart icon color for selected tracks is a fixed dark; unselected tracks remain unchanged",
        ["Мелкие изменения"] = "Minor changes",
        ["Исправлен баг, который при смене версии списка изменений оставлял прошлую позицию прокрутки"] = "Fixed a bug that preserved the previous scroll position when switching changelog versions",
        ["Картинки в окне Списка изменений теперь открываются"] = "Images in the Changelog window now open",
        ["Исправлена проблема при щелчке в начале треков, при их переключении и активном эквалайзере (возможно)"] = "Fixed an issue when clicking at the start of tracks during switching with the equalizer active (possibly)",
        ["Исправлена задержка и подвисание с переключением треков через горячие клавиши"] = "Fixed delay and stuttering when switching tracks via hotkeys",
        ["Исправлена остановка воспроизведения после удаления трека с диска"] = "Fixed playback stopping when deleting a track from disk",
        ["Исправлена проблема с сохранением обложки проигрываемого трека"] = "Fixed an issue with saving the cover art of the currently playing track",
        ["Исправлен баг, который при вкладки в Настройках оставлял прошлую позицию прокрутки"] = "Fixed a bug that left the previous scroll position when switching tabs in Settings",
        ["Мелкие исправления"] = "Minor fixes",
        ["Убрана избыточная документация и словесный мусор по всему проекту с сохранением смысловых оборотов только по делу и правкой мелочей"] = "Removed redundant documentation and verbal clutter across the project, preserving only meaningful phrasing and fixing minor issues",
        ["Убраны подсказки (тултипы) при наведении на кнопки в мини-плеере"] = "Removed hover tooltips from buttons in the mini-player",
        ["Дополнен README.md"] = "Updated README.md",
        ["Добавлена плавная анимация обложки при переключении треков"] = "Added smooth cover animation when switching tracks",
        ["Добавлены новые вкладки в настройки"] = "Added new tabs in Settings",
        ["Добавлена система обновления на основе ZIP-архивов"] = "Added ZIP-based update system",
        ["В настройки мини-плеера для Второй кнопки добавлена кнопка Избранного (сердечко)"] = "Added Favorites (heart) action for the mini-player's Second button",
        ["Добавлена настройка ширины уведомления трека"] = "Added setting for track notification width",
        ["Добавлена настройка включения/отключения прилипания к краям экрана для мини-плеера"] = "Added setting to enable/disable snapping to screen edges for the mini-player",
        ["Добавлен ярлык настроек в панели задач со своей иконкой"] = "Added Settings shortcut to the taskbar with its own icon",
        ["Добавлена кнопка Сбросить плеер к исходному состоянию"] = "Added button to reset the player to its default state",
        ["В настройки добавлен аккордеон с списком всех версий плеера и возможностью установить/откатить/переустановить версию"] = "Settings now includes an accordion listing all player versions with options to install, rollback, or reinstall a version",
        ["Добавлена новый вид полосы воспроизведения: Waveform"] = "Added a new playback bar type: Waveform",
        ["В настройки добавлена отдельная вкладка Обновления и перенесены соответствующие элементы с О плеере туда"] = "Settings now includes a separate Updates tab, and the related items from About have been moved there",
        ["Добавлен выбор ZIP / .exe для установки"] = "Added option to choose ZIP or .exe for installation",
        ["В мини-плеер добавлена возможность скрыть прогресс-бар трека"] = "Added option in mini-player to hide the track progress bar",
        ["Добавлен ReplayGain"] = "Added ReplayGain",
        ["Добавлено закрепление треков в Избранном, а также соответствующее действие в контекстное меню трека в Избранном"] = "Added ability to pin tracks in Favorites, plus the corresponding action in a track's context menu in Favorites",
        ["Добавлен Drag & Drop"] = "Added Drag & Drop support",
        ["Добавлено логирование и кнопка для открытия папки с логами"] = "Added logging and a button to open the logs folder",
        ["Добавлена подробная информация при скачивании обновления"] = "Added detailed information during update download",
        ["При скачивании обновления вместо Скачать и установить появляется кнопка Отмена"] = "During update download, the 'Download and Install' button is replaced by a 'Cancel' button",
        ["Иконка плеера обновлена"] = "Player icon updated",
        ["Окно настроек увеличено по ширине и высоте"] = "Settings window enlarged (width and height)",
        ["Реорганизация настроек"] = "Settings reorganized",
        ["Редизайн карточки разработчика"] = "Developer card redesigned",
        ["Ширина для всех трёх кнопок в всплывающем окне обновления сделана под одинаковую ширину через равные колонки Grid, также немного расширено само окно"] = "All three buttons in the update popup now share equal widths via Grid columns; the popup has also been slightly widened",
        ["Исправлена проблема диалогового окна удаления трека, когда при нажатии горячих клавиш окно появлялось позади всех окон"] = "Fixed issue where the track deletion dialog would appear behind all windows when using hotkeys",
        ["Исправлена проблема перетаскивания мини-плеера при расположении кнопок управления на месте обложки и названия"] = "Fixed mini-player drag issue when control buttons are positioned over the cover and title",
        ["Практически исправлен треск в начале трека при переключении"] = "Mostly fixed crackling at track start when switching",
        ["Вероятно, исправлена проблема в механизме обработки быстрых нажатий глобального хоткея переключения треков"] = "Likely fixed an issue in the mechanism handling rapid presses of the global track-switch hotkey",
        ["Исправлено отображение индикатора процентов громкости при активной настройке скрытия полосы прогресса для мини-плеера"] = "Fixed volume percentage indicator display when the setting to hide the progress bar for the mini-player is active",
        ["В настройках убрана вкладка Экспериментальное"] = "Removed Experimental tab from settings",
        ["Сокращены избыточные комментарии кода"] = "Reduced redundant code comments",
        ["Усилена безопасность обновлений, импорта профилей и загрузки обложек: добавлены проверки ссылок, цифровой подписи, размеров файлов и импортируемых данных"] = "Security for updates, profile import, and cover uploads has been strengthened: added checks for URLs, digital signatures, file sizes, and imported data",
        ["Добавлены ограничения размера скачиваемых установщиков, JSON-профилей, EQ-пресетов и сетевых изображений"] = "Added size limits for downloaded installers, JSON profiles, EQ presets, and network images",
        ["Добавлена валидация импортируемых профилей и пресетов эквалайзера"] = "Added validation for imported profiles and EQ presets",
        ["Усилена проверка ссылок на страницы релизов перед открытием во внешнем браузере"] = "Enhanced verification of release page links before opening them in an external browser",
        ["Добавлена дополнительная проверка издателя цифровой подписи установщика обновлений"] = "Added additional verification of the digital-signature publisher for the update installer",
        ["Повышена надёжность фоновых операций, сохранения настроек, работы в трее и завершения приложения"] = "Improved reliability of background operations, settings saving, tray behavior, and app shutdown",
        ["В окне Список изменений при клике на тип изменения теперь перекидывает на список выбранного типа"] = "In the Changelog window, clicking a change type now navigates to the list for that type",
        ["Добавлена кнопка прокрутки вверх в окно Список изменений"] = "Added a scroll-to-top button in the Changelog window",
        ["Добавлена небольшая анимация списка изменений"] = "Added subtle animation to the changelog list",
        ["Добавлена небольшая анимация при переключении разделов в настройках"] = "Added subtle animation when switching settings sections",
        ["Расширена диагностика ошибок воспроизведения"] = "Expanded diagnostics for playback errors",
        ["Добавлена кнопка для перехода в репозиторий"] = "Added a button to open the repository",
        ["Добавлена настройка оформления от текущей обложки: акцентный цвет и цвет основы окна настраиваются независимо"] = "Added styling option based on the current cover: accent color and window base color can be configured independently",
        ["Добавлены независимые настройки скорости воспроизведения с сохранением высоты тона и сдвига тона в полутонах"] = "Added independent playback speed settings with pitch preservation and pitch shift in semitones",
        ["Добавлены расширенные варианты сброса данных плеера"] = "Added advanced player data reset options",
        ["Улучшено восстановление воспроизведения и освобождение аудиоресурсов при ошибках открытия или запуска трека"] = "Improved playback recovery and audio resource release on track open or start errors",
        ["Обновлён экспорт/импорт профилей .lumi"] = "Updated .lumi profile export/import",
        ["Улучшены отзывчивость интерфейса, переключение треков и устойчивость фоновых операций"] = "Improved UI responsiveness, track switching, and background operation reliability",
        ["Редизайн переключателей, чекбоксов и ползунков"] = "Redesigned switches, checkboxes, and sliders",
        ["Прочие мелкие изменения и улучшения"] = "Other minor changes and improvements",
        ["Добавление и повторное сканирование папок выполняются в фоне, поддерживают отмену и не блокируют интерфейс"] = "Adding and rescanning folders run in the background, support cancellation, and do not block the UI",
        ["Исправлен показ диалога обновления после закрытия приложения"] = "Fixed update dialog appearing after the app was closed",
        ["Исправлена очистка аудиоресурсов при ошибке открытия файла"] = "Fixed audio resource cleanup on file open error",
        ["Исправлено восстановление интерфейса и текущего трека после ошибок загрузки или удаления аудиофайла"] = "Fixed restoration of the UI and current track after audio file load or delete errors",
        ["Уменьшен треск при переключении треков"] = "Reduced popping/crackle when switching tracks",
        ["Исправлено отображение индикатора громкости в мини-плеере при изменении колёсиком мыши"] = "Fixed volume indicator in mini player when adjusted with mouse wheel",
        ["Исправлено применение акцентного цвета к CheckBox и RadioButton в настройках"] = "Fixed accent color being applied to CheckBox and RadioButton in Settings",
        ["Прочие мелкие исправления и улучшения"] = "Other minor fixes and improvements",
        ["Убрана установка обновлений из ZIP-архивов; обновления снова устанавливаются через .exe-установщик"] = "Removed installation of updates from ZIP archives; updates are now installed via the .exe installer",
        ["Убрана экспериментальная настройка скрытия фона у кнопок управления воспроизведением"] = "Removed experimental option to hide the background of playback control buttons",
        ["\n\nОшибок: {0}. {1}"] = "\n\nErrors: {0}. {1}",
        ["Что-то пошло не так, но плеер попробует продолжить работу.\n\nПодробности сохранены в лог-файл, его можно найти в настройках (страница \"Обновления\") или в папке %AppData%\\Lumisense\\logs.\n\n{args.Exception.Message}"] = "Something went wrong, but the player will try to continue.\n\nDetails have been saved to the log file; you can find it in Settings (the \"Updates\" page) or in the %AppData%\\Lumisense\\logs folder.\n\n{args.Exception.Message}",
        ["Lumisense не удалось запуститься.\n\nПодробности сохранены в лог-файл (%AppData%\\Lumisense\\logs).\n\n{ex.Message}"] = "Lumisense failed to start.\n\nDetails have been saved to the log file (%AppData%\\Lumisense\\logs).\n\n{ex.Message}",
        ["Не удалось загрузить обложку:\n{ex.Message}"] = "Failed to load cover:\n{ex.Message}",
        ["Ошибка в фоновой операции \"{operationName}\""] = "Error in background operation \"{operationName}\"",
        ["Не удалось сохранить изображение:\n{ex.Message}"] = "Failed to save image:\n{ex.Message}",
        ["Не удалось скопировать изображение:\n{ex.Message}"] = "Failed to copy image:\n{ex.Message}",
        ["\n\nПропущено: {skipped} (уже соответствует шаблону, конфликтует или играет сейчас)."] = "\n\nSkipped: {skipped} (already matches the template, conflicts, or is currently playing).",
        ["Переименовать файлов: {candidates.Count}.\n\n{examples}{skippedText}\n\n"] = "Files to rename: {candidates.Count}.\n\n{examples}{skippedText}\n\n",
        ["Очистить весь плейлист?\n\nВсе папки и файлы будут убраны из списка (сами файлы на диске не затрагиваются)."] = "Clear the entire playlist?\n\nAll folders and files will be removed from the list (the files on disk will not be affected).",
        ["\n\nОшибок: {result.Errors.Count}. {string.Join("] = "\n\nErrors: {result.Errors.Count}. {string.Join(\"]}",
        ["Не удалось нормализовать имя файла:\n{ex.Message}"] = "Failed to normalize file name:\n{ex.Message}",
        ["Удалить файл «{trackName}» с диска?\n\nФайл будет перемещён в корзину, а трек — убран из всех плейлистов."] = "Delete file \"{trackName}\" from disk?\n\nThe file will be moved to the Recycle Bin and the track removed from all playlists.",
        ["Не удалось удалить файл:\n{filePath}\n\n{ex.Message}"] = "Failed to delete file:\n{filePath}\n\n{ex.Message}",
        ["Не удалось открыть файл:\n{filePath}\n\n{ex.Message}"] = "Failed to open file:\n{filePath}\n\n{ex.Message}",
        ["Не удалось запустить воспроизведение — возможно, устройство вывода звука недоступно.\n\n{ex.Message}"] = "Failed to start playback — the audio output device may be unavailable.\n\n{ex.Message}",
        ["Удалить локально сохранённые интернет-обложки?\n\nПри следующем поиске нужные изображения будут скачаны заново."] = "Delete locally saved online covers?\n\nThey will be downloaded again during the next search.",
        ["Не удалось открыть журнал Discord:\n{ex.Message}"] = "Failed to open the Discord log:\n{ex.Message}",
        ["Удалить пресет \"{preset.Name}\"?"] = "Delete preset \"{preset.Name}\"?",
        ["Не удалось сохранить пресет:\n{ex.Message}"] = "Failed to save preset:\n{ex.Message}",
        ["Не удалось сохранить файл:\n{ex.Message}"] = "Failed to save file:\n{ex.Message}",
        ["Настройки импортированы.\n\nЧасть из них (хоткеи, эквалайзер, поведение трея и мини-плеера) применится полностью после перезапуска плеера."] = "Settings imported.\n\nSome of them (hotkeys, equalizer, tray and mini-player behavior) will take full effect after restarting the player.",
        ["Сбросить тему, акцент, подложку окна, вид и размер плеера, громкость, шафл/повтор, горячие клавиши, мини-плеер и остальные настройки к значениям по умолчанию?\n\n"] = "Reset theme, accent, window backdrop, player appearance and size, volume, shuffle/repeat, hotkeys, mini-player and other settings to their defaults?\n\n",
        ["Плеер сброшен к исходным настройкам.\n\nЧасть из них (хоткеи, эквалайзер, поведение трея и мини-плеера, размер и положение окна) применится полностью после перезапуска плеера."] = "The player has been reset to default settings.\n\nSome settings (hotkeys, equalizer, tray and mini-player behavior, window size and position) will fully take effect after restarting the player.",
        ["Будут удалены настройки, сохранённые плейлисты, избранное, история прослушиваний, статистика и пресеты эквалайзера.\n\n"] = "Settings, saved playlists, favorites, listening history, statistics, and equalizer presets will be deleted.\n\n",
        ["Сбросить счётчики прослушиваний по всем трекам?\n\nЭто обнулит \"Прослушано треков\", "] = "Reset play counters for all tracks?\n\nThis will reset \"Tracks played\", ",
        ["\"Разных треков\" и оба топ-списка. Суммарное время прослушивания не изменится. "] = "\"Distinct tracks\" and both top lists. Total listening time will not change. ",
        ["Сбросить всю статистику прослушивания?\n\nСчётчики прослушиваний по всем трекам и суммарное "] = "Reset all listening statistics?\n\nPlay counts for all tracks and the total ",
        ["Не удалось добавить текст песни.\n\n{ex.Message}"] = "Failed to add song lyrics.\n\n{ex.Message}",
        ["Ничего не играет"] = "Nothing is playing",
        ["Открыть Lumisense"] = "Open Lumisense",
        ["Продолжить"] = "Resume",
        ["Выход"] = "Exit",
        ["Нет описания"] = "No description",
        ["Первый релиз"] = "First release",
        ["Плейлист по папкам и отдельным файлам — каждую группу можно включать и выключать"] = "Playlist organized by folders and individual files — each group can be enabled or disabled",
        ["Воспроизведение, пауза, стоп, переключение треков, перемешивание и повтор"] = "Playback, pause, stop, track switching, shuffle, and repeat",
        ["Перемотка и регулировка громкости мышью по всей полосе, а не только по бегунку"] = "Seek and adjust volume with the mouse anywhere on the bar, not only on the thumb",
        ["Мини-плеер с обложкой, прогрессом и управлением поверх других окон"] = "Mini player with cover art, progress, and controls above other windows",
        ["Глобальные горячие клавиши, которые работают из любого окна, даже когда плеер свёрнут"] = "Global hotkeys that work from any window, even when the player is minimized",
        ["Интеграция с «Сейчас воспроизводится» в Windows 11 и сворачивание в трей"] = "Windows 11 Now Playing integration and tray minimization",
        ["Светлая и тёмная тема, гибкая настройка окна и мини-плеера"] = "Light and dark themes, with flexible customization of the player window and mini player",
        ["Воспроизводится"] = "Playing",
        ["На паузе"] = "Paused",
        ["Скачать Lumisense"] = "Download Lumisense",
        ["Неизвестная композиция"] = "Unknown track",
        ["Синхронный LRC"] = "Synced LRC",
        ["Текстовый файл"] = "Text file",
        ["Текст из тега"] = "Text from tag",
        ["Кэш вставленного текста"] = "Pasted lyrics cache",
        ["LRCLIB · синхронный текст"] = "LRCLIB · synced lyrics",
        ["LRCLIB · текст"] = "LRCLIB · lyrics",
        ["Сбросить тему, акцент, подложку окна, вид и размер плеера, громкость, шафл/повтор, горячие клавиши, мини-плеер и остальные настройки к значениям по умолчанию?\n\nПлейлист, избранное, история прослушиваний, статистика и сохранённые пресеты эквалайзера затронуты не будут."] = "Reset the theme, accent, window backdrop, player appearance and size, volume, shuffle/repeat, hotkeys, mini-player, and other settings to their defaults?\n\nThe playlist, favorites, listening history, statistics, and saved equalizer presets will not be affected.",
        ["Будут удалены настройки, сохранённые плейлисты, избранное, история прослушиваний, статистика и пресеты эквалайзера.\n\nАудиофайлы на диске не удаляются. Продолжить?"] = "Settings, saved playlists, favorites, listening history, statistics, and equalizer presets will be deleted.\n\nAudio files on disk will not be deleted. Continue?",
        ["Сбросить счётчики прослушиваний по всем трекам?\n\nЭто обнулит \"Прослушано треков\", \"Разных треков\" и оба топ-списка. Суммарное время прослушивания не изменится. Отменить это действие нельзя."] = "Reset play counts for all tracks?\n\nThis will reset \"Tracks played\", \"Distinct tracks\", and both top lists. Total listening time will not change. This action cannot be undone.",
        ["Сбросить всю статистику прослушивания?\n\nСчётчики прослушиваний по всем трекам и суммарное время обнулятся. Сами файлы и плейлист не затрагиваются. Отменить это действие нельзя."] = "Reset all listening statistics?\n\nPlay counts for all tracks and total listening time will be reset. The files and playlists themselves are not affected. This action cannot be undone.",
    };

    // Несколько разных русских формулировок могут корректно переводиться одной английской
    // фразой. Для обратного переключения берём первую из таких исходных формулировок вместо
    // использования ToDictionary напрямую, которое выбросило бы исключение на дубликате.
    private static readonly Dictionary<string, string> RussianByEnglish = EnglishByRussian
        .GroupBy(pair => pair.Value, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.Ordinal);

    // Статические словарные записи покрывают XAML напрямую. Этот набор дополнительно
    // обслуживает программно собранные строки с подстановками (версии, пути, ошибки),
    // чтобы их не приходилось локализовать вручную в каждом обработчике.
    private static readonly IReadOnlyList<TemplateTranslation> EnglishTemplates = BuildTemplateTranslations(EnglishByRussian);
    private static readonly IReadOnlyList<TemplateTranslation> RussianTemplates = BuildTemplateTranslations(RussianByEnglish);

    public static string CurrentLanguage { get; private set; } = Russian;
    public static bool IsEnglish => CurrentLanguage == English;
    public static event EventHandler? LanguageChanged;

    public static void Initialize(AppSettings settings, bool isFirstLaunch)
    {
        if (isFirstLaunch && TryReadInstallerLanguage(out var installerLanguage))
        {
            settings.Language = installerLanguage;
            SettingsManager.Save(settings);
        }

        CurrentLanguage = NormalizeLanguage(settings.Language);
        settings.Language = CurrentLanguage;
        ApplyCulture();
    }

    public static void ChangeLanguage(AppSettings settings, string language)
    {
        CurrentLanguage = NormalizeLanguage(language);
        settings.Language = CurrentLanguage;
        ApplyCulture();
        LanguageChanged?.Invoke(null, EventArgs.Empty);

        foreach (Window window in Application.Current.Windows)
            Apply(window);

        _ = SettingsManager.SaveAsync(settings);
    }

    public static string Translate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;

        var directDictionary = IsEnglish ? EnglishByRussian : RussianByEnglish;
        if (directDictionary.TryGetValue(value, out var directTranslation))
            return directTranslation;

        return TranslateTemplate(value, IsEnglish ? EnglishTemplates : RussianTemplates);
    }

    private static string TranslateTemplate(string value, IReadOnlyList<TemplateTranslation> templates)
    {
        foreach (var template in templates)
        {
            var match = template.Pattern.Match(value);
            if (!match.Success) continue;

            string translated = template.Translation;
            for (int index = 0; index < template.Placeholders.Count; index++)
                translated = translated.Replace(template.Placeholders[index], match.Groups[index + 1].Value,
                    StringComparison.Ordinal);

            return translated;
        }

        return value;
    }

    private static IReadOnlyList<TemplateTranslation> BuildTemplateTranslations(IReadOnlyDictionary<string, string> dictionary)
    {
        var templates = new List<TemplateTranslation>();
        foreach (var pair in dictionary)
        {
            var placeholderMatches = Regex.Matches(pair.Key, @"\{[^{}]+\}");
            if (placeholderMatches.Count == 0 ||
                pair.Key.Count(character => character == '{') != placeholderMatches.Count ||
                pair.Key.Count(character => character == '}') != placeholderMatches.Count)
                continue;

            var placeholders = placeholderMatches
                .Select(match => match.Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            string pattern = Regex.Escape(pair.Key);
            foreach (string placeholder in placeholders)
                pattern = pattern.Replace(Regex.Escape(placeholder), "(.+?)", StringComparison.Ordinal);

            templates.Add(new TemplateTranslation(new Regex($"^{pattern}$", RegexOptions.CultureInvariant),
                placeholders, pair.Value));
        }

        // Точные (более длинные) шаблоны проверяются первыми, чтобы общие сообщения
        // вроде «Ошибка: {0}» не перехватывали специализированные варианты.
        return templates.OrderByDescending(template => template.Pattern.ToString().Length).ToList();
    }

    private sealed record TemplateTranslation(Regex Pattern, IReadOnlyList<string> Placeholders, string Translation);

    public static string Format(string russianTemplate, params object[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Translate(russianTemplate), arguments);

    public static void Apply(object? root)
    {
        if (root is not DependencyObject dependencyObject) return;
        ApplyRecursive(dependencyObject, new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance));
    }

    private static void ApplyRecursive(DependencyObject element, HashSet<DependencyObject> visited)
    {
        // WPF обычно содержит одни и те же элементы и в visual, и в logical tree. Без
        // множества посещённых объектов такой обход повторялся экспоненциально на длинных
        // списках, особенно в Changelog, что и давало заметную паузу при открытии окна.
        if (!visited.Add(element)) return;

        ApplyElement(element);

        int visualChildren = element is Visual ? VisualTreeHelper.GetChildrenCount(element) : 0;
        for (int index = 0; index < visualChildren; index++)
            ApplyRecursive(VisualTreeHelper.GetChild(element, index), visited);

        if (element is FrameworkElement frameworkElement)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(frameworkElement))
            {
                if (child is DependencyObject dependencyChild)
                    ApplyRecursive(dependencyChild, visited);
            }

            if (frameworkElement.ContextMenu is not null)
                ApplyRecursive(frameworkElement.ContextMenu, visited);
        }

        if (element is Popup { Child: DependencyObject popupChild })
            ApplyRecursive(popupChild, visited);
    }

    private static void ApplyElement(DependencyObject element)
    {
        if (element is Window window && !string.IsNullOrWhiteSpace(window.Title))
            window.Title = Translate(window.Title);

        // У TextBlock с Inlines (Run/Span) присваивание свойству Text очищает всю коллекцию
        // Inlines, включая Run с Binding. Это удаляло номера версий в англоязычном Changelog.
        // Такие дочерние Run обрабатываются отдельно ниже при обходе logical tree.
        if (element is TextBlock textBlock)
        {
            if (textBlock.Inlines.Count == 0 && !BindingOperations.IsDataBound(textBlock, TextBlock.TextProperty))
                textBlock.Text = Translate(textBlock.Text);
            else
                TranslateInlines(textBlock.Inlines);
        }

        if (element is Run run && !BindingOperations.IsDataBound(run, Run.TextProperty))
            run.Text = Translate(run.Text);

        if (element is TextBox textBox && !BindingOperations.IsDataBound(textBox, TextBox.TextProperty))
            textBox.Text = Translate(textBox.Text);

        if (element is ContentControl contentControl && !BindingOperations.IsDataBound(contentControl, ContentControl.ContentProperty)
            && contentControl.Content is string content)
            contentControl.Content = Translate(content);

        if (element is HeaderedContentControl headered && !BindingOperations.IsDataBound(headered, HeaderedContentControl.HeaderProperty)
            && headered.Header is string header)
            headered.Header = Translate(header);

        // MenuItem наследует HeaderedItemsControl, а не HeaderedContentControl. Из-за этого
        // его Header раньше обходил локализацию, хотя Content обычных кнопок переводился.
        if (element is HeaderedItemsControl headeredItems &&
            !BindingOperations.IsDataBound(headeredItems, HeaderedItemsControl.HeaderProperty) &&
            headeredItems.Header is string itemsHeader)
            headeredItems.Header = Translate(itemsHeader);

        if (element is FrameworkElement frameworkElement && frameworkElement.ToolTip is string toolTip)
            frameworkElement.ToolTip = Translate(toolTip);

        // WPF-UI использует собственные строковые DependencyProperty (например, Title у
        // TitleBar и PlaceholderText у TextBox). Через reflection обрабатываем их без
        // жёсткой привязки к конкретной версии библиотеки и без изменения XAML-разметки.
        TranslateStringProperty(element, "Title");
        TranslateStringProperty(element, "PlaceholderText");
    }

    private static void TranslateInlines(InlineCollection inlines)
    {
        // Изменение Text у Run повышает версию InlineCollection. Поэтому нельзя менять Run
        // внутри прямого foreach: WPF прерывает перечисление с InvalidOperationException.
        // Снимок сохраняет состав коллекции на момент обхода и безопасен для вложенных Span.
        foreach (Inline inline in inlines.ToArray())
        {
            if (inline is Run inlineRun && !BindingOperations.IsDataBound(inlineRun, Run.TextProperty))
                inlineRun.Text = Translate(inlineRun.Text);
            else if (inline is Span span)
                TranslateInlines(span.Inlines);
        }
    }

    private static void TranslateStringProperty(object element, string propertyName)
    {
        var property = element.GetType().GetProperty(propertyName,
            BindingFlags.Instance | BindingFlags.Public);
        if (property is null || !property.CanRead || !property.CanWrite ||
            property.PropertyType != typeof(string) || property.GetIndexParameters().Length != 0)
            return;

        if (property.GetValue(element) is string value)
            property.SetValue(element, Translate(value));
    }

    private static void ApplyCulture()
    {
        var culture = CultureInfo.GetCultureInfo(IsEnglish ? "en-US" : "ru-RU");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    private static string NormalizeLanguage(string? language) =>
        string.Equals(language, English, StringComparison.OrdinalIgnoreCase) ? English : Russian;

    private static bool TryReadInstallerLanguage(out string language)
    {
        language = Russian;
        try
        {
            string markerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Lumisense", "installer-language.txt");
            if (!File.Exists(markerPath)) return false;

            language = NormalizeLanguage(File.ReadAllText(markerPath).Trim());
            File.Delete(markerPath);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
