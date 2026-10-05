# Minecraft Launching

Parent: [[Index]]

## Назначение и источники

`macos/Hako/Hako/Minecraft/MinecraftLaunchPlan.swift` — построение команды и offline-идентичность.
`macos/Hako/Hako/Instances/GameLaunchCoordinator.swift` — общий координатор подготовки и состояния игр.
`macos/Hako/Hako/Minecraft/GameProcessRunner.swift` — процессы, журнал и восстановление.
`macos/Hako/Hako/Minecraft/JavaLaunchValidation.swift` — проверка исполняемой Java.

## Контракты и поток

`MinecraftLaunchPlan.build` возвращает executable, массив аргументов и рабочий каталог `minecraft/`;
не выполняет авторизацию, запуск и сетевые запросы. Читает современные `arguments` с OS/arch/features,
`default-user-jvm`, legacy `minecraftArguments`, `mainClass` и logging. Обязательные JVM/game аргументы
сохраняются во всех режимах. Mojang добавляет `default-user-jvm`, global/custom — пользовательские строки.
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

## Процессы и журнал

`GameProcessRunner` использует Foundation `Process`, без shell. Stdout/stderr направлены непосредственно
в `minecraft/logs/hako-launch.log` (новый файл — 0600); pipe с жизнью лаунчера не используется.
Закрытие Hako не завершает Minecraft. Команда с токенами не записывается в диагностику Hako.

`minecraft/.hako-running.json` хранит UUID сборки, PID и точное время рождения процесса из BSD.
Восстановление сверяет PID и время, поэтому устаревшая запись не блокирует новый запуск при повторном
использовании PID. `DispatchSourceProcess` наблюдает завершение восстановленной игры.
Удаление записи сверяет её принадлежность завершающемуся процессу. Ошибка одной папки не останавливает
восстановление статуса других сборок.

## Проверка

Swift Testing проверяет официальный JSON для современных и legacy версий, ARM/Intel,
рекомендуемые JVM-флаги, classpath, подстановки, окно, приоритет памяти и offline-UUID.
Подменные auth-зависимости проверяют refresh token, истечение и объединение параллельных запросов.
Контролируемые процессы проверяют повторное нажатие, восстановление, переименование и код завершения;
подменные executable Java проверяют версию, архитектуру, права запуска и таймаут при игнорировании SIGTERM.
