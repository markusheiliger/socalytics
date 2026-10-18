# Data Model: Compose Stamp Composition for Self-Hosted Evaluation

No database table, migration, or API resource changes. The feature's entities are deployment artifacts; their full
interface is in [contracts/compose-stamp.md](contracts/compose-stamp.md).

## Stamp composition

The committed definition `src/platform/deploy/compose/compose.yaml`.

- **Services**: `postgres`, `nats`, `storage`, `migrator`, `api`, `web`; one replica each.
- **Networks**: `data` (internal), `app` (internal), `edge`, `egress`; all project-scoped.
- **Volumes**: `postgres-data`, `nats-data`, `storage-data`; all project-scoped.
- **Rules**: no top-level `name`, no `container_name`, no fixed network or volume `name`; only `web` publishes a port,
  on `127.0.0.1`; every infrastructure image pinned by digest; every operator value interpolated as required.

## Stamp

One running instance of the composition.

- **Identity**: the Compose project name (`-p`); every container, network, and volume carries the project label.
- **Owns**: its volumes, networks, credentials, one club, its database, JetStream streams, and bucket.
- **Lifecycle**: `created` (setup output exists) → `migrating` (Migrator runs) → `started` (API running; alive) →
  `healthy` (resolvable default analysis workflow exists) → `stopped` (`docker compose stop`; volumes kept) →
  `removed` (`docker compose down --volumes`; nothing remains). A failed Migrator ends in `failed-migration`: the API is
  never created.

## Stamp environment file

The operator's `stamp.env`, derived from `src/platform/deploy/compose/stamp.env.template`.

- **Secret values** (filled by the setup step, never defaulted): PostgreSQL administrator, migrator, and application
  passwords; object storage access and secret keys; NATS operator, stamp account, and system account seeds; the first
  Club Admin's initial credential.
- **Operator values** (required, empty in the template): the three image references, the release version, the
  published port, the stamp ID, the public base URI placeholder, the club display name and first Club Admin account
  name, and the SMTP relay host, port, security mode, and sender.
- **Optional values**: SMTP relay user name and password (both or neither).
- **Validation**: a missing or empty required value fails `docker compose config` and `up` with a message naming it;
  the API's existing options validation rejects invalid values at start.

## Setup output

The stamp directory that `SocAlytics.Platform.StampSetup` creates.

- **Contents**: `stamp.env` (secrets filled, `SOCALYTICS_STAMP_DIR` set, operator values empty) and `nats.conf` (the
  NATS server configuration trusting the stamp's operator, stamp account, and system account).
- **Rules**: the directory must not exist before the run; nothing is written into an existing directory or into a
  directory inside a Git working tree; on Unix `stamp.env` is readable only by its owner.
