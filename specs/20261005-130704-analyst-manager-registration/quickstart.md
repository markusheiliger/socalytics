# Quickstart: Analyst Manager Registration

Validation guide for [spec.md](spec.md). It proves the feature with automated
tests on any OS and with one manual, composed run on Windows. Contracts:
[openapi.yaml](contracts/openapi.yaml), [local-control.md](contracts/local-control.md),
[proof-profiles.md](contracts/proof-profiles.md). Entities and states:
[data-model.md](data-model.md).

Passing these scenarios is development evidence only; it does not establish
deployment support or production readiness.

## Prerequisites

- The environment feature
  [20261007-115855-environment-verification-coverage](../20261007-115855-environment-verification-coverage/spec.md),
  Platform Persistence Foundation, and Club and Identity Foundation are merged.
- .NET SDK from `src/platform/global.json` (the Manager's `global.json` pins the
  same version).
- Docker running (Testcontainers PostgreSQL for platform integration tests).
- For the manual run only: Windows 10 1803+ or Windows Server 2019+, a trusted
  development certificate (`dotnet dev-certs https --trust`).

## 1. Platform build and tests

From the repository root:

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/SocAlytics.Platform.slnx --no-build
```

Expected: all host, architecture, and integration tests pass, including the
persistence structural test (registration table has the version trigger; no
`club_id`).

Focused Registry run:

```powershell
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~.Registry."
```

Scenarios this run must cover (platform side, test-side Manager simulator):

| Area | Expected outcome | Spec |
| --- | --- | --- |
| Pairing issue | `201` with code, handle, verification URI; `analyst-manager.pairing-issued` audited; wrong `stampId` refused | US1-1, FR-003 |
| Code submission | Registrar creates exactly one `pending_approval`; second, concurrent, expired, unknown, consumed, and malformed codes get byte-identical `400` bodies | US1-2, US1-9, SC-006 |
| Rate limiting | Excess pairing, status, and submission calls get `429` with no existence signal | FR-007 |
| Decisions | Registrar-only user gets `403 forbidden` and exactly one `authorization.denied`; missing `X-CSRF-Token` gets `403 antiforgery-failed`; Club Admin approve/reject succeeds; self-submit plus self-approve yields two events | US1-5, US1-7, US1-8 |
| Activation | Approval issues no token; valid proof makes `active` with a stable `managerId`; replayed, stale, wrong-key, wrong-challenge proofs refused | US1-3, US1-4, US1-11 |
| Expiry | Pairing, approval, and activation windows (driven by `FakeTimeProvider`) lead to `expired` with `expired` audit; later actions get `409` | US1-6, FR-009 |
| Tokens | Valid assertion plus DPoP issues a `DPoP` token; replayed `jti`, wrong audience, other key, wrong stamp, non-HTTPS refused; human endpoints reject DPoP and Manager endpoints reject cookies | FR-013–FR-015 |
| Revocation | After revoke, token and `self` calls fail with the inactive signal for every outstanding token | US3-1, SC-003 |
| Unregistration | Begin then complete moves `active → unregistering → unregistered` | US3-4 |
| Audit and secrets | Exactly one event per transition/decision with actor and correlation identifier; no generated secret appears in audit rows or captured logs | SC-007, SC-008 |

## 2. Analyst Manager build and tests

```powershell
dotnet restore src/analysts/manager/SocAlytics.Analysts.Manager.slnx
dotnet build src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-restore
dotnet test src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-build
```

Expected: all tests pass. On Linux and macOS the Windows CNG and DPAPI adapter
tests are reported as skipped; on Windows they run.

Scenarios this run must cover (fake platform implementing the contract,
test-only key and state stores, simulated runtime and work):

| Area | Expected outcome | Spec |
| --- | --- | --- |
| Register | `register` creates a key, shows verification URI and code once, follows the pairing to `active` | US1-1, US1-3 |
| Unsupported store | Production key store on Linux/macOS refuses `register` with `no-supported-key-store`; no key, state, or pairing request is created | US1-10, FR-002 |
| Restart restore | Intact state restores the same identity after token proof and status check; `paused` comes back `paused`; `running` waits for confirmation and preflight | US2-1–US2-3, SC-004 |
| Fail closed | Missing, unreadable, tampered state, missing key, key mismatch, stamp mismatch → `restore-failed`, no token request, diagnostic within 30 s | US2-4, US2-5, SC-005 |
| Offline | Platform unreachable keeps protected state, non-admitting, retries with backoff | US2-7 |
| Revocation | Inactive signal at start or during operation clears state and key, `revoked → unregistered` within one status interval | US2-6, US3-2, US3-3 |
| Unregister | Drain, begin/complete calls, state removed; unreachable platform keeps `unregistering` and retries | US3-4, FR-022 |
| Preflight | Healthy simulated runtime enters restored intent; each failing check yields `runtime-unavailable`, zero capabilities, remediation; failed accelerator probe not advertised; no integration configured stays unavailable | US4-1–US4-6, SC-010 |
| Operating controls | Pause blocks admission immediately; resume requires passed preflight; safe exit drains and keeps intent; drain timeout applies policy within timeout plus cleanup bound | US5-1–US5-6, SC-009 |
| Secrets | Captured logs, status output, diagnostics, and state files contain none of the generated secrets | SC-007 |

## 3. Composed manual run (Windows)

1. Start the platform: `dotnet run --project src/platform/SocAlytics.Platform.AppHost`.
   Note the API HTTPS endpoint shown in the Aspire dashboard (`Stamp:Id` is
   `local-dev-stamp`).
2. Start the Manager in a second terminal:
   `dotnet run --project src/analysts/manager/SocAlytics.Analysts.Manager -- run`.
3. In a third terminal:
   `dotnet run --project src/analysts/manager/SocAlytics.Analysts.Manager -- register --platform https://localhost:PORT`.
   Expected: verification address and a code `XXXX-XXXX-XXXX`; status shows
   `pairing` / `awaiting-submission` without the code.
4. Sign in as the configured first Club Admin through the Club and Identity
   `signIn` operation (keep the `__Host-socalytics-session` cookie and send the
   returned anti-forgery token as `X-CSRF-Token`), then call
   `submitAnalystManagerPairingCode` with the code and
   `approveAnalystManagerRegistration` with the returned `registrationId`.
   Expected: `pending_approval`, then `approved`; the `register` command then
   reports `active` within a few polling intervals.
5. `... -- status`. Expected: `active`, stamp `local-dev-stamp`, club name,
   `connected`, operating state `runtime-unavailable` with failed check
   `runtime-integration-configured` and no capabilities (no runtime
   integration ships in this slice).
6. `... -- pause`, then stop and restart the `run` terminal. Expected: the same
   `managerId` is restored without human action and intent is `paused`.
7. Call `revokeAnalystManagerRegistration` as Club Admin. Expected: within one
   status-check interval the Manager reports `unregistered`, the state file is
   gone, and `listAnalystManagerRegistrations` shows `revoked`.
8. Repeat steps 3–5, then run `... -- unregister`. Expected: registration ends
   `unregistered` on the platform and locally.
9. Stop everything with `Ctrl+C`.

## 4. Documentation check

```powershell
node .github/scripts/check-markdown.mjs
```

Expected: 0 issues.
