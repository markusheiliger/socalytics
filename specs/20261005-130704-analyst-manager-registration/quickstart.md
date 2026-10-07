# Quickstart: Analyst Manager Registration

Validation guide for [spec.md](spec.md). Automated suites prove the feature on
the Linux runner; manual runs cover the Windows and macOS key stores, real
tray rendering, and autostart, plus one composed end-to-end run before merge.
Contracts: [openapi.yaml](contracts/openapi.yaml),
[local-control.md](contracts/local-control.md),
[proof-profiles.md](contracts/proof-profiles.md). Entities and states:
[data-model.md](data-model.md).

Passing these scenarios is development evidence only; it does not establish
deployment support or production readiness.

## Prerequisites

- Merged first: the environment feature
  [20261007-115855-environment-verification-coverage](../20261007-115855-environment-verification-coverage/spec.md)
  (installs SoftHSM2 and verifies the Manager solution and `contracts/`),
  Platform Persistence Foundation, and Club and Identity Foundation.
- .NET SDK from `src/platform/global.json`; the Manager's own `global.json`
  pins the same version.
- Docker running (Testcontainers PostgreSQL for platform integration tests).
- Linux, for the PKCS#11 tests: `softhsm2` (`/usr/lib/softhsm/libsofthsm2.so`).
  The tests create a per-user token in a temporary `SOFTHSM2_CONF` directory;
  on Linux they fail rather than skip when the module is missing.
- Manual runs: Windows 10 1803+ (with and without a TPM if possible); a Mac
  with the Swift bridge built (`swift build` in the bridge package, Xcode
  command-line tools); a trusted development certificate
  (`dotnet dev-certs https --trust`).

## 1. Platform build, tests, and contracts

From the repository root:

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/SocAlytics.Platform.slnx --no-build
dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests --no-build
```

Focused Registry run:

```powershell
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~.Registry."
```

| Area | Expected outcome | Spec |
| --- | --- | --- |
| Pairing issue | `201` with code, fingerprint, handle, verification URI; source address, time, and `claimedKeyProtection` stored; `analyst-manager.pairing-issued` audited; wrong `stampId` refused | US1-1, FR-003, FR-038, FR-040 |
| Software-key gate | With `Registry:AllowSoftwareKeys` false, `claimedKeyProtection` `software` gets `409 analyst-manager-software-key-not-allowed` and discovery reports `softwareKeysAllowed: false`; with true it pairs; no other value changes any authorization outcome | FR-041 |
| Code submission | Registrar with code and matching fingerprint creates exactly one `pending_approval`; second, concurrent, expired, unknown, consumed, malformed codes and a wrong fingerprint get identical `400` bodies; a wrong fingerprint leaves the code usable and is audited as `analyst-manager.submission-refused`; the third mismatch expires the pairing | US1-2, US1-9, FR-040, SC-006 |
| Decision view | Registration reads return the fingerprint, `pairingOrigin` (source address, time), `claimedKeyProtection`, and the `approvalNotice` while pending | FR-040, FR-041 |
| Rate limiting | Excess pairing, status, and submission calls get `429 analyst-manager-rate-limited` with no existence signal | FR-007 |
| Decisions | Registrar-only user gets `403 forbidden` and exactly one `authorization.denied`; missing `X-CSRF-Token` gets `403 antiforgery-failed`; self-submit plus self-approve yields two events | US1-5, US1-7, US1-8 |
| Activation | Approval issues no token; valid proof makes `active`; replayed, stale, wrong-key, wrong-challenge proofs refused | US1-3, US1-4, US1-11 |
| Expiry | Windows driven by `FakeTimeProvider` lead to `expired` with audit; later actions get `409` | US1-6, FR-009 |
| Tokens | Valid assertion plus DPoP issues a `DPoP` token; replayed `jti`, wrong audience, other key, wrong stamp, non-HTTPS refused; human endpoints reject DPoP, Manager endpoints reject cookies | FR-013–FR-015 |
| Revocation and unregistration | Inactive signal for every outstanding token after revoke; `active → unregistering → unregistered` | US3-1, US3-4, SC-003 |
| Golden fixtures | Every request vector in `contracts/analyst-manager/registration/v1/` (including the fingerprint and wrong-fingerprint submission vectors) yields its manifest outcome and response shape; the manifest and index entry validate in Contracts.Tests | FR-039 |
| Audit and secrets | Exactly one event per transition or decision; no generated secret in audit rows or logs | SC-007, SC-008 |

## 2. Analyst Manager build and tests (Linux runner)

```powershell
dotnet restore src/analysts/manager/SocAlytics.Analysts.Manager.slnx
dotnet build src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-restore
dotnet test src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-build
```

Expected on Linux: all tests pass with no display; Windows CNG, Windows
autostart and socket-ACL, and macOS bridge tests are reported as skipped.

| Area | Expected outcome | Spec |
| --- | --- | --- |
| PKCS#11 provider (SoftHSM2) | Key generated with `CKA_EXTRACTABLE` false; `CKA_VALUE` unreadable; wrap refused; ES256 JWS verifies with the public point; key reloads by `CKA_ID`; `libdl` resolver active; SoftHSM refused when not registered by the test host; it reports `software` | FR-002, FR-038 |
| Software-key gate | When the fake platform's discovery reports `softwareKeysAllowed: false`, a software-only provider refuses registration with `software-key-not-allowed` before creating a key; with `true` it pairs and claims `software` | FR-041 |
| Unsupported store | No provider, module, or token → registration refused with `no-supported-key-store`; nothing created | US1-10 |
| Register | Register flow shows verification address, code, and device fingerprint; the local fingerprint equals the platform's (a mismatch aborts); `claimedKeyProtection` sent; follows pairing to `active` against the fake platform | US1-1, US1-3, FR-040 |
| Signed state | Intact state restores the same identity after token proof and status check; edited payload, swapped signature, copy signed by another key, wrong permissions, missing key, stamp mismatch → `restore-failed` with a diagnostic within 30 s and no token request | US2-1–US2-5, SC-004, SC-005 |
| Offline and revocation | Unreachable platform keeps state and retries; inactive signal clears state and key within one status interval | US2-6, US2-7, US3-2, US3-3 |
| Unregister | Drain, begin and complete calls, state removed; unreachable platform keeps `unregistering` and retries | US3-4, FR-022 |
| Preflight | Simulated runtime: pass enters restored intent; each failing check → `runtime-unavailable` with remediation and zero capabilities; failed accelerator not advertised; no integration configured stays unavailable | US4-1–US4-6, SC-010 |
| Operating controls | Pause blocks admission at once; resume needs passed preflight; safe exit drains and keeps intent; drain timeout policy within timeout plus cleanup bound; session end uses its bound | US5-1–US5-6, SC-009 |
| Timed pause | Admission stops at once; status shows end and remaining time; pause survives restart; auto-resume ≤ 5 s after end when Active and preflight passed; otherwise intent `running` and stays unavailable; early resume | US5-7, US5-8, FR-036, SC-012 |
| Autostart (XDG) | Toggle writes and removes the `.desktop` file under a temporary `XDG_CONFIG_HOME`; read-back drives the check mark; Run-key and plist generation checked as pure functions | US5-9, FR-037 |
| Tray and status window (headless) | Tray menu commands invoke worker operations; enabled states follow the state machine; status window shows all FR-024 fields; without a tray host the status window opens at start | FR-024, edge case "no tray area" |
| Local control and single instance | Owner-only socket; peer uid check rejects others; second start hands off with `show-status` and exits; stale socket replaced; every message validates against the schema | FR-024, FR-035 |
| Golden fixtures | Manager derives the fixture fingerprint from the test key, parses every response vector, and builds request vectors whose headers and claims match (signatures verify with the fixture key) | FR-039 |
| Secrets | Logs, status, windows' view models, diagnostics, and the state file contain none of the generated secrets or the PIN | SC-007 |

## 3. Manual verification of OS-specific parts

Record the outcome of each step in the pull request before merge.

### Windows

1. Start `socalytics-manager` (tray icon appears). Register against the composed
   platform (section 4) and check that the Club Admin view shows
   claimed key protection `tpm` on a TPM host. On a host without a usable TPM,
   registration is refused with `software-key-not-allowed` when the API runs
   with `Registry__AllowSoftwareKeys=false`, and pairs claiming `software` with
   the AppHost's development value `true`.
2. `certutil -user -key -csp "Microsoft Platform Crypto Provider"` (or the
   software KSP) lists the `SocAlytics` key; an export attempt fails
   (`NTE_NOT_SUPPORTED`).
3. Toggle "Start at sign-in": the `HKCU\…\Run` value `SocAlytics.AnalystManager`
   appears and disappears; sign out and in with it enabled: the Manager starts in
   the tray and restores its registration.
4. Start a second instance: the first instance's status window comes forward.
   From another user account, `socalytics-manager status` fails with exit code 3.
5. Edit one byte of `registration.state` or copy it into another account: the
   next start shows `restore-failed` (`state-signature` or `key-missing`).

### macOS (after the Mac spike)

1. Build the bridge, run the app bundle: the menu-bar item appears.
2. Register: the claim is `secure-enclave` on Apple silicon or T2 Macs; other
   Macs are refused unless software keys are allowed, then claim `software`;
   signatures verify on the platform.
3. Toggle "Start at sign-in": `SMAppService` reports enabled (or the LaunchAgent
   plist exists); after re-login the Manager starts and restores.
4. Repeat Windows steps 4 and 5 (peer check via `getpeereid`).

### Linux desktop (optional, real tray and TPM)

1. GNOME with the AppIndicator extension or KDE: the tray icon appears; vanilla
   GNOME without it: the status window opens with the same controls.
2. With tpm2-pkcs11 provisioned (`tpm2_ptool init`, `tpm2_ptool addtoken
   --label=socalytics-device`, user in group `tss`): registration reports
   claimed `pkcs11-token`.

## 4. Composed end-to-end run (before merge)

1. Start the platform: `dotnet run --project src/platform/SocAlytics.Platform.AppHost`
   (API HTTPS endpoint in the Aspire dashboard; `Stamp:Id` is
   `local-dev-stamp`).
2. Start the Manager: `dotnet run --project src/analysts/manager/SocAlytics.Analysts.Manager`.
   Choose "Register…" in the tray and enter `https://localhost:PORT`.
   Expected: verification address, code `XXXX-XXXX-XXXX`, and device
   fingerprint `XXXX-XXXX`; the status window shows `pairing` and the
   fingerprint without the code.
3. Sign in as the first Club Admin through the Club and Identity `signIn`
   operation (cookie `__Host-socalytics-session`, header `X-CSRF-Token`), call
   `submitAnalystManagerPairingCode` first with a wrong fingerprint (expected:
   the generic `400`), then with the code and the fingerprint read from the
   Manager. `getAnalystManagerRegistration` shows the fingerprint, the pairing
   origin, the claimed key protection, and the approval notice; then call
   `approveAnalystManagerRegistration`. Expected: the tray reports `active`
   within a few polling intervals.
4. Status shows stamp `local-dev-stamp`, the club, `connected`, operating state
   `runtime-unavailable` with failed check `runtime-integration-configured`.
5. "Pause for ▸ 30 min", quit with "Safe exit", start again: same Manager
   identity, still paused with the original end time.
6. Call `revokeAnalystManagerRegistration`: within one status interval the
   Manager shows `unregistered` and `registration.state` is gone.
7. Register again, then "Unregister…": the platform shows `unregistered`.

## 5. Documentation check

```powershell
node .github/scripts/check-markdown.mjs
```

Expected: 0 issues.
