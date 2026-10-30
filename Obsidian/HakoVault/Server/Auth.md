# Server Auth

Parent: [[Index]]

`server/crates/auth/` is the Rust Auth service. It owns Telegram registration, login approval, account identity, and launcher sessions. Public API contracts are in [`server/docs/auth-api.md`](../../../server/docs/auth-api.md).

## Contracts and flow

- Public HTTP listener `8081`: registration, login, request status/exchange, current account, logout, playtime submission/total, and `/healthz`.
- Internal HTTP listener `8091`: `/internal/v1/tokens/check` and `/healthz`. Pack authenticates this call with `X-Service-Secret`, whose value must match `HAKO_AUTH_SERVICE_SECRET`.
- Telegram identity is the numeric `telegram_id`, unique in `accounts`; the mutable username is refreshed and used for login lookup.
- Registration and login requests expire after five minutes. Auth obtains the bot username through `getMe`; health can remain `200` while Telegram is unavailable, but registration returns `503 bot_unavailable` until the identity is known.
- Session tokens and exchange secrets are stored as hashes. Successful `/me` and internal token checks renew an active session to 14 days after the request; `/logout` requires an active session and immediately revokes it. Expired and revoked sessions cannot be renewed. Each login makes a separate session.
- The Telegram `/sessions` command shows at most 20 active sessions with shortened device names. Its revoke-all action is also available when an unexpired `pending` or `approved` request exists, so it can invalidate an approved login before exchange. Exchange and account-level revocation transactions serialize on the same PostgreSQL advisory lock.
- In-memory sliding-window limits are 5 registrations, 10 logins, and 30 exchanges per IP per minute. Process restart resets these counters. `X-Forwarded-For` only affects the client IP when the socket peer is in `HAKO_AUTH_TRUSTED_PROXIES`.

The rolling 14-day expiry and per-session login behavior are API contracts. The implementation keeps rate-limit counters only in memory, so they reset on restart; no persistent or distributed counter store is present.

## Profile playtime

`POST /v1/playtime/sessions` accepts a completed UUID and nonnegative integer milliseconds; `GET /v1/playtime` returns the authenticated profile's total. Both use and renew Hako bearer sessions. `playtime_sessions` stores UUIDs globally so a replay with the same account/duration is acknowledged, while another owner or duration returns `409 playtime_session_conflict`. UUID insertion and `playtime_totals` increment share one transaction; deduplication outlives login tokens. Distinct sessions are summed even when their real-time intervals overlap. The total uses signed 64-bit milliseconds; overflow rolls back the submission. API details and deployment order are in `server/docs/auth-api.md`. Windows uses this contract; macOS playtime remains local until its separate implementation.

## Source

- `server/crates/auth/src/api.rs` — public/internal routes, forwarded client IP, and proxy trust checks.
- `server/crates/auth/src/service.rs` — requests, Telegram account updates, sessions, and token renewal.
- `server/crates/auth/src/bot_worker.rs`, `telegram.rs` — `getMe`, update polling, and Telegram outbox delivery.
- `server/crates/auth/src/rate_limit.rs` — in-memory request windows.
- `server/crates/auth/migrations/` — account, request, session, Telegram update, and outbox persistence.

## Limits

Telegram polling is a single background worker. Transient delivery and polling failures retry with bounded exponential backoff. Telegram rate limits (`429`) are transient; `parameters.retry_after` delays the retry, capped at five minutes. Other permanent Telegram `4xx` delivery failures are recorded on their outbox rows and skipped, allowing later effects and polling to proceed. Readiness of the HTTP listener does not mean the bot token is valid. The service has no external identity provider integration beyond Telegram and no multi-instance rate-limit coordination.
