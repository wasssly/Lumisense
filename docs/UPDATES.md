# Установка и обновление Lumisense / Installing and updating Lumisense

- [Русский](#русский)
- [English](#english)

## Русский

Lumisense публикует готовые файлы на странице [GitHub Releases](https://github.com/wasssly/Lumisense/releases). Для обычной установки выбирайте **EXE**. MSI предназначен для добровольного перехода на управляемые Velopack-обновления. Файлы `.nupkg`, `RELEASES` и `releases.win.json` являются служебными и не предназначены для ручного запуска.

> Начиная с release, который внедрит versioned public assets, в названиях пользовательских файлов указывается номер версии. В уже опубликованных исторических release могут встречаться прежние имена, например `Lumisense_Setup.exe` и `Wasssly.Lumisense-win.msi`; это нормально для этих release.

### Файлы release

| Asset | Назначение | Когда использовать |
|---|---|---|
| `Lumisense-<version>-Setup.exe` | Полный установщик Inno Setup. Проверяет и обновляет существующую EXE-установку Lumisense. | Обычная установка, ручное обновление или восстановление EXE-версии. |
| `Lumisense-<version>-win-x64.msi` | MSI для добровольного перехода на Velopack-установку x64. Windows запросит права администратора. | Только если хотите перейти на компактные Velopack-обновления. |
| `Lumisense-<version>-full.nupkg` | Полный Velopack package. Приложение использует его как fallback при обновлении MSI/Velopack-копии. | Не запускайте вручную. Это технический файл для updater. |
| `Lumisense-<version>-delta.nupkg` | Компактная разница с предыдущей Velopack-версией, если workflow смог её создать и updater может её применить. | Не запускайте вручную. Наличие файла не гарантирует, что именно он будет выбран на конкретном ПК. |
| `releases.win.json` | Индекс Velopack Windows-канала: содержит версии, контрольные суммы и имена package-файлов. | Не изменяйте и не запускайте вручную. |
| `RELEASES` | Служебный release index, публикуемый упаковкой Velopack. | Не требуется для ручной установки. |

Внутренний `PackageId` Velopack остаётся `Wasssly.Lumisense`, хотя публичные имена package-файлов используют префикс `Lumisense`. Это сохраняет совместимость уже установленных MSI/Velopack-копий и не означает смену названия приложения.

### Обычная EXE-установка

1. Откройте страницу [Releases](https://github.com/wasssly/Lumisense/releases) и выберите последний стабильный release.
2. Скачайте `Lumisense-<version>-Setup.exe` и запустите файл.
3. Если Lumisense уже установлен через EXE, Inno Setup распознает его как ту же программу и обновит в прежней папке.
4. В самом приложении также можно использовать проверку обновлений: перед запуском скачанный EXE проверяется по SHA-256, опубликованному GitHub Release.

### Переход на MSI и компактные обновления

MSI не является обязательным обновлением EXE-установки. В актуальной EXE-копии откройте проверку обновлений и выберите переход на компактные обновления, либо скачайте versioned MSI со страницы release. Перед запуском MSI закройте Lumisense и будьте готовы подтвердить запрос UAC.

После установки из MSI приложение получает последующие обновления через Velopack. Updater сам выбирает подходящий delta package или full package по фактическому состоянию локальной установки и опубликованному feed. Delta используется не всегда: если нужной базы нет, пакет не подходит или полный вариант надёжнее, будет скачан full package. В окне обновления Lumisense отображает доступную диагностику фактического плана.

MSI-установка может существовать рядом со старой EXE-копией. После проверки новой копии старую EXE-установку можно удалить через **Windows → Installed apps**. Не удаляйте `%AppData%\Lumisense`, если хотите сохранить настройки, плейлисты, избранное и статистику.

### Безопасность и помощь

Скачивайте установщики только со страницы официального GitHub Release. Не переименовывайте, не распаковывайте и не запускайте `.nupkg` вручную. При проблеме с обновлением приложите к [issue](https://github.com/wasssly/Lumisense/issues) номер версии, тип установки (EXE или MSI/Velopack), текст сообщения и журнал диагностики, если он доступен.

## English

Lumisense publishes ready-to-use files on [GitHub Releases](https://github.com/wasssly/Lumisense/releases). Choose the **EXE** for a normal installation. The MSI is for an optional move to managed Velopack updates. The `.nupkg`, `RELEASES`, and `releases.win.json` files are technical assets and are not intended to be run manually.

> Starting with the release that introduces versioned public assets, user-facing file names include the release version. Earlier published releases may use legacy names such as `Lumisense_Setup.exe` and `Wasssly.Lumisense-win.msi`; that is expected for those releases.

### Release assets

| Asset | Purpose | When to use it |
|---|---|---|
| `Lumisense-<version>-Setup.exe` | Full Inno Setup installer. It detects and updates an existing EXE installation of Lumisense. | Normal installation, manual update, or repair of the EXE version. |
| `Lumisense-<version>-win-x64.msi` | MSI for an optional move to the 64-bit Velopack installation. Windows will request administrator permission. | Only if you want compact managed Velopack updates. |
| `Lumisense-<version>-full.nupkg` | Full Velopack package. The application uses it as a fallback when updating an MSI/Velopack installation. | Do not run it manually; it is an updater asset. |
| `Lumisense-<version>-delta.nupkg` | Compact difference from the previous Velopack version, if the workflow created it and the updater can apply it. | Do not run it manually. Its presence does not guarantee that it will be selected on a particular PC. |
| `releases.win.json` | The Velopack Windows-channel index; it contains versions, checksums, and package file names. | Do not modify or run it manually. |
| `RELEASES` | A technical release index produced by Velopack packaging. | Not needed for manual installation. |

The internal Velopack `PackageId` remains `Wasssly.Lumisense` even though public package-file names use the `Lumisense` prefix. This preserves compatibility for existing MSI/Velopack installations and does not represent an application rename.

### Normal EXE installation

1. Open [Releases](https://github.com/wasssly/Lumisense/releases) and select the latest stable release.
2. Download `Lumisense-<version>-Setup.exe` and run it.
3. If Lumisense is already installed through EXE, Inno Setup recognizes it as the same application and updates the existing folder.
4. You can also use the in-app update check. Before it starts, a downloaded EXE is verified against the SHA-256 value published by the GitHub Release.

### Moving to MSI and compact updates

The MSI is not a required update for an EXE installation. In an up-to-date EXE copy, open the update check and choose the move to compact updates, or download the versioned MSI from the release page. Close Lumisense before starting the MSI and be ready to approve the UAC prompt.

After an MSI installation, the application receives later updates through Velopack. The updater selects a suitable delta package or full package based on the actual local installation state and the published feed. Delta is not guaranteed: if the required base is unavailable, the package is unsuitable, or full is safer, the full package is downloaded. Lumisense exposes available diagnostics for the actual plan in its update window.

An MSI installation can exist alongside the older EXE copy. After verifying the new copy, you can remove the old EXE installation through **Windows → Installed apps**. Do not delete `%AppData%\Lumisense` if you want to keep settings, playlists, favorites, and listening statistics.

### Security and support

Download installers only from the official GitHub Release page. Do not rename, unpack, or run `.nupkg` files manually. If an update fails, open an [issue](https://github.com/wasssly/Lumisense/issues) with the version, installation type (EXE or MSI/Velopack), displayed message, and diagnostic log when available.

## References

[1]: https://docs.velopack.io/integrating/overview "Velopack integration overview"
[2]: https://docs.velopack.io/sources/github/ "Velopack GitHub release source"
[3]: https://docs.github.com/rest/releases/assets "GitHub REST API: release assets"
