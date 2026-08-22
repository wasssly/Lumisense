# Локальная проверка обновления Velopack

Этот сценарий проверяет не только создание delta-пакета, а весь пользовательский путь: установленная MSI-копия Lumisense находит следующую версию, скачивает её из **локальной** папки, применяет update и перезапускает приложение.

> Ни tag, ни GitHub Release не создаются. Локальный feed доступен только специальной test-only сборке и включается переменной среды пользователя.

## Что создаёт workflow

Ручная workflow **Velopack local update test** собирает две тестовые версии и прикрепляет private artifact `Lumisense-Velopack-local-update-test`.

| Содержимое artifact | Назначение |
|---|---|
| `Wasssly.Lumisense-win.msi` | Устанавливает базовую test-only Velopack-версию. |
| `releases.win.json` | Локальный feed Windows-канала. |
| `Wasssly.Lumisense-<base>-win-full.nupkg` | Установленная исходная версия. |
| `Wasssly.Lumisense-<candidate>-win-full.nupkg` | Full fallback следующей версии. |
| `Wasssly.Lumisense-<candidate>-win-delta.nupkg` | Delta, который Velopack должен предпочесть при обновлении. |
| `delta-measurement.json` | Размеры full/delta и SHA-256 проверки reconstruction. |
| `README-LOCAL-UPDATE-TEST.md` | Краткие команды прямо в artifact. |

## Подготовка Windows

1. Скачайте artifact из успешного run workflow и распакуйте его, например в `C:\Lumisense-velopack-local-feed`.
2. Полностью закройте Lumisense.
3. Запустите `Wasssly.Lumisense-win.msi` из этой папки с правами администратора. Он устанавливает базовую test-only-версию.
4. Откройте PowerShell и укажите локальный feed для текущего пользователя:

```powershell
[Environment]::SetEnvironmentVariable(
  'LUMISENSE_VELOPACK_TEST_FEED',
  'C:\Lumisense-velopack-local-feed',
  'User'
)
```

5. Важно: полностью закройте Lumisense и откройте его снова из ярлыка MSI-установки, чтобы процесс получил новую переменную среды.
6. Откройте **Настройки → О плеере → Проверить обновления**. Должна быть найдена candidate-версия.
7. Нажмите **Скачать и установить**. Приложение показывает процент, перезапускается, а версия в карточке «О плеере» меняется на candidate.

В журнале должен появиться режим:

```text
Режим обновлений: Velopack test build (локальный update feed).
```

## После проверки

Удалите override, чтобы test-only приложение снова не обращалось к локальной папке:

```powershell
[Environment]::SetEnvironmentVariable(
  'LUMISENSE_VELOPACK_TEST_FEED',
  $null,
  'User'
)
```

Затем полностью закройте Lumisense и откройте заново. Не публикуйте и не распространяйте artifact: тестовые MSI и packages не подписаны.

## Критерии успешности

| Проверка | Успешный результат |
|---|---|
| Поиск обновления | UI видит candidate-версию из `releases.win.json`. |
| Скачивание | Есть процент загрузки без обращения к публичному GitHub Release. |
| Применение | Приложение закрывается и перезапускается само. |
| Версия | После перезапуска показана candidate-версия. |
| Целостность | `delta-measurement.json` содержит равные SHA-256 full и reconstructed package. |
| Очистка | После очистки переменной обновления снова не используют локальный feed. |

## References

[1]: https://docs.velopack.io/integrating/update-sources "Velopack update sources"
[2]: https://docs.velopack.io/integrating/testing "Velopack update testing"
