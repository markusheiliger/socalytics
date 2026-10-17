# Analyst Manager

## Purpose

The Analyst Manager is a per-user desktop application (Avalonia tray app plus a UI-free Core worker) that registers a host with the SocAlytics platform through pairing, approval, and activation, authenticates with DPoP machine tokens, and honors pause, resume, drain, and unregister controls. It has no Analyst SDK or capabilities yet.

This is development evidence, not production readiness. Installers, signing, updates, accessibility, attestation, and production values remain open.

Planned, not implemented: installers and updates with Velopack (per-user, delta updates, one AppImage for both Linux profiles, signing after an unsigned spike), served by each stamp as an update feed; and a status report for the Web UI fleet view. See [Application Profiles](../../../docs/architecture/analyst-manager.md#application-profiles).

## Build, Run, And Test

From the repository root:

```powershell
dotnet restore src/analysts/manager/SocAlytics.Analysts.Manager.slnx
dotnet build src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-restore
dotnet test src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-build
dotnet run --project src/analysts/manager/SocAlytics.Analysts.Manager
dotnet run --project src/analysts/manager/SocAlytics.Analysts.Manager.Server
```

The second run command starts the headless server profile (Linux only; see [Headless Server Profile](#headless-server-profile)).

The Manager has its own `global.json`, pinned to the platform SDK. Linux tests need SoftHSM2. Windows and macOS key-store tests are skipped elsewhere and verified manually.

## Controls

The operator uses the tray menu, or the status window where the desktop has no tray area. The same local-control protocol (one owner-only Unix domain socket per user) backs the scripting CLI of the tray executable `socalytics-manager` and of the headless executable `socalytics-manager-server` (identical commands):

| Command | Effect |
| --- | --- |
| `status [--json]` | Show status |
| `register --platform URL [--stamp-id ID] [--host-label TEXT] [--no-wait]` | Start pairing |
| `pause [--for MINUTES \| --until RFC3339]` | Pause |
| `resume` | Resume |
| `exit` | Safe exit (drain, keep registration) |
| `unregister [--discard-local]` | Unregister, or discard unusable local state |
| `concurrency [N]` | Show the concurrency limit, or set the host-local limit (a positive integer) |
| `autostart [on \| off]` | Per-user autostart |

Exit codes: `0` success, `1` refused by the Manager (error code printed), `2` usage error, `3` Manager not running or socket not accessible. Message shapes are defined by [contracts/local-control.schema.json](contracts/local-control.schema.json); the behavior is described in the feature's `contracts/local-control.md` under `specs/archive/20261005-130704-analyst-manager-registration/`.

## Work Execution

An active Manager requests expiring queue credentials from the platform, pulls Analyst jobs from the NATS work queue, runs them through its runtime adapter, and reports completion or failure. The effective concurrency is the smallest of the host-local limit (`concurrency N`), the stamp maximum, and the runtime maximum; `concurrency` without an argument shows all of them. Pause, drain, revocation, and unregistration stop acquiring work, and revocation also stops running work and returns the Manager to Unregistered.

The `simulated` runtime profile (`Runtime:Profile`, configured through `Runtime:Simulated`) provides a simulated runtime and Analyst with configurable outcomes (succeed, fail, hang, crash). It is registered only in Development and Test. No real Analyst execution, Analyst SDK, or container runtime exists yet.

After a successful run, the Manager checks the Analyst's result manifest in the job's result mount against the `contracts/analysis/result-manifest/v1` schema, requests per-object result write grants from the platform (`contracts/analysis/result-grants/v1`), stores the manifest and artifacts through them, and only then reports completion. A manifest that violates the contract is never uploaded and the attempt is reported as failed. The simulated Analyst's `ResultMode` (per capability in `Runtime:Simulated:Capabilities`) is `valid` by default; `scope-mismatch`, `provenance-mismatch`, `lineage-mismatch`, `artifact-missing`, `validity-invalid`, and `contract-invalid` deliberately violate one acceptance rule (Development and Test only).

Before launching an attempt whose job has upstream dependencies, the Manager reads the upstream results (`contracts/analysis/upstream-results/v1`), downloads each manifest and artifact through the per-object read grants, verifies their digests, and stages them with a `coverage.json` per capability under the attempt's `inputs/` directory, which is mounted read-only for the Analyst. Staging is bounded by `Work:Staging:MaxBytesPerAttempt` (development value 2 GiB). A digest mismatch, storage or read failure, size limit, or full disk fails the attempt without launching the Analyst. The attempt directory, including `inputs/`, is deleted when the attempt ends, and all attempt directories are deleted at Manager start. The simulated Analyst builds a match-scope manifest's `upstreamLineage` from the staged inputs; the `ignore-inputs` result mode (Development and Test only) leaves the lineage empty so acceptance rejects it.

## Status: Queue Depth and Host Utilization

The status window and `status [--json]` show approximate guidance about waiting work and the load of the host:

- **Queue depth**: after each successful registration check while the registration is active (in every operating state), the Manager reads `GET /api/v1/analyst-managers/self/queue-depth` for the capabilities of its current queue credentials and shows the waiting count per capability, the total, and the age of the observation. Only the latest observation is kept in memory. It is shown as stale after a failed request or when no request succeeded within two status check intervals. Without an observation the status says `no information yet`, `no capability assigned`, or `unavailable`. Admission of work never depends on the value.
- **Host utilization**: a sampler reads CPU and memory of the host locally every 5 seconds (`/proc` on Linux, `GetSystemTimes` and `GlobalMemoryStatusEx` on Windows, `host_statistics` and `sysctl` on macOS) and keeps only the latest sample in memory; the first sample has no CPU value and unreadable values show `not available`. Accelerator utilization is not shown yet.

Neither value is persisted or sent to the platform: the status report and the fleet view carry neither. The `queueDepth` and `hostUtilization` members of the `status` response are defined by [contracts/local-control.schema.json](contracts/local-control.schema.json). Windows and macOS sampling is verified on those systems.

## Device Key Providers

| OS | Provider |
| --- | --- |
| Windows | CNG (`windows-cng`) |
| macOS | Secure Enclave, with a Keychain fallback, through the Swift bridge (`macos-bridge`) |
| Linux | PKCS#11 (`pkcs11`) with a tpm2-pkcs11 token |

A software-backed key is used only if the platform stamp allows software keys for the Manager's application profile (`Registry:SoftwareKeys`: `refused` by default, `headless-server-only`, or `all` in development and test only; stamp discovery lists the accepted profiles and the Manager refuses to start a pairing that would be refused).

### Provisioning a tpm2-pkcs11 token (Linux)

```sh
tpm2_ptool init
tpm2_ptool addtoken --label=socalytics-device --userpin=<random value>
```

Write the same value to the owner-only file `pkcs11.pin` (mode `0600`) in the Manager state directory, and make the token store accessible to group `tss`. The state directory defaults to `$XDG_STATE_HOME/socalytics/analyst-manager` (fallback `~/.local/state/socalytics/analyst-manager`) on Linux, `~/Library/Application Support/SocAlytics/AnalystManager` on macOS, and `%LOCALAPPDATA%\SocAlytics\AnalystManager` on Windows; override it with `Manager:StateDirectory`. The module path (`KeyStore:Pkcs11:ModulePath`, default `/usr/lib/x86_64-linux-gnu/pkcs11/libtpm2_pkcs11.so`) and token label (`KeyStore:Pkcs11:TokenLabel`, default `socalytics-device`) are configurable. The PIN is never logged or returned.

## Autostart

- Windows: per-user `Run` registry key.
- Linux (tray profile): XDG autostart file `socalytics-analyst-manager.desktop` in `$XDG_CONFIG_HOME/autostart` (fallback `~/.config/autostart`).
- Linux (headless server profile): systemd user unit `socalytics-analyst-manager.service`, managed with `systemctl --user` by `autostart on|off`.
- macOS: per-user LaunchAgent plist in `~/Library/LaunchAgents`.

The tray menu item "Start at sign-in" shows the read-back state.

## Headless Server Profile

The headless profile is the Linux-only console executable `socalytics-manager-server` (project `SocAlytics.Analysts.Manager.Server`). It runs the Core worker without Avalonia as a systemd user service and has the same CLI commands as the tray executable (`status`, `register`, `pause`, `resume`, `exit`, `unregister`, `concurrency`, `autostart`). Windows and macOS refuse to start it. It claims the `headless-server` application profile at pairing; the platform shows it as claimed.

Supported distributions: Ubuntu Server 22.04 and 24.04 LTS. Installers are not provided: publish the executable with `dotnet publish src/analysts/manager/SocAlytics.Analysts.Manager.Server -r linux-x64` and copy it to the host as the account that runs it.

**Lingering.** A one-time administrator step lets the user service run without a login session: `sudo loginctl enable-linger <user>`. The Manager reads systemd's lingering marker at start and `status` shows it; when it is off, the Manager only warns and names this command.

**Service account.** With a rootless Podman or Docker runtime, any account may run the Manager. With a rootful Docker daemon reached through the `docker` group, the account is root-equivalent, so run the Manager under a dedicated non-login service account (for example `sudo useradd --system --create-home --shell /usr/sbin/nologin socalytics-manager`). Under an account with a login shell and a rootful runtime, a preflight check fails: the Manager is runtime-unavailable with the account reason, admits no work, and stays registered. Linux runtime adapters do not exist yet; the rule is exercised with the simulated runtime (`Runtime:Simulated:DaemonAccess`).

**Autostart.** `socalytics-manager-server autostart on` writes and enables the systemd user unit `socalytics-analyst-manager.service` (restarts after a crash, stops through the safe exit); `autostart off` disables and removes it.

**Registration.** `socalytics-manager-server register --platform <stamp URL> [--host-label TEXT]`, then complete pairing and approval in the Web UI. Without a TPM, the stamp must allow software keys for headless servers (`Registry:SoftwareKeys` `headless-server-only`).

**Logs.** The service logs to the journal: `journalctl --user -u socalytics-analyst-manager.service`.

### Manual validation (open)

The manual validation on Ubuntu Server 22.04 and 24.04 LTS has **not** been performed; it is open evidence until a person runs it after the merge and records the outcome here (hosts without a graphical environment, with a TPM, a virtual TPM, or a stamp that allows software keys for headless servers):

| Release | Outcome |
| --- | --- |
| Ubuntu Server 22.04 LTS | Open |
| Ubuntu Server 24.04 LTS | Open |

1. Publish for `linux-x64` and copy the executable to the host as the account that runs it.
2. Start it without lingering: the warning names `loginctl enable-linger`.
3. Run `sudo loginctl enable-linger <user>` as an administrator.
4. Run `socalytics-manager-server register --platform <stamp URL>`, enter a host label, complete pairing on another device, and approve it as a Club Admin; the decision view and the fleet show `Headless server (claimed)`.
5. Run `socalytics-manager-server autostart on`, reboot, and do not sign in; within 60 seconds after boot, `status` from an SSH session shows the Manager running and connected.
6. Run `pause --for 10m`, `resume`, `concurrency 2`, and `status`; the results match the tray profile.
7. Run `systemctl --user stop socalytics-analyst-manager.service` while a simulated attempt runs: the Manager drains and exits; `systemctl --user start` brings it back.
8. Kill the process with `kill -9`: systemd restarts it after about 10 seconds.

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
