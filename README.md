![Lumisense interface](docs/lumisense-gallery.png)

# Lumisense

[![Release](https://img.shields.io/github/v/release/wasssly/Lumisense?display_name=tag&sort=semver&label=release)](https://github.com/wasssly/Lumisense/releases) [![CI](https://github.com/wasssly/Lumisense/actions/workflows/ci.yml/badge.svg)](https://github.com/wasssly/Lumisense/actions/workflows/ci.yml) [![Release workflow](https://github.com/wasssly/Lumisense/actions/workflows/release.yml/badge.svg)](https://github.com/wasssly/Lumisense/actions/workflows/release.yml) [![Platform](https://img.shields.io/badge/platform-Windows%2010%2B-0078D4?logo=windows11&logoColor=white)](https://github.com/wasssly/Lumisense)

<p align="center">
  <a href="#russian" title="Русский">🇷🇺</a>&nbsp;&nbsp;&nbsp;<a href="#english" title="English">🇬🇧</a>
</p>

<details id="russian" open>
<summary><strong style="font-size: 1.5em;">Русский</strong></summary>

## Содержание

- [Возможности](#возможности)
  - [Воспроизведение и звук](#воспроизведение-и-звук)
  - [Плейлист и коллекция](#плейлист-и-коллекция)
  - [Мини-плеер и внешний вид](#мини-плеер-и-внешний-вид)
  - [Тексты песен и интеграции](#тексты-песен-и-интеграции)
  - [Состояние, диагностика и обновления](#состояние-диагностика-и-обновления)
- [Сборка из исходников](#сборка-из-исходников)
  - [Требования](#требования)
  - [Запуск приложения](#запуск-приложения)
  - [Сборка Release](#сборка-release)
  - [Запуск тестов](#запуск-тестов)
- [Обновления](#обновления)
- [Технологии](#технологии)
  - [Структура репозитория](#структура-репозитория)
- [Обратная связь](#обратная-связь)
- [English](#english)

**Lumisense** — локальный аудиоплеер для Windows 11 в стиле Fluent Design. Он воспроизводит музыку с диска без стриминга и облачных сервисов, поддерживает обложки, теги, плейлисты, эквалайзер, статистику прослушиваний, мини-плеер, Live Lyrics, Discord Rich Presence и обновление через GitHub Releases.

Проект рассчитан прежде всего на Windows 11 и использует Mica/Acrylic, скруглённые элементы, собственный заголовок окна, тёмную и светлую темы, а также три режима интерфейса: обычное окно, квадратный вид с крупной обложкой и компактный мини-плеер.

> **Дисклеймер.** Lumisense в значительной степени создавался с помощью **Claude** и **Manus AI** и первоначально разрабатывался для личного использования — под конкретные привычки и предпочтения автора, а не как универсальный продукт для всех. Поэтому отдельные решения могут быть субъективными, а некоторые функции — ещё требовать доработки. Если вы нашли ошибку, столкнулись с неудобством или считаете, что проекту не хватает важной возможности, пожалуйста, создайте [issue](https://github.com/wasssly/Lumisense/issues).

## Возможности

### Воспроизведение и звук

- Поддержка MP3, WAV, WMA, FLAC, M4A, AAC и OGG.
- Управление воспроизведением: запуск, пауза, остановка, предыдущий и следующий трек.
- Перемотка по прогресс-бару и переход к следующей композиции.
- Три режима повтора: без повтора, повтор всего плейлиста и повтор одного трека.
- Обычный шаффл с учётом истории. Треки из всех активных папок плейлиста попадают в общую колоду и не повторяются до её исчерпания.
- Сохранение истории и очереди сыгранных треков при активном шаффле.
- Логарифмическая и нелогарифмическая регулировка громкости с более плавной квадратичной аудиокривой в нелогарифмическом режиме.
- Десятиполосный эквалайзер с пресетами и пользовательскими настройками.
- Режим EQ Bypass для быстрого сравнения обработанного и исходного сигнала.
- Изменение скорости воспроизведения и тона без изменения исходного файла.
- ReplayGain.
- Выбор устройства вывода Windows и отображение фактически используемого устройства.
- Безопасное восстановление воспроизведения через системное устройство, если сохранённое устройство стало недоступно.
- Экспорт обработанной MP3-копии с текущими скоростью и тоном. Исходный файл не перезаписывается; перед сохранением можно выбрать новое имя.

### Плейлист и коллекция

- Добавление отдельных аудиофайлов и папок вместе с вложенными папками.
- Добавление пустых папок для последующего наполнения.
- Группировка треков по папкам.
- Сворачивание и разворачивание групп.
- Включение и отключение отдельных папок из воспроизведения.
- Поиск по плейлисту.
- Виртуализированный список для больших коллекций.
- Очередь «Играть следующим» с поиском и сортировкой.
- Избранное с отдельным представлением и быстрым переключением из заголовка плейлиста.
- Массовое выделение с помощью `Ctrl` и `Shift`.
- Drag & Drop.
- Проверка отслеживаемых папок на новые файлы.
- Обработка недоступных файлов без удаления исходного файла с диска: можно найти замену или удалить только запись из плейлиста.
- Редактирование тегов, свойств и обложки трека.
- Поиск обложек в интернете и локальное кэширование найденных изображений.
- Нормализация имён аудиофайлов по настраиваемому шаблону.

### Мини-плеер и внешний вид

- Обычный режим, квадратный режим с крупной обложкой и компактный мини-плеер.
- Настройка прозрачности, положения, прилипания к краям экрана и отображаемых элементов мини-плеера.
- Стандартный режим обложки.
- Вращающийся виниловый режим обложки.
- Статичный круглый режим обложки.
- Контур прогресса текущего трека вокруг обложки.
- Настройка толщины контура прогресса.
- Фон мини-плеера, адаптирующийся к цвету текущей обложки.
- Жесты на обложке.
- Полноэкранный режим Now Playing с крупной обложкой, динамичным фоном, управлением и текстом песни.
- Светлая и тёмная темы.
- Системный или пользовательский акцентный цвет.
- Mica/Acrylic и другие параметры оформления Windows-интерфейса.
- Масштаб интерфейса от 85% до 135%.
- Режим «Снизить движение» для отключения декоративных анимаций.
- Автопрокрутка настроек по клику средней кнопкой мыши: первый клик включает режим, повторный клик выключает его.

### Тексты песен и интеграции

- Live Lyrics из `.lrc`.
- Обычные тексты из `.txt` и тега Comment.
- Поиск текстов и ручная загрузка.
- Настройка политики поиска: локальные источники, автоматический точный поиск или ручной поиск.
- Управляемое локальное хранение кэша текстов и обложек.
- Discord Rich Presence с настройками приватности.
- Windows media keys.
- Пользовательские глобальные горячие клавиши.
- System Media Transport Controls.
- Значок в системном трее.
- Автозапуск вместе с Windows и запуск в свёрнутом виде.
- Сворачивание в трей вместо закрытия.
- Создание ярлыка на рабочем столе.

### Состояние, диагностика и обновления

- Сохранение последнего трека и позиции воспроизведения.
- Настройка автоматического возобновления последнего трека.
- Счётчик прослушиваний и отдельное окно статистики.
- Экспорт и импорт профиля в один `.lumi`-файл.
- Версионные миграции настроек.
- Проверка и исправление некорректных значений настроек при запуске.
- Резервное сохранение пользовательских данных.
- Локальные точки восстановления перед сбросом настроек или данных.
- Ограничение размера логов.
- Диагностика аудиовывода и фоновых операций.
- Встроенный список изменений с категориями, поиском и сортировкой.
- Проверка обновлений через GitHub Releases.
- Проверка SHA-256 установщиков перед запуском.
- Поддержка обычного EXE/Inno Setup-сценария и компактных Velopack/MSI-обновлений.
- Уведомление о смене трека можно мгновенно скрыть кликом по карточке.

## Сборка из исходников

### Требования

- Windows 10 или Windows 11.
- .NET 10 SDK.
- Visual Studio 2022 с workload **.NET desktop development** — если используется Visual Studio.
- Git.

### Запуск приложения

В репозитории находятся отдельные проекты приложения и тестов. Поэтому в командах ниже путь к `.csproj` указан явно.

```powershell
git clone https://github.com/wasssly/Lumisense.git
cd Lumisense

dotnet restore .\Lumisense\Lumisense.csproj
dotnet run --project .\Lumisense\Lumisense.csproj
```

### Сборка Release

```powershell
dotnet build .\Lumisense\Lumisense.csproj -c Release
```

### Запуск тестов

```powershell
dotnet test .\Lumisense.Tests\Lumisense.Tests.csproj -c Release
```

Открыть проект можно в Visual Studio, выбрав `Lumisense/Lumisense.csproj`. Тестовый проект находится в `Lumisense.Tests/Lumisense.Tests.csproj`.

## Обновления

Lumisense может проверять наличие новых версий через GitHub Releases. Перед применением загруженного установщика проверяется его SHA-256-контрольная сумма, опубликованная для соответствующего релиза.

В зависимости от типа установки используются разные сценарии:

| Тип установки | Основной сценарий |
|---|---|
| EXE/Inno Setup | Полный установщик `.exe` |
| Velopack/MSI | Компактные обновления через пакеты Velopack |
| Переход EXE → MSI | Отдельный управляемый сценарий миграции |

Если обновление не запускается автоматически, откройте страницу нужного релиза и используйте соответствующий EXE-установщик. Не запускайте отдельные служебные файлы Velopack вручную.

## Технологии

- **.NET 10** и **WPF**.
- **[WPF-UI](https://github.com/lepoco/wpfui)** — Fluent-компоненты и оформление окна.
- **[NAudio](https://github.com/naudio/NAudio)** — декодирование и воспроизведение аудио.
- **SoundTouch.Net** — изменение скорости и тона.
- **[TagLibSharp](https://github.com/mono/taglib-sharp)** — чтение и запись тегов и обложек.
- **[SharpVectors](https://github.com/ElinamLLC/SharpVectors)** — отображение SVG.
- **DiscordRichPresence** — интеграция с Discord.
- **xUnit v3** и Microsoft Testing Platform — автоматические тесты.

### Структура репозитория

```text
Lumisense/
├── .github/workflows/ci.yml            — проверка сборки, unit-тестов и зависимостей
├── .github/workflows/release.yml       — сборка и публикация релизов по тегу
├── Installer/Lumisense.iss             — сценарий установщика Inno Setup
├── docs/UPDATES.md                     — установка, обновления и назначение release assets
├── Lumisense.Tests/                    — unit-тесты
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

## Обратная связь

Перед созданием issue проверьте, что используете последнюю доступную версию. В сообщении укажите:

1. версию Lumisense;
2. версию Windows;
3. шаги воспроизведения проблемы;
4. ожидаемый и фактический результат;
5. скриншот или очищенный фрагмент лога, если это помогает понять проблему.

Не прикладывайте логи с личными путями, именами пользователей или другой чувствительной информацией без предварительного редактирования.

Создать issue можно в [репозитории Lumisense](https://github.com/wasssly/Lumisense/issues).


</details>

<details id="english" open>
<summary><strong style="font-size: 1.5em;">English</strong></summary>

**Lumisense** is a local Fluent Design audio player for Windows 11. It plays music stored on disk without streaming or cloud services, and supports cover art, tags, playlists, an equalizer, listening statistics, a mini player, Live Lyrics, Discord Rich Presence, and updates through GitHub Releases.

The project is designed primarily for Windows 11 and uses Mica/Acrylic, rounded controls, a custom window title bar, dark and light themes, and three interface modes: a standard window, a square layout with large artwork, and a compact mini player.

> **Disclaimer.** Lumisense was created largely with the help of **Claude** and **Manus AI** and was initially developed for personal use, around the author’s own habits and preferences, rather than as a universal product for everyone. Some design decisions may therefore be subjective, and certain features may still need refinement. If you find a bug, encounter an inconvenience, or believe an important feature is missing, please open an [issue](https://github.com/wasssly/Lumisense/issues).

### Features

#### Playback and audio

- MP3, WAV, WMA, FLAC, M4A, AAC, OGG, and other formats supported by NAudio.
- Play, pause, stop, track navigation, seeking, repeat modes, and history-aware shuffle across all active playlist folders.
- Persisted shuffle history and queue state between launches.
- Ten-band equalizer, presets, EQ Bypass, ReplayGain, playback speed, and pitch controls.
- Windows output-device selection with safe fallback and audio recovery when a saved endpoint becomes unavailable.
- Processed MP3 export using the current speed and pitch without overwriting the original file; a new filename can be selected before saving.

#### Playlist and library

- Files, folders, nested folders, empty folders, grouping, search, Favorites, Play Next, Drag & Drop, and a virtualized list for large collections.
- Unavailable-file recovery, tag and artwork editing, online artwork search, local artwork caching, and filename normalization.

#### Mini-player and Windows integration

- Standard, square, and compact mini-player modes with adjustable opacity, edge snapping, artwork gestures, progress outline, standard artwork, rotating vinyl, and static circular artwork.
- Full-screen Now Playing with lyrics and dynamic background.
- Dark and light themes, Mica/Acrylic, custom accents, interface scaling, reduced motion, system tray, media keys, global hotkeys, Discord Rich Presence, and System Media Transport Controls.
- Auto-scroll in Settings toggled by clicking the middle mouse button once to enable it and again to disable it; the pointer indicates the active direction.
- Track-change notification settings; the notification can be dismissed immediately by clicking its card.

### Build from source

Requirements:

- Windows 10 or Windows 11;
- .NET 10 SDK;
- Visual Studio 2022 with **.NET desktop development**, if using Visual Studio;
- Git.

```powershell
git clone https://github.com/wasssly/Lumisense.git
cd Lumisense

dotnet restore .\Lumisense\Lumisense.csproj
dotnet run --project .\Lumisense\Lumisense.csproj
dotnet build .\Lumisense\Lumisense.csproj -c Release
dotnet test .\Lumisense.Tests\Lumisense.Tests.csproj -c Release
```

The repository contains separate application and test projects, so explicit `.csproj` paths are recommended from the repository root.

### Updates

Ready-to-use builds are published on [GitHub Releases](https://github.com/wasssly/Lumisense/releases). Use `Lumisense-<version>-Setup.exe` for normal installation and updates. The MSI package is intended for users who want to migrate to compact Velopack updates. Do not run `.nupkg`, `RELEASES`, or `releases.win.json` files manually. See [docs/UPDATES.md](docs/UPDATES.md) for the exact EXE/MSI scenarios and asset roles.

### Technologies

- **.NET 10** and **WPF**.
- **[WPF-UI](https://github.com/lepoco/wpfui)** for Fluent components and window styling.
- **[NAudio](https://github.com/naudio/NAudio)** for audio decoding and playback.
- **SoundTouch.Net** for playback speed and pitch processing.
- **[TagLibSharp](https://github.com/mono/taglib-sharp)** for tags and cover art.
- **[SharpVectors](https://github.com/ElinamLLC/SharpVectors)** for SVG icons.
- **DiscordRichPresence** for Discord integration.
- **xUnit v3** and Microsoft Testing Platform for automated tests.

Key internal components include `AudioPlaybackCoordinator`, `TrackPreparationService`, and `TrackExportService`.

### Repository structure

```text
Lumisense/
├── .github/workflows/ci.yml            — build, unit-test, and dependency checks
├── .github/workflows/release.yml       — builds and publishes releases from tags
├── Installer/Lumisense.iss             — Inno Setup installer script
├── docs/UPDATES.md                     — installation, updates, and release asset guide
├── Lumisense.Tests/                    — unit tests
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

If you find a bug or would like to propose an improvement, open an [issue in the repository](https://github.com/wasssly/Lumisense/issues). Include the application version, Windows version, reproduction steps, expected and actual results, and a redacted log excerpt or screenshot when available. Remove personal paths, usernames, and other sensitive information before sharing logs.


</details>
