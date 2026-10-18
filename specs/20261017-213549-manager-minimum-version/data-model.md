# Data Model: Analyst Manager Minimum Version

No database table or migration is added. All values are configuration, existing stored data, or in-memory state.

## Minimum Analyst Manager Version (platform configuration)

| Field | Type | Rules |
| --- | --- | --- |
| `Registry:ManagerVersion:Minimum` | string, optional | Empty or absent: no minimum. Otherwise `MAJOR.MINOR.PATCH`, each part a non-negative decimal integer, at most 64 characters. Invalid at start: the platform does not start. Invalid on reload: the last valid value stays in effect and a warning is logged. |

Not stored per registration; read on every status response, credential request, claim, and fleet read.

## ManagerVersion (platform domain value; Manager Core comparer)

- Parsed from `MAJOR.MINOR.PATCH`; ordered by major, then minor, then patch.
- `IsBelow(reported, minimum)`: false when no minimum is configured; otherwise true when `reported` is missing, not
  well formed, or orders before `minimum`. Equal versions are not below.

## Effective Manager Version (derived)

| Source | When used |
| --- | --- |
| `manager_version` of the latest stored status report | A status report exists for the registration |
| `DeviceMetadata.ManagerVersion` (claimed at pairing) | No status report exists (including pending requests) |

Used by the platform gate (queue credentials, claims) and by the fleet read. Never written.

## Fleet entry additions (Club Admin views only)

| Field | Type | Rules |
| --- | --- | --- |
| `minimumManagerVersion` | string or null | The configured minimum; null when none applies |
| `belowMinimumVersion` | boolean | `IsBelow(effective version, minimum)`; false when no minimum |

Serialized only with fleet operations data (`IncludesOperations`), for every current registration including pending
requests; Registrar views carry neither.

## MinimumVersionState (Manager, in memory)

| Field | Type | Rules |
| --- | --- | --- |
| `BuildVersion` | string | From `ManagerBuildVersion`; the same value the status report and pairing metadata send |
| `Minimum` | string or null | Last value from a successful status response; null = none |
| `RefusedByPlatform` | boolean | Set by a version problem response on a claim or credential request; cleared by the next successful status response |
| `IsBelow` | boolean | `RefusedByPlatform` or comparer says below; an unparsable `Minimum` counts as below (fail closed) |

State transitions: `NotBelow` → `Below` on a status response with a higher or unparsable minimum, or on a version
refusal; `Below` → `NotBelow` on a status response whose minimum is null or at or below the build version. Restart
starts `NotBelow` until the first check (the platform's gate still refuses work).

## Update notice (Manager status surfaces)

| Field | Type | Rules |
| --- | --- | --- |
| `update.required` | boolean | Equals `IsBelow` |
| `update.managerVersion` | string | Build version |
| `update.minimumVersion` | string | The minimum as received (raw when unparsable) |

Present in the local-control `status` result, the CLI plain and `--json` output, the tray status window, and a tray
menu status line while `required` is true.
