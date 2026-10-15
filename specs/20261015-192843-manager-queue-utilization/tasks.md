---

description: "Task list for Analyst Manager Queue Depth and Host Utilization"
---

# Tasks: Analyst Manager Queue Depth and Host Utilization

**Input**: Design documents from `specs/20261015-192843-manager-queue-utilization/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/queue-depth.md](contracts/queue-depth.md), [quickstart.md](quickstart.md)

**Tests**: Required by the constitution. Every task adds the tests for the code it creates in the same task, so the platform and Manager solutions build and their test suites stay green after each task.

**Organization**: Tasks are grouped by user story. Merged before this feature starts (do not re-create): Analyst Manager Work Execution (`IssueQueueCredentialsHandler`, `NatsTopology` with consumers `analyst-<capability-id>`, `NatsContainerFixture`, `QueueCredentialProvider`, `PlatformClient`, `ManagerStatusSnapshot`, `IManagerControl.GetStatus`, the status window and CLI status) and Analyst Manager Status Report and Fleet Read API (`AnalystManagerSelfEndpoints`, `RegistrationStatusMonitor`, `StatusReportBuilder`). No migration. The Analyst Manager Headless Server Profile spec moves `ManagerCli.cs` from `src/analysts/manager/SocAlytics.Analysts.Manager/Cli/` to `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Cli/`; edit it where it is when the task runs.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies on incomplete tasks); consecutive `[P]` tasks under one heading run in parallel, each from the same commit
- **[Story]**: Which user story this task belongs to (US1–US2)
- Every task names its exact file paths

---

## Phase 1: Foundational (Blocking Prerequisites)

**Purpose**: The versioned queue-depth contract both implementations validate against.

- [x] T001 Queue-depth contract (contracts/queue-depth.md § 1; data-model.md § Queue Depth Observation; research.md § R7): add `contracts/analyst-manager/queue-depth/v1/queue-depth.schema.json` (JSON Schema 2020-12: `observedAt` date-time; `capabilities` array of `{ id, waiting }` with `id` pattern `^[a-z0-9][a-z0-9.-]{0,63}$` and `waiting` integer ≥ 0, ordered by `id`; `total` integer ≥ 0; no additional properties) and examples under `contracts/analyst-manager/queue-depth/v1/examples/` (`available.json`, `empty.json`, `one-capability-omitted.json`) plus a negative example with a negative count; list the folder in `contracts/README.md`. Tests in `src/platform/Tests/SocAlytics.Platform.Contracts.Tests/QueueDepthContractTests.cs` (examples valid, negative invalid; same structure as `QueueCredentialsContractTests.cs`).

**Checkpoint**: Both sides can build against one contract.

---

## Phase 2: User Story 1 - A host operator sees whether work waits for their Manager (Priority: P1) 🎯 MVP

**Goal**: The platform reports waiting jobs per assigned capability; the Manager polls and shows them as guidance.

**Independent Test**: Make jobs ready for an assigned capability; the status window and the CLI status show the count, total, and age; admission is unchanged; an unreachable platform marks the observation stale.

### Platform operation and Manager polling

- [x] T002 [P] [US1] Platform operation (contracts/queue-depth.md § 1; research.md § R1–§ R3; FR-001–FR-004, FR-007): add `IQueueDepthReader` and `ReadQueueDepthHandler` in `src/platform/SocAlytics.Platform.Application/Registry/QueueDepth/` (validate 1–64 `capability` values with the pattern `[a-z0-9][a-z0-9.-]{0,63}`, duplicates ignored, `400` field `capability` otherwise; registration must be `Active` or `403` `manager-not-active`; keep only ids that are capabilities of the resolved default workflow, as `IssueQueueCredentialsHandler` does; read counts; order by `id`; sum `total`; no audit); add `NatsQueueDepthReader` in `src/platform/SocAlytics.Platform.Infrastructure/Messaging/Nats/NatsQueueDepthReader.cs` (consumer info of `analyst-<id>` on the jobs stream through `NatsJSContext`, `NumPending` as `waiting`, missing consumer omitted, 2-second timeout per capability, broker failure → `QueueUnavailable`) and register both in `src/platform/SocAlytics.Platform.Infrastructure/InfrastructureServiceCollectionExtensions.cs` and the Application composition; map `GET /api/v1/analyst-managers/self/queue-depth` in `src/platform/SocAlytics.Platform.Api/Registry/AnalystManagerSelfEndpoints.cs` with the same authorization and filters as the other self operations and `503` problem type `urn:socalytics:problem:queue-unavailable`; update `src/platform/SocAlytics.Platform.Api/openapi/v1.json`. Tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/QueueDepthTests.cs` with the existing NATS and PostgreSQL containers: counts per capability after publishing ready jobs, delivered-but-unacknowledged jobs not counted, unknown and unprovisioned ids omitted, validation, inactive and non-active registrations, broker unavailable, response valid against `contracts/analyst-manager/queue-depth/v1/queue-depth.schema.json`; and the OpenAPI snapshot test.
- [x] T003 [P] [US1] Manager polling and state (research.md § R4; data-model.md § Queue Depth State; FR-005, FR-006, FR-007): add `GetQueueDepthAsync` to `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Platform/PlatformClient.cs` (capability ids from the current consumer assignments as repeated `capability` query values); add `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Queue/QueueDepthState.cs` (singleton in memory: `NotYetAvailable`, `Available`, `Stale`, `NoCapability`, `Unavailable`; `ObservedAt`, `ReceivedAt`, capabilities, total, last error; stale when `now − ReceivedAt > 2 × status check interval` or the latest request failed; cleared on revocation or unregistration; never persisted) and register it in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Hosting/ManagerServiceCollectionExtensions.cs`; in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Registration/RegistrationStatusMonitor.cs`, request queue depth after each successful registration check while active (in every operating state), using `QueueCredentialProvider.Current` assignments, `NoCapability` without assignments. Tests: `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Queue/QueueDepthStateTests.cs` (every transition of data-model.md, clock-skew independence), `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Registration/QueueDepthPollingTests.cs` (polls only while active, in paused and draining states too, stops on revocation; new queue credentials with a different capability set change the next request and the shown list; with fake time, jobs ready after an observation appear in the state within one status check interval, SC-001; admission decisions identical with and without observations, SC-005), and `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/GoldenFixtures/QueueDepthFixtureTests.cs` (the client parses every example of `contracts/analyst-manager/queue-depth/v1/examples/`).

### Display

- [x] T004 [US1] Queue depth in the status surfaces (contracts/queue-depth.md § 2–§ 4; research.md § R6; FR-010–FR-013): add `QueueDepth` to `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Diagnostics/ManagerStatusSnapshot.cs` and the `status` response under `src/analysts/manager/SocAlytics.Analysts.Manager.Core/LocalControl/`; add `queueDepth` (`state` `available` | `stale` | `not-yet-available` | `no-capability` | `unavailable`, `observedAt`, `ageSeconds`, `capabilities`, `total`) to `src/analysts/manager/contracts/local-control.schema.json` (additive); print the "Queue (approximate guidance, observed … ago)" block and the other states' lines in `ManagerCli.cs`; add the "Waiting work (approximate)" section to `src/analysts/manager/SocAlytics.Analysts.Manager/ViewModels/StatusWindowViewModel.cs` and `src/analysts/manager/SocAlytics.Analysts.Manager/Views/StatusWindow.axaml`. Tests: `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/LocalControl/StatusQueueDepthTests.cs` (JSON for every state, schema validation, text lines including stale and the three "no number" states), a view-model test under `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Ui/`, and `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Registration/StatusReportPrivacyTests.cs` (the status report built by `StatusReportBuilder` carries no queue depth, FR-013).

**Checkpoint**: Host operators see waiting work per capability.

---

## Phase 3: User Story 2 - A host operator sees how loaded the host is (Priority: P2)

**Goal**: CPU and memory of the host appear next to the active Analysts.

**Independent Test**: Load the host; the status shows rising CPU within two sampling periods and correct memory; nothing reaches the platform.

- [x] T005 [US2] Host utilization sampler (research.md § R5; data-model.md § Host Utilization Sample; FR-008, FR-009): add `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Diagnostics/HostUtilizationReaders.cs` (pure parsers and calculations: `/proc/stat` CPU busy share between two readings, `/proc/meminfo` `MemTotal − MemAvailable`; Windows `GetSystemTimes` and `GlobalMemoryStatusEx` through P/Invoke; macOS `host_statistics` `HOST_CPU_LOAD_INFO`, `host_statistics64` `HOST_VM_INFO64` (active + wired + compressed pages × page size) and `sysctl hw.memsize` through P/Invoke to `libSystem`; unreadable values `null`) and `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Diagnostics/HostUtilizationSampler.cs` (background service, 5-second period, keeps only the latest sample in memory, first sample without CPU), registered in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Hosting/ManagerServiceCollectionExtensions.cs`. Tests in `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Diagnostics/HostUtilizationTests.cs`: parsers with sample files, CPU arithmetic with counter wrap, null handling, the live Linux reader returns plausible values (Windows and macOS readers `SkipUnless` on those systems).
- [ ] T006 [US2] Utilization in the status surfaces (contracts/queue-depth.md § 2–§ 4; FR-010, FR-013): add `HostUtilization` to `ManagerStatusSnapshot.cs` and the `status` response; add `hostUtilization` (`sampledAt`, `cpuPercent`, `memoryUsedBytes`, `memoryTotalBytes`, nullable) to `src/analysts/manager/contracts/local-control.schema.json`; print `CPU:` and `Memory: … of …` lines (`not available` when null) in `ManagerCli.cs`; add the "Host" section to `StatusWindowViewModel.cs` and `StatusWindow.axaml`. Tests: extend `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/LocalControl/StatusQueueDepthTests.cs` with utilization JSON, schema, and text cases, the view-model test, and `StatusReportPrivacyTests.cs` (no utilization in the status report).

**Checkpoint**: All user stories work.

---

## Phase 4: Polish & Cross-Cutting Concerns

- [ ] T007 Documentation (plan.md § Architecture Updates): make every narrative listed in plan.md § Architecture Updates and every status statement this feature affects anywhere in `docs/architecture/` true for the merged state, re-reading the documents now rather than only applying the listed rewrites (they are examples, not the whole job): turn the decided queue-depth paragraph in `docs/architecture/analyst-manager.md` into present tense and state in the Tray Profile list that accelerator utilization is not shown yet; mark the "expose local utilization, active attempts, and timestamped queue status" responsibility in `docs/architecture/analyst-runtime-and-recovery.md` as implemented for CPU, memory, and queue depth; note in `docs/architecture/analysts-models-and-hardware.md` (Implementation Validation) that queue depth is observable per Manager while the measurement stays open; update `src/analysts/manager/README.md` (status window and `status` output, polling, sampling), `src/platform/README.md` (the new self operation), and `AGENTS.md` Current State if it lists the Manager's self operations.
- [ ] T008 Run the validation in [quickstart.md](quickstart.md) § Automated validation and `node .github/scripts/check-markdown.mjs`; fix what fails.

---

## Dependencies & Execution Order

- **Phase 1**: T001 first (the contract both sides test against).
- **US1 (Phase 2)**: after T001; T002 and T003 in parallel (platform vs. Manager; both validate against the contract of T001), then T004 (needs the state of T003).
- **US2 (Phase 3)**: after US1; T005, then T006 (needs the sampler).
- **Polish**: after all stories.

### Parallel opportunities

- Phase 2: T002 (platform: Application, Infrastructure, Api, Integration tests, OpenAPI snapshot) with T003 (Manager: PlatformClient, QueueDepthState, RegistrationStatusMonitor, service registration, Manager tests).

## Implementation Strategy

1. Phase 1, then User Story 1 as the MVP: host operators see waiting work.
2. User Story 2: host utilization.
3. Documentation and validation; Windows and macOS sampling is verified on those systems (quickstart.md § Manual validation).
