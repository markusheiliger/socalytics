---

description: "Task list for Capability Fact Payload Schemas"
---

# Tasks: Capability Fact Payload Schemas

**Input**: Design documents from `/specs/20261023-010826-fact-payload-schemas/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md),
[contracts/payload-schema-subset.md](contracts/payload-schema-subset.md), [quickstart.md](quickstart.md)

**Tests**: Included. The spec requires contract, integration, SDK, and Docker-backed end-to-end evidence (FR-012,
FR-015, FR-017, SC-001 to SC-007).

**Organization**: Tasks are grouped by user story. Consecutive `[P]` tasks under one heading change disjoint files.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; they change
  disjoint files and do not depend on each other
- **[Story]**: User story of the task (US1 to US4)

## Phase 1: Setup (Manifest Contract Release)

**Purpose**: Release manifest contract `1.2.0` with the closed `payloadSchema` definition.

- [x] T001 Update `contracts/analysis/analyst-manifest/v1/analyst-manifest.schema.json` to `1.2.0`: add `1.2.0` to the
  `manifestVersion` enum, set `x-socalytics-version` to `1.2.0`, add optional `output.factPayloadSchemas` (object, at
  most 64 properties, property names matching the fact kind pattern of the fact-artifact contract, values
  `#/$defs/payloadSchema`), and add `$defs/payloadSchema` with the closed keyword set of
  `contracts/payload-schema-subset.md` (`additionalProperties: false`, `$ref` restricted to `#/$defs/<name>`, no
  `pattern`, `format`, `$id`, `$schema`, or combinators); a manifest with `factPayloadSchemas` requires
  `manifestVersion` `1.2.0`
- [x] T002 Create the released baseline `contracts/analysis/analyst-manifest/v1/releases/1.2.0.schema.json` from the
  schema of T001, leaving `releases/1.0.0.schema.json` and `releases/1.1.0.schema.json` byte-identical

## Phase 2: Foundational (Golden Examples, Domain Limits, Shared Evidence)

**Purpose**: Shared examples and the Domain subset rules that every story uses.

**CRITICAL**: No user story starts before this phase is complete.

### Valid and invalid manifest examples

- [x] T003 [P] Create valid manifest examples `segment-with-payload-schemas.json` and `match-with-payload-schemas.json`
  in `contracts/analysis/analyst-manifest/v1/examples/valid/` (manifest `1.2.0`, one schema per emitted kind, one
  using `$defs`/`$ref`)
- [x] T004 [P] Create invalid manifest examples in `contracts/analysis/analyst-manifest/v1/examples/invalid/`:
  `payload-schema-unknown-keyword.json`, `payload-schema-external-ref.json`, `payload-schema-cyclic-ref.json`,
  `payload-schema-too-deep.json`, `payload-schema-too-large.json`, `payload-schema-root-not-object.json`,
  `payload-schemas-too-many-kinds.json` (65 kinds), and `payload-schemas-on-old-version.json`

### Expectations, payload cases, and Domain rules

- [x] T005 [P] Register the eight invalid examples of T004 in
  `contracts/analysis/analyst-manifest/v1/examples/invalid/expectations.json`: the first error pointer only for the
  schema-expressible refusals (unknown keyword, external `$ref`, schemas on an old manifest version) and a named Domain
  refusal code for the code-checked ones (cyclic `$ref`, depth, size, root type, kind count)
- [x] T006 [P] Create `contracts/analysis/analyst-manifest/v1/examples/fact-payloads.json` with cases
  `{ manifest, kind, payload, valid, at }` over the valid examples of T003, covering a missing required property, a
  wrong type, a forbidden extra property, an enum, a numeric bound, an array bound, and a `$ref` case
- [x] T007 [P] Add the Domain rules in `src/platform/SocAlytics.Platform.Domain/Registry/FactPayloadSchemaLimits.cs`:
  at most 64 kinds, 16 KiB per schema, depth 8, 256 schema nodes, root `type` `object`, the closed keyword set, `$ref`
  of the form `#/$defs/<name>` that resolves and is acyclic, and the canonical-JSON `sha-256:<hex>` digest of the
  schema map; add `FactPayloadSchemas` (`IReadOnlyDictionary<string, JsonElement>`, empty by default) to
  `CapabilityDeclaration` in `src/platform/SocAlytics.Platform.Domain/Registry/`

### Contract evidence

- [x] T008 Extend `src/platform/Tests/SocAlytics.Platform.Contracts.Tests/AnalystManifestContractTests.cs` and
  `AnalysisExampleTests.cs` so that the `1.2.0` baseline is released, `1.0.0` and `1.1.0` baselines are
  byte-identical, every earlier valid manifest stays valid, and the new valid and invalid examples give their expected
  verdicts and error pointers for the schema-expressible refusals, and each code-checked invalid example is run through
  `FactPayloadSchemaLimits` (T007) and gives its named Domain refusal code (FR-001, FR-002, FR-012, SC-003)
- [x] T009 Add `src/platform/Tests/SocAlytics.Platform.Contracts.Tests/FactPayloadCaseTests.cs` that validates every
  case of `examples/fact-payloads.json` with the platform schema engine (formats not asserted) and the Domain subset
  checks of T007, and runs `FactPayloadSchemaLimits` on each code-checked invalid example of T004 to assert its
  named refusal (FR-015, SC-004)
- [x] T010 Bring the `analyst-manifest` contract into the SDK contract copy (FR-013): the copy and its drift check
  currently cover only the contracts listed in `ANALYSIS_CONTRACTS` of `src/analysts/sdk/scripts/sync_contracts.py`,
  which does not list `analyst-manifest`, so a bare refresh changes nothing (attempts 1 and 2 of this task made no
  change for that reason). Add `"analyst-manifest"` to `ANALYSIS_CONTRACTS`, extend
  `test_copy_includes_every_implemented_contract` in `src/analysts/sdk/tests/test_contract_copy.py` to expect
  `analysis/analyst-manifest/v1/releases/1.2.0.schema.json` in the copy, refresh the copy under
  `src/analysts/sdk/src/socalytics_analyst/contracts/` with `uv run --no-sync python scripts/sync_contracts.py`
  (it now adds `analysis/analyst-manifest/` with the current schema and the `1.0.0`, `1.1.0`, and `1.2.0` baselines),
  and confirm `--check` and `uv run --no-sync pytest tests/test_contract_copy.py` pass; `schemas.py` and the typed
  models stay with T025

**Checkpoint**: Contract `1.2.0`, examples, and Domain rules exist; stories can start.

## Phase 3: User Story 2 - Declare, Import, and Pin Payload Schemas (Priority: P1)

**Goal**: The Registry imports and refuses declarations, and each run's node snapshot pins the schemas.

**Independent Test**: Register an image with schemas and inspect the declaration; register one with an invalid schema
and see the refusal; create a run, register a newer image with a changed schema, and confirm the run keeps its schemas.

### Implementation for User Story 2

- [x] T011 [US2] Import payload schemas in
  `src/platform/SocAlytics.Platform.Infrastructure/Registry/AnalystManifestValidator.cs`: read
  `output.factPayloadSchemas` into `CapabilityDeclaration.FactPayloadSchemas`, refuse invalid, unsupported, external,
  cyclic, oversized, or too deep schemas, root types other than `object`, and schemas on a manifest older than `1.2.0`
  with a named reason and no cached declaration, using the Domain rules of T007; the cached manifest keeps the schemas
  unchanged (FR-002, FR-003)
- [x] T012 [US2] Carry the pinned map and digest through the resolved definitions: add them to the resolved node in
  `src/platform/SocAlytics.Platform.Application/Registry/ResolvedWorkflowDefinition.cs` and fill them in
  `src/platform/SocAlytics.Platform.Infrastructure/Registry/RegistryWorkflowDefinitionResolver.cs` and
  `ConfiguredWorkflowDefinitionResolver.cs` from the image digest's declaration (FR-004)
- [x] T013 [US2] Write `factPayloadSchemas` and `factPayloadSchemasDigest` into the node snapshot in
  `src/platform/SocAlytics.Platform.Application/Analysis/WorkflowSnapshotMapper.cs`, omitting both keys when the
  declaration has no schemas so existing snapshots and run digests stay unchanged (FR-004, FR-007)

### Tests for User Story 2

- [x] T014 [P] [US2] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/FactPayloadImportTests.cs`:
  import shows the schemas unchanged in the registered image's declaration, each refusal of FR-002 (including more than 64
  kinds) returns its named reason and caches nothing, and a manifest without schemas imports as before
- [x] T015 [P] [US2] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/FactPayloadPinningTests.cs`:
  a run's node snapshot carries the schemas and digest, a later registration with a changed schema does not change an
  existing run, a declaration without schemas leaves the snapshot and run digest identical to before

**Checkpoint**: US2 is independently testable.

## Phase 4: User Story 1 - Acceptance Rejects Nonconforming Payloads (Priority: P1)

**Goal**: Result acceptance validates every payload against the pinned schema and leaves nothing visible on failure.

**Independent Test**: Complete one conforming and one single-violation attempt against a schema-declaring capability;
the first is accepted and readable, the second ends failed with the named reason and exposes no fact.

### Implementation for User Story 1

- [x] T016 [P] [US1] Create the port `src/platform/SocAlytics.Platform.Application/Abstractions/IFactPayloadValidator.cs`
  (compile a node's pinned schemas once per verification, validate each payload, return the first violation with kind,
  fact index, JSON pointer, and the count of violations, and report the time bound)
- [x] T017 [P] [US1] Add `PayloadValidation:MaxDuration` (development default 5 seconds, valid range 1 to 60 seconds)
  to `src/platform/SocAlytics.Platform.Application/Analysis/Results/AnalysisResultOptions.cs` with validation like the
  existing `Analysis:Results` options
- [x] T018 [US1] Implement `src/platform/SocAlytics.Platform.Infrastructure/Registry/FactPayloadValidator.cs` with
  `JsonSchema.Net` (JSON Schema 2020-12, no format assertions, instance depth 32, bounded by `MaxDuration`, validating only
  payloads already inside the existing per-result facts size limit and retaining no copies, payloads never modified) and register it in the Infrastructure composition method (FR-010, FR-011)
- [x] T019 [US1] Add the payload step to
  `src/platform/SocAlytics.Platform.Application/Analysis/Results/ResultManifestVerifier.cs` after the existing
  envelope, size, count, and window checks: reasons `result-payload-invalid`, `result-payload-kind-undeclared`, and
  `result-payload-validation-bound` in `ResultRejectionReasons` (the last also covers a payload instance deeper than 32), the bounded locator
  `[kind=..;fact=..;count=..;at=..]` of at most 256 characters without payload values, a fact of an undeclared kind
  failing whenever the snapshot has any schema, no validation when the snapshot has none, and reason tags for metrics
  and audit records (FR-005 to FR-011, FR-018)
- [x] T020 [US1] Add a test to
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/CompletionTests.cs` that a failed completion with
  a 256-character bounded reason is stored and read back unchanged through the completion outcome and its
  `CompletionOutcomeDto` response. The storage columns `analysis_completion_outcomes.reason` (migration 0018) and
  `analysis_execution_attempts.close_reason` (migration 0012) are unbounded `text` and the `attempt-completion`
  contract carries no reason, so no migration or contract change is expected (research.md R4); if the test shows a
  limit that blocks it, widen only that with a forward-only migration `NNNN_analysis_<description>.sql` under
  `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Migrations/` (next free number). This task does not
  edit `research.md`

### Tests for User Story 1

- [x] T021 [P] [US1] Add
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Results/FactPayloadValidatorTests.cs`: every case
  of `examples/fact-payloads.json` gives its verdict and pointer through the Infrastructure validator (FR-015), the
  time bound is reached by a pathological payload without a hang, the memory bound holds (instance depth 32, no payload
  beyond the per-result facts size limit validated), and payloads are returned unmodified
- [x] T022 [P] [US1] Add
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Results/FactPayloadAcceptanceTests.cs`:
  conforming results are accepted and read back identical, one bad payload among many fails with
  `result-payload-invalid` and leaves no accepted result, indexed fact, or visible trace, an undeclared kind fails with
  `result-payload-kind-undeclared`, a size-limit violation keeps its existing reason, a fenced, cancelled, or
  superseded attempt keeps its existing outcome, a capability without schemas and a run without snapshot schemas
  behave as before, and retry rules apply (SC-001, SC-002, SC-003)
- [x] T023 [P] [US1] Add
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Results/FactPayloadTelemetryTests.cs`: metrics and
  audit records carry the payload reason codes and never a payload value (FR-018)
- [x] T024 [P] [US1] Add
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Results/FactPayloadPerformanceTests.cs`: a
  maximum-fact-count result adds at most 10% to acceptance time compared with the same result without schemas (SC-006), measured as the median of repeated runs after a warm-up with a
  generous documented tolerance for CI; reaching the FR-011 bound with its named reason
  (`result-payload-validation-bound`) is the accepted alternative outcome

**Checkpoint**: US1 is independently testable with US2.

## Phase 5: User Story 3 - SDK Validates Facts Before Writing (Priority: P2)

**Goal**: The SDK automatically validates facts against the Analyst's own manifest and always fails on violations.

**Independent Test**: In an SDK unit test, write conforming facts successfully and attempt a nonconforming one; the
named error is raised and no result is produced.

### Implementation for User Story 3

- [x] T025 [P] [US3] Add manifest `1.2.0` and `output.factPayloadSchemas` support to
  `src/analysts/sdk/src/socalytics_analyst/models/manifest.py` (closed subset model, limits and `$ref` checks matching
  `FactPayloadSchemaLimits`, giving the same named refusal code for each code-checked refusal)
- [x] T026 [P] [US3] Add the error categories `fact-payload-invalid` and `fact-kind-undeclared` (exit code 1; messages
  name the kind and fact position, never payload values) to `src/analysts/sdk/src/socalytics_analyst/errors.py`
- [x] T027 [US3] Load the Analyst's own manifest from `SOCALYTICS_ANALYST_MANIFEST` (default
  `/opt/socalytics/analyst-manifest.json`; no file means no schemas, an unreadable or invalid file is an input
  error) in `src/analysts/sdk/src/socalytics_analyst/context.py` and `entrypoint.py`, and pass it to the writer
- [x] T028 [US3] Validate in `ResultWriter.add_fact` in `src/analysts/sdk/src/socalytics_analyst/result.py` with
  `jsonschema` Draft 2020-12 without a format checker before staging, raising `ResultError` with no warning mode,
  failing a kind without a declared schema when any schema exists, and accepting an explicit manifest object for unit
  tests (FR-014)

### Tests for User Story 3

- [x] T029 [P] [US3] Extend `src/analysts/sdk/tests/test_result_writer.py` and `test_models_manifest.py`: conforming
  facts are written, a nonconforming fact raises `fact-payload-invalid` and writes no result, an undeclared kind
  raises `fact-kind-undeclared`, a manifest without schemas behaves as before, and the manifest location and its
  error handling work (SC-005)
- [x] T030 [P] [US3] Add `src/analysts/sdk/tests/test_payload_cases.py` and extend `test_contract_examples.py` so the
  SDK gives the platform's verdicts on every shared manifest example (running each code-checked invalid manifest example through the SDK limit checks and asserting the same named refusal code as `expectations.json`) and every case of `examples/fact-payloads.json`
  (FR-012, FR-015, SC-004)

**Checkpoint**: US3 is independently testable.

## Phase 6: User Story 4 - Reference Media Probe Proves the Path End to End (Priority: P2)

**Goal**: A payload-validated `reference-media-probe` 1.1.0 next to the unchanged 1.0.0, with Docker-backed evidence.

**Independent Test**: Run the Docker-backed reference Analyst tests: facts of 1.1.0 read back equal to the written
payloads; the stricter-schema variant ends failed with `result-payload-invalid` and exposes no fact; 1.0.0 passes
unchanged.

### Implementation for User Story 4

- [x] T031 [P] [US4] Create
  `src/analysts/capabilities/reference-media-probe/analyst-manifest.payload-validated.json` (capability version
  `1.1.0`, manifest `1.2.0`, the `frame-second-probe` schema reduced to the subset from
  `schemas/frame-second-probe.payload.schema.json`), leaving `analyst-manifest.json` untouched
- [x] T032 [P] [US4] Create `src/analysts/capabilities/reference-media-probe/Dockerfile.payload-validated` and
  `Dockerfile.payload-validated.dockerignore` (same code and pins as `Dockerfile`, carrying the new manifest at
  `/opt/socalytics/analyst-manifest.json` and its own manifest label), leaving `Dockerfile` untouched
- [ ] T033 [US4] Add `src/analysts/capabilities/reference-media-probe/tests/test_payload_validated_manifest.py`:
  the 1.1.0 manifest validates against manifest `1.2.0`, its schema equals the schema file, produced facts conform and
  stay deterministic, the Dockerfile variant is consistent with the manifest, and the 1.0.0 files are unchanged
  (FR-016)
- [ ] T034 [US4] Build the second image in
  `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/Support/ReferenceAnalystImage.cs` from `Dockerfile.payload-validated`
  and extend `ReferenceAnalystImageTests.cs` with its image evidence
- [ ] T035 [US4] Add the accepted path to
  `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/ReferenceAnalystEndToEndTests.cs`: register 1.1.0 next to
  1.0.0, run over the synthetic recording, and assert facts read back identical to the written payloads while the
  1.0.0 tests stay unchanged (FR-017)
- [ ] T036 [US4] Add the rejected path in `ReferenceAnalystEndToEndTests.cs` using a derived image from
  `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/Support/TestAnalystImage.cs` whose registered manifest declares a stricter schema (for example `decodedFrames` maximum 0)
  while the in-container manifest declares none: the attempt ends failed with `result-payload-invalid` and the run
  exposes no fact

**Checkpoint**: All user stories are independently functional.

## Phase 7: Polish and Cross-Cutting Concerns

- [ ] T037 Documentation: make `docs/architecture/` describe the implemented state, re-reading it at implementation
  time: apply every rewrite listed in the plan's Architecture Updates to
  `docs/architecture/analysts-models-and-hardware.md` (result validation, fact indexing, the "decided and not
  implemented yet" paragraph in present tense naming manifest `1.2.0` and the reasons),
  `docs/architecture/analyst-capability-catalog.md` (declaration includes payload schemas), and
  `docs/architecture/contracts-and-compatibility.md` (manifest `1.2.0` release); correct any other planned,
  future-tense, or unimplemented wording this feature falsifies and keep the remaining gaps explicit (no catalog
  capability, production limit values, no payload interpretation or quality metrics)
- [ ] T038 [P] Update the status statements in `AGENTS.md` ("capability-specific payload validation deferred" becomes
  payload schema validation as development evidence), `README.md`, `contracts/README.md` (manifest `1.2.0`, shared
  payload examples), `src/platform/README.md`, `src/analysts/sdk/README.md`, and
  `src/analysts/capabilities/reference-media-probe/README.md` (the 1.1.0 variant and its build command)
- [ ] T039 Confirm the OpenAPI snapshot is unchanged with
  `dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests` (no endpoint change;
  never refresh it)
- [ ] T040 Run the validation of [quickstart.md](quickstart.md) (contract, integration, SDK, reference capability, and
  Docker-backed end-to-end commands) and `node .github/scripts/check-markdown.mjs`; fix findings

## Dependencies and Execution Order

- Phase 1 precedes Phase 2; Phase 2 blocks all stories.
- US2 (Phase 3) provides the pinned snapshot that US1 (Phase 4) validates against, so US1 follows T013.
- US3 needs only Phase 2 and may run beside US1 and US2; US4 needs US1, US2, and US3.
- Phase 7 follows all stories; T037 re-reads the final implementation.

## Parallel Execution Examples

- Phase 2: T003 and T004 together, then T005 to T007 together (T005 and T006 need the examples), then T008 to T010
  in order.
- US1: T016 and T017, then T018 and T019; tests T021 to T024 together.
- US3: T025 and T026 together; tests T029 and T030 together.
- US4: T031 and T032 together, then T033.

## Implementation Strategy

1. MVP: Phases 1 and 2, US2, and US1 (declare, import, pin, and enforce at acceptance), validated by the contract and
   integration tests.
2. Add US3 (SDK validation), then US4 (Docker-backed evidence).
3. Finish with documentation and the quickstart validation.
