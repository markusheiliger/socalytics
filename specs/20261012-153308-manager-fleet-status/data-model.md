# Data Model: Analyst Manager Status Report and Fleet Read API

## `registry.analyst_manager_status` (one row per registration, no history)

| Column | Type | Rules |
| --- | --- | --- |
| `registration_id` | uuid PK, FK → registration | |
| `operating_state` | text | `running`, `paused`, `runtime-unavailable`, or `draining` |
| `paused_until` | timestamptz null | Only for `paused` |
| `runtime_unavailable_reason` | text null | Only for `runtime-unavailable`; at most 128 characters |
| `active_analysts`, `max_analysts` | int | `0 ≤ active ≤ max ≤ 1024` |
| `manager_version` | text | At most 64 characters |
| `received_at` | timestamptz | Platform time |
| `interval_seconds` | int | Interval in force at receipt |

Upserted per report and deleted when the registration is revoked or its
unregistration completes. There is no `version` column, so the table is
classified in `PersistedTableClassifications.cs`.

## Fleet entry (read model, Club Admins)

The existing registration DTO plus:

- `status`: `{ operatingState, pausedUntil, runtimeUnavailableReason, activeAnalysts, maxAnalysts, managerVersion, reportedAt }`,
  or `null`.
- `connectivity`: `online`, `offline`, or `not-applicable`.

## Registrar entry

The existing registration DTO without `status` and `connectivity`, limited to
registrations the member submitted.

## Manager status report (Manager side)

Built at each check: operating state, pause end, runtime-unavailable reason,
active attempts, effective concurrency limit, and version.
