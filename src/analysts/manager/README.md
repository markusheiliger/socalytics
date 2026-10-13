# Analyst Manager

## Purpose

The Analyst Manager is a per-user desktop application (Avalonia tray app plus a UI-free Core worker) that registers a host with the SocAlytics platform through pairing, approval, and activation, authenticates with DPoP machine tokens, and honors pause, resume, drain, and unregister controls. It has no Analyst SDK or capabilities yet.

This is development evidence, not production readiness. Installers, signing, updates, accessibility, attestation, and production values remain open.

Planned, not implemented: a headless server profile for Linux, in which the Core worker runs without Avalonia as a systemd user service with lingering and is controlled only by the CLI; installers and updates with Velopack (per-user, delta updates, one AppImage for both Linux profiles, signing after an unsigned spike), served by each stamp as an update feed; and a status report for the Web UI fleet view. See [Application Profiles](../../../docs/architecture/analyst-manager.md#application-profiles).

## Build, Run, And Test

From the repository root:

```powershell
dotnet restore src/analysts/manager/SocAlytics.Analysts.Manager.slnx
dotnet build src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-restore
dotnet test src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-build
dotnet run --project src/analysts/manager/SocAlytics.Analysts.Manager
```

The Manager has its own `global.json`, pinned to the platform SDK. Linux tests need SoftHSM2. Windows and macOS key-store tests are skipped elsewhere and verified manually.

## Controls

The operator uses the tray menu, or the status window where the desktop has no tray area. The same local-control protocol (one owner-only Unix domain socket per user) backs the scripting CLI of the tray executable `socalytics-manager`:

| Command | Effect |
| --- | --- |
| `status [--json]` | Show status |
| `register --platform URL [--stamp-id ID] [--host-label TEXT] [--no-wait]` | Start pairing |
| `pause [--for MINUTES \| --until RFC3339]` | Pause |
| `resume` | Resume |
| `exit` | Safe exit (drain, keep registration) |
| `unregister [--discard-local]` | Unregister, or discard unusable local state |
| `autostart [on \| off]` | Per-user autostart |

Exit codes: `0` success, `1` refused by the Manager (error code printed), `2` usage error, `3` Manager not running or socket not accessible. Message shapes are defined by [contracts/local-control.schema.json](contracts/local-control.schema.json); the behavior is described in the feature's `contracts/local-control.md` under `specs/archive/20261005-130704-analyst-manager-registration/`.

## Device Key Providers

| OS | Provider |
| --- | --- |
| Windows | CNG (`windows-cng`) |
| macOS | Secure Enclave, with a Keychain fallback, through the Swift bridge (`macos-bridge`) |
| Linux | PKCS#11 (`pkcs11`) with a tpm2-pkcs11 token |

A software-backed key is used only if the platform stamp allows software keys (`Registry:AllowSoftwareKeys`, development and test only).

### Provisioning a tpm2-pkcs11 token (Linux)

```sh
tpm2_ptool init
tpm2_ptool addtoken --label=socalytics-device --userpin=<random value>
```

Write the same value to the owner-only file `pkcs11.pin` (mode `0600`) in the Manager state directory, and make the token store accessible to group `tss`. The state directory defaults to `$XDG_STATE_HOME/socalytics/analyst-manager` (fallback `~/.local/state/socalytics/analyst-manager`) on Linux, `~/Library/Application Support/SocAlytics/AnalystManager` on macOS, and `%LOCALAPPDATA%\SocAlytics\AnalystManager` on Windows; override it with `Manager:StateDirectory`. The module path (`KeyStore:Pkcs11:ModulePath`, default `/usr/lib/x86_64-linux-gnu/pkcs11/libtpm2_pkcs11.so`) and token label (`KeyStore:Pkcs11:TokenLabel`, default `socalytics-device`) are configurable. The PIN is never logged or returned.

## Autostart

- Windows: per-user `Run` registry key.
- Linux: XDG autostart file `socalytics-analyst-manager.desktop` in `$XDG_CONFIG_HOME/autostart` (fallback `~/.config/autostart`).
- macOS: per-user LaunchAgent plist in `~/Library/LaunchAgents`.

The tray menu item "Start at sign-in" shows the read-back state.

## macOS Bridge

On macOS, build the Swift bridge into an existing app output directory:

```sh
native/macos/SocAlyticsMacBridge/build.sh <existing-app-output-directory>
```

See [native/macos/SocAlyticsMacBridge/README.md](native/macos/SocAlyticsMacBridge/README.md). It is not part of the solution and the Linux runner never builds it.

## Non-Production Configuration Defaults

These defaults are development values, not adopted production values:

- `Operating:DrainTimeout` 5 minutes, `Operating:DrainTimeoutPolicy` `CancelRequest`, `Operating:CleanupBound` 30 seconds, `Operating:SessionEndDrainBound` 5 seconds, `Operating:MaxTimedPause` 7 days.
- `Platform:StatusCheckInterval` 60 seconds; `Platform:RetryBackoff` 2 seconds initial, 60 seconds maximum.
- `Runtime:PreflightInterval` 60 seconds.

With each status check the active Manager also sends a status report (operating state, pause end, the first failed preflight check as the runtime-unavailable reason, active and maximum Analysts, version); the platform returns the interval in force, which replaces `Platform:StatusCheckInterval`. A refused report is logged and does not affect registration.

## Host Label

`register --host-label TEXT` names the Manager in the platform's fleet view. The label is trimmed, 1–64 characters, and has no control characters; the Manager validates it locally and the platform validates it again.

## Device Fingerprint

The register dialog, the status window, and `status` show a device fingerprint next to the pairing code. The operator gives both to the Registrar in person; the platform compares the fingerprint and rejects a pairing after `Registry:Pairing:MaxFingerprintMismatches` mismatches. The fingerprint is not a secret; the pairing code appears only in the register dialog and the `register` result.
