# Server Deployment

Parent: [[Index]]

The root `compose.yaml` runs `hako-auth`, `hako-pack`, and PostgreSQL. `server/Dockerfile` builds either Rust package as a small non-root image; both services apply their own SQLx migrations at startup. Service images default to the prebuilt `docker.barkfluff.com/hako-auth:latest` and `hako-pack:latest` published by `.github/workflows/server-images.yml` on pushes to `main` that change `server/` (tags `latest` and `sha-<short commit>`, `linux/amd64`, secrets `REGISTRY_USERNAME`/`REGISTRY_PASSWORD`); the Compose `build` sections remain for local building. `server/init-db.sh` creates separate database roles and databases on first initialization of the PostgreSQL volume. Pack's archive bytes use an external S3-compatible service.

## Network and environment

- Compose publishes only Auth `8081` and Pack `8082`, bound to host loopback. Auth's internal `8091` and PostgreSQL have no published host ports.
- Root `.env.example` documents Compose and service variables. Deployment starts by copying it to `.env`, replacing every placeholder, then on the host `docker login docker.barkfluff.com`, `docker compose pull`, `docker compose up -d`; local development builds instead via `docker compose up -d --build`. `HAKO_AUTH_IMAGE` and `HAKO_PACK_IMAGE` override the image references, for example to pin a `sha-<commit>` tag. The two service secrets must match. Empty S3 values disable storage; a partial required S3 group is invalid.
- For public HTTPS, an external reverse proxy on the Docker host terminates TLS and forwards to `127.0.0.1:8081` or `127.0.0.1:8082`. `HAKO_AUTH_TRUSTED_PROXIES` must contain the proxy's source address as seen by the Auth container, expressed as CIDR. Untrusted `X-Forwarded-For` is ignored.
- A valid Telegram token is needed for registration: Auth discovers the username with `getMe`. Invalid tokens do not prevent HTTP health checks but registration returns `503` until the bot is ready.

Operational examples and the complete variable table live in [`server/README.md`](../../../server/README.md).

## Verification and limits

The `server` job in `.github/workflows/tests.yml` runs `cargo fmt --check`, Clippy with warnings denied, workspace tests without a database, and Auth/Pack PostgreSQL integration tests with separate databases from a PostgreSQL service. Each local integration suite uses `HAKO_TEST_DATABASE_URL` pointed at its own database.

No CurseForge backend or macOS/Windows client integration is implemented. Backend deployment and CI are independent of client versioning and releases. There is no Compose reverse proxy or bundled object store; both remain deployment services.
