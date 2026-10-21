# Compose Evaluation Stamp

This folder holds a Docker Compose definition of one self-hosted SocAlytics stamp for **evaluation**. It is not a
production profile and passes no production preflight; see [Limitations](#limitations).

| File | Purpose |
| --- | --- |
| `compose.yaml` | The stamp definition |
| `stamp.env.template` | Every value the definition reads, with a comment each; no value, in particular no secret, has a default |
| `../../SocAlytics.Platform.StampSetup` | Setup console that writes one stamp directory |

## Prerequisites

- Docker Engine with the Compose v2 plugin (`docker compose version`).
- The .NET SDK pinned by `src/platform/global.json`, or the pinned SDK container, to run the setup console.
- The API, Migrator, and Web UI images of one release, for example `socalytics-api:local`,
  `socalytics-migrator:local`, and `socalytics-web:local`, built from the repository root with the `docker build`
  commands that [the repository README](../../../../README.md) documents (they need the build arguments
  `SOCALYTICS_REVISION` and `SOCALYTICS_VERSION`; the image contract is in
  [the deployable image contract](../../../../specs/archive/20261018-092503-oci-images/contracts/container-images.md)).

## Create a stamp

All commands run from the repository root. The setup console writes one stamp directory with `stamp.env` and
`nats.conf`, generates every secret, and sets `SOCALYTICS_STAMP_DIR`. The output directory must **not exist** and must
lie **outside any Git working tree**, so that secrets never reach a repository; otherwise the console exits non-zero
and writes nothing.

```powershell
$stampDir = Join-Path $HOME 'socalytics-stamps/club-a'
dotnet run --project src/platform/SocAlytics.Platform.StampSetup -- --output $stampDir
```

The stamp directory holds secrets: keep it private and do not commit it.

## Fill the operator values

Open `<stamp dir>/stamp.env` and fill the values that the setup step left empty (each has a comment):

- `SOCALYTICS_API_IMAGE`, `SOCALYTICS_MIGRATOR_IMAGE`, `SOCALYTICS_WEB_IMAGE`: the image references of one release.
- `SOCALYTICS_RELEASE_VERSION`: the release version of the images (the API's `WebUi__MinimumVersion`).
- `SOCALYTICS_WEB_PORT`: the loopback port of the Web UI.
- `SOCALYTICS_STAMP_ID`: an identifier of this stamp.
- `SOCALYTICS_PUBLIC_BASE_URI`: an `https` placeholder base URI; no endpoint is behind it. Links in transactional email
  use `http://localhost:<port>` (the value of `SOCALYTICS_WEB_PORT`); only the Analyst Manager pairing verification link
  uses the placeholder base URI.
- `SOCALYTICS_CLUB_DISPLAY_NAME` and `SOCALYTICS_FIRST_CLUB_ADMIN_ACCOUNT_NAME`: the club bootstrap.
- `SOCALYTICS_SMTP_HOST`, `SOCALYTICS_SMTP_PORT`, `SOCALYTICS_SMTP_SECURITY`: your SMTP relay, or the documented
  placeholder `smtp.invalid`, `587`, `StartTls`, which never delivers.
- `SOCALYTICS_SENDER_ADDRESS` and `SOCALYTICS_SENDER_NAME`: the sender of transactional email.
- Optionally `SOCALYTICS_SMTP_USERNAME` and `SOCALYTICS_SMTP_PASSWORD`: set both or neither.

`up` fails before any container starts when a required value is missing or empty, and names the value.

## Run the stamp

Replace `<stamp>` with the Compose project name (for example `club-a`) and `<stamp dir>` with the stamp directory.

```text
docker compose -f src/platform/deploy/compose/compose.yaml -p <stamp> --env-file <stamp dir>/stamp.env up -d
docker compose -f src/platform/deploy/compose/compose.yaml -p <stamp> --env-file <stamp dir>/stamp.env ps
docker compose -f src/platform/deploy/compose/compose.yaml -p <stamp> --env-file <stamp dir>/stamp.env logs
docker compose -f src/platform/deploy/compose/compose.yaml -p <stamp> --env-file <stamp dir>/stamp.env stop
docker compose -f src/platform/deploy/compose/compose.yaml -p <stamp> --env-file <stamp dir>/stamp.env start
docker compose -f src/platform/deploy/compose/compose.yaml -p <stamp> --env-file <stamp dir>/stamp.env down --volumes --remove-orphans
```

- `up -d` starts the stamp. The API container is created only after the Migrator exited with `0`; a failing Migrator
  leaves the API uncreated. Use `ps -a` to see the exited Migrator.
- `ps` shows the state and health of the services; `logs` (optionally with a service name and `-f`) shows their output.
- `stop` and `start` keep the containers and volumes; the club survives a restart and no migration is applied again.
- `down --volumes --remove-orphans` removes every container, network, and volume of the stamp, and with them all of its
  data. The stamp directory is not touched.

## Sign in

Open `http://localhost:<port>` (the value of `SOCALYTICS_WEB_PORT`) and sign in with the first Club Admin account name
and the initial password from `stamp.env`. The required password change follows.

## Run a second stamp

Create a second stamp directory with the setup console, fill its `stamp.env` with another `SOCALYTICS_WEB_PORT`, club,
and stamp ID, and start it under another project name:

```powershell
$stampDirB = Join-Path $HOME 'socalytics-stamps/club-b'
dotnet run --project src/platform/SocAlytics.Platform.StampSetup -- --output $stampDirB
docker compose -f src/platform/deploy/compose/compose.yaml -p club-b --env-file "$stampDirB/stamp.env" up -d
```

The definition fixes no container, network, or volume name, so the resources of two project names never overlap and
the services of one stamp cannot reach the other's. Tearing down one stamp leaves the other running.

## Services, networks, and volumes

| Service | Image | Networks | Published | Notes |
| --- | --- | --- | --- | --- |
| `postgres` | `postgres:18` (pinned by digest) | `data` | no | Roles created by the AppHost's `PostgresInit` script |
| `nats` | `nats:2.14-alpine` (pinned by digest) | `data` | no | Runs with `nats.conf` from the stamp directory |
| `storage` | `rustfs/rustfs:1.0.1` (pinned by digest) | `data` | no | Object storage for recordings and results |
| `migrator` | `SOCALYTICS_MIGRATOR_IMAGE` | `data` | no | One-shot; must exit `0` before the API is created |
| `api` | `SOCALYTICS_API_IMAGE` | `data`, `app`, `egress` | no | Production environment; read-only root filesystem |
| `web` | `SOCALYTICS_WEB_IMAGE` | `app`, `edge` | `127.0.0.1:<port>` only | Serves the Web UI and forwards `/api` and `/alive` to the API |

- Networks: `data` and `app` are internal; `edge` carries only the published port; `egress` lets only the API reach
  external endpoints such as the SMTP relay.
- Volumes: `postgres-data`, `nats-data`, and `storage-data`.
- One replica of each service and no resource limits.

## Limitations

- The composition is for **self-hosted evaluation**. It is **not a production profile** and passes **no production
  preflight**.
- It does not replace the Aspire AppHost, which stays the supported local development entry point.
- The Web UI is published only on loopback (`127.0.0.1`) over plain HTTP without TLS; exposure beyond loopback is out
  of scope.
- Browser recording uploads are unavailable because object storage is internal and not published.
- No Analyst Manager can connect; `SOCALYTICS_PUBLIC_BASE_URI` has no endpoint behind it.
- There is no mail catcher: with the placeholder relay no mail is delivered, and delivery attempts fail and are
  recorded.
- `/health` reports healthy only once the stamp's Registry holds a resolvable default analysis workflow; until then the
  API is alive but not healthy.
- Host, ports, DNS, certificates, secret source, volume paths, replicas, and resource sizes stay **Open / Blocking** as
  [Production Deployment and Operations](../../../../docs/architecture/production-operations.md#versioned-production-profile)
  defines; the values used here are operator values or evaluation choices, never approved values.
