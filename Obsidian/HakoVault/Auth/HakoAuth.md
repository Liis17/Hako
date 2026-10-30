# Hako account authentication

Parent: [[Index]]

The macOS and Windows launchers have an independent Hako account provider authenticated through the Telegram bot. The protocol authority is [`server/docs/auth-api.md`](../../../server/docs/auth-api.md). Microsoft remains the provider for licensed Minecraft sessions and XUID; a Hako session does not replace or mutate Microsoft credentials or Minecraft launch identity. macOS also syncs playtime to the authenticated Hako profile while keeping local guest/Microsoft counters and instance totals separate.

## macOS

`HakoAuthAPIClient` uses independent Codable DTOs for the server API; it does not reuse Microsoft or Minecraft authentication models. Its fixed origin is `https://hako.liis17.com:443/`. Redirects are rejected and each URLSession request has a 20-second deadline covering the complete response body. DTOs and API/storage protocols are explicitly nonisolated and `Sendable`; the `@MainActor @Observable` `HakoAccountCoordinator` owns UI state and request generations.

`HakoPlaytimeAPIClient` uses the same fixed HTTPS origin and Bearer Hako token for `GET /v1/playtime` and `POST /v1/playtime/sessions`. Session payloads contain `session_id` and nonnegative integer `duration_ms`; only a `200` response with the same UUID acknowledges a submission. `409`, network failures and `401` retain the durable SwiftData queue item. Retries reuse its UUID so a lost response remains idempotent. Queue entries pin their Hako account UUID and cannot be sent by another Hako profile. On login, unreserved historical time is reserved across all guest/Microsoft `PlayerPlaytime` totals, excluding prior queue allocations and current sessions; `InstancePlaytime` is excluded. Acknowledgement deletes the entry and subtracts only its recorded local allocations in the same SwiftData save. The Profile card displays the Hako server total and the amount waiting to sync separately. Hako logout or profile changes split active journalled play into owner-pinned segments; the independent helper continues tracking after the launcher closes.

`HakoSessionKeychainStore` stores the whole Hako session as JSON in a generic-password item with service `com.Launcher.Hako.telegram-session` and account `current`. This is separate from Microsoft's `com.Launcher.Hako.auth` items keyed by XUID. Keychain reads distinguish a missing item from access/decoding failures; a failed load shows a startup warning and disables authentication writes or clearing. Keychain operations run on a private serial queue. Save uses `SecItemUpdate`, then `SecItemAdd` only for `errSecItemNotFound`.

Startup restores Hako independently from Microsoft and displays a loading state until Keychain restoration completes. A restored session opens the launcher immediately while `/me` verifies in the background; checks also run daily and on profile entry (coalesced and throttled to one attempt per minute). Successful `/me` saves the refreshed account, device name, expiry, and verification time. Network errors keep the cached session and mark it unverified or expired; `401` clears only Hako.

Remote logout cancels login and verification, calls the server, then clears only the matching local Hako token. On failure, Profile offers retry, cancel, or explicitly warned local-only removal. Authentication and verification use generations to ignore stale responses. If an exchange response is lost, the one-use exchange is never retried and the user starts a fresh username login; a returned token that became stale, expired before storage, or could not be persisted is revoked best effort with a five-second deadline. Session and exchange secrets are redacted from diagnostics.

Hako-only accounts do not create SwiftData `Account` records or change local guest/MS ownership. Their ended playtime segments are sent to the Hako UUID recorded when each segment began, while guest totals remain transferable to Microsoft by the existing sign-in flow.

### macOS sources

| File | Role |
|------|------|
| `macos/Hako/Hako/Auth/HakoAuth.swift` | Domain types, API/store contracts, username normalization, strict Telegram link validation, secret-safe descriptions |
| `macos/Hako/Hako/Auth/HakoAuthAPIClient.swift` | Fixed-origin URLSession API, wire DTOs, redirects and whole-request deadline |
| `macos/Hako/Hako/Auth/HakoPlaytimeAPIClient.swift` | Hako playtime GET/POST contract, UUID acknowledgement and Bearer authentication |
| `macos/Hako/Hako/Auth/HakoAccountCoordinator.swift` | Register/login polling, expiry, exchange, generations, restoration, verification and logout |
| `macos/Hako/Hako/Auth/HakoSessionKeychainStore.swift` | Independent session Keychain item and serial off-main-actor access |
| `macos/Hako/Hako/Auth/HakoDeviceName.swift` | Computer name selection, trimming, Unicode-scalar limit and fallback |
| `macos/Hako/Hako/Views/HakoLoginView.swift`, `Views/WelcomeView.swift` | Registration/login UI and user-triggered Telegram open/copy actions |
| `macos/Hako/Hako/Views/Launcher/ProfileView.swift`, `ContentView.swift`, `HakoServices.swift` | Separate provider cards, root routing and startup ownership |
| `macos/Hako/HakoTests/HakoAuthTests.swift` | Mocked wire, Keychain, coordinator, routing, cancellation and provider-isolation tests |

## Windows

The Windows implementation and its source table follow.

## Request flow — Windows

`HakoAuthApiClient` uses the fixed `https://hako.liis17.com:443/` HTTPS origin and the public API endpoints. Its default `HttpClientHandler` disables automatic redirects so an authorization header is never forwarded to a redirect target; certificate validation remains the platform default. Each API operation has a 20-second deadline covering response headers and the full body, including JSON error bodies; caller cancellation remains cancellation. API JSON DTOs declare their snake_case wire names explicitly in `HakoJsonContext`; saved Hako and Microsoft sessions keep their existing camelCase serialization.

`HakoUsername.TryNormalize` trims whitespace, removes one optional leading `@`, then accepts 5–32 ASCII letters, digits, and underscores. Device names are trimmed and limited to 1–128 nonblank Unicode scalar values. Registration does not ask for a username; the server returns a `bot_link`, which the client accepts only as an HTTPS link to `t.me` with a bot path and a nonempty `start` parameter. The UI opens or copies it only after the user clicks.

1. Registration calls `POST /v1/auth/register`; login calls `POST /v1/auth/login` with username and device name.
2. `HakoAccountCoordinator` polls `GET /v1/auth/requests/{request_id}` every two seconds until a terminal status or `expires_at`.
3. Approval calls `POST /v1/auth/exchange` once. Exchange secret is held in memory and sent as the Bearer credential. Session token and exchange secret are omitted from record diagnostics.
4. The resulting session is committed to `hako-session.dat` only if the attempt generation is still current. If cancellation makes a returned token stale, or local persistence fails, the client attempts to revoke that token with a five-second bound.

`HakoAccountCoordinator.PropertyChanged` identifies the public state properties. `AppCoordinator` forwards these notifications to the WinUI login and profile pages so request progress, Telegram links, errors, and session verification are displayed as they change.

Exchange is one-use. If its response is lost after the server consumed the request, the UI requires a fresh login even when registration initiated the attempt; it never retries the old exchange or registration indefinitely. The user is told the prior server session may remain active in Telegram. Registration provides no username before successful exchange, so the fresh-login form asks for the existing Telegram username.

Terminal statuses and HTTP failures map through `HakoAuthErrorKind.ResourceKey()` to Russian/English resources. `429`, timeouts, invalid/non-JSON Cloudflare responses, redirects, and `5xx` are classified without showing response bodies or server diagnostics.

## Session verification and logout — Windows

`HakoSessionStore` protects `hako-session.dat` with DPAPI `CurrentUser` scope and the separate entropy `Hako.Session.v1`; it does not read or alter Microsoft `session.dat`. Saves use a temporary file and atomically replace it inside a generation-guarded callback. See [[Data/Persistence#Аккаунт Hako: hako-session.dat]].

Startup loads both providers independently and opens Builds when either has a local session. A loaded Hako session is always checked asynchronously with `/me`, even when its saved `VerifiedAt` is less than 24 hours old; this does not block startup. Entering Settings → Profile requests a check, throttled to one attempt per minute regardless of the saved verification time. The UI has no manual verify action; `VerifyManuallyAsync` remains in `Hako.Core`, used by tests only. While the app is running, a `TimeProvider`-backed one-shot schedule checks Hako every 24 hours; overlapping checks coalesce. Successful `/me` saves the updated profile, device name, renewed expiry, and verification time. Network, timeout, rate-limit, and server errors preserve the cached account as unverified (or expired if its stored expiry has passed); `401` clears only Hako state. Verification cannot restart while remote logout or local-only clearing is in progress.

Hako sign-out cancels a pending login and verification, calls `POST /v1/auth/logout`, and then clears the local file. `401` from logout means the server session is already invalid. On network failure the profile offers retry, explicit local-only removal, or cancel; local-only removal warns that the server session can remain active. Microsoft state and builds are unaffected.

Hako-only builds do not create a Microsoft Minecraft profile or XUID. Minecraft continues through the existing offline/guest path. Windows profile playtime uses `Playtime/HakoPlaytimeSync.cs`: Hako login imports unassigned local totals once, completed segments are uploaded through the authenticated playtime API, and build-specific counters remain local under their Microsoft/guest identity. Account boundaries are applied independently of network requests; launches await durable assignment. See [[Data/Persistence#Игровое время Windows]]. Archive activation enters explicit guest mode only when neither Microsoft nor Hako has a session ([[UI/SystemIntegration#Открытие архивов в Windows]]).

## Sources

| File | Role |
|------|------|
| `server/docs/auth-api.md` | Wire protocol, status/error codes, limits, and session behavior |
| `windows/Hako.Core/HakoAuthApiClient.cs`, `HakoAuthContracts.cs` | HTTPS API, explicit DTOs, validation, error mapping, provider contracts |
| `windows/Hako.Core/HakoAccountCoordinator.cs` | Polling, exchange, verification, expiry, cancellation, generations, logout |
| `windows/Hako.Core/HakoSessionStore.cs` | DPAPI session persistence and guarded atomic updates |
| `windows/Hako.Core/AppCoordinator.cs` | Provider routing, startup, restore activation guard, Microsoft coexistence |
| `windows/Hako/HakoLoginPage.xaml`, `windows/Hako/HakoLoginPage.xaml.cs`, `windows/Hako/SettingsPage.xaml.cs` | Localized login/registration and provider-specific profile controls |
| `windows/Hako.Tests/HakoAuthApiTests.cs`, `HakoAccountCoordinatorTests.cs` | Protocol errors, timing, cancellation, stale responses, routing and provider isolation |
