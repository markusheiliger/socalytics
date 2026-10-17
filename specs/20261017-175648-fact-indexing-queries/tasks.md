---

description: "Task list for Accepted Fact Indexing and Capability-Scoped Fact Queries"
---

# Tasks: Accepted Fact Indexing and Capability-Scoped Fact Queries

**Input**: Design documents from `specs/20261017-175648-fact-indexing-queries/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: Required by FR-024 and the success criteria; each task adds or updates the tests that prove it.

**Organization**: Tasks are grouped by user story so each story can be implemented and tested on its own.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; their file footprints are disjoint.
- **[Story]**: The user story the task belongs to (US1–US4).
- Every task names its files; paths are relative to the repository root.

## Path Conventions

- Platform: `src/platform/SocAlytics.Platform.{Domain,Application,Infrastructure,Api,AppHost}/`, tests under `src/platform/Tests/`.
- Analyst Manager: `src/analysts/manager/`, tests under `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/`.
- Contracts: `contracts/analysis/`.

## Phase 1: Setup (Contract)

**Purpose**: The fact-artifact contract every later task validates against.

- [ ] T001 Fact-artifact contract (contracts/fact-artifact.md; research.md § R1, § R2; FR-001, FR-002): add `contracts/analysis/fact-artifact/v1/fact-artifact.schema.json` (JSON Schema 2020-12, `$id` `https://socalytics.invalid/contracts/analysis/fact-artifact/v1/fact-artifact.schema.json`, `x-socalytics-version` `1.0.0`, `additionalProperties: false` on the envelope and on each fact, top-level required `contractVersion`, `capability` (common `capabilityRef`), `schemaVersion` (common `semanticVersion`), `outputType` (`foundational-facts` | `soccer-concepts`), `facts`; each fact requires `kind` (common `key`), `startMs` ("integer ≥ 0"), `endMs` ("integer ≥ 1"), `confidence` ("number 0..1"), and `payload` (open object)), its immutable copy `releases/1.0.0.schema.json`, the valid and invalid examples listed in contracts/fact-artifact.md with `examples/invalid/expectations.json`, and the index row in `contracts/README.md` (owner: Scheduler and Job Registry for the envelope; Analyst capability owners for payload semantics); add `FactArtifactV1` to `src/platform/SocAlytics.Platform.Application/Abstractions/ContractSchemaIds.cs` and `"fact-artifact"` to `SchemaDirectories` in `src/platform/Tests/SocAlytics.Platform.Contracts.Tests/AnalysisExampleTests.cs`; `dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests` passes.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Storage, configuration, and the range value object that indexing and reads share.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

### Storage, configuration, and ranges

- [ ] T002 [P] Indexed fact storage (data-model.md § Indexed Fact; research.md § R5; FR-006, FR-008, FR-009): add the forward-only migration `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Migrations/NNNN_analysis_indexed_facts.sql` (NNNN = next free number on an up-to-date default branch) creating `socalytics.analysis_indexed_facts` with the columns of data-model.md, primary key `(accepted_result_id, ordinal)`, FK `accepted_result_id` → `socalytics.analysis_accepted_results (id)`, checks "`start_ms >= 0 AND end_ms > start_ms`", "`confidence` between 0 and 1", "`jsonb_typeof(payload) = 'object'`", `output_type IN ('foundational-facts', 'soccer-concepts')`, the read index `(run_id, node_id, start_ms, end_ms, accepted_result_id, ordinal)`, the `socalytics.reject_immutable_change` trigger on update/delete, and `REVOKE UPDATE, DELETE … FROM socalytics_app`; add `IndexedFactRecord` and `IIndexedFactStore.InsertAsync(IReadOnlyList<IndexedFactRecord>)` in `src/platform/SocAlytics.Platform.Application/Analysis/Stores/IIndexedFactStore.cs`, the Dapper implementation `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Analysis/IndexedFactStore.cs` (batched insert inside the current unit of work), its registration in `src/platform/SocAlytics.Platform.Infrastructure/InfrastructureServiceCollectionExtensions.cs`, the table's entry in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Structure/PersistedTableClassifications.cs` (immutable, no `version`), and `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Facts/IndexedFactStoreTests.cs` proving insert, the checks, immutability (update and delete rejected), and the duplicate-key guard.
- [ ] T003 [P] Fact limits and read options (data-model.md § Options; clarification on limits; FR-004, FR-011): extend `src/platform/SocAlytics.Platform.Application/Analysis/Results/AnalysisResultOptions.cs` and its validator with `MaxFactArtifactBytes` ("1 KiB .. `MaxObjectBytes`"), `MaxFactPayloadBytes` ("256 B .. 1 MiB"), and `MaxFactsPerResult` ("1 .. 1,000,000"); add `src/platform/SocAlytics.Platform.Application/Analysis/Facts/FactReadOptions.cs` (section `Analysis:Facts`: `DefaultPageSize` "1 .. `MaxPageSize`", `MaxPageSize` "1 .. 1,000") with a validator and its validated binding in `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs`; set the development values (16 MiB, 16 KiB, 50,000, 100, 500) in `src/platform/SocAlytics.Platform.AppHost/Program.cs` and in every test settings source that already sets `Analysis:Reads` (`src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/Support/TestIdentityAccessSettings.cs`, `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Recordings/RecordingOptionsValidationTests.cs`, `src/platform/Tests/SocAlytics.Platform.Host.Tests/MinimumWebVersionTests.cs`, `src/platform/Tests/SocAlytics.Platform.Architecture.Tests/EndpointSecurityTests.cs`); extend `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Results/AnalysisResultOptionsTests.cs` and add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Facts/FactReadOptionsTests.cs` for the bounds.
- [ ] T004 [P] Half-open fact range (data-model.md § Fact Artifact; research.md § R8; FR-004, FR-013): add `src/platform/SocAlytics.Platform.Domain/Analysis/FactRange.cs` (non-negative start, end after start, `IsWithin(window)` for containment in a half-open window, `Overlaps(from?, to?)` for open or closed half-open filters) and `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Domain/FactRangeTests.cs` covering boundaries (a fact ending exactly at the window end is inside; one starting at the window end is outside; overlap at shared boundaries is false).

**Checkpoint**: Storage, options, and ranges exist; user stories can begin.

---

## Phase 3: User Story 1 - Accepted results become queryable facts (Priority: P1) 🎯 MVP

**Goal**: Fact artifacts are validated before acceptance and their facts are stored atomically and idempotently with the accepted result.

**Independent Test**: Complete attempts with a valid fact artifact, a malformed one, a fact outside the window, and a wrong fact count; only the valid one is accepted and indexed exactly once.

- [ ] T005 [US1] Fact validation in the verifier (data-model.md § Validated Result Manifest, verifier check 12; research.md § R3, § R4; FR-002–FR-005; US1 scenarios 2–5): in `src/platform/SocAlytics.Platform.Application/Analysis/Results/ResultManifestVerifier.cs` add the reasons `result-facts-invalid`, `result-facts-out-of-window`, `result-facts-count-mismatch`, and `result-facts-too-large` to `ResultRejectionReasons`, add the twelfth ordered check exactly as listed in data-model.md (artifacts with media type `application/vnd.socalytics.facts+json`; size limit; read through `IObjectStorage.ReadObjectAsync` with storage unavailability returning `StorageUnavailable`; SHA-256 of the read bytes equals the manifest digest, else `result-artifact-invalid`; validation against `ContractSchemaIds.FactArtifactV1`; header equal to the manifest and the node snapshot; payload and count limits; `FactRange` inside the validity window; total equal to `factCount`, including `factCount > 0` without a fact artifact), and return the parsed facts in order (artifact order in the manifest, then fact order) as `Facts` on `ValidatedResultManifest`; extend `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Results/ResultManifestVerifierTests.cs` with one case per reason and boundary, a result without facts, several fact artifacts, and opaque artifacts with other media types.
- [ ] T006 [US1] Atomic, idempotent indexing at acceptance (research.md § R3; data-model.md § Indexed Fact; FR-006–FR-010; US1 scenarios 1, 5, 6; SC-001, SC-002): in `src/platform/SocAlytics.Platform.Application/Analysis/Commands/CompleteAttempt.cs` insert the verified facts through `IIndexedFactStore` (ordinal, run, node, team, match, capability id and version, schema version, output type) in the same unit of work and after `IAcceptedResultStore.InsertAsync` and `IAcceptedResultManifestStore.InsertAsync`; replays return the stored outcome without inserting; add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Results/FactIndexingTests.cs` proving exactly-once storage with the result's identity, no rows for every rejection reason, no duplicates on replay with the same retry key, no rows when the unit of work fails after verification, and acceptance without facts; update the existing fixtures that declare facts in other formats (`src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Support/ResultObjectsHelper.cs`, `CompletionTests.cs`, and `Results/ValidatedAcceptanceTests.cs` in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/`) to write contract-valid fact artifacts or `factCount: 0`.

**Checkpoint**: Accepted results are fully indexed; rejected ones leave nothing.

---

## Phase 4: User Story 2 - Members read a run's facts by capability (Priority: P1)

**Goal**: Authorized members page through a run's facts of one capability with coverage and provenance.

**Independent Test**: A Coach of the team reads one capability's facts across several pages in time order with full coverage; a member without a role on the team gets `404`.

### Read model

- [ ] T007 [P] [US2] Member coverage windows (research.md § R9; data-model.md § Coverage Window; FR-015, FR-016): add `src/platform/SocAlytics.Platform.Application/Analysis/Facts/FactCoverageBuilder.cs` mapping a node's logical jobs to ordered half-open windows (segment job: "`[(n-1)·D, n·D)`" from the run's pinned segmentation policy; match job and nodes not required by the run: "`[0, end of the finalized recording set's mapped match-time coverage)`"; states `accepted`, `failed`, `blocked`, `skipped`, `missing` as in data-model.md) and filtering to a requested range, with `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Facts/FactCoverageBuilderTests.cs` covering every state, ordering, range filtering, and the absence of segment, recording, and storage identifiers.
- [ ] T008 [P] [US2] Fact and stream queries (research.md § R6, § R7, § R10; data-model.md § Fact Page, § Fact Stream; FR-011–FR-014, FR-017, FR-019): add `src/platform/SocAlytics.Platform.Application/Analysis/Facts/IFactQueries.cs` and its Dapper implementation `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Analysis/FactQueries.cs` (node resolution by capability id, version, schema version, and optional node key from the run's node snapshots; keyset page ordered by `start_ms, end_ms, accepted_result_id, ordinal` with overlap filter "`start_ms < toMs AND end_ms > fromMs`"; per-node fact counts; provenance from the run and node snapshot; the raw coverage inputs as its own records: the node's jobs with state, segment number, and accepted-result presence, the run's segmentation policy duration, and the recording set's mapped coverage end, which T009 maps to `FactCoverageBuilder` inputs; a version basis for the weak ETag), registered in `src/platform/SocAlytics.Platform.Infrastructure/InfrastructureServiceCollectionExtensions.cs`, with `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Facts/FactQueriesTests.cs` covering ordering, keyset paging across ties, range overlap, node resolution and ambiguity, and run isolation.

### Read operation

- [ ] T009 [US2] Facts read operation (contracts/member-fact-api.md § `listAnalysisRunFacts`; research.md § R7, § R11; FR-011–FR-017, FR-019–FR-022; US2 scenarios 1–8; SC-003–SC-005): add `src/platform/SocAlytics.Platform.Application/Analysis/Facts/ListRunFacts.cs` (authorization through `IAccessAuthorizer.AuthorizeTeamResourceAsync(new("analysis-run", runId), TeamPermission.Read)` with `404` on denial; validation of required parameters, page size, range, and continuation token bound to the selection hash; problems `analysis-capability-not-in-run` and `analysis-capability-ambiguous`; maps the `IFactQueries` coverage rows to `FactCoverageBuilder` inputs) registered in `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs`; add `src/platform/SocAlytics.Platform.Api/Analysis/AnalysisFactEndpoints.cs` with `MapMemberApi()`, operation `listAnalysisRunFacts` under tag `Analysis facts`, `ProblemResults`, and a weak ETag with `If-None-Match` → `304`, mapped in `src/platform/SocAlytics.Platform.Api/Program.cs`; refresh the committed OpenAPI snapshot `src/platform/SocAlytics.Platform.Api/openapi/v1.json` (`SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1`); add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Facts/FactReadEndpointTests.cs` covering Club Admin, Coach, and Viewer access, `404` for other members, other clubs, and missing runs, `401` without a session, at least three pages without duplicates or gaps, range overlap, coverage for running, failed, and finished runs, accepted coverage with no facts for results accepted without fact artifacts, provenance, run isolation, the two problems, invalid parameters, the `304` path, and that no response contains segment, recording, mapping, policy, or storage identifiers.

**Checkpoint**: Members can read a run's facts per capability.

---

## Phase 5: User Story 3 - Members see which fact streams a run offers (Priority: P2)

**Goal**: A listing of a run's capability nodes with fact counts, provenance, and coverage summary.

**Independent Test**: A Viewer lists a run's fact streams and gets one entry per node whose counts match the facts read.

- [ ] T010 [US3] Fact streams listing (contracts/member-fact-api.md § `listAnalysisRunFactStreams`; data-model.md § Fact Stream; FR-018, FR-020, FR-022; US3 scenarios 1–3): add `src/platform/SocAlytics.Platform.Application/Analysis/Facts/ListRunFactStreams.cs` (same authorization as `ListRunFacts`; one entry per workflow node with `required`, fact count, provenance, and coverage counts from `FactCoverageBuilder`) registered in `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs`; add the operation `listAnalysisRunFactStreams` to `src/platform/SocAlytics.Platform.Api/Analysis/AnalysisFactEndpoints.cs` with a weak ETag; refresh `src/platform/SocAlytics.Platform.Api/openapi/v1.json`; add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Facts/FactStreamEndpointTests.cs` covering entries for required, unrequired, and not-yet-accepted nodes, counts equal to the facts read, and the same authorization outcomes as T009.

**Checkpoint**: Clients can discover and read every fact stream of a run.

---

## Phase 6: User Story 4 - The development workflow produces representative facts (Priority: P3)

**Goal**: The simulated Analyst emits deterministic facts so indexing and reads run end to end.

**Independent Test**: Run the development workflow with the simulated Analyst and read back every accepted fact as a team member.

- [ ] T011 [US4] Simulated Analyst facts (research.md § R12; data-model.md § Simulated Analyst; FR-023; US4 scenarios 1–2): in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Runtime/Simulated/SimulatedResultWriter.cs` write `facts.json` with media type `application/vnd.socalytics.facts+json` following the fact-artifact contract (capability, schema version, output type from the job; three deterministic facts inside the job window, kind `simulated-observation` or `simulated-concept`, confidence 0.9, small payload) and `factCount: 3`, move the consumed coverage of match-level jobs to a supporting artifact `inputs.json` (`application/json`), and add the mode `invalid-facts` (one fact outside the window) to `SimulatedResultModes` in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Runtime/Simulated/SimulatedAnalystOptions.cs`; update `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Runtime/Simulated/SimulatedResultModeTests.cs` and every test that reads the simulated `facts.json` or its consumed coverage (including `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/UpstreamResultReadEndToEndTests.cs`).
- [ ] T012 [US4] End-to-end fact read-back (quickstart.md § 6; FR-024; US4 scenario 3; SC-006): add `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/FactReadEndToEndTests.cs` that completes the development workflow with the simulated Analyst and, signed in as a Coach of the team, lists the run's fact streams and reads every capability's facts page by page, asserting every accepted fact exactly once and accepted coverage for the whole match; add an `invalid-facts` case to `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/InvalidResultEndToEndTests.cs` asserting the attempt fails with `result-facts-out-of-window` and no fact is readable.

**Checkpoint**: All user stories work end to end.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Documentation and full validation.

- [ ] T013 Documentation (plan.md § Architecture Updates; Constitution Principle I): make the narratives listed in plan.md § Architecture Updates and every status statement this feature affects in `docs/architecture/` true for the merged state, re-reading the documents at implementation time (the rewrites listed there are examples, not the whole job): turn the decided-but-not-implemented fact-indexing, member-read, and fact-artifact wording in `docs/architecture/analysts-models-and-hardware.md`, `docs/architecture/match-data-pipeline.md`, and `docs/architecture/contracts-and-compatibility.md` into present tense, remove "fact indexing remains future work" and "(future)", and update `docs/architecture/platform-implementation.md` (Data Organization, Acceptance Evidence, Architecture Reassessment); also update `AGENTS.md` "Current State" (remove "no fact indexing", name the member fact reads) and `README.md` where they describe analysis evidence; run `node .github/scripts/check-markdown.mjs`.
- [ ] T014 Run the validation in [quickstart.md](quickstart.md): restore, build, and test both solutions as documented, the contract and OpenAPI snapshot tests, and `node .github/scripts/check-markdown.mjs`; fix what fails.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: none.
- **Foundational (Phase 2)**: depends on T001 (schema id); blocks all stories.
- **US1 (Phase 3)**: depends on Phase 2.
- **US2 (Phase 4)**: depends on Phase 2; its tests need indexed facts, which they seed through US1's acceptance path or directly through `IIndexedFactStore`.
- **US3 (Phase 5)**: depends on US2 (T007, T008, and the endpoint file of T009).
- **US4 (Phase 6)**: depends on US1 (acceptance) and US2/US3 (reads for T012).
- **Polish (Phase 7)**: depends on all stories.

### Within Each Phase

- T002, T003, T004 run in parallel (disjoint files: persistence and Infrastructure registration; options, Application registration, AppHost, and test settings; Domain range).
- T005 before T006 (indexing consumes the verified facts).
- T007 and T008 run in parallel (Application coverage builder with its own input records; queries with their own row records and Infrastructure registration); T009 needs both and maps the query rows to the builder inputs.
- T011 before T012.

### Parallel Opportunities

- Phase 2: T002, T003, T004.
- Phase 4: T007, T008.

---

## Parallel Example: User Story 2

```text
Task: "T007 Member coverage windows in src/platform/SocAlytics.Platform.Application/Analysis/Facts/FactCoverageBuilder.cs"
Task: "T008 Fact and stream queries in src/platform/SocAlytics.Platform.Infrastructure/Persistence/Analysis/FactQueries.cs"
```

---

## Implementation Strategy

### MVP First (User Story 1)

1. Phase 1 and Phase 2.
2. Phase 3 (US1): accepted results are indexed atomically; validate with the US1 tests.

### Incremental Delivery

1. US1 → indexing.
2. US2 → member facts read (first visible value).
3. US3 → stream discovery for the Web UI review of the next feature.
4. US4 → end-to-end evidence with the simulated Analyst.
5. Polish → documentation and full validation.

---

## Notes

- `[P]` tasks have disjoint footprints, including registrations and settings.
- Never edit an applied migration; renumber `NNNN` before merge if the number was taken.
- Refresh the OpenAPI snapshot only during implementation, never in the verification environment.
