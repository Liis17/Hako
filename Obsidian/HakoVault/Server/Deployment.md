# Server Deployment

Parent: [[Index]]

The root `compose.yaml` runs `hako-auth`, `hako-pack`, and PostgreSQL. `server/Dockerfile` builds either Rust package as a small non-root image; both services apply their own SQLx migrations at startup. Service images default to the prebuilt `docker.barkfluff.com/hako-auth:latest` and `hako-pack:latest` published by `.github/workflows/server-images.yml` on pushes to `main` that change `server/` (tags `latest` and `sha-<short commit>`, `linux/amd64`, secrets `REGISTRY_USERNAME`/`REGISTRY_PASSWORD`); the Compose `build` sections remain for local building. The first-run bootstrap script embedded in `compose.yaml` (top-level `configs`) creates separate database roles and databases on first initialization of the PostgreSQL volume, so a deployment host needs only `compose.yaml` and `.env`. Pack's archive bytes use an external S3-compatible service.

## Network and environment

- No service port is published to the host: Auth `8081`, internal `8091`, Pack `8082`, and PostgreSQL are reachable only inside the Compose network, explicitly named `hako` so that an external reverse-proxy container can join it with `docker network connect`.
- Root `.env.example` documents Compose and service variables. Deployment starts by copying it to `.env`, replacing every placeholder, then on the host `docker login docker.barkfluff.com`, `docker compose pull`, `docker compose up -d`; local development builds instead via `docker compose up -d --build`. `HAKO_AUTH_IMAGE` and `HAKO_PACK_IMAGE` override the image references, for example to pin a `sha-<commit>` tag. The two service secrets must match. Empty S3 values disable storage; a partial required S3 group is invalid.
- For public HTTPS, a reverse-proxy container attached to the `hako` network terminates TLS and routes one domain by path: `/v1/packs` to Pack `8082`, everything else (Auth `/v1/auth/...` and `/healthz`) to Auth `8081`; the proxy resolves service names through Docker DNS at request time (nginx `resolver 127.0.0.11` plus variable `proxy_pass`). `HAKO_AUTH_TRUSTED_PROXIES` must contain the proxy's source range as seen by Auth — the network subnet CIDR from `docker network inspect hako`. Untrusted `X-Forwarded-For` is ignored. Behind Cloudflare, `X-Forwarded-For` carries `CF-Connecting-IP`, and Cloudflare's proxied request-body limit (100 MB on Free/Pro plans) caps pack uploads.
- A valid Telegram token is needed for registration: Auth discovers the username with `getMe`. Invalid tokens do not prevent HTTP health checks but registration returns `503` until the bot is ready.

Operational examples and the complete variable table live in [`server/README.md`](../../../server/README.md).

## Verification and limits

The `server` job in `.github/workflows/tests.yml` runs `cargo fmt --check`, Clippy with warnings denied, workspace tests without a database, and Auth/Pack PostgreSQL integration tests with separate databases from a PostgreSQL service. Each local integration suite uses `HAKO_TEST_DATABASE_URL` pointed at its own database.

No CurseForge backend or macOS/Windows client integration is implemented. Backend deployment and CI are independent of client versioning and releases. There is no Compose reverse proxy or bundled object store; both remain deployment services.
