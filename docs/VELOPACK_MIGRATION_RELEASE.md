# Переход Lumisense: Inno Setup → Velopack MSI

## Цель и границы

Этот migration-механизм добавляет Velopack как **второй** способ доставки обновлений, не ломая уже установленный Lumisense. Новые release публикуют versioned Inno Setup EXE с проверкой SHA-256; исторические release с `Lumisense_Setup.exe` остаются совместимы через точный fallback. Только копия, установленная из Velopack MSI, получает full/delta-пакеты через `UpdateManager`.

> Переход запускается только по явному действию пользователя: в окне обновления старой EXE-копии есть отдельная кнопка «Перейти на компактные обновления (MSI)». Приложение проверяет SHA-256 MSI, передаёт его стандартному установщику Windows и не удаляет старую Inno Setup-копию автоматически. `%AppData%\Lumisense` не затрагивается.

## Почему переход не может быть бесшовным

| Установка | Каталог программы | Механизм обновления | Совместимость с delta |
|---|---|---|---|
| Inno Setup EXE | `Program Files\Lumisense` | Скачивает versioned `Lumisense-<version>-Setup.exe`, проверяет SHA-256 и запускает Inno Setup | Нет: package store Velopack отсутствует |
| Velopack MSI PerMachine | `Program Files\wasssly\Lumisense` по умолчанию | `UpdateManager` читает `releases.win.json` и скачивает delta либо full package | Да |

У уже установленной Inno Setup-версии нет `Update.exe`, package store и metadata Velopack. Поэтому нельзя подменить EXE-инсталлятор delta-пакетом: это привело бы к неустанавливаемому обновлению. Вместо этого migration-релиз публикует оба типа assets в одном GitHub Release.

## Что публикует workflow

Для каждого обычного release workflow публикует Inno Setup asset и добавляет Velopack assets:

| Asset | Назначение |
|---|---|
| `Lumisense-<version>-Setup.exe` | Полный Inno Setup installer для обычной установки и обновления EXE-копий. В исторических release встречается `Lumisense_Setup.exe`. |
| `releases.win.json` | Feed Velopack для Windows-канала `win`; внутренний `PackageId` остаётся `Wasssly.Lumisense`. |
| `Lumisense-<version>-full.nupkg` | Полный публичный Velopack package; нужен для fallback. Внутри сохраняется прежний package identity. |
| `Lumisense-<version>-delta.nupkg` | Бинарная разница с прошлой Velopack-версией, если она создана и применима. |
| `Lumisense-<version>-win-x64.msi` | MSI PerMachine x64, требующий прав администратора; используется для добровольного перехода. Историческое имя: `Wasssly.Lumisense-win.msi`. |

Workflow сначала скачивает прежний feed `win`, затем упаковывает новый release, поэтому `vpk pack` может создать delta. Если прошлый Velopack feed ещё не существует, migration-релиз корректно создаёт только full package и MSI.

## Пользовательский переход

1. Пользователь устанавливает актуальную EXE-версию из `Lumisense-<version>-Setup.exe`, затем вручную открывает **«Проверить обновления»**. Начиная с hotfix `1.16.1`, даже если EXE-копия уже совпадает с последним release, диалог честно предлагает отдельный добровольный переход **«Перейти на компактные обновления (MSI)».**
2. После понятного подтверждения Lumisense скачивает MSI только по доверенному HTTPS-адресу, сверяет SHA-256 с GitHub Release и запускает стандартный установщик Windows. Windows запрашивает права администратора.
3. MSI ставит новую Velopack-копию отдельно; существующую Inno Setup-копию не перезаписывает.
4. После первого успешного запуска Lumisense показывает сообщение, что включены компактные обновления. Настройки, плейлист, избранное и статистика остаются в `%AppData%\Lumisense`.
5. Пользователь проверяет воспроизведение, язык, мини-плеер и настройки.
6. Только затем, при желании, открывает **Настройки → Обновления → «Удалить старую EXE-копию»**. Карточка доступна в любой момент, пока найден точный legacy AppId; она запускает штатный uninstaller и остаётся доступной, если пользователь отменил мастер. В мастере удаления следует выбрать «Нет» при вопросе об удалении `%AppData%\Lumisense`. **Windows → Installed apps** остаётся альтернативным ручным способом.
7. Следующие обновления из новой Velopack-копии используют delta, если он доступен и выгоднее full package.

## Поведение интерфейса

`UpdateChecker.CheckAsync` определяет транспорт не по версии, а по факту настоящей установки `UpdateManager.IsInstalled`.

| Режим | Поведение кнопки «Скачать и установить» |
|---|---|
| Legacy Inno Setup или запуск из исходников | Основная кнопка скачивает только legacy EXE, проверяет SHA-256 и запускает Inno Setup. Если опубликован проверенный MSI, отдельная кнопка предлагает добровольно запустить переход. |
| Velopack MSI | Проверяет `releases.win.json`; Velopack выбирает delta или full package, показывает процент, а затем перезапускает приложение для установки. |

Автоматическое применение pending update при запуске выключено. Lumisense вызывает явное `ApplyUpdatesAndRestart` только после действия пользователя в диалоге.

## Windows-проверка перед commit

Проверять нужно в тестовой VM либо в отдельной учётной записи Windows. Не устанавливайте migration MSI поверх рабочей основной установки.

| Сценарий | Ожидаемый результат |
|---|---|
| Сборка из исходников / `dotnet run` | Лог показывает legacy Inno Setup mode; проверки Velopack не запускаются. |
| Legacy Inno Setup → новый обычный EXE update | Существующий путь SHA-256 и Inno Setup работает как раньше. |
| Актуальная legacy EXE `1.16.1` → ручная проверка → кнопка MSI-перехода | Диалог не заявляет о новой версии; он предлагает добровольный MSI-переход. Нужны явное подтверждение, SHA-256 MSI и UAC; старая копия остаётся до ручного удаления. |
| Чистая Windows → Velopack MSI | MSI требует elevation, приложение стартует, появляется единоразовое сообщение о compact updates. |
| Velopack MSI → следующая test release | Update dialog показывает release notes и процент; `UpdateManager` применяет delta либо fallback full и перезапускает приложение. |
| Удаление старого Inno Setup | Новая MSI-копия запускается; `%AppData%\Lumisense` не удалён. |
| Удаление Velopack MSI | Пользовательские данные в `%AppData%\Lumisense` остаются. |

## Публикация и подпись

Пакеты тестового прототипа не подписаны. До публичного migration-release следует настроить code signing для MSI, Setup.exe, Update.exe и основного приложения. SHA-256 в GitHub assets остаётся проверкой целостности загрузки, но не заменяет подпись издателя Windows.

## References

[1]: https://docs.velopack.io/packaging/installer "Velopack installers and MSI PerMachine"
[2]: https://docs.velopack.io/integrating/overview "Velopack integration and delta fallback"
[3]: https://docs.velopack.io/integrating/testing "Velopack update testing"
[4]: https://docs.velopack.io/integrating/uninstalling "Velopack uninstall behaviour"
