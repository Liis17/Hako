# Minecraft Launching

Parent: [[Index]]

## Назначение и источники

`macos/Hako/Hako/Minecraft/MinecraftLaunchPlan.swift` — построение команды и offline-идентичность.
`macos/Hako/Hako/Instances/GameLaunchCoordinator.swift` — общий координатор подготовки и состояния игр.
`macos/Hako/Hako/Instances/QuickLaunchCoordinator.swift` — запуск по UUID из системных точек входа ([[UI/SystemIntegration]]).
`macos/Hako/Hako/Minecraft/GameProcessRunner.swift` — процессы, журнал и восстановление.
`macos/Hako/Hako/Minecraft/GameAppBundle.swift` — обёртка `.app` для игрового режима macOS.
`macos/Hako/Hako/Minecraft/JavaLaunchValidation.swift` — проверка исполняемой Java.
`macos/Hako/Hako/Playtime/PlaytimeJournal.swift` — журнал сессии и идентичность процесса.
`macos/Hako/Hako/Playtime/PlaytimeHelperClient.swift` — подключение фонового помощника.
`macos/Hako/HakoPlaytimeHelper/main.swift` — отдельный CLI-наблюдатель длительности игры.

## Контракты и поток

`MinecraftLaunchPlan.build` возвращает executable, массив аргументов и рабочий каталог `minecraft/`;
не выполняет авторизацию, запуск и сетевые запросы. Читает современные `arguments` с OS/arch/features,
`default-user-jvm`, legacy `minecraftArguments`, `mainClass` и logging. Обязательные JVM/game аргументы
сохраняются во всех режимах. Mojang добавляет `default-user-jvm`, global/custom — пользовательские строки.
`argumentTemplates` формирует те же группы до подстановки аккаунта и путей; настройки показывают
их через `LaunchArguments.format`, сохраняя кавычки, правила версии и фактический предел heap.
Classpath содержит клиент и разрешённые библиотеки; legacy native classifiers извлечены установщиком
и в classpath не попадают. Virtual/map-to-resources assets получают соответствующие пути.
Аргументы идентификации, каталогов и окна задаются лаунчером; дублирующие пользовательские game-флаги
отклоняются с понятным сообщением. [[Data/Persistence]] описывает нормализацию heap и настройки.

`MinecraftLaunchIdentity.offline(name:)` проверяет ник и формирует Java-compatible UUID v3 из
`OfflinePlayer:<name>`; регистр ника значим. Offline-сессия использует токен `0` и тип `legacy`,
не вызывает Microsoft/Minecraft auth и не заменяет автоматически неудачный онлайн-запуск.

`GameLaunchCoordinator.launch` требует готовой сборки, отсутствия другого запуска этой сборки
и offline-ника либо действительной [[Auth/MicrosoftAuth|Minecraft-сессии]]. Снимок настроек берётся
при нажатии, дальнейшее редактирование действует на следующий запуск. `MinecraftLaunchRequest` —
Sendable-снимок для подготовки вне главного потока. Подготовка проверяет SHA-1 metadata и asset index,
Java и наличие клиента; перед онлайн-стартом повторно проверяется неизменность сессии.
Java из непустого глобального пути используется только в режиме `global`; прочие режимы используют
Java сборки. Ошибка выбранной Java не включает скрытый fallback.

`JavaLaunchValidation.validate` вызывает `-XshowSettings:properties -version` вне главного потока,
проверяет права запуска, minimum major и native архитектуру; таймаут проверки — 10 секунд.
Весь probe выполняется на одном фоновом потоке. Вывод направлен во временный файл 0600,
чтение ограничено 64 КиБ; timeout/cancel принудительно завершает собственный процесс проверки.
Открытый потомком stdout не удерживает подготовку запуска.
`JAVA_TOOL_OPTIONS`, `_JAVA_OPTIONS`, `JDK_JAVA_OPTIONS` удалены из среды дочернего процесса,
чтобы не переопределять лимит памяти и команду.

Состояния игры (`GameRunState`) — preparing/running/failed, независимы от установки.
Разные сборки запускаются независимо. `InstanceStore.launchBusy` блокирует переименование во время
подготовки/игры и повторную установку. Нормальное завершение освобождает сборку; ненулевой код
показывается как ошибка с доступом к журналу.

`GameLaunchCoordinator.launchAndWait` использует ту же попытку, что обычная кнопка «Играть»,
объединяет ожидание уже начатой подготовки и возвращается после старта процесса, не после завершения игры.
Ошибку подготовки или старта передаёт вызывающему коду; обычный `launch` сохраняет прежний контракт.

## Процессы и журнал

`GameProcessRunner` использует Foundation `Process`, без shell. Stdout/stderr направлены непосредственно
в `minecraft/logs/hako-launch.log` (новый файл — 0600); pipe с жизнью лаунчера не используется.
Закрытие Hako не завершает Minecraft. Команда с токенами не записывается в диагностику Hako.

Java запускается не напрямую, а из `.hako-game/Minecraft.app` в корне сборки: `GameAppBundle.prepare`
пересоздаёт его перед каждым стартом. `Contents/MacOS/java` — копия выбранной Java (сборки или глобальной),
`Contents/lib` — symlink на `<JAVA_HOME>/lib` (`bin/java` грузит `libjli` через `@loader_path/../lib`,
JAVA_HOME определяется по realpath библиотеки). `Info.plist` задаёт `LSApplicationCategoryType`
`public.app-category.games`, `GCSupportsGameMode` и `LSSupportsGameMode`: macOS берёт категорию из главного
bundle процесса, у голого `java` его нет, и игровой режим не включается. Ключи в plist самого Hako
на дочерний процесс не действуют. Исходная подпись `java` не покрывает `Info.plist`, и AMFI убивает процесс
(SIGKILL, код 9), поэтому bundle переподписывается ad-hoc через `/usr/bin/codesign` без hardened runtime —
иначе library validation отклонит `libjli` с чужим Team ID. Проверка Java выполняется на исходном файле.

`minecraft/.hako-running.json` хранит UUID сборки, PID, точное время рождения процесса из BSD и
необязательный `sessionID`; прежний JSON без сессии продолжает читаться.
Восстановление сверяет PID и время, поэтому устаревшая запись не блокирует новый запуск при повторном
использовании PID. `DispatchSourceProcess` наблюдает завершение восстановленной игры.
Удаление записи сверяет её принадлежность завершающемуся процессу. Ошибка одной папки не останавливает
восстановление статуса других сборок.

## Учёт игрового времени

`GameLaunchCoordinator` получает общий `PlaytimeCoordinator` ([[Data/Persistence]]).
Перед подготовкой запуска создаётся сохранённая нулевая сессия с владельцем по XUID либо гостем;
ошибка подготовки или `Process.run()` не начисляет время. Offline-mode при выполненном входе
принадлежит текущему аккаунту, ник offline-player не является владельцем статистики.
Игра после запуска сохраняет владельца даже при выходе или смене аккаунта. Гостевые сессии,
включая ещё подготавливаемые, переходят аккаунту при следующем успешном входе.

`GameProcessRunner` после старта Java создаёт `PlaytimeJournal` и через `PlaytimeHelperClient`
запускает встроенный `Contents/MacOS/HakoPlaytimeHelper`, передавая только путь к журналу.
Помощник не получает токены и не открывает SwiftData. Он подтверждает подключение своей BSD-идентичностью
в журнале, наблюдает PID/время рождения Java через `DispatchSourceProcess` и пишет длительность каждые
10 секунд и при завершении. `systemUptime` исключает сон Mac и изменение календарных часов;
меню, пауза и AFK считаются. Разные сборки начисляют время независимо, в том числе одновременно.
Отсутствие или ошибка запуска помощника завершает только Java, созданную этой попыткой.

Журналы лежат в `~/Library/Application Support/Hako/Playtime/sessions/<UUID>.json`, вне папок сборок.
JSON заменяется атомарно; `flock` на `<UUID>.lock` разрешает одного писателя. Помощник продолжает
работать после закрытия Hako. Родитель фиксирует быстро завершившуюся игру, если помощник ещё
не получил блокировку; одновременно два процесса журнал не пишут. Ошибку записи помощник повторяет
на следующем тике, после завершения игры сохранённая в памяти длительность больше не растёт.
Если Java завершилась до подключения помощника, он сохраняет последнюю контрольную точку,
не прибавляя задержку подключения.
Если Hako уже сохранил время и удалил итоговый журнал во время подключения, клиент помощника
считает это завершением только при отсутствии Java с исходными PID и временем рождения.

При открытии Hako, каждые 10 секунд и при завершении игры `PlaytimeCoordinator.reconcile()` читает
сохранённые длительности и начисляет только ещё не учтённую часть. Итоговый журнал удаляется после
сохранения счётчиков и удаления сессии; ошибка сохранения оставляет файл для повтора.
Файл, оставшийся после сохранённого удаления сессии, очищается без начисления.
Повторное чтение и перезапуск не дублируют время. Восстановление использует
существующую сессию и живого помощника. Прежняя работающая игра без `sessionID` учитывается с
момента подключения трекера, за текущим аккаунтом или гостем; историческое время не восстанавливается.
После аварийного выключения Mac или потери обоих процессов учитывается последняя контрольная точка.

## Проверка

Swift Testing проверяет официальный JSON для современных и legacy версий, ARM/Intel,
рекомендуемые JVM-флаги, classpath, подстановки, окно, приоритет памяти и offline-UUID.
Подменные auth-зависимости проверяют refresh token, истечение и объединение параллельных запросов.
Контролируемые процессы проверяют повторное нажатие, восстановление, переименование и код завершения;
подменные executable Java проверяют версию, архитектуру, права запуска и таймаут при игнорировании SIGTERM.
Тесты времени проверяют миграцию store, перенос гостевых сессий, независимые счётчики, ошибки сохранения,
сон через подменный uptime, восстановление, короткие процессы и работу помощника после выхода его родителя.

## Fabric

`MinecraftLaunchRequest` содержит неизменяемую конфигурацию Loader и SHA-1 локального профиля.
Подготовка проверяет `minecraft/.hako-fabric.json`, родительскую Minecraft-версию, выбранный Loader
и наличие его библиотек; сетевые API для запуска не нужны. Неизвестный загрузчик или отсутствующий
профиль дают ошибку, автоматического запуска Vanilla нет.
`MinecraftLaunchPlan.build` и `argumentTemplates` принимают один `FabricProfile`: заменяют mainClass,
добавляют обязательные аргументы для всех режимов и разрешают конфликты Maven-библиотек в пользу Fabric
по group/artifact/classifier. Клиентский JAR, assets, Java и natives принадлежат базовому Mojang manifest.
`version_name` использует ID дочернего профиля; аргументы JSON не разбиваются по пробелам.
Предпросмотр настроек использует тот же профиль и механизм аргументов.

## Windows-клиент

Код — `windows/Hako.Core/Minecraft/` и `windows/Hako.Core/Instances/GameLaunchCoordinator.cs`; поведение повторяет macOS,
отличия ниже. Сессия онлайн-запуска — [[Auth/MicrosoftAuth#Обновление сессии для онлайн-запуска]].

| Файл | Символ | Роль |
|------|--------|------|
| `Minecraft/MinecraftLaunchPlan.cs` | `MinecraftLaunchPlan.Build`, `ArgumentTemplates`, `MinecraftLaunchIdentity`, `MinecraftLaunchProfile` | Аргументы, classpath, offline-идентичность; `MinecraftLaunchProfile` — точка расширения для Fabric-профиля |
| `Minecraft/LaunchArguments.cs` | `LaunchArguments`, `JavaMemoryPolicy` | Разбор аргументов без shell, единый `-Xmx`, политика памяти |
| `Minecraft/JavaLaunchValidation.cs` | `JavaLaunchValidation` | Проба `-XshowSettings:properties -version` |
| `Minecraft/Win32Process.cs`, `GameProcessRunner.cs`, `JavaAnsiEncoding.cs` | `GameProcessRunner`, `GameProcessRecord`, `JavaAnsiEncoding` | Запуск, `@argfile`, `.hako-running.json`, восстановление |
| `Instances/GameLaunchCoordinator.cs` | `GameLaunchCoordinator` | Порядок запуска, `GameRunState`, события сессий |

Для Fabric `GameLaunchCoordinator` читает исходный профиль из `minecraft/.hako-fabric.json`, проверяет SHA-1 из
`GameInstance.FabricProfileSHA1`, родительскую версию и `fabric-loader-{loader}-{minecraft}` ID. Отсутствующий,
неизвестный или повреждённый профиль и отсутствующая Maven-библиотека завершают запуск ошибкой без перехода
на Vanilla. `MinecraftLaunchPlan` использует Fabric `mainClass`, дочерний ID для `version_name` и обязательные
аргументы профиля во всех режимах; Maven-конфликты group/artifact/classifier разрешаются в пользу Fabric.
Профиль и библиотеки читаются локально, сетевые запросы при запуске не выполняются.

- **Аргументы.** Правила OS/arch/features вычисляются для закреплённой архитектуры сборки (`${arch}` в natives тоже).
  Пользовательские game-флаги, которыми управляет лаунчер (`--username`, `--gameDir`, `--width` и др.), отклоняются
  (`LaunchReservedGameArgument`). Все `-Xmx`/`-XX:MaxHeapSize` заменяются одним лимитом; начальный heap больше лимита уменьшается.
- **Память.** `JavaMemoryPolicy`: от 512 МиБ до физической памяти, шаг 256 МиБ; для x86-Java предел 1536 МиБ;
  `Warns` — больше 70 % физической памяти (`LaunchMemoryWarning`).
- **Java.** Глобальный `JavaPath` используется только при источнике аргументов `Global`; иначе `java/<javaExecutable>`
  сборки (`bin/javaw.exe`). Отсутствующий файл → `LaunchJavaMissing`, скрытой подмены нет. Проба: таймаут 10 с,
  major и `os.arch` против закреплённой архитектуры, вывод во временный файл (чтение ≤ 64 КиБ), kill только своей пробы,
  среда без `JAVA_TOOL_OPTIONS`, `_JAVA_OPTIONS`, `JDK_JAVA_OPTIONS` (их нет и в среде игры).
- **Проверки перед запуском.** SHA-1 metadata (`MetadataSHA1`), asset index, размер и SHA-1 клиента; Minecraft-сессия
  проверяется до подготовки и ещё раз перед стартом (`LaunchSessionChanged`).
- **Процесс.** `CreateProcessW` без shell и pipe, `CREATE_NO_WINDOW`, рабочий каталог `minecraft/`; stdout/stderr —
  наследуемый handle `minecraft/logs/hako-launch.log`, наследование ограничено им через `PROC_THREAD_ATTRIBUTE_HANDLE_LIST`
  (иначе игра, переживающая Hako, удержала бы чужие handle, например pipe параллельной пробы Java). Игра переживает Hako.
  Известное ограничение: пока handle лога помечен наследуемым, параллельный `Process.Start` без списка handle
  (например, проба Java) может унаследовать его на время жизни своего процесса.
- **Длина командной строки.** Java ≥ 9: аргументы в `minecraft/.hako-launch/<id>.args` по синтаксису Java argfiles
  (значения с пробелами, `"`, `\`, `'` или `#` берутся в двойные кавычки); файл удаляется после выхода Java, сироты —
  при восстановлении. Java 8 — без argfile. Строка `CreateProcessW` ограничена 32767 (`LaunchCommandLineTooLong`).
- **Кодировка аргументов** (`JavaAnsiEncoding`). `java.exe` — ANSI-программа: командную строку читает через
  `GetCommandLineA`, argfile — сырыми байтами в `sun.jnu.encoding`, который на Windows остаётся ANSI и после JEP 400.
  Поэтому для любой Java argfile кодируется в `GetACP()` строгим `WideCharToMultiByte(WC_NO_BEST_FIT_CHARS)`
  (при ACP 65001 — UTF-8 без BOM), а для Java 8 той же проверке подлежит командная строка. Абсолютный путь
  (весь аргумент, значение `-D…=`, `@file`, элементы списка через `;`), непредставимый в ACP, заменяется 8.3-именем
  `GetShortPathNameW`; если его нет (8.3 отключены на томе) или остался иной непредставимый символ —
  `LaunchArgumentFileEncoding`. Best-fit-замены (`?`, похожие буквы) не допускаются.
- **`.hako-running.json`.** Ключи как у Swift `GameProcessRecord`: `instanceID`, `pid`, `startSeconds`/`startMicroseconds`
  (Unix-время создания из `GetProcessTimes`), необязательный `sessionID`. Восстановление сверяет PID и время создания;
  запись удаляет только наблюдатель того же запуска. Ожидание выхода — `RegisterWaitForSingleObject`, без занятого потока.
- **`GameLaunchCoordinator`.** Требует `Ready` и lease `InstanceStore.TryBeginLaunch`; `GameRunState`
  `Preparing`/`Running`/`Failed` (ненулевой код выхода → `LaunchGameExitCode` с путём лога). `RestoreRunningGamesAsync`
  берёт lease до восстановления; повреждённая запись одной сборки даёт `Failed` (`LaunchProcessRecordInvalid`) и не
  мешает остальным. `GameSessionStarted`/`GameSessionEnded` (instance, record с `sessionID`, код выхода) — точка
  расширения учёта времени, срабатывает и для восстановленных игр. События приходят с фоновых потоков.
- **Помощник времени.** `Hako.exe --playtime-helper <journal> [pid]` распознаётся в `Program.Main` до WinUI; пока это
  прототип: берёт `pid`, `startSeconds`/`startMicroseconds` из журнала (корень или `process`), ждёт процесс только при
  совпадении PID и времени создания (`GameProcessRunner.TryWaitForRecordedProcess`) и пишет `<journal>.finished`.
