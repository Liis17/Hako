# Установка Minecraft и Java

Parent: [[Index]]

## Назначение и контракты

`Minecraft/MojangClient.swift` относительно `macos/Hako/Hako/` содержит actor `MojangClient`:
`catalog()` возвращает весь официальный каталог; `prepare(_:platform:)` проверяет SHA-1 описания,
клиент, компонент/версию Java и все группы нативных библиотек. `MojangError.unsupported` — отсутствие
подходящих файлов; сетевые и HTTP-ошибки означают сбой проверки, а не несовместимость.
`Minecraft/MojangModels.swift` описывает современные и legacy metadata, rules, libraries, assets и runtime.
Версия runtime распознаётся и в формате `8u74…`, и в числовых форматах `1.8…`, `17…`, `25…`.

`Minecraft/MinecraftInstaller.swift` — actor `MinecraftInstaller.install(_:at:platform:progress:)`.
Каждая установка пишет только в папку переданной сборки. Файлы скачиваются через URLSession во временный
файл, проверяются по SHA-1 и размеру, затем публикуются. До четырёх бинарных загрузок одновременно.
Проверенный существующий файл используется повторно; испорченный скачивается заново.
Java собирается из raw-файлов, исполняемых прав и относительных симлинков внутри Java.
Клиент и полный JSON — `minecraft/versions/{id}/`, библиотеки — `minecraft/libraries/`,
старые natives — `minecraft/natives/`, logging — `minecraft/assets/log_configs/`.
Assets сохраняются в objects/indexes; virtual и map_to_resources создают локальные копии.
Современные native JAR остаются в libraries. Запуска Minecraft установщик не выполняет.

`Instances/InstallationCoordinator.swift` — общий `@MainActor @Observable` координатор приложения.
Очередь FIFO по дате создания, одна активная сборка. `start()` восстанавливает прерванные установки,
`enqueue` ставит сборку в очередь, `pause` сначала сохраняет `pauseRequested`, затем отменяет активные задачи.
После их завершения состояние становится paused; при перезапуске сохранённый запрос сразу восстанавливается как paused.
Ошибка пути отдельной сборки переводит её в failed и не блокирует следующую запись очереди.
Сборка остаётся installing, пока отменённые загрузки ещё могут писать файлы; в это время переименование
заблокировано. Переход между страницами и выход не отменяют загрузку. После закрытия приложения
установка продолжается при следующем запуске, пользовательская пауза остаётся паузой.

## Официальные источники

- [Каталог версий](https://piston-meta.mojang.com/mc/game/version_manifest_v2.json): URL и SHA-1 полного JSON.
- [Каталог Java](https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json): платформы `mac-os` и `mac-os-arm64`, компоненты и ссылки на runtime manifest.
  Длинный сегмент URL не является хешем текущего содержимого; URL задан централизованно в `MojangClient`.
- [1.6.4](https://piston-meta.mojang.com/v1/packages/b71bae449192fbbe1582ff32fb3765edf0b9b0a8/1.6.4.json): отсутствие javaVersion означает Java 8 / jre-legacy.
- [22w18a](https://piston-meta.mojang.com/v1/packages/1de25e62031021df204de79c264822898c937447/22w18a.json): ARM Java доступна, но ARM-библиотек игры нет.
- [1.19](https://piston-meta.mojang.com/v1/packages/14bbfb25fb1c1c798e3c9b9482b081a78d1f3a9d/1.19.json): отдельные native artifacts для Intel и ARM.

## Ограничения

Используется только нативная архитектура компьютера и Java компонента, указанного Mojang;
Intel fallback на Apple Silicon, запуск игры через Rosetta, другие поставщики Java и подмена библиотек исключены
по решению пользователя. Каталог остаётся полным, но несовместимую сборку создать нельзя.
Совместимость определяется метаданными, а не сравнением текстовых номеров версий. Intel-процесс лаунчера
под Rosetta определяется через `sysctl.proc_translated` как Apple Silicon ([документация Apple](https://developer.apple.com/documentation/apple-silicon/about-the-rosetta-translation-environment)).
URL загрузок должен быть HTTPS; пути, архивы и симлинки не могут выходить за папку сборки.

## Windows: Mojang и установка

Windows-реализация находится в `windows/Hako.Core/Minecraft/`. `MojangClient.CatalogAsync()` читает
полный каталог версий, а `PrepareAsync(version, host)` проверяет SHA-1 описания, наличие клиента,
компонент Java и все требуемые группы Windows natives. Отсутствие файлов возвращается как
`MojangError.Unsupported`; ошибки HTTP, сети и целостности остаются отдельными ошибками подготовки.
JSON внешнего API описан в `MojangModels.cs` и сериализуется через `MojangJsonContext`.
Это отдельный source-generated контекст типов ответа Mojang, не контекст моделей сохранённых данных Hako.

`WindowsHostArchitectureProvider` определяет архитектуру ОС через `IsWow64Process2`, поэтому x64-процесс
Hako под ARM64 тоже распознаёт ARM64-хост. На ARM64 сначала выбирается полный набор `windows-arm64`
(Java и все natives); если его нет, выбирается целиком x64-набор при `UserEnabled` из
`GetMachineTypeAttributes(IMAGE_FILE_MACHINE_AMD64)`: API возвращает HRESULT, успех — `hr >= 0`;
если API отсутствует, например в Windows 10, x64-fallback недоступен. Это соответствует решению пользователя
разрешить x64 под эмуляцией на ARM64. Если AMD64 недоступен, установка несовместимой версии отклоняется до загрузки.
На x64 используется только x64, на x86 — только x86. Выбранный `GameArchitecture` входит в
`PreparedInstallation`, повторная подготовка принимает закреплённую архитектуру; установщик не
определяет её заново. `InstallationResult.JavaExecutable` и `GameInstance.JavaExecutable` хранят путь
относительно папки `java/`, с разделителем `/`; для Windows это `bin/javaw.exe`. Такой же относительный
контракт используется в общем `data.json` резервной копии. Поле `GameInstance.Architecture` из реестра
этапа хранения сохраняет выбор между подготовкой, установкой и последующими повторами. Каталог Java
использует Mojang-ключи `windows-arm64`, `windows-x64` и `windows-x86`.

`MojangCompatibility.Libraries` сохраняет порядок библиотек из манифеста, включая legacy natives.
Суффикс `${arch}` в legacy classifier подменяется на `64`, `32` или `arm64`; `natives-windows` без
суффикса также поддерживается для x86.

`MinecraftInstaller.InstallAsync` пишет только в корень переданной сборки и использует
`InstanceStorage.ContainedPath` этапа хранения для путей и запрета reparse points. Java собирается из
runtime manifest raw-файлов. Link-записи отклоняются на подготовке и установке понятной ошибкой;
символические ссылки и другие reparse points не создаются. До установки сиротские `.hako-*.tmp` ищутся
только в `java/` и `minecraft/{versions,libraries,assets,natives,resources}` с обходом вложенных каталогов,
а также непосредственно в `minecraft/`; `saves`, `mods`, `screenshots` и `resourcepacks` не обходятся.
Очистка best-effort и не останавливает установку при ошибках доступа, ввода-вывода или гонках. Это безопасно,
поскольку `InstanceStore` не допускает одновременные установки одного корня.
Клиент и полный JSON версии размещаются в `minecraft/versions/{id}/`, библиотеки — в
`minecraft/libraries/`, legacy natives после безопасной проверки ZIP — в `minecraft/natives/`, logging —
в `minecraft/assets/log_configs/`. Современные native JAR остаются в libraries. Asset objects хранятся
в `minecraft/assets/objects/`; только virtual и `map_to_resources` проверяют имена и создают проверяемые
копии, коллизии Windows-путей без учёта регистра отклоняются. Создаются `minecraft/mods` и папка
ресурспаков (`resourcepacks`, либо `texturepacks` для legacy версии). Все legacy native archives сначала
распаковываются во временную staging-папку и публикуются заменой каталога `minecraft/natives` после
завершения; если переименование staging не удалось, прежний каталог восстанавливается. Отмена и ошибки
распаковки удаляют staging без частично опубликованных файлов.

`Instances/InstallationCoordinator.cs` — app-lifetime FIFO-очередь, одна активная установка, порядок по `CreatedAt`.
`StartAsync` загружает реестр при необходимости: прерванный `installing` возвращается в очередь, а сохранённый
`pauseRequested` восстанавливается как `paused`. Публичные `EnqueueAsync`, `PauseAsync`, `ResumeAsync`, `RetryAsync`
и `CreateAndEnqueueAsync` выполняют синхронные операции реестра на фоне. Последний регистрирует проверенный
`PreparedInstallation` до того, как очередь сможет выбрать созданную запись. Если её успели поставить на паузу до
регистрации подготовки, запись остаётся `paused`, а подготовка отбрасывается; `ResumeAsync` подготавливает версию
повторно с уже закреплённой архитектурой. URL/SHA-1 версии и архитектура сохраняются через `InstanceStore`; повторная
установка использует подготовку без лишнего запроса. После перезапуска, паузы и повтора `MojangClient.PrepareAsync`
получает закреплённую в профиле архитектуру. Для прежних записей без архитектуры координатор выбирает её при подготовке
и сохраняет до запуска загрузок установщика.

Установка занимает content-lease `InstanceStore` на всё время работы установщика. Состояния `queued` и `installing`
блокируют переименование, обновление параметров, удаление, дублирование и резервное копирование. `PauseAsync`
сначала сохраняет `pauseRequested`, затем отменяет активный token вне lock; если установка успела освободить свой
`CancellationTokenSource`, отмена считается уже завершившейся. Активный профиль остаётся `installing`, пока установщик
не дождётся завершения всех отменённых загрузок и очистки временных файлов, после чего становится `paused`. Ошибка
установщика во время этой паузы также завершается состоянием `paused`. Content-lease освобождается до записи
терминального `ready`/`paused`/`failed`, поэтому обработчик события терминального состояния уже может запустить игру
или управлять файлами. Терминальная запись и очистка активной задачи выполняются в одной lock-секции. Поставленная на
паузу сборка больше не удерживает блокировку и может быть удалена. `ResumeAsync` ставит её в очередь; `RetryAsync`
повторяет `failed` и восстанавливает осиротевший `installing` без активной задачи и без `pauseRequested`. Занятая
сборка пропускается при обходе очереди; `BusyStateChanged` повторно запускает pump. Ошибка установки сохраняет код и
сообщение в профиле и не мешает следующим сборкам. Если запись успешного завершения не удалась, координатор пытается
записать `failed` с кодом этой ошибки; при повторном сбое состояние остаётся `installing` и восстанавливается через
`RetryAsync`. При успехе сохраняются относительный `javaExecutable` (`bin/javaw.exe`), `javaMajorVersion`, legacy-флаг
ресурспаков и `ready`.

При выходе Windows-приложения `InstallationCoordinator.DisposeAsync()` отменяет и ожидает активную попытку.
Если она завершилась неуспешно или отменой при `PauseRequested == false`, сборка остаётся `installing`,
и следующий `StartAsync` возвращает её в очередь. Успешный результат записывается как `ready`, даже если
Dispose уже отменил токен; пауза по запросу пользователя сохраняется как `paused`.
Координатор отключает повторный запуск очереди до завершения процесса.
`InstanceStore.DisposeAsync()` ожидает всю цепочку фоновой очистки `.delete-*`; тесты освобождают coordinator и store
перед удалением временного корня.

Прогресс по UUID и `PropertyChanged` доступны для UI через координатор. Репорты одной стадии троттлятся при
изменении доли меньше 0,002 и интервале менее 150 мс; смена стадии и завершение проходят всегда. После запроса
паузы поздние репорты загрузок не заменяют `Pausing`. Уведомления приходят в потоке, который изменил состояние,
в том числе из фонового потока установки; обработчик не должен блокироваться или синхронно ждать UI-поток,
а WinUI должен маршалить обновления через
`DispatcherQueue`. Ключи состояния — `InstallationStateQueued`, `InstallationStateInstalling`,
`InstallationStatePaused`, `InstallationStateReady`, `InstallationStateFailed`; ключи стадий —
`InstallationStagePreparingMetadata`, `InstallationStagePreparingFiles`, `InstallationStageDownloading`,
`InstallationStagePreparingLibraries`, `InstallationStagePreparingAssets`, `InstallationStageComplete` и
`InstallationStagePausing` в русских и английских `Resources.resw`.
`GameInstance.ErrorCode` использует коды `mojang.http`, `mojang.unsupported`, `mojang.invalid`,
`mojang.integrity`, `storage.<error>` и `installation.cancelled`/`installation.unexpected`. Их отображение
задаёт `InstallationResourceKeys.ErrorResourceKey`; все ключи имеют русские и английские строки. Ошибки открытия
HTTP-потока установщика классифицируются как `mojang.http`, а несовпадение хеша или размера — как `mojang.integrity`.

`TryBeginLaunch` и `TryBeginContent` не бросают исключение, если реестр ещё не загружен или последняя загрузка
завершилась ошибкой: они возвращают `false` с `InstanceRegistryNotLoaded` или `InstanceRegistryUnavailable`.
После ошибки загрузки снимок `Instances` очищается, а `LoadError` сохраняет причину, чтобы потребители не использовали
устаревшие записи.

HTTP-запросы используют `User-Agent: Hako/<version>` и только HTTPS. Бинарные файлы передаются потоково
во временный файл рядом с целью, проверяются по размеру и SHA-1 и публикуются атомарно; тело ответа
считается зависшим после 60 секунд без данных, а поток обрывается при превышении известного `Size`.
Ошибка одной загрузки сразу отменяет остальные; после их завершения возвращается исходная не-отмена ошибка.
Одновременно выполняется не более четырёх загрузок. Проверенные файлы используются повторно,
повреждённые загружаются заново, а отмена удаляет временные файлы. MSTest используют Mojang JSON-фикстуры из
`macos/Hako/HakoTests/Fixtures/` и подменный `HttpMessageHandler`, без сетевых запросов.

## Каталог Fabric и Fabric API

`Minecraft/FabricClient.swift` читает Fabric Meta и только официальный Fabric API (`P7dR8mSH`) из Modrinth.
`prepare(minecraft:java:)` выбирает новейший release API, иначе beta/alpha, по точному Minecraft,
проверяет кэшированный JAR по SHA-512/SHA-1 и читает `fabric.mod.json` через `unzip -p` без распаковки.
`FabricClient.archiveEntry` ждёт `unzip` опросом `isRunning`: `waitUntilExit` крутит run loop текущего потока
и на потоках Swift Concurrency может не вернуться, оставив блокировку файловых операций сборки.
Версии Loader фильтруются по требованиям Loader/Java; рекомендуемая версия определяется флагом Meta `stable`.
Кэш расположен в `Library/Caches/Hako/FabricAPI`, папка сборки до сохранения не создаётся.
`FabricVersionPredicate` использует AND между термами строки и OR между строками массива; поддерживает
сравнения, диапазоны `~`/`^`, wildcard и prerelease. Modrinth запрашивается с User-Agent Hako.

## Установка Fabric

`MinecraftInstaller.install(..., fabric:)` принимает закреплённую `FabricConfiguration`.
Fabric profile/json проверяется по `inheritsFrom`, сохраняется отдельно в `minecraft/.hako-fabric.json`;
его SHA-1 возвращается координатору и хранится в сборке. Maven-библиотеки используют SHA-1 из профиля
или `.jar.sha1`, проходят общий установщик и располагаются в `minecraft/libraries`.
Для собственных сборок API устанавливается actor `InstanceContent`, общим для установщика и пользовательских операций.
`minecraft/.hako-mods.json` хранит происхождение внешних модов и `apiProvisioned`; повторная установка
не возвращает API после пользовательского удаления или отключения. Готовность включает все компоненты.

## Windows: Fabric

`windows/Hako.Core/Minecraft/FabricClient.cs` запрашивает Fabric Meta и использует минимальный
`windows/Hako.Core/Minecraft/ModrinthClient.cs` для версий официального проекта Modrinth `P7dR8mSH` и проверяемых
загрузок; этап каталога расширяет `ModrinthClient`. Запросы используют User-Agent Hako и только HTTPS.
Кэш API — `HakoPaths.CacheRoot/FabricAPI` (в production `%LOCALAPPDATA%\Hako\Cache\FabricAPI`). Загрузка
публикуется после проверки размера, SHA-1 и SHA-512. `fabric.mod.json` читается из JAR через `ZipArchive` с лимитом
1 МиБ; требования Loader и Java фильтруют список Meta, а первым выбирается стабильный Loader.

`InstanceCreationPage` до сохранения проверяет Minecraft/Java, API и Fabric-профиль выбранного Loader; выбор
Vanilla/Fabric и Loader является частью ключа проверки, а смена выбора отменяет устаревшие запросы. Очередь
передаёт сохранённый `FabricConfiguration` в `MinecraftInstaller.InstallFabricAsync`: профиль проверяется по
`inheritsFrom` и ID Loader, исходный JSON пишется в `minecraft/.hako-fabric.json`, SHA-1 закрепляется в сборке.
Maven-библиотеки профиля получают SHA-1 из JSON или `.jar.sha1` и загружаются общим проверяющим установщиком.
Fabric API проверяется по требованиям JAR и публикуется в `minecraft/mods`; установка завершится ошибкой,
если не готовы Minecraft, Java, Loader или API.

## Установка готового модпака

`InstallationCoordinator.modpacks` — общий `ModpackInstaller`. Перед установкой он сверяет сохранённый
план с Minecraft и Fabric-конфигурацией профиля; после `MinecraftInstaller.install` импортирует содержимое
модпака. Состояние ready выставляется только после обоих этапов. Пауза, ошибки и восстановление очереди
используют прежние состояния и блокировки. Сборки без плана проходят прежний путь.

`FabricConfiguration.api` необязателен: у модпака он отсутствует, Loader берётся точно из манифеста,
а Fabric API ставится только как файл самого пакета. Прежние конфигурации с API продолжают читаться;
создание своей Fabric-сборки сохраняет предварительную проверку и автоматическую установку API.
Состав, кэш и защита архивов описаны в [[Minecraft/Modrinth]].
