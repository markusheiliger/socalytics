---

description: "Task list for Analysis Result Read for Jobs"
---

# Tasks: Analysis Result Read for Jobs

**Input**: Design documents from `specs/20261013-134112-analysis-result-read/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/upstream-results-api.md](contracts/upstream-results-api.md), [contracts/staged-inputs.md](contracts/staged-inputs.md), [quickstart.md](quickstart.md)

**Tests**: Required. The spec requires refusal tests without disclosure (SC-002), coverage tests (SC-003), staging and cleanup tests (SC-005, SC-007), contract tests in both solutions (SC-006), and an end-to-end test across both solutions (FR-012); the constitution requires focused tests. Every task adds the tests for the code it creates in the same task, so the platform and Manager test suites stay green after each task.

**Organization**: Tasks are grouped by user story. Merged before this feature starts (do not re-create): Analysis Result Manifests and Validated Acceptance `specs/archive/20261013-125502-analysis-result-manifests` (result locations, `analysis_accepted_result_manifests`, `IObjectStorage` single-object operations, `AnalysisAuditEvents`, the storage `HttpClient` and `ResultPublisher` in the Manager, simulated result modes), Analyst Manager Work Execution `specs/archive/20261012-141233-manager-work-execution` (`AttemptRunner`, `Runtime/Simulated/`, `SocAlytics.Platform.EndToEnd.Tests`), and Durable Analysis Workflow `specs/archive/20261005-130703-durable-analysis-workflow` (`analysis_logical_job_inputs` with `InputSnapshotEntry`). No migration and no new packages.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies on incomplete tasks); consecutive `[P]` tasks under one heading run in parallel, each from the same commit
- **[Story]**: Which user story this task belongs to (US1–US3)
- Every task names its exact file paths

## Path Conventions

- Contracts: repository-root `contracts/analysis/`, index `contracts/README.md`.
- Platform: `src/platform/SocAlytics.Platform.{Application,Infrastructure,Api,AppHost}/`, tests under `src/platform/Tests/`.
- Manager: `src/analysts/manager/SocAlytics.Analysts.Manager.Core/`, tests under `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/`.

---

## Phase 1: Setup (Contract)

**Purpose**: The read envelope both solutions validate against.

- [x] T001 Upstream-results envelope contract (research.md § R4; contracts/upstream-results-api.md; FR-010, SC-006): add `contracts/analysis/upstream-results/v1/upstream-results.schema.json` (JSON Schema 2020-12, `$id` per the contracts index convention; `contractVersion`, `upstream` with capability id and version, `schemaVersion`, `edgeKind`; `results[]` with `acceptedResultId`, scope `segment` or `match`, `manifestChecksum`, `manifest` by `$ref` to `contracts/analysis/result-manifest/v1/result-manifest.schema.json`, and `grants[]` of `name`, `url`, `expiresAt`; `coverage[]` with `segmentId` or `match` and `state` "`accepted`, `missing`, `failed`, `blocked`, or `skipped`"; `continuationToken`; `additionalProperties: false` everywhere), `releases/1.0.0.schema.json`, `examples/valid/` (segment upstream with partial coverage, empty page), and `examples/invalid/` with `expectations.json` (unknown coverage state, credential field, extra property, missing manifest checksum); add the index row to `contracts/README.md`. Tests: extend `src/platform/Tests/SocAlytics.Platform.Contracts.Tests/` for the catalog, index, meta-validation, and example expectations.

**Checkpoint**: The contract exists and validates.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Read grants, the upstream reader, and configuration. No user story starts before this phase is complete.

### Storage, reader, and configuration

- [x] T002 [P] Presigned read grants (research.md § R3; FR-003): add `PresignGetObject(objectKey, expiresAt)` to `src/platform/SocAlytics.Platform.Application/Abstractions/ObjectStorage/IObjectStorage.cs` and implement it in `src/platform/SocAlytics.Platform.Infrastructure/ObjectStorage/S3ObjectStorage.cs`; update the test double in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Recordings/CompletionVerificationTests.cs`. Tests: `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Results/ReadGrantConformanceTests.cs` against RustFS (reads the object before expiry, refused after expiry, refused for another key).
- [x] T003 [P] Upstream result reader and coverage (research.md § R2; data-model.md § Coverage state mapping; FR-002, FR-005): add `src/platform/SocAlytics.Platform.Application/Analysis/Results/IUpstreamResultReader.cs` and `src/platform/SocAlytics.Platform.Application/Analysis/Results/CoverageBuilder.cs`, and implement `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Analysis/UpstreamResultReader.cs` (reads the job's `analysis_logical_job_inputs` snapshot entry for one upstream node, joins `analysis_accepted_results` and `analysis_accepted_result_manifests` for exactly its `AcceptedResultIds`, orders by segment window start then accepted-result id with an opaque continuation, and reads the upstream node's logical job states per segment) registered in `src/platform/SocAlytics.Platform.Infrastructure/InfrastructureServiceCollectionExtensions.cs`; `CoverageBuilder` applies the mapping table verbatim ("A snapshot accepted result covers the segment → `accepted`", "Snapshot status `not-applicable` → `skipped`", "`Blocked` → `blocked`", "`Failed` → `failed`", "Anything else, including results accepted after the snapshot → `missing`"). Tests: `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Results/UpstreamResultReaderTests.cs` (snapshot-bound results, a result accepted after the snapshot reported `missing`, every coverage state, paging stability with every result exactly once, range filters).
- [x] T004 [P] Read configuration (data-model.md § Configuration): add `src/platform/SocAlytics.Platform.Application/Analysis/Results/AnalysisReadOptions.cs` (section `Analysis:Reads`: `GrantLifetime` "> 0, ≤ 1 hour", `MaxPageSize` "1–1000"), validated at start and registered in `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs`; set the development values (`00:10:00`, `100`) in `src/platform/SocAlytics.Platform.AppHost/Program.cs` and in the shared test settings (`src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/Support/TestIdentityAccessSettings.cs`, the settings of `src/platform/Tests/SocAlytics.Platform.Architecture.Tests/EndpointSecurityTests.cs`, the API configuration used by `src/platform/Tests/SocAlytics.Platform.Host.Tests/`, and the API configuration of `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/`). Tests: `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Results/AnalysisReadOptionsTests.cs`.

### Corrective task

- [x] T011 Complete the read configuration in the remaining test settings (corrects T004; the phase verification failed in `RecordingOptionsValidationTests` because its own settings dictionary lacks `Analysis:Reads`, which `AddApplication()` now validates at start; no research decision is affected): add `["Analysis:Reads:GrantLifetime"] = "00:10:00"` and `["Analysis:Reads:MaxPageSize"] = "100"` to `ValidSettings()` in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Recordings/RecordingOptionsValidationTests.cs`, then search `src/platform/Tests/` for every other test that builds its own settings and calls `AddApplication()` (for example `SocAlytics.Platform.Host.Tests` and `SocAlytics.Platform.EndToEnd.Tests`) and add the same two values wherever they are missing. Tests: the full `SocAlytics.Platform.Integration.Tests` project passes (`RecordingOptionsValidationTests` and `AnalysisReadOptionsTests` included).

**Checkpoint**: Grants, the reader, and configuration are in place; all existing tests pass.

---

## Phase 3: User Story 1 - Read a job's accepted upstream results (Priority: P1) 🎯 MVP

**Goal**: The Manager holding a current attempt reads exactly the job's upstream inputs with grants and coverage.

**Independent Test**: For a run with accepted segment-level results, the match-level attempt's Manager reads one upstream capability and receives every accepted result with readable artifacts and full coverage; reads for other runs, non-upstream capabilities, or fenced attempts are refused.

- [x] T005 [US1] Upstream results read operation (contracts/upstream-results-api.md; research.md § R1, § R2, § R3; data-model.md § Read query; FR-001–FR-005, FR-013; US1 scenarios 1–7): add `src/platform/SocAlytics.Platform.Application/Analysis/Results/ListUpstreamResults.cs` (current-attempt and owner checks for `Active` or `Unregistering` Managers as the existing authentication admits; the query rules of data-model.md § Read query, "`fencingToken` Required; must equal the current attempt's token", capability and schema version "must match one upstream dependency of the job", "`segmentIds` or `matchStartMs`/`matchEndMs` Optional; mutually exclusive; range half-open", "`pageSize` 1 to `Analysis:Reads:MaxPageSize`"; manifests read from storage and verified against the accepted manifest checksum; one grant per artifact and one for the manifest expiring at "the earlier of now plus `Analysis:Reads:GrantLifetime` … and the attempt's current lease expiry"), add `analysis.upstream-read-refused` (reasons `foreign-attempt` and `out-of-scope`) to `src/platform/SocAlytics.Platform.Application/Analysis/Results/AnalysisAuditEvents.cs`, and map `GET /api/v1/analysis/attempts/{attemptId}/upstream-results` (operation id `listAnalysisUpstreamResults`, policy `AnalystManager`, `Cache-Control: no-store`, the refusals of the contract table) in `src/platform/SocAlytics.Platform.Api/Analysis/AnalystManagerEndpoints.cs`; count read grants with `socalytics.analysis.read_grants_issued` and log attempt identity only; regenerate `src/platform/SocAlytics.Platform.Api/openapi/v1.json` if the OpenAPI snapshot exists. Tests: `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Results/UpstreamReadTests.cs` for US1 scenarios 1–7 (including a revoked Manager refused by authentication and an unregistering Manager reading), the envelope validated against the contract, every refusal without disclosure (SC-002), lease-bounded grants (SC-004), and no grant URL in logs or audit events.

**Checkpoint**: The read works against the real platform.

---

## Phase 4: User Story 2 - Stage upstream results for the Analyst (Priority: P2)

**Goal**: The Manager stages verified upstream results into the input mount before launch and cleans up afterwards.

**Independent Test**: A match-level attempt's input mount holds every accepted upstream manifest and artifact with verified digests and a coverage file before launch; a tampered artifact fails the attempt without launch; nothing remains after the attempt ends.

- [x] T006 [US2] Upstream staging in the Manager (research.md § R5; data-model.md § Staged inputs, § Staging failures; contracts/staged-inputs.md; FR-006–FR-009; US2 scenarios 1–5; clarifications on limits and staging once): add `ReadUpstreamResultsAsync` to `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Platform/PlatformClient.cs`; add `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Work/StagingOptions.cs` (`Work:Staging:MaxBytesPerAttempt`, development value `2147483648`, "≥ 1 MiB") and `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Work/UpstreamStager.cs` (reads every upstream dependency of the job and all pages; checks the summed sizes against the limit and the staging volume's free space before downloading; downloads with the storage `HttpClient` into the layout of contracts/staged-inputs.md; verifies each manifest against `manifestChecksum` and each artifact against the manifest; writes `coverage.json` per dependency; re-reads on a storage `403` while the attempt is current; retries storage unavailability with bounded backoff until the execution timeout; returns a local reason `staging-digest-mismatch`, `staging-limit-exceeded`, `staging-disk-full`, `staging-storage-unavailable`, `staging-read-refused`, or "obsolete"); call it from `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Work/AttemptRunner.cs` before launch for jobs with upstream dependencies, report a failed completion with the `failure.category` of data-model.md § Staging failures (`input-unavailable` or `resource-exhausted`) without launching the Analyst while logging the local reason, stop without completion when the read is obsolete, and mount `inputs/` read-only for the (simulated) runtime. Tests: `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Work/UpstreamStagerTests.cs` (fake platform and storage, temporary directories: US2 scenarios 1–5, digest mismatch with 0 launches and one failed completion with category `input-unavailable` (SC-005), limit and disk refusals with `resource-exhausted`, every completion valid against the attempt-completion contract, renewal, obsolete stop, storage retries) and validation of the contract's coverage definitions in the Manager tests (SC-006).
- [x] T007 [US2] Staged-data cleanup (FR-014, SC-007; clarification on cleanup): in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Work/AttemptRunner.cs` delete the attempt directory, including `inputs/`, when the attempt ends (completed, failed, fenced, or timed out), and at Manager start delete every attempt directory left by a previous process. Tests: `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Work/AttemptCleanupTests.cs` (0 bytes remain after each kind of end and after a simulated crash and restart).

**Checkpoint**: Staging works with the real Manager against fakes.

---

## Phase 5: User Story 3 - Match-level analysis from staged results, end to end (Priority: P3)

**Goal**: The simulated Analyst builds lineage from staged inputs; the whole chain is proven across both solutions.

**Independent Test**: Run the development workflow end to end; the match-level result's lineage equals the staged segment-level results, and `ignore-inputs` is rejected for lineage.

- [x] T008 [US3] Simulated lineage from staged inputs and end-to-end proof (research.md § R6, § R7; FR-011, FR-012; US3 scenarios 1–3; SC-001): make the simulated Analyst in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Runtime/Simulated/` build a match-scope manifest's `upstreamLineage` from `inputs/*/coverage.json` and the staged manifests (accepted result id, capability, schema version, manifest checksum) and record the consumed coverage in its artifact, and add the per-capability mode `ignore-inputs` (empty lineage; Development and Test only). Tests: simulated tests in `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Runtime/Simulated/`; extend `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/` so the development workflow's match node stages its segment-level results, its accepted lineage equals the staged results (SC-001), partial coverage is recorded, `ignore-inputs` is rejected with `result-lineage-mismatch`, no staged data remains on the Manager host after the attempts end (SC-007), and the simulated Analyst's environment, arguments, and mounts contain no grant URL, DPoP or access token, or platform address (SC-004, FR-007).

**Checkpoint**: All user stories work independently.

---

## Phase 6: Polish & Cross-Cutting Concerns

- [x] T009 Documentation (plan.md § Architecture Updates; Constitution Principle I): make the narratives listed in plan.md § Architecture Updates and every status statement this feature affects true for the merged state, re-reading `docs/architecture/` at implementation time; the listed rewrites are examples, not the whole job. In particular, turn the "decided, not yet implemented" wording about the Manager-mediated read and the input mount in `docs/architecture/analysts-models-and-hardware.md` and `docs/architecture/analyst-runtime-and-recovery.md` into present tense, add the staging responsibility to § Analyst Manager Responsibilities in `docs/architecture/analyst-runtime-and-recovery.md`, and update `docs/architecture/contracts-and-compatibility.md` § Scope And Dependencies, `docs/architecture/analysts-models-and-hardware.md` § Implementation Validation, and `docs/architecture/platform-implementation.md` (§ Acceptance Evidence, § Architecture Reassessment). Update `src/platform/README.md`, `src/analysts/manager/README.md`, and the "Current State" in `AGENTS.md`.
- [ ] T010 Run the validation in [quickstart.md](quickstart.md): restore, build, and test both solutions as documented, and `node .github/scripts/check-markdown.mjs`; fix what fails.

---

## Dependencies & Execution Order

- **Setup (Phase 1)**: T001 first; the platform validates the envelope against it.
- **Foundational (Phase 2)**: after Setup; T002, T003, and T004 in parallel, then T011 (corrects T004's missing test settings). Blocks all stories.
- **US1 (Phase 3)**: after Foundational. **US2 (Phase 4)**: after US1 (the Manager calls the read); T006, then T007 (both change `AttemptRunner.cs`). **US3 (Phase 5)**: after US2 (the simulated Analyst consumes staged inputs).
- **Polish (Phase 6)**: after all stories.

### Parallel opportunities

- Phase 2: T002 (`IObjectStorage.cs`, `S3ObjectStorage.cs`, the test double in `Recordings/CompletionVerificationTests.cs`, `Analysis/Results/ReadGrantConformanceTests.cs`), T003 (`IUpstreamResultReader.cs`, `CoverageBuilder.cs`, `Persistence/Analysis/UpstreamResultReader.cs`, `InfrastructureServiceCollectionExtensions.cs`, `Analysis/Results/UpstreamResultReaderTests.cs`), and T004 (`AnalysisReadOptions.cs`, `ApplicationServiceCollectionExtensions.cs`, `AppHost/Program.cs`, the shared test settings, `Analysis/Results/AnalysisReadOptionsTests.cs`) change disjoint files.
- No story phase has tasks with disjoint footprints: each builds on the previous task's code.

## Implementation Strategy

1. Contract and foundations, then User Story 1 as the MVP (the read works for any Manager).
2. Add User Story 2 (staging), then 3 (end-to-end lineage).
3. Documentation and full validation.
