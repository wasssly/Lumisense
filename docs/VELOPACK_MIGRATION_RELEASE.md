# Переход Lumisense: Inno Setup → Velopack MSI

## Цель и границы

Этот migration-релиз добавляет Velopack как **второй** способ доставки обновлений, не ломая уже установленный Lumisense. Старые Inno Setup-копии продолжают использовать существующий `Lumisense_Setup.exe` с проверкой SHA-256. Только копия, установленная из Velopack MSI, получает full/delta-пакеты через `UpdateManager`.

> Переход намеренно выполняется вручную и по инициативе пользователя. Приложение не удаляет старую Inno Setup-копию автоматически и не трогает `%AppData%\Lumisense`.

## Почему переход не может быть бесшовным

| Установка | Каталог программы | Механизм обновления | Совместимость с delta |
|---|---|---|---|
| Текущая Inno Setup | `Program Files\Lumisense` | Скачивает `Lumisense_Setup.exe`, проверяет SHA-256 и запускает Inno Setup | Нет: package store Velopack отсутствует |
| Новая Velopack MSI PerMachine | `Program Files\wasssly\Lumisense` по умолчанию | `UpdateManager` читает `releases.win.json` и скачивает delta либо full package | Да |

У уже установленной Inno Setup-версии нет `Update.exe`, package store и metadata Velopack. Поэтому нельзя подменить EXE-инсталлятор delta-пакетом: это привело бы к неустанавливаемому обновлению. Вместо этого migration-релиз публикует оба типа assets в одном GitHub Release.

## Что публикует workflow

Для каждого обычного release workflow сохраняет legacy asset и добавляет Velopack assets:

| Asset | Назначение |
|---|---|
| `Lumisense_Setup.exe` | Полный Inno Setup installer для всех старых установок и fallback. |
| `releases.win.json` | Feed Velopack для Windows-канала `win`. |
| `Wasssly.Lumisense-<version>-win-full.nupkg` | Полный Velopack package; нужен для первой Velopack-установки и fallback. |
| `Wasssly.Lumisense-<version>-win-delta.nupkg` | Бинарная разница с прошлой Velopack-версией; появится начиная со второго такого release. |
| `Wasssly.Lumisense-win.msi` | MSI PerMachine, требующий прав администратора; используется для ручного перехода. |

Workflow сначала скачивает прежний feed `win`, затем упаковывает новый release, поэтому `vpk pack` может создать delta. Если прошлый Velopack feed ещё не существует, migration-релиз корректно создаёт только full package и MSI.

## Пользовательский переход

1. Пользователь скачивает **MSI** из migration-release и закрывает Lumisense.
2. Устанавливает MSI с правами администратора. Он ставит новую Velopack-копию отдельно; существующую Inno Setup-копию не перезаписывает.
3. После первого успешного запуска Lumisense показывает сообщение, что включены компактные обновления. Настройки, плейлист, избранное и статистика остаются в `%AppData%\Lumisense`.
4. Пользователь проверяет воспроизведение, язык, мини-плеер и настройки.
5. Только затем, при желании, удаляет старую Inno Setup-копию через **Windows → Installed apps**. Нельзя удалять `%AppData%\Lumisense` в диалоге удаления, если пользователь хочет сохранить данные.
6. Следующие обновления из новой Velopack-копии используют delta, если он доступен и выгоднее full package.

## Поведение интерфейса

`UpdateChecker.CheckAsync` определяет транспорт не по версии, а по факту настоящей установки `UpdateManager.IsInstalled`.

| Режим | Поведение кнопки «Скачать и установить» |
|---|---|
| Legacy Inno Setup или запуск из исходников | Скачивает только legacy EXE, проверяет SHA-256 и запускает Inno Setup. |
| Velopack MSI | Проверяет `releases.win.json`; Velopack выбирает delta или full package, показывает процент, а затем перезапускает приложение для установки. |

Автоматическое применение pending update при запуске выключено. Lumisense вызывает явное `ApplyUpdatesAndRestart` только после действия пользователя в диалоге.

## Windows-проверка перед commit

Проверять нужно в тестовой VM либо в отдельной учётной записи Windows. Не устанавливайте migration MSI поверх рабочей основной установки.

| Сценарий | Ожидаемый результат |
|---|---|
| Сборка из исходников / `dotnet run` | Лог показывает legacy Inno Setup mode; проверки Velopack не запускаются. |
| Legacy Inno Setup → новый обычный EXE update | Существующий путь SHA-256 и Inno Setup работает как раньше. |
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
