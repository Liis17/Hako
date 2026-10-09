# Backups

Parent: [[Index]]

## Назначение

Резервные копии сборок для будущего восстановления. Копия — ZIP-архив с расширением `.hakobackup`
в `~/.hako/backups/`: вся папка сборки и `data.json` со сведениями для окна восстановления и
профилем, без которого восстановленную сборку не запустить. Окна восстановления пока нет.
Копия создаётся из меню «⋯» профиля сборки ([[UI/Launcher]]).

## Источники

| Файл | Значимый символ | Роль |
|------|-----------------|------|
| `macos/Hako/Hako/Instances/InstanceBackup.swift` | `InstanceBackupManifest` | Схема `data.json`, общие `encoder()`/`decoder()` |
| `macos/Hako/Hako/Instances/InstanceBackup.swift` | `InstanceStore.backup(_:content:)` | Проверки, снимок полей сборки и список модов |
| `macos/Hako/Hako/Instances/InstanceBackup.swift` | `InstanceBackup.archive(_:manifest:into:)` | Сборка архива системным `/usr/bin/zip` |
| `macos/Hako/Hako/Instances/InstanceStorage.swift` | `InstanceStorage.backupsFolder` | Имя папки `backups` в корне `~/.hako` |

## Архив

- Имя: `{folderName}_{yyyy-MM-dd_HH-mm-ss}.hakobackup` (локальное время, `en_US_POSIX`). Существующий файл
  не перезаписывается.
- Корень архива: `data.json` и содержимое папки сборки — `java/`, `minecraft/`, `icon.png`, служебные
  `.hako-*` (реестр модов, отключённые ресурспаки). Родительской папки сборки в архиве нет.
- `minecraft/.hako-running.json` (запись о запущенной игре) и `.hako-game/` (обёртка `.app` для запуска) исключаются.
- Симлинки сохраняются ссылками (`zip -y`): Java-бандл содержит относительные ссылки.
  Восстановление обязано проверять пути и цели ссылок, как `InstanceStorage.containedURL`,
  и не распаковывать записи вне папки новой сборки.
- Архив пишется во временный `.{UUID}.partial` в `backups/` и переименовывается целиком; при ошибке
  временный файл удаляется. Процесс `zip` ожидается через `terminationHandler`, без `waitUntilExit`.
- `data.json` можно прочитать без распаковки: `FabricClient.archiveEntry("data.json", in:limit:)`.

## data.json

JSON с отсортированными ключами, даты — ISO 8601. Читать через `InstanceBackupManifest.decoder()`.
Необязательные поля со значением `nil` в JSON отсутствуют.

| Поле | Смысл |
|------|-------|
| `formatVersion` | Версия формата, сейчас `1` (`InstanceBackupManifest.currentFormat`) |
| `backupCreatedAt` | Дата создания копии |
| `name`, `folderName` | Имя и папка сборки на момент копии |
| `instanceCreatedAt` | Дата создания сборки |
| `minecraftVersion` | ID версии Minecraft |
| `modLoader`, `loaderVersion` | `vanilla` или `fabric`; версия Fabric Loader либо отсутствует |
| `javaMajorVersion` | Мажорная версия управляемой Java |
| `iconSymbol` | SF Symbol иконки; пустая строка — своя картинка |
| `iconPNG` | Base64 PNG 256×256 своей картинки (`icon.png`); отсутствует у иконки-символа |
| `modCount`, `mods` | Число и список JAR из `minecraft/mods`: `file` (имя без `.disabled`), `enabled`, `source`, `projectID`, `versionID` |
| `profile` | `metadataURL`, `metadataSHA1`, `argumentSource`, `offlineMode`, `offlineUsername`, `parameters` (`InstanceParameters`), `javaExecutable`, `legacyTexturepacks`, `fabricConfiguration`, `fabricProfileSHA1` |

Окно восстановления показывает иконку через `InstanceIcon(symbol:data:)`: `iconPNG` как `data`
либо `iconSymbol`. `profile` нужен для запуска: по `metadataURL`/`metadataSHA1` проверяется
целостность версии, по `fabricConfiguration`/`fabricProfileSHA1` — профиль Fabric ([[Minecraft/Launching]]).
Если реестр модов не читается, список строится без происхождения (`source: local`).

## Ограничения

- Копия доступна только для готовой сборки и блокируется очередью/установкой, подготовкой или работой
  игры и файловыми операциями (`InstanceStore.managementBlockedReason`). На время копии ставится
  `contentBusy`, который блокирует запуск, переименование, установку и операции с модами.
- `backups` зарезервировано: `InstanceStore.validateName` отклоняет такую папку без учёта регистра.
  Если папку `backups` уже занимает сборка, копия завершается ошибкой с просьбой переименовать сборку.
- Копии лежат внутри `~/.hako`, поэтому учитываются в объёме сборок в настройках отдельной категорией «Резервные копии» (`InstanceStorage.usage()`).
- Отмены и прогресса в процентах нет; профиль показывает индикатор «Создаём резервную копию…».

## Резервные копии миров

`WorldBackupManifest` и `WorldBackup.archive` находятся в
`macos/Hako/Hako/Instances/WorldBackup.swift`. Архив `.hakoworld` сохраняется в `~/.hako/worlds/`
из меню мира ([[Minecraft/Worlds]]). Корень содержит `data.json` и папку `world/` со всеми файлами
сохранения: `level.dat`, `level.dat_old`, измерениями, иконкой и включёнными/отключёнными датапаками.
Разделение позволяет сохранить собственный `data.json` мира без конфликта с манифестом.

Имя — `{instanceFolder}_{worldFolder}_{yyyy-MM-dd_HH-mm-ss}.hakoworld`. Запись использует staging
в системной временной папке, `.partial` рядом с итоговым архивом и целое переименование;
при ошибке промежуточные файлы удаляются. Символические ссылки внутри мира запрещены:
копия содержит независимые файлы. Существующий архив не перезаписывается.

JSON читается через `WorldBackupManifest.decoder()`; даты ISO 8601, картинки `Data` кодируются base64.
Необязательные поля отсутствуют при `nil`.

| Поле | Смысл |
|------|-------|
| `formatVersion` | `1`; версия схемы `.hakoworld` |
| `worldPath` | `world`; папка содержимого сохранения внутри архива |
| `backupCreatedAt` | Дата создания копии |
| `name`, `folderName` | Название из NBT и имя папки saves |
| `saveVersion`, `gameType`, `lastPlayed`, `size` | Версия самого сохранения, режим, дата последней игры и логический размер до упаковки |
| `iconPNG` | Base64 `icon.png` мира, если доступен |
| `sourceInstance` | `id`, `name`, `folderName`, `minecraftVersion`, `modLoader`, `loaderVersion` исходной сборки |
| `datapacks` | ZIP и папки: `file`, относительный `path`, `enabled`, `isDirectory`, SHA-512 `sha512` для ZIP и base64 `iconPNG`, если есть `pack.png` |

Пути датапаков отсчитываются от `worldPath`: `datapacks/<имя>` или
`.hako-disabled-datapacks/<имя>`. Происхождение локальных паков не требует сетевого запроса:
SHA-512 позволяет будущему восстановлению распознать ZIP через Modrinth при необходимости.

`worlds` зарезервировано в `InstanceStore.validateName` без учёта регистра. Если существующая
сборка уже занимает папку, резервное копирование завершается объяснимой ошибкой.
`manageWorld` держит общую блокировку файлов и запуска на всё время операции. Чтение и хеширование
паков выполняются на `InstanceContent`, копирование/zip — вне главного потока.
Восстановление пока не реализовано; будущий импорт обязан проверять версию схемы, все пути
и записи архива перед распаковкой, не доверяя `worldPath` и `datapacks[].path` из JSON.
