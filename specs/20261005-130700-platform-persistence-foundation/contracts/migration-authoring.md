# Contract: Migration Authoring (Consumed by Later Features)

These rules apply to every migration that any platform feature adds. They are
verified by the catalog validation, the Migrator, and the structural tests in
`SocAlytics.Platform.Integration.Tests`.

## Files

- Location: `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Migrations/`, included as `EmbeddedResource`.
- Name: `NNNN_<area>_<description>.sql`. `NNNN` is four digits, assigned at implementation time as the next free number in merge order. `<area>` is one lowercase token (`foundation`, `club`, `identityaccess`, `recordings`, `registry`, `analysis`, `agentorchestration`). `<description>` is lowercase words joined by `_`.
- The identity is the file name without `.sql`. It never changes.
- The checksum is `sha-256:` plus the lowercase hex SHA-256 of the BOM-stripped, LF-normalized UTF-8 content. Line-ending changes alone do not change it, but any other edit does.
- Migrations are forward-only. Never edit or delete an applied migration; deliver corrections as a new migration.
- Each migration runs in its own transaction together with its history record. Do not issue `COMMIT`, `ROLLBACK`, or statements that cannot run inside a transaction (for example `CREATE INDEX CONCURRENTLY`, `CREATE DATABASE`).
- Use schema-qualified names (`socalytics.<table>`). Do not create schemas, roles, or credentials, and do not grant privileges to roles other than `socalytics_app`. Table and sequence privileges for `socalytics_app` come from default privileges automatically.
- Never add a `club_id`, `clubid`, or `tenant_id` column or any other cross-club discriminator.

## Versioned Aggregates

For a mutable aggregate root:

```sql
-- column on the root only
version bigint NOT NULL DEFAULT 1
-- after CREATE TABLE
CALL socalytics.attach_version_trigger('socalytics.<root>');
```

For each child table whose rows always change with that root:

```sql
CALL socalytics.attach_aggregate_child_triggers(
    'socalytics.<child>', 'socalytics.<root>', '<child_key_column>'
    -- , '<root_key_column>' when it is not 'id'
);
```

Child tables carry no `version` column. Immutable tables carry no `version`.
Add every table without a `version` column to
`Tests/SocAlytics.Platform.Integration.Tests/Structure/PersistedTableClassifications.cs`
as `ChildOf(…)`, `Immutable`, or `Unversioned(reason)`.

| Database object | Signature | Effect |
| --- | --- | --- |
| `socalytics.advance_version()` | trigger function | `BEFORE UPDATE … WHEN (OLD.* IS DISTINCT FROM NEW.*)`: sets `NEW.version := OLD.version + 1`. |
| `socalytics.touch_aggregate_root()` | trigger function, arguments `(root_table, root_key, child_key)` | `AFTER INSERT OR DELETE` and `AFTER UPDATE WHEN changed`: advances the root's version by one per changed child row. |
| `socalytics.attach_version_trigger(target regclass)` | procedure (`EXECUTE` revoked from `PUBLIC`) | Creates trigger `<table>_version_advance`. |
| `socalytics.attach_aggregate_child_triggers(child regclass, root regclass, child_key name, root_key name DEFAULT 'id')` | procedure (`EXECUTE` revoked from `PUBLIC`) | Creates triggers `<child>_root_touch` and `<child>_root_touch_update`. |

## Data Migrations and Version Opt-Out

Data migrations advance the version of every changed versioned row by exactly
one, like any other write. Clients holding older versions then see a conflict.
A migration that must not advance versions declares the opt-out as its first
statement:

```sql
SET LOCAL socalytics.suppress_version = 'on';
```

The setting lasts only for that migration's transaction. It is honored only
for members of `socalytics_migrator`, so runtime code cannot use it.

## Handler Write Patterns

- **Edit**: `UPDATE socalytics.<root> SET … WHERE id = @Id AND version = @ExpectedVersion RETURNING version`, executed through `VersionedWrites` inside an `IUnitOfWork` scope. A conflict means `412` for edits that require `If-Match`.
- **Lifecycle transition**: `… WHERE id = @Id AND state = @ExpectedState RETURNING version`. A conflict means `409`.
- **Never** increment `version` manually as the sole change mechanism, and never derive meaning from the size of a version step.
