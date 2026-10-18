# Contract: Compose Evaluation Stamp

This contract is the interface the committed composition offers to an operator who evaluates a self-hosted stamp. It
builds on the [deployable image contract](../../20261018-092503-oci-images/contracts/container-images.md). Values
marked *open* are production profile values that stay **Open / Blocking** in
[Production Deployment and Operations](../../../docs/architecture/production-operations.md#versioned-production-profile);
the evaluation stamp uses them as operator values or documented evaluation choices, never as approved values.

## 1. Files

| File | Purpose |
| --- | --- |
| `src/platform/deploy/compose/compose.yaml` | The stamp definition |
| `src/platform/deploy/compose/stamp.env.template` | Every value the definition reads, with a comment each; no secret default |
| `src/platform/deploy/compose/README.md` | Setup, start, inspect, stop, teardown, second stamp, limitations, non-production statement |
| `src/platform/SocAlytics.Platform.StampSetup` | Setup console writing one stamp directory |

## 2. Commands (repository root)

```text
dotnet run --project src/platform/SocAlytics.Platform.StampSetup -- --output <stamp dir>
docker compose -f src/platform/deploy/compose/compose.yaml -p <stamp> --env-file <stamp dir>/stamp.env up -d
docker compose -f src/platform/deploy/compose/compose.yaml -p <stamp> --env-file <stamp dir>/stamp.env ps
docker compose -f src/platform/deploy/compose/compose.yaml -p <stamp> --env-file <stamp dir>/stamp.env stop
docker compose -f src/platform/deploy/compose/compose.yaml -p <stamp> --env-file <stamp dir>/stamp.env down --volumes --remove-orphans
```

- The setup console exits non-zero and writes nothing when `<stamp dir>` exists or lies inside a Git working tree.
- `up` fails before any container starts when a required value is missing or empty, naming the value.

## 3. Services

| Service | Image | Networks | Published | Health check | Depends on | Restart | Root filesystem |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `postgres` | `postgres:18@sha256:…` | `data` | no | `pg_isready` | — | unless-stopped | image default |
| `nats` | `nats:2.14-alpine@sha256:…` | `data` | no | `/healthz?js-enabled-only=true` on 8222 | — | unless-stopped | image default |
| `storage` | `rustfs/rustfs:1.0.1@sha256:…` | `data` | no | `/health` on 9000 | — | unless-stopped | image default |
| `migrator` | `${SOCALYTICS_MIGRATOR_IMAGE}` | `data` | no | — (one-shot) | `postgres` healthy | no | read-only, tmpfs `/tmp` |
| `api` | `${SOCALYTICS_API_IMAGE}` | `data`, `app`, `egress` | no | — (none in image) | `migrator` completed successfully; `postgres`, `nats`, `storage` healthy | unless-stopped | read-only, tmpfs `/tmp` |
| `web` | `${SOCALYTICS_WEB_IMAGE}` | `app`, `edge` | `127.0.0.1:${SOCALYTICS_WEB_PORT}:8080` | `/` on 8080 | `api` started | unless-stopped | read-only, tmpfs `/tmp` |

- Networks `data` and `app` are `internal: true`; `edge` carries only the published port; `egress` lets only the API
  reach its configured external endpoints (SMTP relay; other configured endpoints such as image registries).
- Volumes: `postgres-data` (`/var/lib/postgresql`), `nats-data` (`/data`), `storage-data` (`/data`).
- No `container_name`, no top-level `name`, no fixed network or volume `name`; one replica each; no resource limits
  (*open*).
- `postgres` mounts `src/platform/SocAlytics.Platform.AppHost/PostgresInit/01-socalytics-roles.sh` read-only into
  `/docker-entrypoint-initdb.d/`; `nats` mounts `${SOCALYTICS_STAMP_DIR}/nats.conf` read-only and runs with it.

## 4. Environment file

| Variable | Kind | Used by | Meaning |
| --- | --- | --- | --- |
| `SOCALYTICS_STAMP_DIR` | setup | `nats` | Absolute path of the stamp directory |
| `SOCALYTICS_API_IMAGE`, `SOCALYTICS_MIGRATOR_IMAGE`, `SOCALYTICS_WEB_IMAGE` | operator | images | References of one release's images |
| `SOCALYTICS_RELEASE_VERSION` | operator | `api` | `WebUi__MinimumVersion`; the release version of the images |
| `SOCALYTICS_WEB_PORT` | operator (*open*) | `web`, `api` | Loopback port of the Web UI; also forms `http://localhost:<port>` for links |
| `SOCALYTICS_STAMP_ID` | operator | `api` | `Stamp__Id` |
| `SOCALYTICS_PUBLIC_BASE_URI` | operator (*open*) | `api` | `Stamp__PublicBaseUri`; an `https` placeholder, no endpoint behind it |
| `SOCALYTICS_CLUB_DISPLAY_NAME`, `SOCALYTICS_FIRST_CLUB_ADMIN_ACCOUNT_NAME` | operator | `api` | Club bootstrap |
| `SOCALYTICS_FIRST_CLUB_ADMIN_INITIAL_PASSWORD` | secret | `api` | Club bootstrap initial credential |
| `SOCALYTICS_POSTGRES_PASSWORD` | secret | `postgres` | Administrator password |
| `SOCALYTICS_MIGRATOR_DB_PASSWORD`, `SOCALYTICS_APP_DB_PASSWORD` | secret | `postgres`, `migrator`, `api` | Role passwords |
| `SOCALYTICS_STORAGE_ACCESS_KEY`, `SOCALYTICS_STORAGE_SECRET_KEY` | secret | `storage`, `api` | Object storage keys |
| `SOCALYTICS_NATS_OPERATOR_SEED`, `SOCALYTICS_NATS_ACCOUNT_SEED`, `SOCALYTICS_NATS_SYSTEM_ACCOUNT_SEED` | secret | `api` | NATS JWT authentication |
| `SOCALYTICS_SMTP_HOST`, `SOCALYTICS_SMTP_PORT`, `SOCALYTICS_SMTP_SECURITY` | operator (*open*) | `api` | Relay; placeholder `smtp.invalid`, `587`, `StartTls` never delivers |
| `SOCALYTICS_SMTP_USERNAME`, `SOCALYTICS_SMTP_PASSWORD` | optional secret | `api` | Relay credentials, both or neither |
| `SOCALYTICS_SENDER_ADDRESS`, `SOCALYTICS_SENDER_NAME` | operator (*open*) | `api` | Sender |

Every value except the optional relay credentials is interpolated as required (`${NAME:?…}`); secrets have no default.

## 5. API configuration fixed by the definition

Production environment (no `ASPNETCORE_ENVIRONMENT`), with: `ConnectionStrings__socalytics` (role `socalytics_app`),
`ConnectionStrings__nats` and `AnalystWork__QueueUrl` = `nats://nats:4222`, `Nats__Authentication__Mode=Jwt` with the
three seeds, `Messaging__Nats__ProvisionTopology=true`, `ObjectStorage__ServiceUrl=http://storage:9000`,
`ObjectStorage__Bucket=socalytics-recordings`, `ObjectStorage__Region=us-east-1`,
`ObjectStorage__EnsureBucketOnStartup=true`, `Notifications__WebUiBaseUri` and
`Registry__Pairing__VerificationBaseUri` = `http://localhost:${SOCALYTICS_WEB_PORT}`, and the AppHost's non-secret
upload, email, notification, analysis, and identity option values as evaluation choices. No Development- or Test-only
relaxation is set. The Migrator receives `ConnectionStrings__socalytics-migrator` (role `socalytics_migrator`); the
Web UI receives `SOCALYTICS_API_UPSTREAM=http://api:8080` and no object storage origin.

## 6. Guarantees and limitations

- Order: the API container is created only after the Migrator exited with `0`; a failing Migrator leaves the API
  uncreated.
- Readiness: unchanged; `/health` is healthy once the stamp's Registry holds a resolvable default analysis workflow.
- Ingress: only the Web UI, on loopback, over plain HTTP; TLS and exposure beyond loopback are out of scope (*open*).
- Uploads: browser recording uploads are unavailable (object storage is internal).
- Analyst Managers cannot connect (out of scope); `Stamp__PublicBaseUri` has no endpoint behind it.
- Email: no mail catcher; with the placeholder relay, delivery attempts fail and are recorded.
- Isolation: resources of two project names never overlap; services of one stamp cannot reach the other's.
- Not a production profile; no production preflight; the Aspire AppHost stays the supported local entry point.
