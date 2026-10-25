# Pack API

Pack stores private `.hakopack` files in S3-compatible object storage and keeps the current revision and owner in PostgreSQL. Every endpoint except `/healthz` requires `Authorization: Bearer <token>`. Pack checks the token using Auth's internal token endpoint and uses its `account_id` as the owner. Auth checks time out after two seconds; invalid tokens return `401`, and an unavailable Auth service returns `503`.

All API errors use the Auth JSON shape:

```json
{"error":{"code":"precondition_required","message":"A conditional request header is required."}}
```

## Archive format v1

A `.hakopack` is a ZIP containing exactly one root `manifest.json`, an optional root `icon.png`, and any user files under `overrides/`. The `overrides/` check is a path prefix check. The manifest follows [`hakopack-manifest.schema.json`](hakopack-manifest.schema.json); [`hakopack-example.json`](hakopack-example.json) is a manifest example, not a complete ZIP archive. `profile` must be a JSON object and is validated without filtering the original archive, so arbitrary client profile fields remain intact. Fabric API is represented as a normal item in `modrinth_files`.

If the manifest has an `icon` field, it must be `"icon.png"`, and the archive must contain that root entry beginning with PNG signature bytes `89 50 4E 47 0D 0A 1A 0A`. An unreferenced root `icon.png` is not used as the pack icon. The server rejects encrypted entries, symlinks, absolute paths, `..` path segments, duplicate paths (case-insensitive), and collisions between `X` and `X.disabled`. Backslashes and ambiguous empty or `.` path segments are also rejected. The server reads ZIP metadata and the bounded manifest/icon data without extracting archive entries.

Default limits are:

| Environment variable | Default | Meaning |
| --- | ---: | --- |
| `HAKO_PACK_MAX_ARCHIVE_BYTES` | `1073741824` | Maximum incoming ZIP bytes; upload is streamed to a temp file and stopped with `413` when exceeded |
| `HAKO_PACK_MAX_UNCOMPRESSED_BYTES` | `4294967296` | Maximum sum of uncompressed sizes declared by ZIP entries |
| `HAKO_PACK_MAX_ENTRIES` | `10000` | Maximum ZIP entry count, including directories |
| `HAKO_PACK_MAX_MANIFEST_BYTES` | `1048576` | Maximum uncompressed manifest size |

Configured limits must be positive integers. ZIP entries are not extracted. The aggregate uncompressed limit is checked against the central-directory sizes.

## Endpoints

Create, metadata, and replacement responses return a `PackMetadata` JSON object and a strong `ETag` header. The object's `etag` field is unquoted; the HTTP header wraps the same opaque revision value in quotes. `sha256` is the lowercase SHA-256 of the full ZIP archive and is separate from the ETag.

```json
{
  "pack_id": "<uuid>",
  "revision": 1,
  "etag": "<opaque-revision-value>",
  "size_bytes": 123456,
  "sha256": "<64 lowercase hexadecimal characters>",
  "name": "My pack",
  "minecraft_version": "1.21.4",
  "loader": "fabric",
  "created_at": "<RFC3339 timestamp>",
  "updated_at": "<RFC3339 timestamp>"
}
```

### `GET /v1/packs?limit=&cursor=`

Returns `{ "items": [...], "next_cursor": "..." }`. `limit` defaults to `50`, must be positive, and is capped at `200`. Pagination uses an opaque URL-safe cursor and a stable `(updated_at DESC, pack_id ASC)` ordering. `next_cursor` is `null` when there is no next page.

### `POST /v1/packs/{pack_id}`

Creates a pack. `pack_id` is a client-generated UUID. The request body is the raw ZIP and `Content-Type` must be `application/zip` or `application/octet-stream`.

`If-None-Match: *` is required. Missing condition returns `428`; if that owner already has the UUID, the response is `412`. Success returns `201` and metadata.

### `GET /v1/packs/{pack_id}`

Returns current metadata and a strong `ETag` response header. The JSON `etag` field is the unquoted value; send the response header value, including quotes, in `If-Match`.

### `GET /v1/packs/{pack_id}/archive`

Streams the current ZIP from S3 with `Content-Type: application/zip`, `Content-Length`, and a strong `ETag`. A download is limited to one hour; a stream that exceeds this duration is terminated.

### `PUT /v1/packs/{pack_id}/archive`

Replaces the current ZIP using the same body and content type as creation. `If-Match: "<current-etag>"` is required. Missing condition returns `428`; stale, weak, wildcard, or multiple tags return `412`. Exactly one concurrent replacement using the same current tag can succeed. New metadata is published only after S3 confirms the object write; a failed write leaves the current revision available.

### `DELETE /v1/packs/{pack_id}`

Requires `If-Match: "<current-etag>"`. Missing condition returns `428`, stale condition returns `412`, and success returns `204`. The metadata row is removed and its S3 object is queued for delayed cleanup.

Pack IDs are unique per owner, so two accounts can independently use the same UUID. Only the current revision is retained; history and public sharing are not part of this API.

## Cleanup and storage

S3 keys have the form `packs/{owner_id}/{pack_id}/r{revision}-{etag}.zip`. The S3 implementation uses the AWS SDK for Rust and honors endpoint, region, static credentials, optional session token, and path-style configuration. If all required S3 variables are unset, Pack still starts and `/healthz` returns `200`; create, replace, and archive download return `503 storage_unavailable`. Metadata and delete operations use PostgreSQL; deletes queue object cleanup for later retry. If only some required S3 variables are set, Pack fails configuration validation at startup.

Replaced and deleted objects are queued in `pack_cleanup` with `eligible_at`, attempts, and last error. The worker checks every 30 seconds, claims work with a 60-second lease, and resumes queued work after restart. Failed deletes are logged and retried with exponential backoff from one minute up to one hour.

`HAKO_PACK_CLEANUP_DELAY` is an integer number of seconds. It defaults to `86400` (one day), must exceed the one-hour maximum download duration, and cannot exceed one day. Cleanup queue state is stored in the Pack database.

## Environment variables

| Variable | Requirement / default | Purpose |
| --- | --- | --- |
| `HAKO_PACK_ADDR` | `0.0.0.0:8082` | HTTP listener |
| `HAKO_PACK_DATABASE_URL` | Required | Pack PostgreSQL database |
| `HAKO_PACK_AUTH_INTERNAL_URL` | `http://auth:8091` | Auth internal API base URL |
| `HAKO_PACK_SERVICE_SECRET` | Required; same as Auth's `HAKO_AUTH_SERVICE_SECRET` | `X-Service-Secret` sent to Auth |
| `HAKO_S3_ENDPOINT` | Optional S3 group | S3-compatible endpoint |
| `HAKO_S3_BUCKET` | Optional S3 group | Bucket name |
| `HAKO_S3_REGION` | Optional S3 group | Signing region |
| `HAKO_S3_ACCESS_KEY_ID` | Optional S3 group | Access key ID |
| `HAKO_S3_SECRET_ACCESS_KEY` | Optional S3 group | Secret access key |
| `HAKO_S3_SESSION_TOKEN` | Optional | Temporary credentials token |
| `HAKO_S3_PATH_STYLE` | `true` | Force path-style addressing |
| `HAKO_PACK_MAX_ARCHIVE_BYTES` | `1073741824` | Incoming archive limit |
| `HAKO_PACK_MAX_UNCOMPRESSED_BYTES` | `4294967296` | Aggregate ZIP entry size limit |
| `HAKO_PACK_MAX_ENTRIES` | `10000` | ZIP entry count limit |
| `HAKO_PACK_MAX_MANIFEST_BYTES` | `1048576` | Manifest size limit |
| `HAKO_PACK_CLEANUP_DELAY` | `86400` seconds | Delayed S3 cleanup |

Set all five required S3 variables to enable storage. A partial S3 configuration prevents Pack from starting, which avoids silently disabling a broken deployment configuration.

## Error statuses

| Status | Typical codes | Meaning |
| ---: | --- | --- |
| `400` | `invalid_request`, `invalid_archive` | Invalid pack UUID, pagination query/cursor, content length, unreadable conditional header, or ZIP/manifest |
| `401` | `unauthorized` | Missing/invalid bearer token or Auth rejected it |
| `404` | `not_found` | Pack does not exist for this owner |
| `405` | `method_not_allowed` | Method is not supported on this route |
| `412` | `precondition_failed` | Pack exists on create or conditional ETag did not match |
| `413` | `payload_too_large` | Archive, entry count, manifest, or expanded size limit exceeded |
| `415` | `unsupported_media_type` | Upload content type is not supported |
| `428` | `precondition_required` | Required `If-None-Match` or `If-Match` is missing |
| `500` | `internal_error` | Unexpected database or internal failure |
| `503` | `auth_unavailable`, `storage_unavailable` | Auth or S3 is unavailable |

## PostgreSQL integration test

The Pack integration test skips with an explicit message if `HAKO_TEST_DATABASE_URL` is unset. Point it at an isolated database, for example the Compose-created `hako_pack_test` database:

```sh
HAKO_TEST_DATABASE_URL=postgres://postgres:postgres@localhost:5432/hako_pack_test \
  cargo test --locked --package hako-pack --test postgres_pack
```
