![Lumisense interface](docs/lumisense-gallery.png)

# Lumisense

[![Release](https://img.shields.io/github/v/release/wasssly/Lumisense?display_name=tag&sort=semver&label=release)](https://github.com/wasssly/Lumisense/releases) [![Release workflow](https://github.com/wasssly/Lumisense/actions/workflows/release.yml/badge.svg)](https://github.com/wasssly/Lumisense/actions/workflows/release.yml) [![Platform](https://img.shields.io/badge/platform-Windows%2011-0078D4?logo=windows11&logoColor=white)](https://github.com/wasssly/Lumisense)

<details name="lumisense-language" open>
<summary><strong>Русский</strong> — нажмите, чтобы свернуть</summary>


**Lumisense** — локальный аудиоплеер для Windows 11 в стиле Fluent Design. Он воспроизводит музыку с диска без стриминга и облачных сервисов, поддерживает обложки, теги, плейлисты, эквалайзер, статистику прослушиваний, мини-плеер, Live Lyrics, Discord Rich Presence и обновление через GitHub Releases.

Проект рассчитан прежде всего на Windows 11 и использует Mica/Acrylic, скруглённые элементы, собственный заголовок окна, тёмную и светлую темы, а также три режима интерфейса: обычное окно, квадратный вид с крупной обложкой и компактный мини-плеер.

> **Дисклеймер.** Lumisense в значительной степени создавался с помощью **Claude** и **Manus AI** и первоначально разрабатывался для личного использования — под конкретные привычки и предпочтения автора, а не как универсальный продукт для всех. Поэтому отдельные решения могут быть субъективными, а некоторые функции — ещё требовать доработки. Если вы нашли ошибку, столкнулись с неудобством или считаете, что проекту не хватает важной возможности, пожалуйста, создайте [issue](https://github.com/wasssly/Lumisense/issues).

### Возможности

#### Воспроизведение и звук

- Воспроизведение MP3, WAV, WMA, FLAC, M4A, AAC, OGG и других поддерживаемых NAudio форматов.
- Play/pause/stop, переход между треками, перемотка по прогресс-бару и автоматический переход к следующей композиции.
- Регулировка громкости, включая плавную логарифмическую регулировку в нижнем диапазоне.
- Десятиполосный эквалайзер с пресетами от 31 Гц до 16 кГц и режимом EQ Bypass для быстрого сравнения звука с обработкой и без неё. Пользовательские пресеты можно сохранять, экспортировать и импортировать.
- Изменение скорости воспроизведения и тона с сохранением высоты тона.
- Шафл и три режима повтора: без повтора, повтор плейлиста и повтор одного трека. История предыдущих треков при активном перемешивании сохраняется между запусками.

#### Плейлист и медиатека

- Добавление отдельных файлов, папок с подпапками и пустых папок для последующего наполнения.
- Группировка треков по папкам, сворачивание и разворачивание групп, включение и отключение отдельных групп в воспроизведении.
- Проверка добавленных папок на новые файлы и поиск по плейлисту.
- Виртуализированный список, рассчитанный на работу с большими плейлистами.
- Избранное с отдельным виртуальным представлением и быстрым переключением из заголовка плейлиста.
- Редактирование тегов и свойств трека прямо из приложения.
- Нормализация имён аудиофайлов по шаблону, в том числе из контекстного меню трека.

#### Интерфейс и интеграция с Windows

- Обычный, квадратный и компактный режим мини-плеера.
- Мини-плеер поверх других окон, настройка прозрачности, перемещение с привязкой к краям экрана и фон, адаптирующийся к текущей обложке. Для обложки доступны стандартный вид и вращающийся винил, а также контур прогресса текущего трека.
- Полноэкранный режим Now Playing с крупной обложкой, управлением воспроизведением, динамичным фоном и текстом песни; открывается по `F11`, через меню видов или контекстное меню мини-плеера.
- Жесты на обложке с возможностью отключения в настройках.
- Тёмная и светлая темы, системный или пользовательский акцентный цвет, Mica/Acrylic и дополнительные параметры внешнего вида.
- Русский и английский языки с мгновенным переключением открытых окон, меню, статистики, Now Playing и списка изменений.
- Медиа-клавиши Windows, пользовательские горячие клавиши, значок в системном трее и Now Playing через System Media Transport Controls.
- Необязательная интеграция Discord Rich Presence с настройками приватности.
- Всплывающее уведомление о смене трека.
- Автозапуск вместе с Windows, запуск свёрнутым в трей, сворачивание в трей вместо закрытия и создание ярлыка на рабочем столе.

#### Метаданные и история

- Чтение обложек из тегов, поиск обложек в интернете, ручная установка изображения и локальное кэширование найденных обложек.
- Просмотр и редактирование свойств обложки.
- Live Lyrics: синхронные тексты из `.lrc`, обычные тексты из `.txt` и тега Comment, встроенный поиск, ручная загрузка и локальное кэширование добавленных текстов.
- Статистика прослушиваний со счётчиком для каждого трека и отдельным окном сводки.
- Возобновление последнего трека после запуска с отдельной возможностью отключить автоматическое воспроизведение.
- Защита данных плейлиста, избранного и статистики от раннего перезаписывания при запуске, включая резервное сохранение пользовательских данных.
- Экспорт и импорт настроек в один `.lumi`-файл, включая выбранный язык интерфейса.
- Список изменений внутри приложения с поиском, сортировкой, визуальными категориями и автоматическим расчётом версии по SemVer. Номер опубликованной версии открывает соответствующий GitHub Release.
- Проверка обновлений через GitHub Releases и установка новой версии из приложения.

### Требования

Для запуска из исходников потребуется Windows 10 или Windows 11 с поддержкой WPF и .NET 8. Для разработки можно использовать Visual Studio 2022 версии 17.8 или новее с workload **.NET desktop development**, либо установленный **.NET 8 SDK**.

### Запуск из исходников

1. Клонируйте репозиторий:

   ```powershell
   git clone https://github.com/wasssly/Lumisense.git
   cd Lumisense/Lumisense
   ```

2. Восстановите зависимости и запустите проект:

   ```powershell
   dotnet restore
   dotnet run
   ```

Также можно открыть `Lumisense.csproj` в Visual Studio и запустить приложение клавишей **F5**. При первом восстановлении NuGet автоматически загрузит необходимые пакеты.

### Готовые сборки и обновления

Готовые установщики `Lumisense_Setup.exe` публикуются на странице [Releases](https://github.com/wasssly/Lumisense/releases) при создании тегов формата `v*.*.*`. Установщик предлагает выбрать русский или английский язык. Сборка выполняется автоматически через [GitHub Actions](https://github.com/wasssly/Lumisense/actions) с использованием self-contained `dotnet publish` и Inno Setup из `Installer/Lumisense.iss`.

Сам плеер умеет проверять наличие новых версий через GitHub Releases и подсказывать пользователю об обновлении. Исходный код приложения и установщик распространяются отдельно: перед использованием конкретной сборки ознакомьтесь с описанием соответствующего релиза.

### Технологический стек

- **.NET 8** и WPF (`net8.0-windows10.0.19041.0`).
- **[WPF-UI](https://github.com/lepoco/wpfui)** — Fluent-компоненты, `FluentWindow`, Mica-фон и системные элементы интерфейса.
- **[NAudio](https://github.com/naudio/NAudio)** — декодирование и воспроизведение аудио.
- **SoundTouch.Net.NAudioSupport** — изменение скорости и тона во время воспроизведения.
- **[TagLibSharp](https://github.com/mono/taglib-sharp)** — чтение и запись тегов и обложек.
- **DiscordRichPresence** — локальная интеграция Discord Rich Presence.
- **[SharpVectors](https://github.com/ElinamLLC/SharpVectors)** — отображение SVG-иконок интерфейса.
- **Windows Forms** — системный трей и `NotifyIcon`.

### Структура репозитория

```text
Lumisense/
├── .github/workflows/release.yml       — сборка и публикация релизов по тегу
├── Installer/Lumisense.iss             — сценарий установщика Inno Setup
└── Lumisense/                          — исходный код плеера
    ├── Lumisense.csproj
    ├── App.xaml / .cs                   — точка входа и подключение тем
    ├── MainWindow.xaml / .cs            — основное окно и воспроизведение
    ├── MiniPlayerWindow.xaml / .cs      — компактный мини-плеер
    ├── NowPlayingWindow.xaml / .cs      — полноэкранный режим Now Playing
    ├── LyricsService.cs                 — загрузка, поиск и кэширование текстов
    ├── LocalizationService.cs           — русская и английская локализация
    ├── DiscordRichPresenceManager.cs    — интеграция Discord
    ├── AppSettings.cs                   — настройки и сохранение состояния
    ├── PlaylistFolder.cs / Favorites.cs — плейлист и избранное
    ├── EqualizerSampleProvider.cs       — десятиполосный эквалайзер
    ├── CoverArt*.xaml(.cs)              — работа с обложками
    ├── Track*.xaml(.cs)                 — свойства и теги треков
    ├── StatisticsWindow.xaml(.cs)       — статистика прослушиваний
    ├── LumiProfile.cs                   — экспорт и импорт профилей
    ├── SettingsWindow.xaml / .cs        — окно настроек
    ├── Changelog/                       — история изменений
    ├── TrayIconManager.cs               — системный трей
    ├── GlobalMediaHotKeys.cs            — медиа-клавиши и горячие клавиши
    ├── NowPlayingIntegration.cs         — интеграция Now Playing
    ├── UpdateChecker.cs                 — проверка обновлений
    └── Icons/                           — SVG-иконки и иконка приложения
```

### Обратная связь

Если вы нашли ошибку или хотите предложить улучшение, создайте [issue в репозитории](https://github.com/wasssly/Lumisense/issues). В описании желательно указать версию приложения, шаги воспроизведения проблемы и, если возможно, фрагмент лога или скриншот.

### Лицензия

Информация о лицензии будет добавлена в репозиторий отдельно. До её публикации ознакомьтесь с условиями использования исходного кода и сторонних зависимостей, перечисленных в разделе [«Технологический стек»](#технологический-стек).

</details>

<details name="lumisense-language">
<summary><strong>English</strong> — click to expand</summary>


**Lumisense** is a local Fluent Design audio player for Windows 11. It plays music stored on disk without streaming or cloud services, and supports cover art, tags, playlists, an equalizer, listening statistics, a mini player, Live Lyrics, Discord Rich Presence, and updates through GitHub Releases.

The project is designed primarily for Windows 11 and uses Mica/Acrylic, rounded controls, a custom window title bar, dark and light themes, and three interface modes: a standard window, a square layout with large artwork, and a compact mini player.

> **Disclaimer.** Lumisense was created largely with the help of **Claude** and **Manus AI** and was initially developed for personal use, around the author’s own habits and preferences, rather than as a universal product for everyone. Some design decisions may therefore be subjective, and certain features may still need refinement. If you find a bug, encounter an inconvenience, or believe an important feature is missing, please open an [issue](https://github.com/wasssly/Lumisense/issues).

### Features

#### Playback and audio

- Playback of MP3, WAV, WMA, FLAC, M4A, AAC, OGG, and other formats supported by NAudio.
- Play, pause, stop, track navigation, seeking through the progress bar, and automatic advance to the next track.
- Volume control, including smooth logarithmic adjustment in the lower range.
- A ten-band equalizer with presets from 31 Hz to 16 kHz and an EQ Bypass mode for quickly comparing processed and unprocessed sound. Custom presets can be saved, exported, and imported.
- Playback-speed and pitch adjustment while preserving pitch.
- Shuffle and three repeat modes: no repeat, repeat playlist, and repeat one track. The previous-track history is retained between launches when shuffle is active.

#### Playlist and library

- Adding individual files, folders with subfolders, and empty folders for later use.
- Grouping tracks by folder, collapsing and expanding groups, and enabling or disabling individual groups for playback.
- Checking added folders for new files and searching within the playlist.
- A virtualized list designed for large playlists.
- Favorites with a dedicated virtual view and quick access from the playlist header.
- Editing track tags and properties directly in the application.
- Normalizing audio-file names from a template, including through a track’s context menu.

#### Interface and Windows integration

- Standard, square, and compact mini-player modes.
- A mini player that can stay above other windows, supports adjustable opacity, edge snapping, and a background that adapts to the current artwork. Artwork can use the standard view or a rotating vinyl view, with an optional track-progress outline.
- A full-screen Now Playing mode with large artwork, playback controls, a dynamic background, and lyrics; it can be opened with `F11`, through the view menu, or from the mini player’s context menu.
- Configurable artwork gestures that can be disabled in Settings.
- Dark and light themes, a system or custom accent color, Mica/Acrylic, and additional appearance settings.
- Russian and English languages, with immediate updates to open windows, menus, statistics, Now Playing, and the changelog.
- Windows media keys, custom hotkeys, a system-tray icon, and Now Playing via System Media Transport Controls.
- Optional Discord Rich Presence integration with privacy settings.
- A notification when the track changes.
- Launching with Windows, starting minimized to the tray, minimizing to the tray instead of closing, and creating a desktop shortcut.

#### Metadata and history

- Reading embedded cover art, searching for cover art online, manually setting an image, and locally caching found artwork.
- Viewing and editing cover-art properties.
- Live Lyrics: synchronized text from `.lrc`, plain text from `.txt` and the Comment tag, built-in search, manual loading, and local caching of added lyrics.
- Listening statistics with a per-track counter and a dedicated summary window.
- Resuming the last track after launch, with a separate option to prevent automatic playback.
- Protecting playlist, favorites, and statistics data from early overwrite at startup, including a backup of user data.
- Exporting and importing settings in a single `.lumi` file, including the selected interface language.
- An in-app changelog with search, sorting, visual categories, and automatic SemVer version calculation. The number of a published version opens its corresponding GitHub Release.
- Checking for updates through GitHub Releases and installing a new version from the application.

### Requirements

Running from source requires Windows 10 or Windows 11 with WPF and .NET 8 support. For development, you can use Visual Studio 2022 version 17.8 or newer with the **.NET desktop development** workload, or an installed **.NET 8 SDK**.

### Running from source

1. Clone the repository:

   ```powershell
   git clone https://github.com/wasssly/Lumisense.git
   cd Lumisense/Lumisense
   ```

2. Restore dependencies and run the project:

   ```powershell
   dotnet restore
   dotnet run
   ```

You can also open `Lumisense.csproj` in Visual Studio and start the application with **F5**. NuGet will automatically download the required packages during the first restore.

### Ready-made builds and updates

Ready-to-use `Lumisense_Setup.exe` installers are published on the [Releases](https://github.com/wasssly/Lumisense/releases) page when tags in the `v*.*.*` format are created. The installer offers a choice between Russian and English. Builds are produced automatically through [GitHub Actions](https://github.com/wasssly/Lumisense/actions), using self-contained `dotnet publish` and Inno Setup from `Installer/Lumisense.iss`.

The player can check GitHub Releases for new versions and notify the user about an update. The application source code and installer are distributed separately; before using a specific build, read the description of its corresponding release.

### Technology stack

- **.NET 8** and WPF (`net8.0-windows10.0.19041.0`).
- **[WPF-UI](https://github.com/lepoco/wpfui)** for Fluent components, `FluentWindow`, the Mica background, and system UI elements.
- **[NAudio](https://github.com/naudio/NAudio)** for audio decoding and playback.
- **SoundTouch.Net.NAudioSupport** for playback-speed and pitch adjustment.
- **[TagLibSharp](https://github.com/mono/taglib-sharp)** for reading and writing tags and cover art.
- **DiscordRichPresence** for local Discord Rich Presence integration.
- **[SharpVectors](https://github.com/ElinamLLC/SharpVectors)** for rendering SVG interface icons.
- **Windows Forms** for the system tray and `NotifyIcon`.

### Repository structure

```text
Lumisense/
├── .github/workflows/release.yml       — builds and publishes releases from tags
├── Installer/Lumisense.iss             — Inno Setup installer script
└── Lumisense/                          — player source code
    ├── Lumisense.csproj
    ├── App.xaml / .cs                   — entry point and theme setup
    ├── MainWindow.xaml / .cs            — main window and playback
    ├── MiniPlayerWindow.xaml / .cs      — compact mini player
    ├── NowPlayingWindow.xaml / .cs      — full-screen Now Playing mode
    ├── LyricsService.cs                 — lyrics loading, search, and caching
    ├── LocalizationService.cs           — Russian and English localization
    ├── DiscordRichPresenceManager.cs    — Discord integration
    ├── AppSettings.cs                   — settings and persisted state
    ├── PlaylistFolder.cs / Favorites.cs — playlist and favorites
    ├── EqualizerSampleProvider.cs       — ten-band equalizer
    ├── CoverArt*.xaml(.cs)              — cover-art handling
    ├── Track*.xaml(.cs)                 — track properties and tags
    ├── StatisticsWindow.xaml(.cs)       — listening statistics
    ├── LumiProfile.cs                   — profile export and import
    ├── SettingsWindow.xaml / .cs        — Settings window
    ├── Changelog/                       — change history
    ├── TrayIconManager.cs               — system tray
    ├── GlobalMediaHotKeys.cs            — media keys and hotkeys
    ├── NowPlayingIntegration.cs         — Windows Now Playing integration
    ├── UpdateChecker.cs                 — update checks
    └── Icons/                           — SVG icons and application icons
```

### Feedback

If you find a bug or would like to propose an improvement, please create an [issue in the repository](https://github.com/wasssly/Lumisense/issues). Include the application version, steps to reproduce the problem, and, if possible, a log excerpt or screenshot.

### License

License information will be added to the repository separately. Until then, review the terms governing the source code and third-party dependencies listed in the [Technology stack](#technology-stack) section.

</details>
