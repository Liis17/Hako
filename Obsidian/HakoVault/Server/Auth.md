# Server Auth

Parent: [[Index]]

`server/crates/auth/` is the Rust Auth service. It owns Telegram registration, login approval, account identity, and launcher sessions. Public API contracts are in [`server/docs/auth-api.md`](../../../server/docs/auth-api.md).

## Contracts and flow

- Public HTTP listener `8081`: registration, login, request status/exchange, current account, logout, and `/healthz`.
- Internal HTTP listener `8091`: `/internal/v1/tokens/check` and `/healthz`. Pack authenticates this call with `X-Service-Secret`, whose value must match `HAKO_AUTH_SERVICE_SECRET`.
- Telegram identity is the numeric `telegram_id`, unique in `accounts`; the mutable username is refreshed and used for login lookup.
- Registration and login requests expire after five minutes. Auth obtains the bot username through `getMe`; health can remain `200` while Telegram is unavailable, but registration returns `503 bot_unavailable` until the identity is known.
- Session tokens and exchange secrets are stored as hashes. Successful `/me` and internal token checks renew an active session to 14 days after the request; `/logout` requires an active session and immediately revokes it. Expired and revoked sessions cannot be renewed. Each login makes a separate session.
- In-memory sliding-window limits are 5 registrations, 10 logins, and 30 exchanges per IP per minute. Process restart resets these counters. `X-Forwarded-For` only affects the client IP when the socket peer is in `HAKO_AUTH_TRUSTED_PROXIES`.

The rolling 14-day expiry and per-session login behavior are API contracts. The implementation keeps rate-limit counters only in memory, so they reset on restart; no persistent or distributed counter store is present.

## Source

- `server/crates/auth/src/api.rs` — public/internal routes, forwarded client IP, and proxy trust checks.
- `server/crates/auth/src/service.rs` — requests, Telegram account updates, sessions, and token renewal.
- `server/crates/auth/src/bot_worker.rs`, `telegram.rs` — `getMe`, update polling, and Telegram outbox delivery.
- `server/crates/auth/src/rate_limit.rs` — in-memory request windows.
- `server/crates/auth/migrations/` — account, request, session, Telegram update, and outbox persistence.

## Limits

Telegram polling is a single background worker. It retries transient failures with bounded exponential backoff; readiness of the HTTP listener does not mean the bot token is valid. The service has no external identity provider integration beyond Telegram and no multi-instance rate-limit coordination.
