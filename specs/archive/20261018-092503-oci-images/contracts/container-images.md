# Contract: Deployable Container Images

This contract is the interface the three images offer to every deployment profile (the Compose stamp and the Azure
Container Apps target profile). Values marked *profile* are deployment configuration and stay **Open / Blocking** in
[Production Deployment and Operations](../../../../docs/architecture/production-operations.md#versioned-production-profile).

## 1. Build

Run from the repository root (build context `.`); every build requires both arguments.

| Image | Dockerfile | Required build arguments |
| --- | --- | --- |
| Platform API | `src/platform/SocAlytics.Platform.Api/Dockerfile` | `SOCALYTICS_REVISION`, `SOCALYTICS_VERSION` |
| Migrator | `src/platform/SocAlytics.Platform.Migrator/Dockerfile` | `SOCALYTICS_REVISION`, `SOCALYTICS_VERSION` |
| Web UI | `src/clients/web/Dockerfile` | `SOCALYTICS_REVISION`, `SOCALYTICS_VERSION` |

- `SOCALYTICS_REVISION`: the full source commit hash; must not be empty.
- `SOCALYTICS_VERSION`: the release version, shape `YYYY.MMDD.HHMMSS` (the Web UI release-version shape); the three
  images of one release use the same value.
- A missing or malformed argument fails the build with a message that names the argument.
- Each Dockerfile has a `Dockerfile.dockerignore` next to it that admits only the files its image needs.
- Base images are pinned by digest; no image definition names a CPU architecture.

## 2. Labels (all images)

| Label | Value |
| --- | --- |
| `org.opencontainers.image.revision` | `SOCALYTICS_REVISION` |
| `org.opencontainers.image.version` | `SOCALYTICS_VERSION` |
| `org.opencontainers.image.source` | `https://github.com/markusheiliger/socalytics` |
| `org.opencontainers.image.title` | `socalytics-api`, `socalytics-migrator`, or `socalytics-web` |
| `org.opencontainers.image.description` | one sentence naming the deployable |

## 3. Runtime (all images)

| Property | Platform API | Migrator | Web UI |
| --- | --- | --- | --- |
| User (numeric, non-root) | 1654 | 1654 | 101 |
| Listening port | 8080 (HTTP) | none | 8080 (HTTP) |
| Root filesystem | read-only supported | read-only supported | read-only supported |
| Writable location | tmpfs at `/tmp` | tmpfs at `/tmp` | tmpfs at `/tmp` |
| Default hosting environment | Production | Production | not applicable |
| In-container health probe | none | none | none |
| Lifetime | long-running | one run, then exit | long-running |

- Configuration is read only from environment variables (and from mounted files only where an existing option takes a
  file path); images contain no stamp, profile, cloud, or secret values.
- TLS is terminated outside the images (*profile*).
- Any non-root UID assigned by the orchestrator works; image files are root-owned and world-readable.

## 4. Platform API

- Entry point: the existing API host; it contains no Migrator, no DbUp, and no migration entry point.
- `GET /alive`: liveness, unchanged.
- `GET /health`: readiness, unchanged; it reports healthy only when the database migration state is current, the club
  is established, and a default analysis workflow is resolvable, among the existing checks.
- Configuration: the existing options, for example `ConnectionStrings__socalytics`, `ConnectionStrings__nats`,
  `Nats__Authentication__*`, `ObjectStorage__*`, `Email__*`, `Stamp__*`, `ClubBootstrap__*`, `IdentityAccess__*`,
  `WebUi__MinimumVersion`, and `ASPNETCORE_FORWARDEDHEADERS_ENABLED`; invalid options stop the host at start (*profile*
  values).

## 5. Migrator

- Entry point: the existing Migrator; it applies pending migrations once and exits with the existing outcome code
  (`0` success, `2` configuration invalid, `3` database unavailable, `8` lock timeout, `9` cancelled, and the other
  existing codes; see `src/platform/README.md`, Migrator Contract).
- Configuration: `ConnectionStrings__socalytics-migrator` and the optional `Migrator__*` timeouts.
- It contains no API host and must not be restarted after a successful run by the orchestrator.

## 6. Web UI

### 6.1 Configuration

| Variable | Required | Meaning |
| --- | --- | --- |
| `SOCALYTICS_API_UPSTREAM` | yes | Base URL of the platform API container, for example `http://api:8080` (*profile*) |
| `SOCALYTICS_OBJECT_STORAGE_ORIGIN` | no | Origin added to `connect-src` for direct uploads (*profile*) |

### 6.2 Static files

| Request | Response | `Cache-Control` |
| --- | --- | --- |
| `/index.html`, `/sw.js`, `/shell-manifest.json`, `/manifest.webmanifest` | the built file | `no-cache` |
| `/assets/**` (fingerprinted) | the built file | `public, max-age=31536000, immutable` |
| other existing files (icons) | the built file | `no-cache` |
| missing path with a file extension or under `/assets/` | 404 | `no-cache` |
| any other path (client route) | `/index.html` | `no-cache` |

Files are served byte for byte as built, so every file in `/shell-manifest.json` matches its SHA-384 `integrity`.
`.webmanifest` is served as `application/manifest+json`.

### 6.3 Security headers (responses the Web UI server produces)

- `Content-Security-Policy`: the production policy of `src/clients/web/build/securityHeaders.ts` (the
  [security-header contract](../../20261012-150657-web-ui-foundation/contracts/security-headers.md) as extended
  by the [PWA contract](../../20261015-194113-web-ui-pwa-updates/contracts/web-ui-pwa.md)), with
  `connect-src 'self'` plus `SOCALYTICS_OBJECT_STORAGE_ORIGIN` when set.
- `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`,
  `Permissions-Policy: camera=(), microphone=(), geolocation=()`.

### 6.4 One-origin forwarding

| Forwarded paths | `/api`, `/api/**`, `/alive` (the paths the AppHost's development proxy forwards) |
| --- | --- |
| Target | `SOCALYTICS_API_UPSTREAM`, resolved per request |
| Request | method, path, query, headers, and body unchanged; adds `X-Forwarded-For`, `X-Forwarded-Proto`, `X-Forwarded-Host` |
| Response | status, headers (including `SocAlytics-Minimum-Web-Version`), and body unchanged |
| Buffering | none for requests or responses (streamed) |
| Limits | no request-body limit; read and send timeouts of one hour |
| API unreachable | `502`; static files and client routes keep working |

The API applies forwarding headers only when its configuration enables them (`ASPNETCORE_FORWARDEDHEADERS_ENABLED`,
*profile*).
