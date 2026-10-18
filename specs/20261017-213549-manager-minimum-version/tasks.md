---

description: "Task list for Analyst Manager Minimum Version"
---

# Tasks: Analyst Manager Minimum Version

**Input**: Design documents from `specs/20261017-213549-manager-minimum-version/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: The success criteria require automated evidence (SC-001 to SC-006), so every story has test tasks. Test
tasks follow the implementation tasks of their story so that each landed task keeps the build and test suites green.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.
Foundational work (the contract, the version values, and the platform setting) precedes every story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; they change disjoint
  files and do not depend on each other
- **[Story]**: Which user story this task belongs to (US1, US2, US3)
- Every task names its exact file paths

---

## Phase 1: Foundational (Blocking Prerequisites)

**Purpose**: The status contract `1.1.0`, the version comparison on both sides, the Manager's single build version,
and the platform setting

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

### Contract and version values

- [x] T001 [P] Release the status contract `1.1.0` in `contracts/analyst-manager/status/v1/`: copy the current `status.schema.json` unchanged to `releases/1.0.0.schema.json`; in `status.schema.json` set `x-socalytics-version` to `1.1.0` and add to the `response` definition the optional property `minimumManagerVersion` with `"type": ["string", "null"], "maxLength": 64, "pattern": "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$"`; add `examples/valid/accepted-with-minimum.response.json` (`"0.2.0"`), `examples/valid/accepted-without-minimum.response.json` (`null`), `examples/invalid/malformed-minimum.response.json` (`"0.2"`), and `minimum-version.fixtures.json` with the five comparison cases of contracts/manager-minimum-version.md §1; update the status row of `contracts/README.md` to version `1.1.0` with its `releases/1.0.0.schema.json` baseline and the fixtures file (FR-004, FR-005, FR-012)
- [x] T002 [P] Add the Domain value `ManagerVersion` in `src/platform/SocAlytics.Platform.Domain/Registry/ManagerVersion.cs`: `TryParse` accepts only `MAJOR.MINOR.PATCH` with each part a non-negative decimal integer and at most 64 characters, ordering compares major, then minor, then patch, and `IsBelow(string? reported, ManagerVersion? minimum)` returns false without a minimum and true when `reported` is missing, not well formed, or orders before the minimum (equal is not below) (data-model.md "ManagerVersion"; research R2; FR-012)
- [x] T003 [P] Give the Analyst Manager one build version and its comparer: set `<Version>0.1.0</Version>` in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/SocAlytics.Analysts.Manager.Core.csproj`; add `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Diagnostics/ManagerBuildVersion.cs` (singleton `Value` from the Core assembly version `ToString(3)`, constructible with any value for tests) and `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Registration/ManagerVersionComparer.cs` (same parsing and ordering as the platform's `ManagerVersion`; an unparsable minimum counts as below); replace `RegistrationCoordinator.ManagerVersion` and the assembly lookup in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Registration/RegistrationCoordinator.cs`, `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Diagnostics/StatusReportBuilder.cs`, `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Diagnostics/ManagerStatusSnapshot.cs`, and `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Hosting/ManagerControl.cs` with `ManagerBuildVersion`, registered in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Hosting/ManagerServiceCollectionExtensions.cs` (research R3; FR-005)

### Platform setting

- [x] T004 Add the platform setting in `src/platform/SocAlytics.Platform.Application/Registry/ManagerVersions/ManagerVersionOptions.cs` (section `Registry:ManagerVersion`, property `Minimum`, empty default = no minimum) and `src/platform/SocAlytics.Platform.Application/Registry/ManagerVersions/MinimumManagerVersionProvider.cs` (singleton over `IOptionsMonitor<ManagerVersionOptions>` exposing `Current` as `ManagerVersion?`; on reload it re-validates and keeps the last valid value, logging one warning for an invalid value); register both in `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs` with `BindConfiguration` and `ValidateOnStart` and the message "Registry:ManagerVersion:Minimum must be empty or have the shape MAJOR.MINOR.PATCH."; add `src/platform/Tests/SocAlytics.Platform.Host.Tests/ManagerVersionOptionsTests.cs` covering the empty default, a valid value, start failure for `0.2`, `v1.0.0`, and `1.0.0-beta`, and that the existing host starts unchanged without the setting (research R1; FR-001, FR-002, FR-003, FR-015)

### Foundation tests

- [x] T005 [P] Cover the platform side of the foundation: extend `src/platform/Tests/SocAlytics.Platform.Contracts.Tests/StatusReportContractTests.cs` and `src/platform/Tests/SocAlytics.Platform.Contracts.Tests/ContractCatalog.cs` so the new valid examples validate, the malformed minimum is rejected, the existing `1.0.0` examples stay valid, and `1.1.0` is same-major compatible with `releases/1.0.0.schema.json`; add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/ManagerVersionTests.cs` checking `ManagerVersion` against every case of `contracts/analyst-manager/status/v1/minimum-version.fixtures.json`, linking `contracts/analyst-manager/status/v1/**` (schema and fixtures, also used by T007) in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/SocAlytics.Platform.Integration.Tests.csproj` like the existing registration link (FR-005, FR-012; SC-006)
- [x] T006 [P] Cover the Manager side of the foundation: add `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Registration/ManagerVersionComparerTests.cs` checking `ManagerVersionComparer` against every case of `contracts/analyst-manager/status/v1/minimum-version.fixtures.json` plus an unparsable minimum, and that pairing metadata, the status report, and the status snapshot all carry the `ManagerBuildVersion` value; extend `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/GoldenFixtures/GoldenFixtureTests.cs` so both new status response examples deserialize (FR-005)

**Checkpoint**: The contract, both comparers, the single build version, and the platform setting exist; behavior is
unchanged

---

## Phase 2: User Story 1 - An outdated Manager stops taking new work and says why (Priority: P1) 🎯 MVP

**Goal**: The Manager learns the minimum with each status report, admits no new work while below it, lets running
attempts finish, shows the update notice, and resumes when the minimum no longer applies

**Independent Test**: A registered Manager below the configured minimum claims no job and requests no queue
credential, its running attempt completes, its status shows the notice with both versions, and after the minimum is
lowered it claims again without re-registration

### Implementation for User Story 1

- [x] T007 [US1] Return the minimum with the status report: add `string? MinimumManagerVersion` to `ReportStatusResult` in `src/platform/SocAlytics.Platform.Application/Registry/MachineAccess/ReportStatus.cs`, filled from `MinimumManagerVersionProvider.Current` (null when none) for every authenticated report, accepted or not; confirm the response of `POST /api/v1/analyst-managers/self/status` in `src/platform/SocAlytics.Platform.Api/Registry/AnalystManagerSelfEndpoints.cs` serializes it as `minimumManagerVersion`; add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/StatusMinimumVersionTests.cs` asserting the property with and without a minimum, its schema validity against `contracts/analyst-manager/status/v1/status.schema.json`, and that a minimum changed through the test host's configuration root and `Reload()` appears in the next response without re-registration, while an invalid reloaded value (`0.2`) leaves the last valid minimum in the response (research R4; FR-003, FR-004)
- [x] T008 [US1] Apply the minimum in the Manager: add `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Registration/MinimumVersionState.cs` (fields `BuildVersion`, `Minimum`, `RefusedByPlatform`, `IsBelow` per data-model.md "MinimumVersionState"; thread-safe; `Update(string? minimum)` from a successful status response clears `RefusedByPlatform`; `MarkRefused(string? minimum)`); add `string? MinimumManagerVersion = null` to `OwnRegistration` in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Platform/PlatformContracts.cs`; update the state after every successful status response in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Registration/RegistrationStatusMonitor.cs` (keeping the last value when only `GET .../self/registration` was used); make `TryAdmit` in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Operating/AdmissionGate.cs` return false while `IsBelow`, so the existing `NotAdmitted` path of `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Work/AttemptRunner.cs` negatively acknowledges delivered, unclaimed jobs; make `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Queue/QueueCredentialProvider.cs` request no credential while `IsBelow` and request again once it clears; register the state in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Hosting/ManagerServiceCollectionExtensions.cs`; running attempts, heartbeats, result uploads, completions, registration checks, and status reports are unchanged (research R7; FR-005, FR-006, FR-007, FR-009)
- [x] T009 [US1] Show the notice in the Manager status: add `UpdateRequired` (`Required`, `ManagerVersion`, `MinimumVersion` as received, raw when unparsable) to `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Diagnostics/ManagerStatusSnapshot.cs` and fill it in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Hosting/ManagerControl.cs`; add the optional `update` object (`required`, `managerVersion`, `minimumVersion`) to the `status` result in `src/analysts/manager/contracts/local-control.schema.json` and `src/analysts/manager/SocAlytics.Analysts.Manager.Core/LocalControl/LocalControlServer.cs`, present only while required; print `Update required: Manager <version> is below the stamp minimum <minimum>.` in the plain `status` text of `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Cli/ManagerCli.cs` (the `--json` output carries the object) (contracts/manager-minimum-version.md §4; FR-008)
- [ ] T010 [US1] Show the notice in the tray: in `src/analysts/manager/SocAlytics.Analysts.Manager/ViewModels/StatusWindowViewModel.cs` and `src/analysts/manager/SocAlytics.Analysts.Manager/Views/StatusWindow.axaml` show the same sentence above the operating state while required (with an accessible name), and in `src/analysts/manager/SocAlytics.Analysts.Manager/Tray/TrayMenuBuilder.cs` add a disabled status line `Update required (minimum <minimum>)` while required; no operating-system notification (research R8; FR-008)

### Tests for User Story 1

Each test task keeps its fakes inside its own test file and does not change the shared `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Support/` files, so the group stays disjoint.

- [ ] T011 [P] [US1] Add `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Registration/MinimumVersionStateTests.cs` (transitions from status responses with a higher, equal, lower, null, and unparsable minimum; the last value kept across `GET`-only checks; restart starts not below) and `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Work/MinimumVersionAdmissionTests.cs` (below the minimum: no claim, delivered jobs negatively acknowledged, no queue-credential request, a running attempt keeps heartbeating and completes; paused and draining states keep their state; after the minimum is lowered: credentials requested and jobs claimed again within one check) (FR-005, FR-006, FR-007, FR-009; SC-001, SC-002, SC-003)
- [ ] T012 [P] [US1] Add `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/LocalControl/StatusMinimumVersionTests.cs` covering the `status` result's `update` object against `src/analysts/manager/contracts/local-control.schema.json`, its absence when not required, and the CLI plain and `--json` output for both the tray and the headless executable (FR-008)
- [ ] T013 [P] [US1] Add `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Ui/StatusWindowMinimumVersionTests.cs` covering the status window sentence with its accessible name and the disabled tray menu line, both shown only while required, with Avalonia headless (FR-008)

**Checkpoint**: A Manager below the minimum stops taking new work on its own and tells its operator; the MVP is usable

---

## Phase 3: User Story 2 - The platform refuses work to outdated Managers on its own (Priority: P1)

**Goal**: Queue credentials and new claims are refused below the minimum with a specific problem response, logged and
counted, while operations on work already held continue

**Independent Test**: With a minimum above a Manager's reported version, direct API requests for queue credentials and
a new claim are refused with the version problem, while a heartbeat and the completion of an attempt claimed before
the change succeed

### Implementation for User Story 2

- [ ] T014 [US2] Add the gate: `Task<string?> FindVersionAsync(Guid registrationId, CancellationToken)` (latest stored report's version) on `src/platform/SocAlytics.Platform.Application/Registry/StatusReporting/IManagerStatusStore.cs` and `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Registry/ManagerStatusStore.cs`; an optional `IReadOnlyDictionary<string, string>? Extensions` (default null) on `src/platform/SocAlytics.Platform.Application/Abstractions/OperationFailure.cs`; `src/platform/SocAlytics.Platform.Application/Registry/ManagerVersions/ManagerVersionGate.cs` returning null without a minimum, otherwise comparing the effective version (latest report, else `DeviceMetadata.ManagerVersion`) and returning `OperationFailure.Forbidden("analyst-manager-version-below-minimum")` with the extension `minimumManagerVersion`, logging a structured warning with registration id, effective version, minimum, and operation (no credentials, tokens, or proofs), and incrementing the counter `socalytics.analysis.manager_version_refusals` with the single tag `operation` (`queue-credentials` or `claim`) added to `src/platform/SocAlytics.Platform.Application/Telemetry/AnalysisMetrics.cs`; register the gate in `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs` (research R5, R6, R10; FR-011, FR-012, FR-018)

### Enforcement points

- [ ] T015 [P] [US2] Enforce the gate on the platform: call it in `IssueQueueCredentialsHandler` (`src/platform/SocAlytics.Platform.Application/Registry/QueueCredentials/IssueQueueCredentials.cs`) after the `Active` check and before any credential is recorded, and in `src/platform/SocAlytics.Platform.Application/Analysis/Commands/ClaimLogicalJob.cs` only on the path that creates a new attempt (an idempotent replay of an existing grant still succeeds); write failure extensions into the problem body and add the title "The Analyst Manager version is below the stamp's minimum." and the detail "Update the Analyst Manager to the stamp's minimum version or later before it acquires new work." for the code in `src/platform/SocAlytics.Platform.Api/Http/ProblemResults.cs`; no other Manager operation calls the gate (contracts/manager-minimum-version.md §2; FR-011, FR-013, FR-014, FR-015)
- [ ] T016 [P] [US2] Handle version refusals in the Manager: parse the problem code `analyst-manager-version-below-minimum` and its `minimumManagerVersion` in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Platform/PlatformClient.cs` and `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Platform/PlatformWorkContracts.cs`; on a refused claim in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Work/AttemptRunner.cs` negatively acknowledge the delivery and call `MinimumVersionState.MarkRefused`; on a refused credential request in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Queue/QueueCredentialProvider.cs` call `MarkRefused` and stop requesting until the next successful status response clears it (research R7; FR-010)

### Tests for User Story 2

Each test task keeps its fakes and harness settings inside the files it names, so the group stays disjoint.

- [ ] T017 [P] [US2] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/MinimumVersionEnforcementTests.cs`: below the minimum, queue credentials and a new claim return `403` with `code` `analyst-manager-version-below-minimum` and `minimumManagerVersion`, record no credential and create no attempt, and the job stays claimable by a Manager at the minimum; the pairing version applies before the first report and the latest report afterwards; a malformed reported version is below; heartbeats, result grants, upstream reads, completion, status, queue depth, and unregistration of an attempt claimed before the minimum was raised succeed; an idempotent claim replay returns its grant; a reload that lowers the minimum lifts the refusal; without a minimum behavior is unchanged; the counter is incremented per operation and the log carries no token (FR-011 to FR-015, FR-018; SC-002, SC-004)
- [ ] T018 [P] [US2] Add `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Work/MinimumVersionRefusalTests.cs`: a refused claim negatively acknowledges the delivery and stops admission, a refused credential request stops further requests, and the next successful status response with a satisfied minimum restores both (FR-010)
- [ ] T019 [P] [US2] Add `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/MinimumVersionEndToEndTests.cs`, extending `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/EndToEndHarness.cs` only to set and reload `Registry:ManagerVersion:Minimum`: a real Manager against the simulated runtime finishes the attempt it is running when the minimum is raised above its version, claims nothing further while the platform refuses it, shows `update.required` in its local-control status, and claims work again within one check interval plus 10 seconds after the minimum is lowered, without re-registration (SC-001, SC-002, SC-003)

**Checkpoint**: Correctness no longer depends on the Manager; User Stories 1 and 2 are complete

---

## Phase 4: User Story 3 - Club Admins see which Managers need an update (Priority: P2)

**Goal**: The Club Admin fleet read and the Web UI fleet view mark every Manager below the minimum, including pending
requests, and name the minimum

**Independent Test**: With one Manager below and one at the minimum, the Club Admin fleet read and the Web UI mark only
the outdated one with its version and the minimum; a Registrar sees no fleet operations data

### Implementation for User Story 3

- [ ] T020 [US3] Add the fleet flags: compute `MinimumManagerVersion` and `BelowMinimumVersion` per `FleetEntry` in `src/platform/SocAlytics.Platform.Application/Registry/Decisions/ReadRegistrations.cs` from the effective version (`Status` report, else device metadata) and `MinimumManagerVersionProvider.Current`; add `minimumManagerVersion` (`string?`) and `belowMinimumVersion` (`bool`) to `AnalystManagerRegistrationDto` in `src/platform/SocAlytics.Platform.Api/Registry/AnalystManagerRegistrationContracts.cs`, serialized only with `IncludesOperations` under the existing `RegistrationDtoJson` rule, and map them in `src/platform/SocAlytics.Platform.Api/Registry/AnalystManagerRegistrationEndpoints.cs`; refresh `src/platform/SocAlytics.Platform.Api/openapi/v1.json` with `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests` (contracts/manager-minimum-version.md §3; FR-016)

### Fleet consumers

- [ ] T021 [P] [US3] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/FleetMinimumVersionTests.cs`: the Club Admin list and detail carry `minimumManagerVersion` and `belowMinimumVersion` for active, offline, and pending registrations (pending from the pairing version); no minimum yields `null` and `false`; the Registrar's view of their own submissions and decision responses carry neither (FR-016; SC-005)
- [ ] T022 [P] [US3] Show the marking in the Web UI: regenerate the Kiota client with `npm run generate` in `src/clients/web`; map both properties in `src/clients/web/src/platform/models/registrations.ts` and `src/clients/web/src/platform/adapters/registrationAdapter.ts`; render `Update required: version <version>, minimum <minimum>` as a list item with a text label and an icon that is not the only carrier in `src/clients/web/src/features/managers/StatusLines.tsx`, used by `src/clients/web/src/features/managers/FleetPage.tsx` and `src/clients/web/src/features/managers/RegistrationPage.tsx` for current and pending registrations; add the strings to `src/clients/web/src/i18n/en/managers.json` (research R9; FR-017)

### Tests for User Story 3

- [ ] T023 [P] [US3] Extend `src/clients/web/tests/unit/platform/registrationAdapter.test.ts` (mapping with and without a minimum) and `src/clients/web/tests/unit/features/managers/fleet/fleet.test.tsx` (only Managers below the minimum are marked, with version and minimum, announced by text, pending requests included) (FR-017; SC-005)
- [ ] T024 [P] [US3] Extend `src/clients/web/tests/e2e/support/simulatedApi.ts` with a configurable minimum and per-registration flags and `src/clients/web/tests/e2e/simulated/fleet.spec.ts` with a fleet containing one Manager below and one at the minimum, checking the marking in the list and the detail and an axe scan of both (FR-017; SC-005)

**Checkpoint**: All user stories are independently functional

---

## Phase 5: Polish & Cross-Cutting Concerns

**Purpose**: Documentation and full verification

- [ ] T025 Make `docs/architecture/` describe the merged state: re-read the documents at implementation time and make the narratives and every status statement listed in plan.md "Architecture Updates" true, including `docs/architecture/analyst-manager.md` (Updates paragraph and Fleet Visibility In The Web UI marking in present tense, the update notice in Tray Profile and Headless Server Profile, the refusals in Machine Authentication), `docs/architecture/platform-implementation.md` (Analyst Technology: the minimum version is implemented through deployment configuration while the feed, channels, and pinning stay planned), and `docs/architecture/analysts-models-and-hardware.md` (Implementation Validation: the spike no longer needs to prove the minimum version), plus any other statement the feature makes false; the listed rewrites are examples, not the whole job. Also document `Registry:ManagerVersion:Minimum`, its empty default, the refusal problem, and the counter in `src/platform/README.md`, the update notice and refusal handling in `src/analysts/manager/README.md`, stating in both that the update feed, installers, and self-update do not exist yet, and check `contracts/README.md`; run `node .github/scripts/check-markdown.mjs` (FR-019)
- [ ] T026 Run the full verification of quickstart.md: `dotnet build` and `dotnet test` of `src/platform/SocAlytics.Platform.slnx` and `src/analysts/manager/SocAlytics.Analysts.Manager.slnx`, and `npm run build`, `npm test`, and `npm run test:e2e -- --project simulated` in `src/clients/web`; fix any regression so the suites pass unchanged without a minimum (SC-006)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Foundational (Phase 1)**: No dependencies; blocks all user stories
- **User Story 1 (Phase 2)**: Depends on Phase 1
- **User Story 2 (Phase 3)**: Depends on Phase 1; T016 and T019 also depend on User Story 1 (`MinimumVersionState`, the
  notice)
- **User Story 3 (Phase 4)**: Depends on Phase 1 (`ManagerVersion` and the provider); independent of User Stories 1
  and 2
- **Polish (Phase 5)**: Depends on all stories

### Within Each Story

- Implementation before its tests; platform changes before the Manager and Web UI changes that consume them
- T004 before T005 and T006; T007 before T008; T008 before T009 and T010; T014 before T015 and T016; T020 before T021
  to T024

### Parallel Opportunities

- T001, T002, T003 (contracts, platform Domain, Manager Core)
- T005, T006 (platform tests, Manager tests)
- T011, T012, T013 (separate Manager test files)
- T015, T016 (platform Application and Api files, Manager Core files)
- T017, T018, T019 (separate test files)
- T021, T022 (platform tests, Web UI sources); then T023, T024 (Web UI unit and browser tests)

---

## Parallel Example: User Story 2

```text
Task: "T015 [US2] Enforce the gate in IssueQueueCredentials.cs, ClaimLogicalJob.cs, and ProblemResults.cs"
Task: "T016 [US2] Handle version refusals in PlatformClient.cs, AttemptRunner.cs, and QueueCredentialProvider.cs"
```

---

## Implementation Strategy

### MVP First (User Story 1)

1. Complete Phase 1: Foundational
2. Complete Phase 2: User Story 1
3. **STOP and VALIDATE**: a Manager below the minimum stops taking work and shows the notice

### Incremental Delivery

1. Foundational → contract, versions, setting
2. User Story 1 → Manager self-restraint and notice (MVP)
3. User Story 2 → platform enforcement independent of the Manager
4. User Story 3 → fleet visibility for Club Admins
5. Polish → architecture and READMEs true for the merged state, full verification
