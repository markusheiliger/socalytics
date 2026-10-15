# Data Model: Analyst Manager Headless Server Profile

No new tables and no migration. The claimed profile is stored with the other
claimed device metadata in `socalytics.analyst_manager_registration.device_metadata`
(jsonb), created by
`0009_registry_analyst_manager_registration.sql`.

## Application Profile (Manager, Core)

| Value | API name | Meaning |
| --- | --- | --- |
| `Tray` | `tray` | The Avalonia tray executable `socalytics-manager` on Windows, macOS, or Linux desktops |
| `HeadlessServer` | `headless-server` | The console executable `socalytics-manager-server` on Linux, run as a systemd user service |

- Fixed by the executable when it builds the host; not configurable.
- Sent at pairing as `deviceMetadata.claimedApplicationProfile`; shown by
  `status` (`applicationProfile`).

## Device Metadata (platform domain, `Registry/DeviceMetadata.cs`)

Existing: `os`, `osVersion`, `architecture`, `managerVersion`,
`claimedKeyProtection`, `hostLabel`.

Added:

| Field | Type | Rules |
| --- | --- | --- |
| `claimedApplicationProfile` | `ClaimedApplicationProfile?` (`tray`, `headless-server`) | Optional on the wire; unknown values refused (`claimedApplicationProfile`: "Must be tray or headless-server."); `headless-server` requires `os` = `linux` ("The headless server profile is available only on Linux."); absent → not reported |

- Immutable for the registration (FR-009): stored once at pairing; no operation
  changes `device_metadata` afterwards.
- Read responses return it when present; absent means "not reported".

## Software Key Policy (platform options, `RegistryOptions.SoftwareKeys`)

| Value | Configuration | Software-backed keys accepted for | Allowed environments |
| --- | --- | --- | --- |
| `Refused` | `refused` (default) | no profile | all |
| `HeadlessServerOnly` | `headless-server-only` | `headless-server` claims | all |
| `All` | `all` | every claim, including absent | Development, Test (start-up validation fails elsewhere) |

Decision at pairing (`StartPairing`): accept a `software` key protection only
when the policy accepts the claimed profile; an absent claim counts as `tray`.
Refusal: `409` with the existing `software-key-not-allowed` problem type and a
detail naming hardware-backed key storage; audit `PairingRefused` with
`reason = software-key-not-allowed`, `claimedApplicationProfile`, and
`claimedKeyProtection` (no key material).

Stamp discovery derives:

- `softwareKeysAllowed` = policy is `All` (unchanged meaning for older Managers).
- `softwareKeysAllowedFor` = `[]`, `["headless-server"]`, or
  `["tray", "headless-server"]`.

## Runtime Probe and Preflight (Manager, Core `Runtime/`)

`RuntimeProbe.DaemonAccess`: `rootless`, `rootful-group`, or `null` (unknown).

Preflight check `account-isolation` (headless profile only):

| Daemon access | Account login shell | Check |
| --- | --- | --- |
| `rootful-group` | login-capable | fails: runtime unavailable, reason "dedicated non-login service account required" |
| `rootful-group` | no-login (`nologin`, `false`) | passes |
| `rootless` or `null` | any | not applicable (passes) |

A failed check makes preflight fail; the operating state becomes
runtime-unavailable with the check's detail, which the status report and the
fleet already carry.

## Headless Host Facts (Manager, `status`)

| Field | Values |
| --- | --- |
| `applicationProfile` | `tray`, `headless-server` |
| `lingering` (headless only) | `on`, `off`, `unknown` (marker directory unreadable) |

## Headless Service Unit

File `$XDG_CONFIG_HOME/systemd/user/socalytics-analyst-manager.service`
(default `~/.config/systemd/user/`), owned by the account; created by
`autostart on`, removed by `autostart off`; content in
[contracts/headless-manager.md](contracts/headless-manager.md).
