---

description: "Task list for Analyst Manager Status Report and Fleet Read API"
---

# Tasks: Analyst Manager Status Report and Fleet Read API

**Input**: Design documents from `specs/20261012-153308-manager-fleet-status/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/self-status-api.md](contracts/self-status-api.md), [contracts/fleet-read-api.md](contracts/fleet-read-api.md), [quickstart.md](quickstart.md)

**Tests**: Required by the constitution. Every task adds the tests for the code it creates in the same task, so both solutions build and their test suites stay green after each task.

**Organization**: Tasks are grouped by user story. Merged before this feature starts (do not re-create): Analyst Manager Registration (pairing with `DeviceMetadata.HostLabel`, `GET /api/v1/analyst-managers/self/registration`, `RegistrationStatusMonitor`, the registration list and detail, `RevokeRegistration`, `CompleteUnregistration`, `ClubPermission.Register` and `Administer`, `RegisteredAnalystManagerClient`, `FakePlatform`, `GoldenFixtureTests`) and Analyst Manager Work Execution `specs/20261012-141233-manager-work-execution` (`AttemptRegistry`, `ConcurrencyLimit`, revocation fencing in `RevokeRegistration` and `CompleteUnregistration`). The migration takes the next free number on an up-to-date default branch (`0016` at planning time).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies on incomplete tasks); consecutive `[P]` tasks under one heading run in parallel, each from the same commit
- **[Story]**: Which user story this task belongs to (US1–US3)
- Every task names its exact file paths

---

## Phase 1: Foundational (Blocking Prerequisites)

**Purpose**: The report contract, the domain validation, and the storage.

- [ ] T001 [P] Status report fixtures (contracts/self-status-api.md): add `contracts/analyst-manager/status/v1/status-report.schema.json` (request and response, JSON Schema 2020-12), valid examples for each operating state, and invalid examples (active above maximum, `pausedUntil` without `paused`, unknown state); add the index row to `contracts/README.md`; validate them in `src/platform/Tests/SocAlytics.Platform.Contracts.Tests/StatusReportContractTests.cs` and in `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/GoldenFixtures/StatusReportFixtureTests.cs` (link the folder in the test project file as the registration fixtures are linked).
- [ ] T002 [P] Report validation and storage (research.md § R2, § R3; data-model.md; FR-002, FR-003): add `src/platform/SocAlytics.Platform.Domain/Registry/ManagerStatusReport.cs` (the validation rules of research.md § R2, returning field violations); add `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Migrations/NNNN_registry_analyst_manager_status.sql` creating `registry.analyst_manager_status` per data-model.md; classify it in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Structure/PersistedTableClassifications.cs`; add `IManagerStatusStore` (upsert, find, find many, delete) in `src/platform/SocAlytics.Platform.Application/Registry/StatusReporting/IManagerStatusStore.cs` and its internal Dapper implementation in `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Registry/ManagerStatusStore.cs`, registered in `src/platform/SocAlytics.Platform.Infrastructure/InfrastructureServiceCollectionExtensions.cs`; add `StatusReportingOptions` (`Registry:StatusReporting`: `Interval` 60 s, `MissedIntervalsBeforeOffline` 3) and `Connectivity.Derive` in `Application/Registry/StatusReporting/`. Tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/ManagerStatusStoreTests.cs` and `Registry/ManagerStatusReportValidationTests.cs`.

**Checkpoint**: The contract, validation, and storage exist.

---

## Phase 2: User Story 1 - Club Admins see the state of every Analyst Manager (Priority: P1) 🎯 MVP

**Goal**: Managers report with their check; Club Admins read the fleet with status and connectivity.

**Independent Test**: Two Managers report different states; one stops; the fleet list shows each state and the offline transition.

### Report operation

- [ ] T003 [US1] Report operation (contracts/self-status-api.md; research.md § R1, § R2; FR-001–FR-004): add `ReportStatusHandler` in `src/platform/SocAlytics.Platform.Application/Registry/MachineAccess/ReportStatus.cs` (validates with `ManagerStatusReport`, upserts with platform receipt time and the interval in force, returns the own registration with `statusCheckIntervalSeconds`, `reportAccepted`, and `reportViolations`), add `StatusCheckIntervalSeconds` to `OwnRegistration` in `Application/Registry/MachineAccess/GetOwnRegistration.cs`, and map `POST /api/v1/analyst-managers/self/status` in `src/platform/SocAlytics.Platform.Api/Registry/AnalystManagerSelfEndpoints.cs` (`RequireAnalystManager`, HTTPS, `no-store`). Tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/StatusReportTests.cs` through the real DPoP path: storage, overwrite without history, invalid reports refused while the registration answer is returned, interval in both responses, revoked Managers refused, and the stored report time is the platform's receipt time even when the Manager's clock differs.

### Fleet read and Manager reporting

- [ ] T004 [P] [US1] Fleet read for Club Admins and report deletion (contracts/fleet-read-api.md; research.md § R3, § R4; FR-005, FR-006, FR-009, FR-012): add `status` and `connectivity` to the registration DTO in `src/platform/SocAlytics.Platform.Api/Registry/AnalystManagerRegistrationEndpoints.cs`, filled by `ListRegistrationsHandler` and `GetRegistrationHandler` in `src/platform/SocAlytics.Platform.Application/Registry/Decisions/ReadRegistrations.cs` for Club Admins; delete the report in the existing units of work of `src/platform/SocAlytics.Platform.Application/Registry/Decisions/RevokeRegistration.cs` and `src/platform/SocAlytics.Platform.Application/Registry/CompleteUnregistration.cs`. Tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/FleetReadTests.cs`: online, offline after the configured missed intervals, never-reported Managers offline, not applicable for ended registrations, deletion on revocation and unregistration completion, approval, rejection, and revocation unchanged, and a changed interval does not change the connectivity of reports received before the change.
- [ ] T005 [P] [US1] Report sending (research.md § R1, § R6; FR-001, FR-004): add `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Diagnostics/StatusReportBuilder.cs` (operating state and pause end from `OperatingStateMachine`, first failed preflight check as the runtime reason, `AttemptRegistry.ActiveCount`, `ConcurrencyLimit.Effective`, assembly version); add `BuildStatusReport` to `Platform/PlatformClient.cs`; make `Registration/RegistrationStatusMonitor.cs` send the report with every check while Active, keep `GET …/self/registration` while Unregistering, use the returned `StatusCheckIntervalSeconds`, and log refused reports without changing behavior; extend `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Support/FakePlatform.cs`. Tests in `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Registration/StatusReportingTests.cs`: report content per operating state, the platform interval used, refusals logged, revocation still observed.

**Checkpoint**: User Story 1 works and is the MVP.

---

## Phase 3: User Story 2 - Registrars follow the requests they submitted (Priority: P2)

**Goal**: Registrars read only their own submissions.

**Independent Test**: Two Registrars each submit a code; each sees only their own request; a Club Admin's decision is visible to its Registrar.

- [ ] T006 [US2] Registrar view (contracts/fleet-read-api.md; research.md § R5; FR-007, FR-008): in `src/platform/SocAlytics.Platform.Application/Registry/Decisions/ReadRegistrations.cs` fall back from `ClubPermission.Administer` to `ClubPermission.Register` with a `submitted_by` filter and no `status` or `connectivity`; return `404` for registrations the Registrar did not submit; add the `submittedBy` filter to `IRegistrationStore` and `src/platform/SocAlytics.Platform.Infrastructure/Registry/DapperRegistrationStore.cs`. Tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/RegistrarViewTests.cs` covering US2 scenarios 1–3 and `403` for other members.

**Checkpoint**: User Stories 1 and 2 work independently.

---

## Phase 4: User Story 3 - Host operators name their Manager (Priority: P3)

**Goal**: The host label is validated on both sides and visible to Registrars and Club Admins.

**Independent Test**: Register with and without a label through the CLI; submit as a Registrar; read the fleet.

### Label validation on both sides

- [ ] T007 [P] [US3] Platform label rules (research.md § R7; FR-010, FR-011): refuse control characters in `hostLabel` in `src/platform/SocAlytics.Platform.Domain/Registry/DeviceMetadata.cs`. Tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/HostLabelTests.cs`: control characters refused, the label and fingerprint present in the Registrar's submission response and in the fleet list.
- [ ] T008 [P] [US3] Manager label validation (research.md § R7; FR-010; SC-005): validate `--host-label` locally (trimmed, 1–64 characters, no control characters) in `src/analysts/manager/SocAlytics.Analysts.Manager/Cli/ManagerCli.cs` and in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Registration/RegistrationCoordinator.cs`. Tests in `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Registration/HostLabelTests.cs`: invalid labels refused locally with a message, the pairing request carries exactly the entered label or none, and the machine name never appears in any request (SC-005).

**Checkpoint**: All user stories work.

---

## Phase 5: Polish & Cross-Cutting Concerns

- [ ] T009 Documentation: in `docs/architecture/analyst-manager.md` § Fleet Visibility In The Web UI replace "The status report and the fleet read operation are not yet implemented" with a pointer to the operations; update `src/platform/README.md` (status report operation, `Registry:StatusReporting`, the migration), `src/analysts/manager/README.md` (status reporting and the host label), and the "Current State" in `AGENTS.md`.
- [ ] T010 Run the validation in [quickstart.md](quickstart.md) and `node .github/scripts/check-markdown.mjs`; fix what fails.

---

## Dependencies & Execution Order

- **Phase 1**: T001 and T002 in parallel (disjoint files).
- **US1 (Phase 2)**: after Phase 1; T003 first (the report operation); then T004 (platform fleet read) and T005 (Manager reporting, tested against `FakePlatform`) in parallel.
- **US2 (Phase 3)**: after US1 (changes `ReadRegistrations.cs` after T004).
- **US3 (Phase 4)**: after Phase 1; T007 and T008 in parallel (platform domain vs. Manager).
- **Polish**: after all stories.

### Parallel opportunities

- Phase 1: T001 (`contracts/`, contract and fixture tests) with T002 (platform domain, persistence, options).
- Phase 2: T004 (platform) with T005 (Manager).
- Phase 4: T007 (platform) with T008 (Manager).

## Implementation Strategy

1. Phase 1, then User Story 1 as the MVP (reports stored, fleet readable, Managers reporting).
2. User Story 2 (Registrar view), then User Story 3 (label validation).
3. Documentation and validation.
