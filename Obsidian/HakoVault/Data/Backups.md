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
- `minecraft/.hako-running.json` (запись о запущенной игре) исключается.
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
- Копии лежат внутри `~/.hako`, поэтому учитываются в объёме сборок в настройках (`allocatedSize`).
- Отмены и прогресса в процентах нет; профиль показывает индикатор «Создаём резервную копию…».
