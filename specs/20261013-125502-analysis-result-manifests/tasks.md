---

description: "Task list for Analysis Result Manifests and Validated Acceptance"
---

# Tasks: Analysis Result Manifests and Validated Acceptance

**Input**: Design documents from `specs/20261013-125502-analysis-result-manifests/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/result-grants-api.md](contracts/result-grants-api.md), [contracts/result-manifest.md](contracts/result-manifest.md), [contracts/result-acceptance.md](contracts/result-acceptance.md), [quickstart.md](quickstart.md)

**Tests**: Required. The spec requires one test per acceptance check (SC-002), contract tests in both solutions (SC-006), and an end-to-end test across both solutions (FR-016); the constitution requires focused tests. Every task adds the tests for the code it creates in the same task, so the platform and Manager test suites stay green after each task.

**Organization**: Tasks are grouped by user story. Merged before this feature starts (do not re-create): Analyst Manager Work Execution `specs/archive/20261012-141233-manager-work-execution` (Manager `Work/`, `AttemptRunner`, `Runtime/Simulated/`, `PlatformClient` claim, heartbeat, and completion, the `SocAlytics.Platform.EndToEnd.Tests` project), Durable Analysis Workflow `specs/archive/20261005-130703-durable-analysis-workflow` (`CompleteAttempt`, `IAcceptedResultStore`, the attempt-completion contract), and Recording Lineage and Upload `specs/archive/20261005-130702-recording-lineage-upload` (`IObjectStorage`, `S3ObjectStorage`, `RustFsContainerFixture`, `JsonSchemaContractValidator`). No new package versions.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies on incomplete tasks); consecutive `[P]` tasks under one heading run in parallel, each from the same commit
- **[Story]**: Which user story this task belongs to (US1–US3)
- Every task names its exact file paths

## Path Conventions

- Contracts: repository-root `contracts/analysis/`, index `contracts/README.md`.
- Platform: `src/platform/SocAlytics.Platform.{Application,Infrastructure,Api,AppHost}/`, tests under `src/platform/Tests/`.
- Manager: `src/analysts/manager/SocAlytics.Analysts.Manager.Core/`, tests under `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/`.

---

## Phase 1: Setup (Contracts)

**Purpose**: The machine-readable contracts both solutions validate against.

- [X] T001 Result-manifest and result-grants contracts (contracts/result-manifest.md; contracts/result-grants-api.md; research.md § R2; FR-005, SC-006): add `contracts/analysis/result-manifest/v1/result-manifest.schema.json` (JSON Schema 2020-12, `$id` per the contracts index convention, the shape of contracts/result-manifest.md with `additionalProperties: false` at every level and digests from `contracts/common/v1/`), `contracts/analysis/result-manifest/v1/releases/1.0.0.schema.json`, `examples/valid/segment-result.json`, `examples/valid/match-result-with-lineage.json`, and `examples/invalid/` with `expectations.json` (missing scope, unknown property, presigned URL in an artifact name, credential field, inline media, negative fact count, confidence above 1, missing `contractVersion`); add `contracts/analysis/result-grants/v1/` with a request schema, a response schema, valid examples, and invalid examples (duplicate names, invalid name, invalid digest, size 0) with expectations; add both index rows to `contracts/README.md`. Tests: extend `src/platform/Tests/SocAlytics.Platform.Contracts.Tests/` so the catalog, index, meta-validation, and example expectations cover both contracts.

**Checkpoint**: The contracts exist and validate.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Storage operations, persistence, and configuration the stories build on. No user story starts before this phase is complete.

### Storage, persistence, and configuration

- [X] T002 [P] Single-object storage operations (research.md § R4; FR-002, FR-010): add `PresignPutObject(objectKey, contentLength, Sha256Digest, expiresAt)`, `GetObjectChecksumAsync(objectKey, ct)` (full-object SHA-256 and length, or null when storage cannot attest it), and `ReadObjectAsync(objectKey, maxBytes, ct)` (bounded read that reports "too large" without reading further) to `src/platform/SocAlytics.Platform.Application/Abstractions/ObjectStorage/IObjectStorage.cs` and implement them in `src/platform/SocAlytics.Platform.Infrastructure/ObjectStorage/S3ObjectStorage.cs` (presigned `PUT` signing `Content-Length` and `x-amz-checksum-sha256`; `HeadObject` with checksum mode; ranged `GET`), mapping storage failures to `ObjectStorageUnavailableException`; update the test double in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Recordings/CompletionVerificationTests.cs` to the extended interface. Tests: `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Results/SingleObjectStorageConformanceTests.cs` against RustFS (wrong body, wrong length, and missing checksum header refused; checksum and length reported after a correct `PUT`; bounded read refuses oversize objects).
- [x] T003 [P] Result persistence (data-model.md; research.md § R5): add the next free forward-only migration `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Migrations/NNNN_analysis_result_manifests.sql` (numbered at implementation time on an up-to-date default branch) creating `analysis_result_object_declarations` (`attempt_id`, `name`, `size_bytes`, `digest`, `declared_at`; primary key `(attempt_id, name)`) and `analysis_accepted_result_manifests` (`accepted_result_id` primary key and foreign key to `analysis_accepted_results`, `contract_version`, `output_type` "`foundational-facts` or `soccer-concepts`", `validity_start_ms`, `validity_end_ms`, `fact_count` "≥ 0", `artifacts` jsonb, `upstream_lineage` jsonb), both immutable through `socalytics.reject_immutable_change()`; add `src/platform/SocAlytics.Platform.Application/Analysis/Results/IResultDeclarationStore.cs` and `IAcceptedResultManifestStore.cs`, their Dapper implementations in `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Analysis/ResultDeclarationStore.cs` and `AcceptedResultManifestStore.cs`, and their registration in `src/platform/SocAlytics.Platform.Infrastructure/InfrastructureServiceCollectionExtensions.cs`; classify both tables as immutable in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Structure/PersistedTableClassifications.cs`. Tests: `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Results/ResultPersistenceTests.cs` (insert and read; updates and deletes refused; idempotent same-digest declaration; different digest refused).
- [x] T004 [P] Result configuration (data-model.md § Configuration; FR-003): add `src/platform/SocAlytics.Platform.Application/Analysis/Results/AnalysisResultOptions.cs` (section `Analysis:Results`: `GrantLifetime` "> 0, ≤ 1 hour", `MaxObjectsPerAttempt` "≥ 1", `MaxObjectBytes` "≥ `MaxManifestBytes`", `MaxAttemptBytes` "≥ `MaxObjectBytes`", `MaxManifestBytes` "≥ 1 KiB"), validated at start and registered in `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs`; set the development values (`00:10:00`, `16`, `104857600`, `1073741824`, `1048576`) in `src/platform/SocAlytics.Platform.AppHost/Program.cs` and in the shared test settings (`src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/Support/TestIdentityAccessSettings.cs`, the settings of `src/platform/Tests/SocAlytics.Platform.Architecture.Tests/EndpointSecurityTests.cs`, the API configuration used by `src/platform/Tests/SocAlytics.Platform.Host.Tests/`, and the API configuration of `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/`). Tests: `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Results/AnalysisResultOptionsTests.cs` (each invalid value refused at start).

**Checkpoint**: Storage, tables, and configuration are in place; all existing tests pass.

---

## Phase 3: User Story 1 - Write an attempt's results to object storage (Priority: P1) 🎯 MVP

**Goal**: Managers obtain per-object grants for their current attempt and store results directly in object storage.

**Independent Test**: For a claimed attempt, request grants for a manifest and an artifact, store both, and find them under the attempt's location; requests for fenced, foreign, or revoked attempts are refused.

- [x] T005 [US1] Result write grants operation (contracts/result-grants-api.md; research.md § R1, § R6; FR-001–FR-004, FR-017; US1 scenarios 1–6): add `src/platform/SocAlytics.Platform.Application/Analysis/Results/IssueResultGrants.cs` (current-attempt and owner checks for a Manager whose registration is `Active` or `Unregistering`, as the existing Manager authentication admits; the object-name rule "`^[a-z0-9][a-z0-9._-]{0,127}$`" with `manifest.json` for the manifest, the limits of `AnalysisResultOptions`, declarations through `IResultDeclarationStore` with same-digest idempotency and `result-object-redeclared`, grant expiry "the earlier of now plus `GrantLifetime` and the attempt's current lease expiry", location `results/{runId}/{logicalJobId}/{attemptId}/`) and `src/platform/SocAlytics.Platform.Application/Analysis/Results/AnalysisAuditEvents.cs` (`analysis.result-grant-refused` with reason `foreign-attempt`, actor `AnalystManager`, no URLs or headers); map `POST /api/v1/analysis/attempts/{attemptId}/result-grants` (operation id `issueAnalysisResultGrants`, policy `AnalystManager`, `Cache-Control: no-store`, the refusals of the contract table) in `src/platform/SocAlytics.Platform.Api/Analysis/AnalystManagerEndpoints.cs`; count issued grants with the metric `socalytics.analysis.result_grants_issued` and log attempt identity and object names only; regenerate `src/platform/SocAlytics.Platform.Api/openapi/v1.json` if the OpenAPI snapshot exists. Tests: `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Results/ResultGrantTests.cs` for US1 scenarios 1–6, every refusal of the contract table, a revoked Manager refused by authentication, an unregistering Manager obtaining grants for its current attempt, lease-bounded expiry (SC-004), the audit events, and that no log or audit record contains `X-Amz-Signature` or a grant URL (SC-007); extend the golden-fixture tests in `src/platform/Tests/SocAlytics.Platform.Contracts.Tests/` with the operation's real responses.
- [x] T006 [US1] Manager result publishing with the simulated Analyst (research.md § R7; FR-004, FR-014, FR-015 valid mode; SC-003): add `RequestResultGrantsAsync` to `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Platform/PlatformClient.cs`; add `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Work/ResultPublisher.cs` (reads `manifest.json` and artifacts from the attempt's result directory, checks `manifest.json` against the embedded result-manifest schema and reports a failed completion with `failure.category` `analyst-error` without uploading when it is invalid, logging the local reason `result-manifest-schema-invalid`, computes SHA-256 digests, requests grants, `PUT`s each object with only the grant's required headers through a separate storage `HttpClient` that accepts plain HTTP only for loopback hosts in Development and Test, renews grants on `403` while the attempt is current, and stops the attempt when grants are refused as obsolete or forbidden) and call it from `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Work/AttemptRunner.cs` before completing with the manifest reference and checksum, and make `AttemptRunner` end the attempt without retrying when the completion response reports `outcome: "failed"` (contracts/result-acceptance.md), so the Manager is ready before the platform starts rejecting results; make the simulated Analyst in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Runtime/Simulated/` write a deterministic `manifest.json` and one artifact for every successful attempt (mode `valid`), with scope, provenance, and lineage taken from the job and its accepted inputs; reference `JsonSchema.Net` and embed `contracts/analysis/result-manifest/v1/*.schema.json` in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/SocAlytics.Analysts.Manager.Core.csproj`. Tests: `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Work/ResultPublisherTests.cs` (fake platform and storage: happy path, schema-invalid not uploaded and completed with category `analyst-error`, renewal, refusal stops the attempt, a completion answered with `outcome: "failed"` ends the attempt) and validation of the contract examples in the Manager tests (SC-006); extend `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/` with a RustFS container (the image pinned in `RustFsContainerFixture`) and assert that every completed attempt's manifest and artifact are stored under its result location and that no request to the platform carries result bytes (SC-003).

**Checkpoint**: Results are written through grants by the real Manager.

---

## Phase 4: User Story 2 - Accept a result only after validating its manifest (Priority: P2)

**Goal**: Completion accepts a result only when its stored manifest passes every check; failures become failed attempts with named reasons.

**Independent Test**: A valid manifest yields one accepted result; manifests that violate exactly one check yield failed attempts with the matching reason and no accepted result.

- [x] T007 [US2] Manifest verifier (contracts/result-acceptance.md; research.md § R3, § R4; FR-007–FR-011): add `src/platform/SocAlytics.Platform.Application/Analysis/Results/ResultManifestVerifier.cs` performing the eleven ordered checks of contracts/result-acceptance.md (location and declaration, existence, `MaxManifestBytes`, storage-attested checksum equal to the reported and declared digest, supported `contractVersion`, schema through `JsonSchemaContractValidator`, scope and attempt, provenance against the Logical Job and run snapshots, lineage equal to the job's recorded upstream accepted results with capability and schema versions and manifest checksums, artifacts declared and storage-attested, validity window within the segment or match window and `factCount` ≥ 0) and returning either the validated manifest or the reason code of the first failing check, or "storage unavailable". Tests: `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Results/ResultManifestVerifierTests.cs` with RustFS and one test per check where only that check is violated (SC-002), plus a test where storage cannot attest a checksum (fail closed).
- [x] T008 [US2] Validated acceptance in completion (contracts/result-acceptance.md; research.md § R3, § R6; FR-012, FR-013, FR-017; US2 scenarios 1–9; clarification on retries): in `src/platform/SocAlytics.Platform.Application/Analysis/Commands/CompleteAttempt.cs` run `ResultManifestVerifier` for `succeeded` completions after the existing checks and before the unit of work; inside the unit of work re-check the current attempt, then either insert the accepted result plus its `analysis_accepted_result_manifests` record, or record the completion outcome `failed` with the reason, apply the existing failed-attempt transition, and write `analysis.result-rejected` through `AnalysisAuditEvents`; answer `503 object-storage-unavailable` without state change when storage is unavailable; return `outcome: "failed"` with `reason` in the completion response as contracts/result-acceptance.md defines. Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Support/ResultObjectsHelper.cs` (declares and stores a valid manifest and artifact for an attempt) and use it in the existing Analysis tests that complete attempts successfully (`src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/CompletionTests.cs` and the other tests in that folder that accept results), so they keep passing. Tests: `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Results/ValidatedAcceptanceTests.cs` for US2 scenarios 1–9, the retry under the attempt budget, ten concurrent identical completions (SC-005), and the rejection audit event without secrets (SC-007); extend `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/` to assert accepted results with manifest records for both nodes and the match-level lineage naming the segment-level results (SC-001).

**Checkpoint**: Only validated manifests become accepted results.

---

## Phase 5: User Story 3 - The simulated Analyst produces real result manifests (Priority: P3)

**Goal**: Every rejection is reachable with the real Manager; contract violations stop at the Manager.

**Independent Test**: Configure each invalid mode and observe the platform's failure reason and retry, or, for `contract-invalid`, the Manager's failed completion with nothing uploaded.

- [X] T009 [US3] Invalid simulated modes and Manager handling (data-model.md § Simulated Analyst result modes; research.md § R7; FR-014, FR-015, FR-016; US3 scenarios 1–4; clarification on the Manager check): add the per-capability modes `scope-mismatch`, `provenance-mismatch`, `lineage-mismatch`, `artifact-missing`, `validity-invalid`, and `contract-invalid` to the simulated Analyst in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Runtime/Simulated/` (Development and Test only). Tests: simulated-mode tests in `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Runtime/Simulated/`; extend `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/` with `lineage-mismatch` for the match capability (failed attempt with `result-lineage-mismatch`, a new attempt, and a failed job once the budget is spent) and `contract-invalid` (nothing stored under the result location, failed completion with category `analyst-error`) (FR-016).

**Checkpoint**: All user stories work independently.

---

## Phase 6: Polish & Cross-Cutting Concerns

- [ ] T010 Documentation (plan.md § Architecture Updates; Constitution Principle I): make the narratives listed in plan.md § Architecture Updates and every status statement this feature affects true for the merged state, re-reading `docs/architecture/` at implementation time; the listed rewrites are examples, not the whole job. In particular, turn the "decided, not yet implemented" wording about result write grants, the result-manifest contract, and validation in `docs/architecture/analysts-models-and-hardware.md` and `docs/architecture/analyst-runtime-and-recovery.md` into present tense, and update `docs/architecture/contracts-and-compatibility.md` § Scope And Dependencies, `docs/architecture/platform-implementation.md` (§ Analyst Technology, § Acceptance Evidence, § Architecture Reassessment), and `docs/architecture/match-data-pipeline.md` (validation implemented, indexing future). Update `src/platform/README.md`, `src/analysts/manager/README.md`, and the "Current State" in `AGENTS.md`.
- [ ] T011 Run the validation in [quickstart.md](quickstart.md): restore, build, and test both solutions as documented, and `node .github/scripts/check-markdown.mjs`; fix what fails.

---

## Dependencies & Execution Order

- **Setup (Phase 1)**: T001 first; the platform and the Manager embed its schemas.
- **Foundational (Phase 2)**: after Setup; T002, T003, and T004 in parallel. Blocks all stories.
- **US1 (Phase 3)**: T005, then T006 (the Manager needs the grants operation). **US2 (Phase 4)**: after US1, because enforcing validation requires the Manager to store manifests (T006) so the end-to-end test keeps passing; T007, then T008. **US3 (Phase 5)**: after US2.
- **Polish (Phase 6)**: after all stories.

### Parallel opportunities

- Phase 2: T002 (`IObjectStorage.cs`, `S3ObjectStorage.cs`, the test double in `Recordings/CompletionVerificationTests.cs`, `Analysis/Results/SingleObjectStorageConformanceTests.cs`), T003 (the migration, `IResultDeclarationStore.cs`, `IAcceptedResultManifestStore.cs`, `Persistence/Analysis/*Store.cs`, `InfrastructureServiceCollectionExtensions.cs`, `PersistedTableClassifications.cs`, `Analysis/Results/ResultPersistenceTests.cs`), and T004 (`AnalysisResultOptions.cs`, `ApplicationServiceCollectionExtensions.cs`, `AppHost/Program.cs`, the shared test settings, `Analysis/Results/AnalysisResultOptionsTests.cs`) change disjoint files.
- No story phase has tasks with disjoint footprints: each builds on the previous task's code.

## Implementation Strategy

1. Contracts and foundations, then User Story 1 as the MVP (results stored through grants by the real Manager).
2. Add User Story 2 (validated acceptance), then 3 (invalid modes).
3. Documentation and full validation.
