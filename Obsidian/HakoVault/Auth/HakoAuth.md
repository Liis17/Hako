# Hako account authentication on Windows

Parent: [[Index]]

The Windows launcher has an independent Hako account provider authenticated through the Telegram bot. The protocol authority is [`server/docs/auth-api.md`](../../../server/docs/auth-api.md). Microsoft remains the provider for licensed Minecraft sessions and XUID; a Hako session does not replace or mutate Microsoft `AccountSession`, Minecraft launch credentials, or guest playtime transfer.

## Request flow

`HakoAuthApiClient` uses the fixed `https://hako.liis17.com:443/` HTTPS origin and the public API endpoints. Its default `HttpClientHandler` disables automatic redirects so an authorization header is never forwarded to a redirect target; certificate validation remains the platform default. Each API operation has a 20-second deadline covering response headers and the full body, including JSON error bodies; caller cancellation remains cancellation. API JSON DTOs declare their snake_case wire names explicitly in `HakoJsonContext`; saved Hako and Microsoft sessions keep their existing camelCase serialization.

`HakoUsername.TryNormalize` trims whitespace, removes one optional leading `@`, then accepts 5–32 ASCII letters, digits, and underscores. Device names are trimmed and limited to 1–128 nonblank Unicode scalar values. Registration does not ask for a username; the server returns a `bot_link`, which the client accepts only as an HTTPS link to `t.me` with a bot path and a nonempty `start` parameter. The UI opens or copies it only after the user clicks.

1. Registration calls `POST /v1/auth/register`; login calls `POST /v1/auth/login` with username and device name.
2. `HakoAccountCoordinator` polls `GET /v1/auth/requests/{request_id}` every two seconds until a terminal status or `expires_at`.
3. Approval calls `POST /v1/auth/exchange` once. Exchange secret is held in memory and sent as the Bearer credential. Session token and exchange secret are omitted from record diagnostics.
4. The resulting session is committed to `hako-session.dat` only if the attempt generation is still current. If cancellation makes a returned token stale, or local persistence fails, the client attempts to revoke that token with a five-second bound.

Exchange is one-use. If its response is lost after the server consumed the request, the UI requires a fresh login even when registration initiated the attempt; it never retries the old exchange or registration indefinitely. The user is told the prior server session may remain active in Telegram. Registration provides no username before successful exchange, so the fresh-login form asks for the existing Telegram username.

Terminal statuses and HTTP failures map through `HakoAuthErrorKind.ResourceKey()` to Russian/English resources. `429`, timeouts, invalid/non-JSON Cloudflare responses, redirects, and `5xx` are classified without showing response bodies or server diagnostics.

## Session verification and logout

`HakoSessionStore` protects `hako-session.dat` with DPAPI `CurrentUser` scope and the separate entropy `Hako.Session.v1`; it does not read or alter Microsoft `session.dat`. Saves use a temporary file and atomically replace it inside a generation-guarded callback. See [[Data/Persistence#Аккаунт Hako: hako-session.dat]].

Startup loads both providers independently and opens Builds when either has a local session. A loaded Hako session is always checked asynchronously with `/me`, even when its saved `VerifiedAt` is less than 24 hours old; this does not block startup. Entering Settings → Profile requests a check, throttled to one attempt per minute regardless of the saved verification time. The manual button bypasses that throttle. While the app is running, a `TimeProvider`-backed one-shot schedule checks Hako every 24 hours; overlapping checks coalesce. Successful `/me` saves the updated profile, device name, renewed expiry, and verification time. Network, timeout, rate-limit, and server errors preserve the cached account as unverified (or expired if its stored expiry has passed); `401` clears only Hako state. Verification cannot restart while remote logout or local-only clearing is in progress.

Hako sign-out cancels a pending login and verification, calls `POST /v1/auth/logout`, and then clears the local file. `401` from logout means the server session is already invalid. On network failure the profile offers retry, explicit local-only removal, or cancel; local-only removal warns that the server session can remain active. Microsoft state and builds are unaffected.

Hako-only builds do not create a Microsoft Minecraft profile or XUID. Minecraft continues through the existing offline/guest path, and playtime stays under the guest identity. Archive activation enters explicit guest mode only when neither Microsoft nor Hako has a session ([[UI/SystemIntegration#Открытие архивов в Windows]]).

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
