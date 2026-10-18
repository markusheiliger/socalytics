---

description: "Task list for Partial-Coverage Match Barriers"
---

# Tasks: Partial-Coverage Match Barriers

**Input**: Design documents from `specs/20261017-212004-partial-coverage-barriers/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: The spec requires automated evidence (FR-021), so every story has test tasks. Test tasks follow the
implementation tasks of their story so that each landed task keeps the build and test suites green.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.
Foundational work (contracts, schema, policy type, and carrying the declaration into runs) precedes every story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; they change disjoint
  files and do not depend on each other
- **[Story]**: Which user story this task belongs to (US1, US2, US3, US4)
- Every task names its exact file paths

---

## Phase 1: Foundational (Blocking Prerequisites)

**Purpose**: Contract versions, the coverage policy, the migration, and copying the declaration into run snapshots

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

### Contract schemas

- [x] T001 [P] Release `analyst-manifest` 1.1.0 in `contracts/analysis/analyst-manifest/v1/`: in `analyst-manifest.schema.json` set `x-socalytics-version` to `1.1.0`, change `manifestVersion` to `enum ["1.0.0", "1.1.0"]`, and add the optional `upstream[].coverage` object (`additionalProperties: false`, required `mode` with `enum ["partial"]`, optional `minimumAcceptedFraction` with `"type": "number", "exclusiveMinimum": 0, "maximum": 1`); add the immutable copy `releases/1.1.0.schema.json` (keep `releases/1.0.0.schema.json` unchanged); add `examples/valid/match-partial-coverage.json`, `examples/valid/match-partial-coverage-fraction.json`, `examples/invalid/partial-coverage-zero-fraction.json`, and `examples/invalid/partial-coverage-unknown-mode.json` with their entries in `examples/invalid/expectations.json` (contracts/analysis-contracts.md; FR-001, FR-002, FR-022)
- [x] T002 [P] Release `result-manifest` 1.1.0 in `contracts/analysis/result-manifest/v1/`: in `result-manifest.schema.json` set `x-socalytics-version` to `1.1.0` and add the optional `inputCoverage` array (`maxItems: 64`; items with `capability`, `schemaVersion`, and `units` (`maxItems: 1024`, each exactly one of `segmentId` or `match: true` and `state` in `["accepted", "missing", "failed", "blocked", "skipped"]`), `additionalProperties: false`); add `releases/1.1.0.schema.json` (keep `releases/1.0.0.schema.json` unchanged), `examples/valid/match-result-partial-coverage.json`, and `examples/invalid/coverage-unknown-state.json` with its entry in `examples/invalid/expectations.json` (contracts/analysis-contracts.md; FR-014, FR-022)
- [x] T003 [P] Release `upstream-results` 1.1.0 in `contracts/analysis/upstream-results/v1/`: in `upstream-results.schema.json` set `x-socalytics-version` to `1.1.0` and add the optional `upstream.coverage` object (`additionalProperties: false`, required `mode` with `enum ["complete", "partial"]`, optional `minimumAcceptedFraction` with `"type": "number", "exclusiveMinimum": 0, "maximum": 1`); add `releases/1.1.0.schema.json` (keep `releases/1.0.0.schema.json` unchanged) and `examples/valid/match-partial-barrier-failed-segment.json` (contracts/analysis-contracts.md; FR-012, FR-022)

### Contract index and contract tests

- [x] T004 Update `contracts/README.md` to list version `1.1.0` and the `releases/1.1.0.schema.json` baselines of the three schemas, and extend `src/platform/Tests/SocAlytics.Platform.Contracts.Tests/AnalystManifestContractTests.cs`, `src/platform/Tests/SocAlytics.Platform.Contracts.Tests/ResultContractsTests.cs`, and `src/platform/Tests/SocAlytics.Platform.Contracts.Tests/ContractCatalog.cs` so the new valid examples validate, the new invalid examples are rejected, `1.0.0` documents stay valid, and the `1.1.0` schemas are same-major compatible with their `1.0.0` baselines (FR-001, FR-014, FR-022; SC-004)

### Coverage policy and schema

- [x] T005 [P] Add the Domain value `CoveragePolicy` in `src/platform/SocAlytics.Platform.Domain/Analysis/CoveragePolicy.cs` (`Mode` `complete` | `partial`, `MinimumAcceptedFraction` "`0 < f <= 1`" only with `partial`, `RequiredAccepted(expected) = max(1, ceil(f × expected))`, `1` when `f` is null, and `Complete` as default), the coverage mode names and the failure reason `upstream-coverage-below-minimum` in `src/platform/SocAlytics.Platform.Domain/Analysis/AnalysisPersistedNames.cs`, the `CoveragePolicy` (default complete) on `RunEdgeState` in `src/platform/SocAlytics.Platform.Domain/Analysis/RunGraphState.cs`, and the parsed coverage policy on `DeclaredUpstream` in `src/platform/SocAlytics.Platform.Domain/Registry/CapabilityDeclaration.cs` (data-model.md "Coverage Policy"; research R2; FR-002)
- [x] T006 [P] Add the forward-only migration `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Migrations/NNNN_analysis_partial_coverage.sql`, numbered as the next free number on an up-to-date default branch: add `coverage_mode text NOT NULL DEFAULT 'complete'` with "`CHECK (coverage_mode IN ('complete', 'partial'))`" and `minimum_accepted_fraction numeric NULL` with "`CHECK (minimum_accepted_fraction IS NULL OR (coverage_mode = 'partial' AND minimum_accepted_fraction > 0 AND minimum_accepted_fraction <= 1))`" to `socalytics.analysis_dependency_edges`, and replace the failure-reason check of `socalytics.analysis_logical_jobs` so it allows `'attempts-exhausted'`, `'upstream-required-failed'`, and `'upstream-coverage-below-minimum'` (data-model.md; FR-004, FR-008)

### Declaration into run snapshots

- [x] T007 Carry the declaration into runs: add `CoveragePolicy` to `ResolvedWorkflowEdge` in `src/platform/SocAlytics.Platform.Application/Registry/ResolvedWorkflowDefinition.cs`; set it in `src/platform/SocAlytics.Platform.Infrastructure/Registry/RegistryWorkflowDefinitionResolver.cs` and `src/platform/SocAlytics.Platform.Infrastructure/Registry/ConfiguredWorkflowDefinitionResolver.cs` from the dependent node's declared upstream entry whose capability id matches the upstream node's capability (complete when absent), adding an optional `Coverage` (`Mode`, `MinimumAcceptedFraction`) to the configured edge options in `ConfiguredWorkflowDefinitionResolver.cs`; write a `coverage` object (`mode`, `minimumAcceptedFraction`) only for partial edges in `src/platform/SocAlytics.Platform.Application/Analysis/WorkflowSnapshotMapper.cs` so complete edges serialize as today; persist and load `coverage_mode` and `minimum_accepted_fraction` in `src/platform/SocAlytics.Platform.Application/Analysis/Stores/IAnalysisRunStore.cs` and `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Analysis/AnalysisRunStore.cs` (run creation and the `RunGraphState` load) (research R4; FR-004)
- [x] T008 Cover the foundation: `CoveragePolicy` rules and rounding (`RequiredAccepted` for 1, 3, and 4 expected units with fractions null, 0.5, 1) in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Domain/AnalysisValueObjectTests.cs`; the migration's columns, defaults, and checks in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/AnalysisSchemaTests.cs`; and in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/RunCreationTests.cs` that a run copies the edge policy into `analysis_dependency_edges` and the run snapshot, that a workflow without partial edges keeps its previous snapshot digest, and that a later Registry change does not alter an existing run (FR-004; SC-004)

**Checkpoint**: Contracts are released, runs carry the coverage policy; behavior is unchanged

---

## Phase 2: User Story 1 - A match analysis survives one failed segment (Priority: P1) 🎯 MVP

**Goal**: A match-scoped job behind a partial barrier waits for every upstream segment job to be final, becomes ready
at or above the minimum with coverage in its input snapshot, is blocked below it, and the run never ends `Completed`
when a bridged segment failed.

**Independent Test**: With a workflow whose match-scoped node has a partial edge on a segment-scoped node and one
upstream segment job failed permanently, the match-scoped job becomes ready only after all segment jobs are final, its
input snapshot names the failed segment, and the run ends `PartiallyCompleted`.

### Readiness rule

- [X] T009 [US1] Implement partial barriers in `src/platform/SocAlytics.Platform.Domain/Analysis/ReadinessEvaluator.cs`: an edge is a partial barrier when its `CoveragePolicy` is partial, its upstream node is segment-scoped, and its dependent node is match-scoped (otherwise it is evaluated as complete); `BlockingUpstream` ignores a `Failed` or `Blocked` upstream node through a partial barrier; `RequiredSatisfied` requires every relevant upstream job to be `Completed`, `Failed`, or `Blocked` and the accepted count to reach `RequiredAccepted(expected)`; when all are final and the count is below it, block the dependent node (blocker the upstream node) and its jobs; `Satisfiable` treats a partial barrier as satisfiable while a relevant job is not final or the minimum is met; extend `InputSnapshotEntry` with `CoverageMode`, `MinimumAcceptedFraction`, and for partial barriers `Coverage` (`[{ segmentId, state }]` ordered by segment window, states `accepted`, `failed`, `blocked`) and add `SnapshotStatus.Partial` ("`partial`" when at least one unit is not accepted, `accepted` otherwise); expose which jobs were blocked below the minimum in `BlockedJob` (research R5, R6; FR-006, FR-007, FR-008, FR-009, FR-011)
- [X] T010 [US1] Persist and observe the rule: write the new snapshot fields into `analysis_logical_job_inputs.inputs` and read snapshots without them as `complete` in `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Analysis/LogicalJobStore.cs` and `src/platform/SocAlytics.Platform.Application/Analysis/Stores/ILogicalJobStore.cs`; record the failure reason `upstream-coverage-below-minimum` for jobs blocked below the minimum and count `socalytics.analysis.partial_barriers_ready` (tag `coverage` = `complete` or `partial`) and `socalytics.analysis.partial_barriers_blocked` in `src/platform/SocAlytics.Platform.Application/Analysis/RunReadinessCoordinator.cs` and `src/platform/SocAlytics.Platform.Application/Telemetry/AnalysisMetrics.cs`, with structured logs using only the run and job identifiers analysis logs already carry (research R11; FR-009, FR-010, FR-023)

### Readiness evidence

- [X] T011 [P] [US1] Cover the evaluator in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Domain/ReadinessEvaluatorTests.cs`: waiting while upstream segment jobs are waiting, ready, or claimed even with the minimum met; ready with one failed segment and coverage in the snapshot; blocked below the minimum and with no accepted segment; fraction rounding; an upstream node blocked by its own failed upstream; all accepted giving status `accepted` and a `Completed` run; several dependencies where a complete edge still blocks; a conditional partial edge that applies and one that is `not-applicable`; a partial policy on a match-scoped upstream behaving as complete; segment-to-segment edges keeping node-level blocking; and run outcomes `PartiallyCompleted` with a failed bridged segment and `Failed` when nothing requested completed (FR-006, FR-007, FR-008, FR-009, FR-011; SC-002)
- [x] T012 [P] [US1] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/PartialCoverageReadinessTests.cs` (Docker): with a partial-coverage test workflow, fail one segment job by exhausting its attempts and assert the persisted input snapshot, publication of the match-scoped job only after the last segment job is final, the blocked job's `upstream-coverage-below-minimum` reason and no publication below the minimum, identical results after a platform restart and after the analysis reconciliation, immutability of the input snapshot on a retried match-scoped attempt, the run state `PartiallyCompleted`, and the two counters (FR-007, FR-008, FR-009, FR-010, FR-011, FR-023; SC-002)

**Checkpoint**: Partial barriers decide readiness and run outcomes; complete-coverage tests stay green

---

## Phase 3: User Story 2 - The match-level Analyst sees and reports the coverage it used (Priority: P1)

**Goal**: The Manager reads and stages exactly the snapshot's coverage and coverage mode, the simulated Analyst reports
it, and acceptance rejects missing or different coverage reports.

**Independent Test**: For a partial-coverage job with one failed segment, the upstream read and the staged
`coverage.json` name the failed segment; a result reporting that coverage is accepted, one with `coverage-mismatch` or
`coverage-omitted` fails with `result-coverage-mismatch`.

### Read, staging, reporting, and acceptance

- [x] T013 [P] [US2] Serve snapshot-fixed coverage in the upstream read: for partial barriers build coverage from the snapshot's `Coverage` entries (other entries keep today's mapping) in `src/platform/SocAlytics.Platform.Application/Analysis/Results/CoverageBuilder.cs` and `src/platform/SocAlytics.Platform.Application/Analysis/Results/ListUpstreamResults.cs`, load the snapshot fields in `src/platform/SocAlytics.Platform.Application/Analysis/Results/IUpstreamResultReader.cs` and `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Analysis/UpstreamResultReader.cs`, and emit `upstream.coverage` (`mode` `complete` or `partial`, `minimumAcceptedFraction`) and `contractVersion` `1.1.0` on every page in `src/platform/SocAlytics.Platform.Api/Analysis/AnalystManagerEndpoints.cs` (research R7; FR-012)
- [x] T014 [P] [US2] Stage the coverage mode in the Manager: read `upstream.coverage` in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Platform/PlatformWorkContracts.cs` and `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Platform/PlatformClient.cs` (accepting pages with `contractVersion` `1.0.0` and `1.1.0`), write `mode` and `minimumAcceptedFraction` next to `coverage` in each `coverage.json` in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Work/UpstreamStager.cs`, and cover staging of both modes and an identical `coverage.json` for a retried attempt in `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Work/UpstreamStagerTests.cs` (research R7; FR-013)
- [x] T015 [P] [US2] Extend the simulated Analyst (Development and Test only): `FailSegmentStartsMs` (fail only jobs whose segment window starts at a listed millisecond, with the configured reason) and the result modes `coverage-mismatch` (flips one unit state) and `coverage-omitted` (drops `inputCoverage`) in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Runtime/Simulated/SimulatedAnalystOptions.cs` and `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Runtime/Simulated/SimulatedRuntimeAdapter.cs`; write a `1.1.0` manifest with `inputCoverage` built from every staged `coverage.json` in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Runtime/Simulated/SimulatedResultWriter.cs`; cover them in `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Runtime/Simulated/SimulatedResultModeTests.cs` and `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Runtime/Simulated/SimulatedRuntimeTests.cs` (research R10; FR-019)

- [x] T016 [P] [US2] Add the coverage check to `src/platform/SocAlytics.Platform.Application/Analysis/Results/ResultManifestVerifier.cs`: accept `contractVersion` `1.0.0` and `1.1.0`, add `ResultRejectionReasons.CoverageMismatch` = `result-coverage-mismatch`, and after the lineage check compare `inputCoverage` with the job's input snapshot: reject when a partial barrier has no report, a report names a dependency the job does not have, a unit is duplicated, a partial barrier's report differs from the snapshot coverage in any unit or state (order not significant), or a report for another dependency does not report exactly the units of the snapshot's accepted results as `accepted`; pass the job's input snapshot to the verifier from `src/platform/SocAlytics.Platform.Application/Analysis/Commands/CompleteAttempt.cs` where it does not already receive it; never relax an existing check (research R8; FR-015, FR-016)

### Coverage evidence

- [x] T017 [P] [US2] Cover the platform side in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Results/UpstreamReadTests.cs` (snapshot coverage and `upstream.coverage` for partial and complete dependencies, unchanged reads for other jobs), `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Results/ResultManifestVerifierTests.cs` (equal, missing, different, duplicated, and unknown-dependency reports; a report for a complete dependency checked only on its `accepted` units; `1.0.0` manifests without reports for jobs without partial barriers), and `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Results/ValidatedAcceptanceTests.cs` (a rejected report ends the attempt as failed with `result-coverage-mismatch`, counted by `socalytics.analysis.completions_rejected`, and the retry rules apply) (FR-012, FR-015, FR-016, FR-023; SC-003)
- [x] T018 [P] [US2] Cover the Manager's own contract check of `1.1.0` result manifests (accepted with `inputCoverage`, rejected with an unknown state as `result-manifest-schema-invalid`) in `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Work/AttemptRunnerTests.cs` (FR-014)

**Checkpoint**: Coverage is fixed, staged, reported, and verified

---

## Phase 4: User Story 3 - Capabilities declare partial coverage through the Registry (Priority: P2)

**Goal**: Imported and seeded declarations are validated and stored, so runs created from them carry the policy.

**Independent Test**: Import a `1.1.0` manifest with a partial-coverage declaration and a `1.0.0` manifest; both are
stored, and invalid declarations are refused as `manifest-invalid` with the validation error `partial-coverage`.

### Declaration validation

- [x] T019 [US3] Validate declarations in `src/platform/SocAlytics.Platform.Infrastructure/Registry/AnalystManifestValidator.cs`: accept `manifestVersion` `1.0.0` and `1.1.0`, parse `upstream[].coverage` into `DeclaredUpstream`, and return an invalid result with the error `partial-coverage` at `/upstream/<i>/coverage` when the capability's `executionScope` is `segment` or the entry's `kind` is `optional` (the fraction range comes from the schema); update the class comment that names the `1.0.0` release (research R3; FR-001, FR-003)
- [x] T020 [US3] Seed declarations in `src/platform/SocAlytics.Platform.Infrastructure/Registry/ConfiguredWorkflowSeeder.cs`: build the seed manifest of a dependent node as `1.1.0` with `upstream[].coverage` from the configured edge's `Coverage` (and `1.0.0` without), and validate it through `IAnalystManifestValidator` so an invalid configured declaration fails the seed visibly instead of being stored (research R3; FR-005)

### Declaration evidence

- [x] T021 [P] [US3] Cover import in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/AnalystImageImportTests.cs`: a `1.1.0` manifest with a partial declaration and one with a fraction are stored with the declaration, a `1.0.0` manifest is stored with complete coverage, and partial declarations on a segment-scoped capability, on an optional dependency, or with fraction `0` or above `1` are refused (FR-001, FR-002, FR-003; SC-004)
- [x] T022 [P] [US3] Cover the seed and resolution in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/RegistryResolutionTests.cs`: a configured partial edge is seeded and resolved with its policy, an invalid configured declaration is not seeded, and a Registry workflow resolves the policy from the dependent's declaration (FR-004, FR-005)

**Checkpoint**: Capabilities opt in through the Registry and the development seed

---

## Phase 5: User Story 4 - Members see what coverage a match-level result was built from (Priority: P3)

**Goal**: The member run status read shows each node's input coverage counts per upstream dependency.

**Independent Test**: For a `PartiallyCompleted` run with one failed segment, a Coach reads the run status; the
partial-coverage node shows one failed and the rest accepted, and nodes without a snapshot show `inputCoverage` `null`.

### Run status read

- [x] T023 [US4] Add `InputCoverage` (`null` or `[{ UpstreamNodeKey, CoverageMode, MinimumAcceptedFraction, Accepted, Failed, Blocked, Skipped, Missing }]`, ordered by upstream node key, summed over the node's jobs with input snapshots; partial entries count their `coverage` units, other entries count by snapshot status as research R9 maps them) to `AnalysisNodeStatusView` in `src/platform/SocAlytics.Platform.Application/Analysis/Queries/AnalysisRunViews.cs` and compute it in `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Analysis/AnalysisRunQueries.cs` (research R9; FR-017, FR-018)
- [x] T024 [US4] Expose `inputCoverage` on `AnalysisNodeStatusDto` in `src/platform/SocAlytics.Platform.Api/Analysis/AnalysisRunEndpoints.cs` (authorization unchanged) and refresh the committed snapshot `src/platform/SocAlytics.Platform.Api/openapi/v1.json` with `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests` (contracts/run-status-input-coverage.md; FR-017, FR-022)
- [x] T025 [US4] Cover the read in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/AnalysisEndpointTests.cs`: partial-coverage counts with one failed segment, complete counts for a complete edge, `null` before any snapshot and for nodes without incoming edges, and the unchanged refusal for members without read access to the team; when the member fact reads of `specs/archive/20261017-175648-fact-indexing-queries` exist on the base branch, also assert that the partial-coverage node's own coverage there is one accepted match window (FR-017, FR-018; SC-005)

**Checkpoint**: People can see what coverage each match-level result used

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: The development workflow case, end-to-end evidence, documentation, and validation

### Development workflow

- [x] T026 Add the partial-coverage case to the development workflow: in `src/platform/SocAlytics.Platform.Api/appsettings.Development.json` add `dev-partial-match-capability` to `RequestedCapabilities`, the node `dev-partial-match-node` (`high-level`, `match`, its own profile and image digests in the existing style), and the `required` edge from `dev-segment-node` with `"Coverage": { "Mode": "partial" }` and no fraction; add the capability constant and the simulated Manager capability configuration in `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/Support/EndToEndHarness.cs`; and update the expectations of `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/WorkflowCapabilitiesTests.cs` and `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Messaging/ManagerConsumerTopologyTests.cs` to the three development capabilities (research R10; FR-020)

### End-to-end evidence

- [ ] T027 Add `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/PartialCoverageEndToEndTests.cs`: run the development workflow with a recording and segmentation duration that yield at least two segments (the harness may override the configured workflow's segmentation duration) and the simulated Analyst failing the segment starting at `0` (`FailSegmentStartsMs`), and assert that `dev-match-node` is blocked, `dev-partial-match-node`'s result is accepted with `inputCoverage` naming the failed segment, the run ends `PartiallyCompleted`, the staged coverage, accepted result, and run status read agree for every unit, and that with all segments succeeding the run ends `Completed`; keep the existing end-to-end tests green with the three-node workflow (FR-021; SC-001, SC-005)

### Documentation

- [ ] T028 Make `docs/architecture/` true for the merged state: re-read [job-processing.md](../../docs/architecture/job-processing.md) (Readiness And Dependencies, including scheduler step 4, and Run State And Recovery), [analysts-models-and-hardware.md](../../docs/architecture/analysts-models-and-hardware.md) (Capability Declaration, Result Manifest And Lineage, Capability-Scoped Result Access), [platform-implementation.md](../../docs/architecture/platform-implementation.md) (development evidence), and every other document that mentions coverage or segment barriers, and correct every statement this feature makes false, including the "decided as follows and not yet implemented" and "(decided, not yet implemented)" wording added for partial coverage, which becomes present tense as development evidence against the simulated Analyst; the rewrites listed in plan.md → Architecture Updates are examples, not the whole job. Keep `AGENTS.md` "Current State" and `src/platform/README.md` consistent with the implemented partial-coverage barriers, and pass `node .github/scripts/check-markdown.mjs` (Constitution Principle I)

### Validation

- [ ] T029 Run the validation in [quickstart.md](quickstart.md) (contracts, integration, host, Manager, and end-to-end tests) and fix any failure in the files of the task that caused it (SC-001 to SC-005)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Foundational (Phase 1)**: No dependencies; blocks all stories.
- **US1 (Phase 2)**: Depends on Phase 1.
- **US2 (Phase 3)**: Depends on US1 (input snapshot coverage).
- **US3 (Phase 4)**: Depends on Phase 1 (policy and resolver options); independent of US1 and US2 for its own tests.
- **US4 (Phase 5)**: Depends on US1 (snapshot fields).
- **Polish (Phase 6)**: Depends on all stories (the development workflow needs the seed of US3, the end-to-end test
  needs US1, US2, and US4).

### Within Phases

- T001–T003 run in parallel; T004 follows them (shared `contracts/README.md` and contract tests).
- T005 and T006 run in parallel; T007 needs T005; T008 needs T006 and T007.
- T009 → T010 → T011 and T012 in parallel.
- T013, T014, T015, and T016 run in parallel (platform read, Manager staging, simulated runtime, acceptance check);
  T017 and T018 run in parallel after them.
- T019 → T020 → T021 and T022 in parallel.
- T023 → T024 → T025.
- T026 → T027 → T028 → T029.

## Parallel Example: User Story 2

```text
T013 platform upstream read (Application, Infrastructure, Api)
T014 Manager staging (Platform client, UpstreamStager, UpstreamStagerTests)
T015 simulated Analyst (Runtime/Simulated and its tests)
T016 acceptance coverage check (ResultManifestVerifier, CompleteAttempt)
```

## Implementation Strategy

### MVP First

1. Phase 1 (contracts, policy, migration, run snapshots).
2. Phase 2 (US1): partial barriers decide readiness and outcomes; validate with T011 and T012.

### Incremental Delivery

1. US2 makes the coverage visible to Analysts and verified on acceptance.
2. US3 lets real images and the seed opt in.
3. US4 shows the coverage to members.
4. Polish adds the development workflow case, end-to-end evidence, and documentation.
