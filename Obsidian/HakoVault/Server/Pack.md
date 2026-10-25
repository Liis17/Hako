# Server Pack

Parent: [[Index]]

`server/crates/pack/` provides private `.hakopack` storage. PostgreSQL owns metadata and cleanup work; archive bytes live in external S3-compatible storage. API contracts and error statuses are in [`server/docs/pack-api.md`](../../../server/docs/pack-api.md); the manifest contract is [`server/docs/hakopack-manifest.schema.json`](../../../server/docs/hakopack-manifest.schema.json).

## Contracts

- Every Pack route except `/healthz` requires a bearer session token. Pack checks it through Auth's internal `/internal/v1/tokens/check`, using the shared service secret. Auth's `account_id` scopes every query and S3 key.
- `GET /v1/packs` lists an account's current packs. Create uses a client UUID and `If-None-Match: *`; replace and delete require one strong `If-Match` ETag. Only the current revision is retained; the API has no revision history or public sharing.
- A v1 `.hakopack` is a ZIP with one root `manifest.json`, optional root `icon.png`, and other content under `overrides/`. Manifest JSON is checked against the checked-in schema; ZIP entries are streamed/staged without extracting them. Defaults cap the upload at 1 GiB, expanded entry sizes at 4 GiB, entry count at 10,000, and the manifest at 1 MiB.
- S3 is optional for service startup, so health checks work without it; create, replace, and download operations requiring object storage return `503 storage_unavailable`. Partial required S3 configuration is rejected at startup.
- Replaced/deleted objects enter the PostgreSQL `pack_cleanup` queue. Default cleanup delay is 24 hours; it must exceed the one-hour download lifetime and cannot exceed one day. The worker persists retries across restarts.

The cleanup delay keeps an old object eligible for a bounded stream lifetime; the configured one-day default is the maximum allowed delay. Only these limits and behaviors are established by the implementation; no separate retention policy or history feature is present.

## Source

- `server/crates/pack/src/api.rs` — routes, bearer checks, conditional ETags, and streaming responses.
- `server/crates/pack/src/archive.rs` — streaming upload bounds and `.hakopack` validation.
- `server/crates/pack/src/service.rs` — account-scoped metadata, revisions, and delayed cleanup.
- `server/crates/pack/src/store.rs` — S3 and unavailable-store implementations.
- `server/crates/pack/src/config.rs`, `server/crates/pack/migrations/` — configuration, limits, metadata, and cleanup queue.

## Not implemented

The service does not retain revision history or share packs publicly. CurseForge and client integrations are not implemented; macOS and Windows clients remain independently versioned and released.
