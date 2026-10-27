# MicrosoftAuth

Parent: [[Index]]

## Назначение

Вход через Microsoft device code flow: приложение показывает код, пользователь вводит его
на microsoft.com/link, приложение получает токены, профиль Xbox (gamertag, XUID, аватар),
email и, если доступен, профиль Minecraft. Без Minecraft вход всё равно завершается —
аккаунт остаётся только Microsoft. Хранение результата — [[Data/Persistence]],
экран — [[UI/Screens#LoginView|LoginView]].

## Источники

| Файл | Значимый символ | Роль |
|------|-----------------|------|
| `macos/Hako/Hako/Auth/MicrosoftAuth.swift` | `MicrosoftAuth`, `MicrosoftAuthError`, `DeviceCode`, `MicrosoftToken`, `XboxProfile`, `MinecraftProfile`, `MinecraftSession` | Сетевые запросы цепочки входа и ошибки |
| `macos/Hako/Hako/Views/LoginView.swift` | `LoginView.signIn()` | Оркестрация входа и сохранение результата |
| `macos/Hako/Hako/Auth/MinecraftSessionCoordinator.swift` | `MinecraftSessionCoordinator.connect` | Общая сессия, refresh token, срок действия и повтор подключения |
| `macos/Hako/Hako/Auth/MinecraftProfileAPI.swift` | `MinecraftProfileAPIClient` | Чтение Java-профиля, загрузка PNG-скина и выбор или снятие плаща |
| `macos/Hako/Hako/Auth/MinecraftCharacterEditorCoordinator.swift` | `MinecraftCharacterEditorCoordinator` | Черновик, локальный предпросмотр, сверка отдельных записей и сохранение профиля |

## Публичные контракты

| Контракт | Поведение и условия |
|----------|---------------------|
| `MicrosoftAuth.clientID` | Client ID приложения Azure (Entra ID) проекта, задан в коде. Это публичный идентификатор, не секрет: он и так попадает в бинарник приложения. Пустое значение → `MicrosoftAuthError.missingClientID` |
| `requestDeviceCode() async throws -> DeviceCode` | POST `login.microsoftonline.com/consumers/oauth2/v2.0/devicecode`, `scope=XboxLive.signin openid profile email offline_access`. Возвращает `userCode`, `deviceCode`, `verificationUri`, `expiresIn`, `interval` |
| `waitForToken(_:) async throws -> MicrosoftToken` | Опрос `/consumers/oauth2/v2.0/token` (`grant_type=urn:ietf:params:oauth:grant-type:device_code`) каждые `interval` секунд до `expiresIn`. `authorization_pending` — ждать, `slow_down` — +5 с, `authorization_declined` → `.declined`, `expired_token` или дедлайн → `.codeExpired`. Отмена задачи прерывает опрос (`CancellationError` из `Task.sleep`) |
| `MicrosoftToken.email` | `email` или `preferred_username` из payload `id_token` (base64url JWT, подпись не проверяется — только для отображения). `nil`, если `id_token` нет |
| `MinecraftProfile` | `uuid`, `name`, `skinURL?`, `skinVariant: MinecraftSkinVariant?`, `skinID?`; вариант используется для ширины рук в 3D-превью, ID приходит из Java Profile API |
| `refresh(_:) async throws -> MicrosoftToken` | `grant_type=refresh_token`, `scope=XboxLive.signin offline_access`. Ответ содержит новый refresh token |
| `signIn(with:) async throws -> (XboxProfile, MinecraftSession?)` | Xbox Live → профиль Xbox → Minecraft. Ошибки Xbox прерывают вход; **любая** ошибка шага Minecraft даёт `nil`. После шага Minecraft проверяет отмену задачи |
| `signInToMinecraft(with:) async throws -> MinecraftSession` | Xbox Live → Minecraft для уже сохранённого аккаунта; вызывается после `refresh` из `MinecraftSessionCoordinator` |

## Поток

1. Xbox Live: `user.auth.xboxlive.com/user/authenticate` — `AuthMethod: RPS`, `RpsTicket: d=<токен Microsoft>` → пользовательский токен.
2. Профиль Xbox: XSTS `xsts.auth.xboxlive.com/xsts/authorize` с `RelyingParty: http://xboxlive.com` →
   `xid` (XUID) и `gtg` (gamertag) из `DisplayClaims.xui`. Аватар — GET
   `profile.xboxlive.com/users/me/profile/settings?settings=GameDisplayPicRaw`
   (`Authorization: XBL3.0 x=<uhs>;<xsts>`, `x-xbl-contract-version: 3`); ошибка аватара → `avatarURL = nil`.
3. Minecraft: XSTS с `RelyingParty: rp://api.minecraftservices.com/` →
   `api.minecraftservices.com/authentication/login_with_xbox` (`identityToken: XBL3.0 x=<uhs>;<xsts>`) →
   GET `/minecraft/profile` с `Bearer` → `id` (UUID без дефисов), `name`, `skins`.
   Скин — `ACTIVE` из `skins` (иначе первый); ссылку textures.minecraft.net переводят на https,
   потому что App Transport Security не пропускает http. `MinecraftProfileResponse.minecraftProfile`
   также разбирает `variant` активного скина (`CLASSIC` / `SLIM`); отсутствующее или неизвестное
   значение даёт `nil`, не прерывая вход. Вариант сохраняется через `Account.connect(_:)`
   ([[Data/Persistence]]).

### Редактор персонажа

Кнопка «Изменить персонажа» доступна только при действующей Java-сессии и сохранённых UUID/нику.
`MinecraftProfileAPIClient` читает `GET /minecraft/profile` вместе со всеми принадлежащими аккаунту плащами,
отправляет PNG через `POST /minecraft/profile/skins` (`variant=classic|slim`) и выбирает/снимает плащ
через `PUT`/`DELETE /minecraft/profile/capes/active`. У вызовов записи нет токенного тела или журналирования.
Смена варианта модели закрепляет проверенные PNG-байты текущего скина в черновике, как импорт:
предварительное чтение сервера обновляет базовый профиль, но не подменяет текстуру предпросмотра и загрузки.
Возврат к исходному варианту без изменения пикселей не требует записи скина.

Один 401 приводит к refresh Minecraft-сессии и ровно одной повторной попытке, только если обновлён Minecraft
access token. 403 и 429 не запускают refresh. `MinecraftCharacterEditorCoordinator` пишет только реально
изменённые части, сначала скин, затем плащ. При ошибке или неподтверждённом результате скина
сохранение останавливается после сверки: черновик плаща остаётся до следующего Save. Отката нет.
После каждой записи профиль читается заново: скин подтверждается
по активному ID, варианту и совпадающим нормализованным пикселям, плащ — по активному ID. Таймаут или неудачная
сверка помечают результат неизвестным; следующий Save сначала снова читает сервер. Успешная часть сохраняется
и остаётся подтверждённой при ошибке второй. Ошибка SwiftData после удалённого успеха оставляет локальное
сохранение ожидающим, поэтому повтор не загружает тот же скин повторно.
Редактор и обновление сессии сохраняют `ModelContext` изменяемого `Account`, в том числе когда
SwiftUI передаёт ту же запись из отдельного контекста с выключенным autosave.

`MinecraftSessionCoordinator.refreshAfterUnauthorized` требует новый Minecraft access token и объединяет
параллельный refresh. Поздний 401 проверяется поколением до refresh и не может зарегистрировать
старый объект аккаунта вместо текущего. Поколение редактора отбрасывает поздние ответы
при смене аккаунта/выходе, а ревизия
[[UI/MinecraftSkin|авторитетного внешнего вида]] не даёт старому refresh перезаписать профиль после успешного
изменения. Новых полей SwiftData и изменений токенного хранилища нет.

`LoginView.signIn()` сохраняет токены в Keychain и читает журналы игрового времени до вставки `Account`.
Затем `PlaytimeCoordinator.transferGuest(to:)` сохраняет аккаунт вместе с переносом гостевых счётчиков
и владельца гостевых сессий одной операцией ([[Data/Persistence]]). Ошибка записи прерывает вход;
повторный вход не дублирует уже перенесённое время.

## Ошибки

`MicrosoftAuthError: LocalizedError` — русские сообщения для экрана входа и профиля:

| Случай | Источник |
|--------|----------|
| `.missingClientID` | Пустой `clientID` |
| `.oauth(String)` | `error_description` из ответа Microsoft (например, `AADSTS…` при неверном Client ID) |
| `.codeExpired`, `.declined` | Ответы опроса токена |
| `.xbox(code:)` | HTTP 401 от Xbox/XSTS с `XErr`: 2148916233 — нет профиля Xbox, 2148916235 — регион, 2148916236/37 — подтверждение возраста, 2148916238 — детский аккаунт |
| `.appNotApproved` | HTTP 403 от `login_with_xbox` |
| `.noMinecraft` | HTTP 404 от `/minecraft/profile` |
| `.unexpectedResponse(status:)` | Прочие HTTP-статусы |

Сетевые ошибки `URLSession` показываются как есть (системное локализованное описание).
При входе ошибки шага Minecraft не показываются — `signIn` их поглощает; ошибку автоподключения
показывает вкладка профиля.

## Ограничения и важные детали

- Приложение Azure должно поддерживать личные аккаунты Microsoft и иметь включённые
  public client flows (device code flow не использует секрет клиента).
- Endpoint `devicecode` принимает `XboxLive.signin` вместе с OIDC-scope `openid profile email`
  (проверено запросом). Наличие `email` в `id_token` проверяется только настоящим входом.
- Mojang пропускает в Minecraft API только одобренные Client ID (заявка: aka.ms/mce-reviewappid).
  Client ID проекта одобрен Mojang (подтверждено владельцем проекта). Ответ 403 от `login_with_xbox`
  (`.appNotApproved`) означает отзыв или неверный Client ID.
- Device code flow не использует секрет клиента; секреты приложения Azure в репозиторий не добавляются.
- Исходящие соединения в песочнице разрешены настройкой `ENABLE_OUTGOING_NETWORK_CONNECTIONS = YES`
  (entitlement `com.apple.security.network.client`).
- Код модуля изолирован на главном акторе (`SWIFT_DEFAULT_ACTOR_ISOLATION = MainActor`);
  сетевые вызовы — асинхронные `URLSession.shared.data(for:)`.

## Общая Minecraft-сессия

`MinecraftSessionCoordinator` — @MainActor @Observable сервис на приложение; `ContentView` вызывает
`monitor` для текущего аккаунта. `identity(for:)` возвращает сессию только до срока истечения токена
и при наличии Minecraft UUID/ника. Microsoft/Xbox-профиль без Minecraft-токена запуск не разрешает.
`connect` повторно использует действительный токен; за 60 секунд до истечения обновляет его через
существующую цепочку auth. Параллельные запросы одного XUID объединены в одну задачу.
В памяти сессия удерживает `Account` и сравнивает его актуальный persistent ID: ссылка учитывает
замену временного ID при первом сохранении, а сравнение позволяет читать ту же запись из отдельного
SwiftUI ModelContext. Замена записи того же XUID отменяет старые задачи; запрос 401 не может
самостоятельно изменить эту привязку.
Обновлённый Microsoft refresh token сохраняется до запроса Minecraft, даже если тот неуспешен.

Проверка срока выполняется каждые 30 секунд. После ошибки auth фоновый цикл не повторяет сетевые
запросы бесконечно; явный `connect(force: true)` из профиля повторяет подключение.
Выход отменяет задачи и удаляет кэш сессий; проверка отмены не позволяет завершившемуся запросу
восстановить удалённый аккаунт. Старые поколения задач не освобождают слот нового подключения.
Токены остаются только в Keychain и памяти; сервис не пишет их в журнал.

## Windows-клиент

Реализация — `windows/Hako.Core/MicrosoftDeviceAuth.cs` (`MicrosoftDeviceAuth` реализует `IMicrosoftAuthService`),
состояние входа ведёт `AppCoordinator`. Этот провайдер остаётся источником лицензированной Minecraft-сессии;
Hako Telegram auth хранится отдельно и не меняет Microsoft `AccountSession`, XUID или перенос гостевого времени
([[Auth/HakoAuth]]). Экраны — [[UI/Screens#Windows-клиент]], хранение результата —
[[Data/Persistence#Windows-клиент]]. Client ID тот же, что и в macOS (`MicrosoftDeviceAuth.ClientId`),
поэтому статус одобрения Mojang для него — как в разделе «Ограничения и важные детали» выше.

### Источники

| Файл | Значимый символ | Роль |
|------|-----------------|------|
| `windows/Hako.Core/MicrosoftDeviceAuth.cs` | `MicrosoftDeviceAuth`, `ClassifyXboxError`, `ReadDisplayEmail` | Запросы цепочки входа, опрос токена, классификация ошибок |
| `windows/Hako.Core/AuthContracts.cs` | `IMicrosoftAuthService`, `MicrosoftToken`, `AuthErrorKind`, `AuthServiceException`, `SignInPhase` | Контракт сервиса, типы ошибок и фаз |
| `windows/Hako.Core/DeviceCode.cs` | `DeviceCode` | Код пользователя, URI, срок и интервал опроса |
| `windows/Hako.Core/AppCoordinator.cs` | `AppCoordinator.BeginSignIn`, `RetrySignIn`, `BackFromSignIn`, `CancelSignIn`, `LogoutAsync` | Попытки входа, отмена, запись аккаунта и выход |
| `windows/Hako/LoginPage.xaml.cs` | `LoginPage` | Панели по фазе, копирование кода, открытие страницы входа |
| `windows/Hako.Tests/MicrosoftDeviceAuthTests.cs`, `windows/Hako.Tests/AppCoordinatorTests.cs` | — | Протокол опроса, Xbox/Minecraft-шаги, гонки отмены и записи |

### Публичные контракты

| Контракт | Поведение и условия |
|----------|---------------------|
| `RequestDeviceCodeAsync(ct)` | POST `login.microsoftonline.com/consumers/oauth2/v2.0/devicecode`, `scope=XboxLive.signin openid profile email offline_access`. Ответ без `device_code` или `user_code`, с не-https `verification_uri` либо с `expires_in <= 0` → `InvalidResponse`. Интервал опроса не меньше 1 с |
| `WaitForTokenAsync(deviceCode, ct)` | Опрос `/consumers/oauth2/v2.0/token` с `grant_type=urn:ietf:params:oauth:grant-type:device_code`. `authorization_pending` — ждать; `slow_down` — интервал +5 с; `authorization_declined` и `access_denied` → `AuthorizationDeclined`; `expired_token` или истёкший срок → `CodeExpired`; прочие ошибки → `MicrosoftRejected` с HTTP-статусом. Срок проверяется до и после задержки; запрос после срока не отправляется |
| `MicrosoftToken.Email` | `email` или `preferred_username` из payload `id_token`; подпись не проверяется, только для отображения (как в macOS-клиенте) |
| `MicrosoftToken.ToString()` | Выводит только `Email`; access и refresh token в строку не попадают |
| `SignInAsync(token, ct)` | Возвращает `AccountSession`; обязательные и необязательные шаги перечислены ниже |

### Шаги входа

1. Xbox Live: POST `user.auth.xboxlive.com/user/authenticate`, `RpsTicket = d=<access token Microsoft>`,
   `RelyingParty = http://auth.xboxlive.com` — **обязательно**.
2. XSTS `http://xboxlive.com` (`xsts.auth.xboxlive.com/xsts/authorize`) → `xid`, `gtg` и `uhs` из `DisplayClaims.xui`.
   Пустое значение любого из них → `InvalidResponse` — **обязательно**.
3. Аватар: GET `profile.xboxlive.com/users/me/profile/settings?settings=GameDisplayPicRaw` с `XBL3.0 x=<uhs>;<токен>`.
   **Необязательно**: любая ошибка (сеть, таймаут, HTTP-статус, JSON, исключение) даёт `null`; принимается только https-ссылка.
4. Minecraft: XSTS `rp://api.minecraftservices.com/` → `api.minecraftservices.com/authentication/login_with_xbox`
   (`identityToken`) → GET `/minecraft/profile` с `Bearer`. **Необязательно**: любая ошибка шага (XSTS с `XErr`, любой
   HTTP-статус из этих трёх запросов, пустой `uhs`, пустой или некорректный JSON, сеть, таймаут) даёт пустые поля Minecraft,
   вход продолжается с профилем Xbox. Причина не сохраняется и не показывается. Исключение выходит наружу только при отмене
   нашим `CancellationToken`. `MinecraftTokenExpiresAt` = момент получения + `expires_in`.
5. Перед `AccountSessionStore.SaveAsync` `AppCoordinator` фиксирует на ledger границу ожидающего переноса гостевого
   времени. `session.dat` сохраняется только если попытка ещё актуальна; после успешной записи перенос применяется
   одной атомарной операцией, только для сохранённого целевого XUID и зафиксированных на границе сумм/сессий.
   При ошибке или отмене запись переноса удаляется; при сбое между фазами решение восстанавливается при запуске
   по текущему `session.dat` ([[Data/Persistence#Игровое время Windows]]).

Коды `XErr` шагов 1–2 классифицируются: `2148916233` → `XboxProfileMissing`, `2148916235` → `XboxUnavailable`,
`2148916236` и `2148916237` → `XboxAgeVerification`, `2148916238` → `XboxChildAccount`, остальные → `XboxRejected`.

### Фазы и экраны входа

`AppCoordinator` задаёт `SignInPhase`: `RequestingCode` → `WaitingForCode` (код показан) → `SigningIn`
(Xbox, профиль, запись). Успех: `Account` заполнен, экран `Builds`. Ошибка: `Failed` и `LoginError`.
`BeginSignIn` запоминает экран возврата: `Welcome`, либо `Settings` → «Профиль», если вход начат из настроек.
`BackFromSignIn` возвращает на него, `RetrySignIn` запускает новую попытку.

### Ошибки

`AuthErrorKind.ResourceKey()` даёт ключ строки из `Resources.resw`: `AuthNetworkError`, `AuthInvalidResponse`,
`AuthCodeExpired`, `AuthDeclined`, `AuthMicrosoftRejected`, `XboxProfileMissing`, `XboxUnavailable`,
`XboxAgeVerification`, `XboxChildAccount`, `XboxRejected`, для `Persistence` — `AccountSaveFailed`.
`RunAttemptAsync` переводит исключения: сетевая ошибка и таймаут → `Network`, `AccountStoreException` →
`Persistence`, `AuthServiceException` → собственный `Kind`, прочее (включая `JsonException`) → `InvalidResponse`.

### Одна попытка, отмена и сериализация

- Одна активная попытка: `StartAttempt` через `InvalidateAttemptLocked` увеличивает поколение и отменяет
  предыдущую попытку.
- Отмена: `CancelSignIn` (закрытие `MainWindow`), `BackFromSignIn`, `LogoutAsync`. Ошибки отменённой попытки не показываются.
- Все переходы состояния попытки и финальная запись аккаунта выполняются под `_commitLock`. Подготовка счётчиков
  выполняется до финальной фиксации; сама запись проходит только при `IsCurrentAttempt` (то же поколение, попытка
  активна и не отменена), а `File.Move` остаётся внутри этого условия. Фаза применения сериализована с выходом,
  но файловые операции подготовки не блокируют UI и не удерживают commit lock. Устаревший вход не заменит аккаунт,
  а завершившийся вход не восстановит аккаунт после выхода.
- Выход: `LogoutAsync` сначала инвалидирует попытку, затем удаляет `session.dat` под тем же условием. Выбранный язык
  при выходе сохраняется (тест `LogoutKeepsSelectedLanguage`).

### Обновление сессии для онлайн-запуска

Строгий путь, отдельный от мягкого шага 4 первого входа (тот не изменён).
`IMicrosoftAuthService.ConnectMinecraftAsync(refreshToken, persistRefreshToken, ct)`: refresh-grant Microsoft →
**сразу** `persistRefreshToken` с ротированным токеном → Xbox → XSTS Minecraft → `login_with_xbox` → профиль.
Сбой после refresh не теряет новый токен. Ошибки различимы: `invalid_grant` → `RefreshTokenRejected`,
403/404 входа или 403 профиля → `MinecraftLicenseMissing`, 404 профиля или пустой профиль → `MinecraftProfileMissing`;
сеть/таймаут → `Network`. Ключи resw: `MinecraftRefreshTokenRejected`, `MinecraftLicenseMissing`, `MinecraftProfileMissing`.
`MinecraftConnection.ToString()` не выводит access token.

`MinecraftSessionCoordinator` (`AppCoordinator.MinecraftSessions`, `windows/Hako.Core/MinecraftSessionCoordinator.cs`):
- `ConnectForLaunchAsync` возвращает текущий `AccountSession`, если Minecraft-токен действует ещё больше минуты
  (время — через `TimeProvider`), иначе запускает refresh. Параллельные вызовы ждут одну операцию.
- Каждая запись (`session.dat` и `AppCoordinator.Account`) идёт через `AppCoordinator.TryCommitAccountUpdate` внутри
  `commitIfCurrent` с поколением аккаунта; вход, загрузка и выход увеличивают поколение (`AccountGenerationChanged`)
  и отменяют refresh. Поздний результат не восстановит удалённый `session.dat` и не заменит новый аккаунт.
- `Status` (`Idle`/`Connecting`/`Connected`/`Failed`) и `Error` для UI; события приходят с потока refresh —
  UI маршалит их в `DispatcherQueue`.
- Уведомление `AppCoordinator.Account` при refresh также приходит с фонового потока; `MainWindow`
  переносит обработку в сохранённую `DispatcherQueue` окна и пропускает её после закрытия.
  Ошибка отдельного подписчика `Account` не прерывает фиксацию аккаунта и доставку остальным подписчикам.

`MinecraftCharacterEditor` (`windows/Hako.Core/MinecraftCharacterEditor.cs`) использует ту же сессию
для Java-профиля и изменения внешнего вида через `MinecraftProfileApi`. После 401 выполняется один
принудительный `MinecraftSessionCoordinator.RefreshAsync` и один повтор запроса. Клиент текстур
не передаёт токены. Закрытие редактора и изменение поколения аккаунта отменяют его операции;
поздние ответы не публикуют внешний вид. Сохранение скина и плаща подтверждается повторным чтением
Java-профиля и текстуры; при частичном результате повтор сначала сверяет фактическое состояние,
чтобы не загружать уже применённый скин повторно. Формат `session.dat` не изменяется.
