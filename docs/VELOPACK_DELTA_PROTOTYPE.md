# Прототип Velopack: delta-обновления Lumisense

## Назначение

Этот прототип проверяет пакетные **delta-обновления Velopack** для Windows, не публикует релиз и не заменяет рабочий механизм обновления через Inno Setup. Он добавляет явную раннюю инициализацию `VelopackApp`, сервис проверки Velopack и ручной workflow, который собирает два последовательных пакета, создаёт delta, восстанавливает из неё полный пакет и сравнивает SHA-256.

> Прототип не предназначен для доставки пользователям и не должен заменять `Installer/Lumisense.iss` до успешного Windows-тестирования и отдельного решения о migration-релизе.

## Что остаётся без изменений

| Область | Поведение в прототипе |
|---|---|
| Текущие пользователи Inno Setup | Продолжают пользоваться существующей проверкой обновлений, SHA-256 и `Lumisense_Setup.exe`. |
| GitHub Releases | Workflow прототипа ничего не создаёт и не загружает: он выдаёт только private workflow artifact. |
| Пользовательские данные | Остаются в `%AppData%\Lumisense`; обновление Velopack не должно хранить их в каталоге приложения. |
| Основной update UI | Не переключается на Velopack автоматически. Это будет отдельный этап после миграционного теста. |

## Выбранная модель установки

Прототип собирает **Velopack MSI PerMachine** с `PackId` `Wasssly.Lumisense`. MSI нужен, чтобы сохранить модель установки для всех пользователей компьютера и административный контекст, знакомые текущему Inno Setup. Velopack хранит рабочую версию в каталоге `current`, который заменяется при обновлении; поэтому настройки, журналы и cache не должны размещаться рядом с исполняемым файлом. Lumisense уже использует `%AppData%\Lumisense`, что соответствует этому требованию.

## Почему нельзя автоматически перевести 1.15.0 на Velopack

Установленный сейчас Lumisense использует Inno Setup с собственным `AppId` и каталогом `Program Files\Lumisense`. Velopack MSI создаёт другой installer identity и layout. Простая публикация Velopack-пакетов в обычном GitHub Release не заставит уже установленный `UpdateChecker` скачать или применить delta: он ожидает `Lumisense_Setup.exe` и проверяет его SHA-256.

Безопасный migration-релиз должен быть отдельной задачей и обязан:

1. Обнаружить старую Inno Setup-установку.
2. Объяснить пользователю однократный переход на новый updater.
3. Закрыть Lumisense, не удаляя `%AppData%\Lumisense`.
4. Корректно удалить legacy-программу либо выполнить контролируемую установку рядом только на время миграции.
5. Установить Velopack MSI и проверить запуск из нового stable launcher.
6. Оставить полный пакет как fallback для восстановления.

До завершения этого этапа `UpdateMigrationGuard` лишь логирует режим запуска: `legacy Inno Setup` или `Velopack prototype`.

## Ручной workflow

Файл `.github/workflows/velopack-prototype.yml` запускается **только вручную**. Он принимает:

| Параметр | Пример | Смысл |
|---|---|---|
| `base_ref` | `v1.15.0` | Настоящий git ref базовой версии. |
| `base_version` | `1.15.0` | Версия базового Velopack full package. |
| `candidate_version` | `1.15.1-prototype.1` | Более новая SemVer-версия кандидата. |

Workflow выполняет следующие безопасные действия.

1. Собирает базовую версию из `base_ref` и создаёт её full package. Для базы разрешён `--skipVeloAppCheck`, так как опубликованный v1.15.0 ещё не содержит Velopack bootstrap.
2. Собирает текущий кандидат с `VelopackApp.Build().Run()`.
3. Генерирует candidate full package, delta package и MSI PerMachine.
4. Восстанавливает full package из base + delta через `vpk delta patch`.
5. Сравнивает SHA-256 восстановленного пакета и исходного candidate full package.
6. Загружает packages и `delta-measurement.json` как private artifact со сроком хранения 7 дней.

## Локальное измерение без GitHub Actions

Сценарий `tools/Measure-VelopackDelta.ps1` выполняет ту же проверку на Windows локально и **не публикует** файлы в GitHub. Ему нужны две уже собранные self-contained папки publish: одна для базы и одна для кандидата.

```powershell
.\tools\Measure-VelopackDelta.ps1 `
  -BasePublishDirectory .\baseline-publish `
  -CandidatePublishDirectory .\candidate-publish `
  -BaseVersion 1.15.0 `
  -CandidateVersion 1.15.1-prototype.1
```

В `VelopackPrototype\Releases\delta-measurement.json` будут размеры full и delta-пакета, процент delta от full и SHA-256. Скрипт считает проверку успешной только тогда, когда full package, восстановленный из `base + delta`, имеет тот же SHA-256, что и исходный candidate full package.

## Критерии успешного прототипа

| Проверка | Ожидаемый результат |
|---|---|
| Сборка | `dotnet publish` и обе команды `vpk pack` завершаются без ошибок. |
| Delta | В `Releases` есть `*-full.nupkg` и `*-delta.nupkg` для candidate. |
| Целостность | SHA-256 reconstructed package совпадает с SHA-256 candidate full package. |
| Размер | `delta-measurement.json` содержит размер full, delta и процент delta от full. |
| Установка | MSI устанавливается в тестовой VM с правами администратора и запускает Lumisense. |
| Legacy guard | Старая Inno Setup-установка по-прежнему использует прежний Inno/SHA-256 путь; prototype не пытается применить delta. |
| UI/данные | После MSI-установки открываются RU и EN интерфейс, плейлист, избранное и настройки остаются доступны из `%AppData%\Lumisense`. |

## Что проверять на Windows

1. Распаковать архив прототипа в отдельную папку, не поверх рабочей копии Lumisense.
2. Выполнить `dotnet restore` и `dotnet build -c Release` из папки `Lumisense`.
3. Запустить приложение из исходников и убедиться, что оно работает как раньше. В журнале будет строка о legacy Inno Setup режиме — это ожидаемо.
4. После commit в отдельной ветке вручную запустить workflow `Velopack delta prototype` с базой `v1.15.0`.
5. Скачать artifact. Сверить `delta-measurement.json`: `reconstructedFullSha256` обязан совпасть с `fullPackage.sha256`.
6. В изолированной тестовой Windows VM установить созданный MSI. Не ставить его поверх основной рабочей установки, пока migration-путь не реализован.
7. Проверить запуск, закрытие, повторный запуск, сохранение данных, удаление и отсутствие затрагивания legacy-папки.

## Следующее решение после теста

После измерения нужно выбрать один из двух путей.

| Результат | Действие |
|---|---|
| Delta существенно меньше full и MSI стабильно работает | Реализовать переходный Inno → Velopack release и отдельный UI для `UpdateManager`. |
| Delta недостаточно выгодна или миграция неудобна | Оставить текущий Inno Setup + SHA-256; прототип не публиковать. |

## References

[1]: https://docs.velopack.io/getting-started/csharp "Velopack C# getting started"
[2]: https://docs.velopack.io/integrating/overview "Velopack integration overview"
[3]: https://docs.velopack.io/distributing/github-actions "Velopack GitHub Actions"
[4]: https://docs.velopack.io/packaging/installer "Velopack installer and MSI options"
