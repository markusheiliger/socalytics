# Implementation Plan: Analyst Manager Queue Depth and Host Utilization

**Branch**: `20261015-192843-manager-queue-utilization` | **Date**: 2026-10-15 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261015-192843-manager-queue-utilization/spec.md`

**Note**: This template is filled in by the `/speckit-plan` command; its definition describes the execution workflow.

## Summary

A new Manager self operation, `GET /api/v1/analyst-managers/self/queue-depth`,
returns for the capability ids the Manager is assigned the number of ready jobs
its JetStream consumers have not yet delivered (`NumPending`) and the
observation time. The Manager asks for it after each registration check, keeps
only the latest observation in memory, and marks it stale after a failure or
two intervals. A background sampler reads host CPU and memory every 5 seconds
on Linux, Windows, and macOS. The status snapshot feeds both values to the
Avalonia status window and the CLI status (text and JSON); the status report
to the platform is unchanged. A versioned queue-depth contract with examples is
validated by the platform and the Manager.

## Technical Context

**Language/Version**: C# on .NET 10 (`src/platform/global.json`,
`src/analysts/manager/global.json`).

**Primary Dependencies**: Existing platform minimal APIs, DPoP Manager
authentication, `NATS.Client.JetStream` (consumer info); existing Manager Core
(`RegistrationStatusMonitor`, `QueueCredentialProvider`, `ManagerStatusSnapshot`,
local control) and Avalonia status window. No new packages.

**Storage**: None (transient values; no migration).

**Testing**: Platform Integration tests with the existing NATS and PostgreSQL
containers; Host tests for the OpenAPI snapshot; Contracts tests for the new
schema; Manager unit tests (state machine, polling, sampler parsing, snapshot,
CLI, view model, status report unchanged) and golden fixture tests.

**Target Platform**: The platform API; the Manager on Windows, macOS, and Linux.

**Project Type**: Web service plus desktop agent (existing).

**Performance Goals**: One consumer-info read per assigned capability per
status check interval per Manager; 2-second read timeout per capability.

**Constraints**: Guidance only (no admission effect); nothing sent to the
platform beyond the capability ids; no persistence or history.

**Scale/Scope**: A handful of capabilities per Manager and of Managers per club.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Evidence |
| --- | --- | --- |
| I. Architecture is the design authority | Pass | Implements the status-window queue depth and utilization of `analyst-manager.md` and the "expose local utilization, active attempts, and timestamped queue status" responsibility of `analyst-runtime-and-recovery.md`; the stamp operation reaches the architecture through `## Architecture Updates`. |
| II. Source-area ownership | Pass | Platform code in the Registry area of `src/platform`, Manager code in `src/analysts/manager`, contract fixtures in `contracts/`. |
| III. API-first control plane | Pass | The Manager reads queue depth only through a versioned API operation in the OpenAPI snapshot with a JSON Schema contract; the control plane reads metadata only. |
| IV. Evidence over claims | Pass | Integration, Contracts, and Manager tests (quickstart.md); Windows and macOS sampling verified on those systems, like the key stores. |
| V. Focused, minimal changes | Pass | No storage, no packages, one operation (research.md § R2). |
| Tooling | Pass | .NET 10, Docker, and the Manager test tooling are provided by the environment actions; no environment feature is needed (research.md § R7). |

Re-check after Phase 1 design: still passes; no Complexity Tracking entries.

## Architecture Updates

### Narratives

- [Analyst Manager](../../docs/architecture/analyst-manager.md): Tray Profile
  (status window: host utilization and queue depth) and Fleet Visibility In The
  Web UI ("Queue depth remains guidance in the Manager's own status view") —
  implemented by this feature, except accelerator utilization.
- [Analyst Runtime and Recovery](../../docs/architecture/analyst-runtime-and-recovery.md):
  the Analyst Manager responsibility "expose local utilization, active
  attempts, and timestamped queue status".
- [Analysts, Models, and Hardware](../../docs/architecture/analysts-models-and-hardware.md):
  Implementation Validation ("measure concurrency control, queue-depth …"):
  queue depth becomes measurable per Manager.

### Design updates

Applied now, worded "decided, not yet implemented":

1. **Queue depth operation and Manager handling** (`analyst-manager.md` Tray
   Profile): `GET /api/v1/analyst-managers/self/queue-depth` with the
   consumer's undelivered count per assigned capability and the platform's
   observation time; polling after each registration check; latest
   observation in memory only; stale after a failure or two intervals; local
   CPU and memory sampling; neither value leaves the host; accelerator
   utilization follows an accelerated runtime.

The implementation turns update 1 into present tense and drops its "decided,
not yet implemented" marker.

### Status statements at merge

- `analyst-manager.md`, Tray Profile: the status window list ("host CPU and
  memory utilization and available accelerator utilization where the runtime
  can report it"; "the approximate stamp-local queue depth … together with the
  observation time") becomes true for CPU, memory, and queue depth; state that
  accelerator utilization is not shown yet.
- `analyst-runtime-and-recovery.md`, Analyst Manager responsibilities:
  "expose local utilization, active attempts, and timestamped queue status"
  becomes implemented (CPU and memory; accelerators pending).
- `analysts-models-and-hardware.md`, Implementation Validation: queue depth is
  observable per Manager; the measurement itself stays open evidence.
- Outside `docs/architecture/` (kept current by the documentation task):
  `src/analysts/manager/README.md` (status window and `status` output, the
  queue depth operation, sampling), `src/platform/README.md` (the new self
  operation in the Manager API list), and `AGENTS.md` Current State if it lists
  the Manager's self operations.

The documentation task in `tasks.md` must make every narrative above and every
status statement the feature affects anywhere in `docs/architecture/` true for
the merged state, re-reading the documents at implementation time; the
rewrites listed here are examples, not the whole job.

## Project Structure

### Documentation (this feature)

```text
specs/20261015-192843-manager-queue-utilization/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── queue-depth.md
├── checklists/
│   └── requirements.md
└── tasks.md             # /speckit-tasks
```

### Source Code (repository root)

```text
contracts/analyst-manager/queue-depth/v1/
├── queue-depth.schema.json
└── examples/                                          # available, empty, omitted capability

src/platform/
├── SocAlytics.Platform.Application/Registry/QueueDepth/
│   ├── ReadQueueDepth.cs                              # handler: validation, active check, resolved consumers
│   └── IQueueDepthReader.cs
├── SocAlytics.Platform.Infrastructure/Messaging/Nats/NatsQueueDepthReader.cs   # consumer info NumPending, timeout
├── SocAlytics.Platform.Infrastructure/InfrastructureServiceCollectionExtensions.cs
├── SocAlytics.Platform.Api/Registry/AnalystManagerSelfEndpoints.cs             # GET self/queue-depth
├── SocAlytics.Platform.Api/openapi/v1.json
└── Tests/
    ├── SocAlytics.Platform.Integration.Tests/Registry/QueueDepthTests.cs
    ├── SocAlytics.Platform.Contracts.Tests/QueueDepthContractTests.cs
    └── SocAlytics.Platform.Host.Tests/                                         # snapshot

src/analysts/manager/
├── SocAlytics.Analysts.Manager.Core/Platform/PlatformClient.cs                 # GetQueueDepthAsync
├── SocAlytics.Analysts.Manager.Core/Queue/QueueDepthState.cs                   # in-memory state machine
├── SocAlytics.Analysts.Manager.Core/Registration/RegistrationStatusMonitor.cs  # poll after each check
├── SocAlytics.Analysts.Manager.Core/Diagnostics/HostUtilizationSampler.cs, HostUtilizationReaders.cs  # Linux, Windows, macOS
├── SocAlytics.Analysts.Manager.Core/Diagnostics/ManagerStatusSnapshot.cs
├── SocAlytics.Analysts.Manager.Core/Hosting/ManagerServiceCollectionExtensions.cs
├── SocAlytics.Analysts.Manager.Core/LocalControl/…                             # status fields
├── SocAlytics.Analysts.Manager/Cli/ManagerCli.cs                               # text output (Core/Cli once the headless feature moved it)
├── SocAlytics.Analysts.Manager/ViewModels/StatusWindowViewModel.cs, Views/StatusWindow.axaml
├── contracts/local-control.schema.json
├── README.md
└── Tests/SocAlytics.Analysts.Manager.Tests/                                    # Queue/, Diagnostics/, LocalControl/, Ui/, GoldenFixtures/
```

**Structure Decision**: Queue depth is a Registry self operation on the
platform with its NATS reader in Infrastructure; the Manager adds an in-memory
state and a sampler in Core and extends the existing status surfaces.

## Complexity Tracking

No constitution violations.
