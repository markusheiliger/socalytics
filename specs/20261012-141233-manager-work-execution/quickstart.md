# Quickstart: Validate Analyst Manager Work Execution

Development evidence only. This does not show deployment support or production
readiness.

## Prerequisites

- .NET SDK from `src/platform/global.json` (the same pin as
  `src/analysts/manager/global.json`).
- A running Docker-compatible container runtime (PostgreSQL and NATS
  containers).
- Linux with SoftHSM2 (`/usr/lib/softhsm/libsofthsm2.so`, `softhsm2-util`) for
  the end-to-end test and the Manager's PKCS#11 tests. On Windows and macOS
  these tests are skipped with a reason.

## Automated validation

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/SocAlytics.Platform.slnx --no-build
dotnet restore src/analysts/manager/SocAlytics.Analysts.Manager.slnx
dotnet build src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-restore
dotnet test src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-build
```

What each project proves:

| Project | Covers |
| --- | --- |
| `SocAlytics.Platform.Integration.Tests` | Queue-credential issuance and refusals; per-capability consumers; JWT permissions (allowed pull and acknowledge, denied everything else); revocation fencing and budget consumption; immediate disconnect after revocation; retried account pushes |
| `SocAlytics.Platform.Contracts.Tests` and Manager `GoldenFixtureTests` | `contracts/analyst-manager/queue-credentials/v1/` fixtures on both sides |
| `SocAlytics.Analysts.Manager.Tests` | Admission and concurrency, pause and timed pause, heartbeat cadence and backoff, execution timeout, fencing and abandonment, completion retries, drain policies, revocation stop, restart behavior, the simulated-Analyst environment guard, status, local control, and the CLI |
| `SocAlytics.Platform.EndToEnd.Tests` | The full loop of SC-010: a registered Manager pulls, claims, heartbeats, and completes; a failure retries; revocation fences and disconnects |

## Manual exploration (optional)

1. `dotnet run --project src/platform/SocAlytics.Platform.AppHost` starts
   PostgreSQL, NATS in JWT mode with generated development trust, the
   Migrator, and the API.
2. Run the Manager in the `Development` environment with
   `Runtime:Profile=simulated` and a `Runtime:Simulated:Capabilities` entry for
   a capability of the configured workflow definition. Register it as the
   registration feature describes.
3. Finalize a recording set to start a run, then watch
   `socalytics-manager status` list active attempts until the run completes.
4. Run `socalytics-manager concurrency 2`, then pause and resume, and observe
   that admission follows.
5. Revoke the Manager as a Club Admin and observe that its attempts are fenced
   and it returns to Unregistered.
