# Implementation Plan: Analyst Manager Work Execution With a Simulated Analyst

**Branch**: `20261012-141233-manager-work-execution` | **Date**: 2026-10-12 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261012-141233-manager-work-execution/spec.md`

## Summary

Close the loop between the durable analysis workflow and a registered Analyst
Manager, in three parts that meet at versioned contracts:

- **Platform (`src/platform`)**:
  - per-capability durable pull consumers on the existing
    `SOCALYTICS_ANALYSIS_JOBS` work-queue stream;
  - NATS decentralized JWT authentication (operator, system account, and one
    stamp account) so that the platform mints short-lived, per-Manager user
    JWTs that may only pull and acknowledge from those consumers;
  - a new Manager-facing operation that issues them together with the stamp's
    maximum concurrency per Manager;
  - revocation that, in one unit of work, fences every active attempt of the
    Manager (consuming budget like a stale attempt) and records a queue
    revocation, which is pushed to NATS as an updated account JWT so the server
    disconnects and refuses that Manager immediately.
- **Analyst Manager (`src/analysts/manager`)**:
  - a work-acquisition service that pulls from the consumers of its validated
    capabilities only while admission allows and a concurrency slot is free;
  - claims through the existing claim operation and acknowledges only after a
    successful or obsolete claim;
  - runs each attempt through an extended runtime adapter boundary, here the
    simulated runtime (moved from the test project into Core and selectable
    only in Development and Test environments);
  - heartbeats at a cadence derived from the pinned lease, enforces the
    execution timeout, reports one idempotent completion, abandons fenced
    attempts, and stops everything on revocation;
  - a persisted host-local concurrency limit, and active attempts in the status
    snapshot, local-control contract, CLI, and status window.
- **End-to-end verification**: a new Linux-only test project
  `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests` hosts the platform
  API in-process against disposable PostgreSQL and JWT-mode NATS containers
  and drives a real Manager Core host (SoftHSM device key, simulated runtime)
  through pull, claim, heartbeat, completion, fencing, and revocation.

Design rationale: [research.md](research.md). Entities and state machines:
[data-model.md](data-model.md). Contracts: [contracts/](contracts/).
Validation: [quickstart.md](quickstart.md).

## Technical Context

**Language/Version**: C# on .NET 10 for both parts; SDK pinned by
`src/platform/global.json` and identically by `src/analysts/manager/global.json`;
nullable enabled, warnings as errors.

**Primary Dependencies**:

- Existing platform: ASP.NET Core minimal APIs, Npgsql, Dapper, DbUp, the unit
  of work, `NATS.Net`/`NATS.Client.JetStream` 3.3.0, `Testcontainers.Nats`
  4.15.0, the Manager machine authentication (`RequireAnalystManager`, DPoP),
  and the claim, heartbeat, and completion commands of the durable analysis
  workflow.
- New platform packages: `NATS.Jwt` and `NATS.NKeys` (nats-io, Apache-2.0) in
  Infrastructure to mint operator, account, and user JWTs (research R1).
- Existing Manager: Generic Host, `Microsoft.Extensions.Http`, the
  `PlatformClient` with DPoP, `OperatingStateMachine`, `AdmissionGate`,
  `DrainCoordinator`, `IManagerWorkTracker`, `PreflightScheduler`, local control,
  `System.CommandLine`, Avalonia.
- New Manager packages: `NATS.Net` 3.3.0 (same version as the platform) and
  `NATS.NKeys` for the ephemeral user key that signs the server nonce.

**Storage**: PostgreSQL through the persistence foundation. Migration
`0014_registry_queue_credentials.sql` adds issued queue credentials and the
pending queue-revocation state (no `version` column; classified in
`PersistedTableClassifications.cs`). The Manager persists only its host-local
concurrency limit in its existing signed local state; credentials, NKey seeds,
fencing tokens, and attempts stay in process memory.

**Testing**: xUnit v3 in both solutions. Platform integration tests with
PostgreSQL and NATS containers (existing fixtures, extended with a JWT-mode NATS
fixture). Manager unit tests with `FakePlatform`, fake time, and an in-memory
queue fake. New end-to-end test project in the platform test folder (Linux
only, needs SoftHSM2 and Docker, both provided by the verification
environment). Shared golden fixtures under
`contracts/analyst-manager/queue-credentials/v1/` verified by both solutions.

**Target Platform**: Platform on Linux containers (Aspire locally). Manager on
Windows, macOS, and Linux; automated verification on Linux.

**Project Type**: Distributed system: web service (platform) plus desktop
worker (Manager) connected by REST and NATS JetStream.

**Performance Goals**: SC-001 (ready job to accepted result within 30 s plus
simulated run time in validation), SC-005 (Manager stops all work within 10 s
of observing revocation), SC-009 (queue disconnect within 5 s of revocation).

**Constraints**: At-least-once delivery; acknowledge only after a successful
or obsolete claim; never more active attempts than the effective limit; no
durable queue secret; simulated Analyst only in Development and Test; all
lease, heartbeat, retry, credential-lifetime, and concurrency values are
explicit non-production configuration.

**Scale/Scope**: One stamp, a handful of Managers and capabilities in tests;
no production capacity targets.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | Conforms to `analyst-runtime-and-recovery.md` (Execution Attempts; Registration And Runtime Credentials: expiring NATS credentials restricted to pull and acknowledge, revocation revokes the NATS identity and fences active attempts) and `analyst-manager.md` (Capacity And Pause Controls). The chosen NATS mechanism (decentralized JWT, account revocations) is recorded in `platform-implementation.md` in the same change; production key custody and values stay unresolved under `GOV-CRED-004`. | Pass |
| II. Source-area ownership | Platform code in `src/platform`, Manager code in `src/analysts/manager`, shared fixtures in repository-root `contracts/`. The end-to-end test project lives in `src/platform/Tests` and references Manager Core as a test-only dependency; no production project references the other area. | Pass |
| III. API-first control plane | Queue credentials, concurrency maximum, claim, heartbeat, completion, and revocation are API operations; the Manager never touches platform data directly; no video bytes. New REST and local-control contracts are versioned. | Pass |
| IV. Evidence over claims | Every requirement maps to automated tests, including the end-to-end loop (SC-010); no production readiness is claimed. | Pass |
| V. Focused, minimal changes | No real runtime adapter, object storage, status report, or UI beyond the active-attempt list. Two NATS key packages added, justified by FR-006 and FR-023. | Pass |
| Tooling constraints | Needs only .NET, Docker (Testcontainers), and SoftHSM2, which `environment-setup` already provides; NuGet packages are restored by the existing commands. The end-to-end project is found by the platform test run (`*Tests.csproj` under `src/platform`). No environment feature is needed. | Pass |

Post-design re-check (after Phase 1): still passes; no Complexity Tracking
entries.

## Project Structure

### Documentation (this feature)

```text
specs/20261012-141233-manager-work-execution/
├── plan.md              # This file
├── research.md          # Phase 0 decisions R1–R10
├── data-model.md        # Entities, state machines, migration 0014
├── quickstart.md        # Validation guide
├── contracts/
│   ├── queue-credentials-api.md     # POST /api/v1/analyst-managers/self/queue-credentials
│   ├── queue-topology-and-permissions.md
│   ├── revocation-fencing.md
│   └── local-control-active-attempts.md
├── checklists/requirements.md
└── tasks.md             # Phase 2 (/speckit-tasks)
```

### Source Code (repository root)

```text
contracts/analyst-manager/queue-credentials/v1/        # golden request/response fixtures (both solutions)

src/platform/
├── SocAlytics.Platform.Application/
│   ├── Registry/QueueCredentials/                     # IssueQueueCredentials, IQueueCredentialStore, IQueueCredentialIssuer, IQueueAccessRevoker, options
│   ├── Registry/Decisions/RevokeRegistration.cs       # + fence attempts, record queue revocation
│   ├── Registry/CompleteUnregistration.cs             # + record queue revocation
│   └── Analysis/Commands/                             # shared CloseAttemptAsStale used by ExpireStaleAttempts and FenceManagerAttempts
├── SocAlytics.Platform.Infrastructure/
│   ├── Messaging/Nats/                                # NatsAuthOptions, JWT minting, platform user creds, per-capability consumers, account push
│   ├── Persistence/Registry/QueueCredentialStore.cs
│   ├── Persistence/Migrations/0014_registry_queue_credentials.sql
│   └── Workers/QueueRevocationWorker.cs               # retries pending account pushes
├── SocAlytics.Platform.Api/Registry/AnalystManagerSelfEndpoints.cs   # + queue-credentials
├── SocAlytics.Platform.AppHost/                       # NATS in JWT mode with generated development trust
└── Tests/
    ├── SocAlytics.Platform.Integration.Tests/         # credential issuance, revocation fencing, consumers, JWT-mode NATS fixture
    ├── SocAlytics.Platform.Contracts.Tests/           # queue-credentials fixtures
    ├── SocAlytics.Platform.Architecture.Tests/        # NATS JWT types stay internal to Infrastructure
    └── SocAlytics.Platform.EndToEnd.Tests/            # NEW: Manager Core against in-process platform

src/analysts/manager/
├── SocAlytics.Analysts.Manager.Core/
│   ├── Work/                                          # WorkAcquisitionService, AttemptRunner, HeartbeatLoop, AttemptRegistry (IManagerWorkTracker), ConcurrencyLimit
│   ├── Queue/                                         # QueueCredentialProvider, QueueConnection, ephemeral NKey
│   ├── Platform/PlatformClient.cs                     # + queue credentials, claim, heartbeat, completion
│   ├── Runtime/IContainerRuntimeAdapter.cs            # + StartAttemptAsync / IAttemptExecution
│   ├── Runtime/Simulated/                             # simulated runtime + Analyst (Development/Test only)
│   ├── Diagnostics/ManagerStatusSnapshot.cs           # + ActiveAttempts, EffectiveConcurrencyLimit
│   └── LocalControl/                                  # + concurrency command
├── SocAlytics.Analysts.Manager/                       # CLI `concurrency`, status rendering, status window list
├── contracts/local-control.schema.json                # + activeAttempts, concurrency
└── Tests/SocAlytics.Analysts.Manager.Tests/           # Work/, Queue/, Runtime/Simulated tests; golden fixtures

docs/architecture/platform-implementation.md          # record the NATS JWT credential mechanism
src/analysts/manager/README.md, src/platform/README.md, README.md, AGENTS.md   # current state and commands
```

**Structure Decision**: Extend the existing platform layers (Registry and
Analysis functional areas, NATS infrastructure) and the existing Manager Core
folders; add one Manager `Work/` and `Queue/` folder each and one platform test
project for the cross-solution end-to-end test.

## Complexity Tracking

No constitution violations.
