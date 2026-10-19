---

description: "Task list for Python Analyst SDK"
---

# Tasks: Python Analyst SDK

**Input**: Design documents from `specs/20261017-220709-python-analyst-sdk/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: The spec requires automated evidence (FR-022, FR-023), so every story has test tasks. Test tasks follow the
implementation tasks of their story so that each landed task keeps the build and test suites green.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.
Setup creates the SDK project; foundational work adds the contract versions, the verified schema copy, and the typed
models every story uses.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; they change disjoint
  files and do not depend on each other
- **[Story]**: Which user story this task belongs to (US1, US2, US3, US4)
- Every task names its exact file paths; SDK paths are relative to the repository root

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: The independent SDK project the environment recognizes, with a locked build and one passing test

- [x] T001 Create the SDK project at `src/analysts/sdk/` following the environment project convention (specs/archive/20261017-163801-analyst-media-environment/contracts/environment-interface.md; research R2): `src/analysts/sdk/pyproject.toml` (`[project]` name `socalytics-analyst-sdk`, version `1.0.0`, `requires-python = "==3.12.*"`, dependencies `pydantic>=2,<3` and `jsonschema>=4,<5`; `[build-system]` `hatchling`; `[dependency-groups]` `dev = ["pytest", "hatchling"]`; Hatch wheel target `packages = ["src/socalytics_analyst"]` including `src/socalytics_analyst/contracts/**/*.json`; no `[tool.uv.workspace]`), `src/analysts/sdk/src/socalytics_analyst/__init__.py` exposing `__version__ = "1.0.0"`, `src/analysts/sdk/tests/test_package.py` asserting the version, `src/analysts/sdk/.gitignore` (`.venv/`, `dist/`, `.pytest_cache/`), and the committed `src/analysts/sdk/uv.lock` produced by `uv lock` with the newest releases that ship CPython 3.12 manylinux x86_64 wheels; confirm the environment's exact command sequence (quickstart.md §1) passes (FR-001)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Contract versions, the verified schema copy, schema validation, errors, and typed models

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

### Contract schemas

- [x] T002 [P] Release `analyst-job` 1.1.0 in `contracts/analysis/analyst-job/v1/`: in `analyst-job.schema.json` set `x-socalytics-version` to `1.1.0` and add the optional `workflow` object (`additionalProperties: false`, required `id` (common `key`) and `version` (common `semanticVersion`)) and the optional `segmentWindow` object (`additionalProperties: false`, required `segmentId` (common `identifier`), `startMs` (`"type": "integer", "minimum": 0`), and `endMs` (`"type": "integer", "minimum": 1`)), plus an `allOf` rule that `segmentWindow` is not allowed when `executionScope` is `match`; add `releases/1.1.0.schema.json` (keep `releases/1.0.0.schema.json` unchanged), `examples/valid/segment-job-with-window.json`, `examples/valid/match-job-with-workflow.json`, `examples/invalid/segment-window-on-match-job.json`, and `examples/invalid/segment-window-unknown-property.json` with their entries in `examples/invalid/expectations.json` (contracts/analysis-contracts.md; FR-006, FR-023)
- [x] T003 [P] Add the new contract `analyst-attempt` 1.0.0 in `contracts/analysis/analyst-attempt/v1/`: `analyst-attempt.schema.json` with `$id` `https://socalytics.invalid/contracts/analysis/analyst-attempt/v1/analyst-attempt.schema.json`, `x-socalytics-version` `1.0.0`, a closed object with required `contractVersion` (common `contractVersionV1`), `attemptId` (common `identifier`), and `attemptNumber` (common `attemptNumber`); `releases/1.0.0.schema.json`; `examples/valid/attempt.json`; `examples/invalid/missing-attempt-number.json`, `examples/invalid/carries-fencing-token.json`, and `examples/invalid/zero-attempt-number.json` with `examples/invalid/expectations.json` (contracts/analysis-contracts.md; data-model.md "Attempt Context"; FR-008, FR-023)

### Contract index and platform contract tests

- [x] T004 Update `contracts/README.md` (analyst-job version `1.1.0` with its `releases/1.1.0.schema.json` baseline; a new row for `analysis/analyst-attempt/v1/analyst-attempt.schema.json` with owner "Analyst Manager; Analyst SDK as consumer" and the validation commands `dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests`, `dotnet test src/analysts/manager/SocAlytics.Analysts.Manager.slnx`, and the SDK tests) and extend `src/platform/Tests/SocAlytics.Platform.Contracts.Tests/ContractCatalog.cs` and `src/platform/Tests/SocAlytics.Platform.Contracts.Tests/AnalysisExampleTests.cs` so the new valid examples validate, the new invalid examples are rejected with their expected keyword, `1.0.0` job examples stay valid, and analyst-job 1.1.0 is same-major compatible with its `1.0.0` baseline (FR-006, FR-008, FR-023)

### SDK schema copy and validation

- [x] T005 Add `src/analysts/sdk/scripts/sync_contracts.py` (standard library only; copies `contracts/common/v1/common.schema.json` and the schemas and `releases/` copies of `contracts/analysis/{analyst-job,analyst-attempt,result-manifest,upstream-results,fact-artifact}/v1/` into `src/analysts/sdk/src/socalytics_analyst/contracts/` with the same relative layout; `--check` reports differences and exits non-zero), run it to create the copy, and add `src/analysts/sdk/tests/test_contract_copy.py` that fails with the refresh command when a copied file differs from or lacks its canonical file (research R3; FR-003, FR-022)
- [x] T006 Add `src/analysts/sdk/src/socalytics_analyst/errors.py` (`AnalystSdkError` with `category`; subclasses `InputError` (exit 2) and `ResultError` (exit 1); the categories of data-model.md "Error categories" as constants) and `src/analysts/sdk/src/socalytics_analyst/schemas.py` (an offline `referencing` registry over the packaged copy keyed by `$id`, Draft 2020-12 validation with format checking, selection of the schema by document kind and `contractVersion`, refusal of another major or a newer minor than supported with `unsupported-contract-version`, supported versions per research R5, and validation errors that name the schema keyword and path but no document values), with `src/analysts/sdk/tests/test_schemas.py` (FR-002, FR-003, FR-004)

### Typed models

- [x] T007 Add `src/analysts/sdk/src/socalytics_analyst/models/common.py`: a frozen base model with `extra="forbid"`, `populate_by_name=True`, and camelCase aliases; `Annotated` types mirroring the common definitions verbatim (`identifier` "`^[A-Za-z0-9][A-Za-z0-9._\-]*$`, 1–128", `key` "`^[a-z0-9][a-z0-9._\-]*$`, 1–100", `semanticVersion`, `contractVersionV1` "`^1\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$`", `timestamp` with `Z`, `digest` "`^sha-256:[0-9a-f]{64}$`", `safeLabel`, `attemptNumber` "≥ 1", milliseconds "integer ≥ 0", result `objectName` "`^[a-z0-9][a-z0-9._\-]{0,127}$`"), `CapabilityRef`, `Correlation`, `AnalysisSegmentReference`, `AcceptedResultLineage`, and `CoverageEntry` (exactly one of `segmentId` or `match: true`; `state` in `accepted`, `missing`, `failed`, `blocked`, `skipped`) (research R4; FR-002)

### Contract models

- [x] T008 [P] Add `src/analysts/sdk/src/socalytics_analyst/models/job.py`: `LogicalJob` with every analyst-job 1.1.0 property (`tier`, `executionScope`, `segment` exactly for segment jobs, `inputs` entries with `edgeKind` and `status` rules, `pinned` with nullable `modelDigest`, `resources`, `attemptBudget`, `timeouts`, `createdAt`, `correlation`, optional `workflow` and `segmentWindow` with `segmentWindow` only for segment jobs) (data-model.md "Logical Job additions"; FR-002, FR-006)
- [X] T009 [P] Add `src/analysts/sdk/src/socalytics_analyst/models/attempt.py`: `AttemptContext` with `contractVersion`, `attemptId`, and `attemptNumber`, closed (data-model.md "Attempt Context"; FR-002, FR-008)
- [x] T010 [P] Add `src/analysts/sdk/src/socalytics_analyst/models/manifest.py`: `ResultManifest` for result-manifest 1.0.0 and 1.1.0 (scope with exactly one of `segment` or `match`, provenance, attempt, `artifacts` "`maxItems` 256, `sizeBytes` ≥ 1, `mediaType` pattern", `validityWindow`, `factCount` "≥ 0", `upstreamLineage`, `outputType` in `foundational-facts`/`soccer-concepts`, `confidence` "0..1", `evidenceReferences`, optional `inputCoverage` "`maxItems` 64, units `maxItems` 1024") (FR-002, FR-004)
- [x] T011 [P] Add `src/analysts/sdk/src/socalytics_analyst/models/facts.py`: `FactArtifact` (`contractVersion`, `capability`, `schemaVersion`, `outputType`, `facts`) and `Fact` (`kind` (common `key`), `startMs` "integer ≥ 0", `endMs` "integer ≥ 1", `confidence` "0..1", `payload` "JSON object"), both closed, with the media type constant `application/vnd.socalytics.facts+json` (specs/archive/20261017-175648-fact-indexing-queries/contracts/fact-artifact.md; FR-002, FR-011)

### Model exports

- [x] T012 Add `src/analysts/sdk/src/socalytics_analyst/models/upstream.py` (`StagedCoverage` for the staged `coverage.json` with `upstream` (capability, schema version, edge kind, optional 1.1.0 `coverage` mode `complete`/`partial` and `minimumAcceptedFraction` "`0 < f <= 1`") and `coverage` entries) and `src/analysts/sdk/src/socalytics_analyst/models/__init__.py` exporting every model of T007–T012 (FR-002, FR-019)

**Checkpoint**: Contracts, schema copy, validation, and models are ready

---

## Phase 3: User Story 1 - An Analyst author writes an acceptable result without knowing the contract details (Priority: P1) 🎯 MVP

**Goal**: Read and validate the job and attempt context, write facts and supporting artifacts, and finalize an atomic,
acceptable result through the entry point

**Independent Test**: Run a test Analyst through the entry point against prepared mount directories for a segment and
a match job; the result mount holds exactly `facts.json` and `manifest.json` (plus declared artifacts), both valid
against the canonical schemas and equal to the job's values; failures exit 1 or 2 without a manifest

### Implementation for User Story 1

- [x] T013 [US1] Add `src/analysts/sdk/src/socalytics_analyst/context.py`: loading `job.json` and `attempt.json` from a mount root (default `Path("/socalytics")`, overridable only through an explicit parameter; no environment variables), schema validation then model parsing, `input-missing`, `input-invalid`, and `unsupported-contract-version` errors, `job-context-incomplete` when the job lacks `workflow` or a segment job lacks `segmentWindow`, and `AnalystContext` (`job`, `attempt`, `inputs` (empty until US3), `result`, `scratch`) (research R1, R5; FR-003, FR-005)
- [x] T014 [US1] Add `src/analysts/sdk/src/socalytics_analyst/result.py`: `ResultWriter` with `add_fact`, `add_artifact`, `set_validity_window`, `set_confidence`, `add_evidence_reference`, and `finalize` exactly as contracts/sdk-api.md and data-model.md "ResultWriter states" define; scope, attempt, provenance (capability, schema version, Analyst profile, image digest, model digest only when not null, `policies` → `policyVersions`, workflow), and `upstreamLineage` (the job's `inputs[].acceptedResults` in input-snapshot order) derived from the context and not overridable; segment window default and narrowing; match window stated by the author; fact refusal when "`startMs < endMs`" fails or the range is outside the half-open window, at `add_fact` against the window known then and again at finalization against the final window; all facts in one `facts.json` (`application/vnd.socalytics.facts+json`) and `factCount` equal to their number; streaming SHA-256 digests and sizes from the written bytes; staging under `/socalytics/result/.socalytics-staging/` with flush, `fsync`, `os.replace` of every artifact and the manifest last, removal of the staging directory, refusal of top-level files the writer did not write, result-manifest `contractVersion` `1.1.0`, canonical-schema validation of every written document (`output-invalid`), and refusal of mutation or finalization after finalization (research R6, R7; FR-010–FR-016)
- [x] T015 [US1] Add `src/analysts/sdk/src/socalytics_analyst/entrypoint.py` with `run(analyst, *, output_type, root=Path("/socalytics"))` (load, call, finalize; print `socalytics-analyst: finalized facts=<n> artifacts=<m>` to standard output and exit 0; exit 2 for `InputError`, 1 for every other exception; before a non-zero exit remove the staging directory and any manifest it wrote and print one line `socalytics-analyst: failed <category>` of at most 200 characters to standard error; no telemetry), export `run`, `AnalystContext`, `Fact`, and the errors from `src/analysts/sdk/src/socalytics_analyst/__init__.py`, and add the minimal example `src/analysts/sdk/examples/minimal_analyst.py` (at most 20 lines) (research R9; FR-020, FR-021; SC-004)

### Test fixtures

- [x] T016 [US1] Add `src/analysts/sdk/tests/conftest.py` and `src/analysts/sdk/tests/fixtures/mounts.py`: builders that create a temporary mount root with `job.json` (from the analyst-job 1.1.0 valid examples, adjustable), `attempt.json`, `result/`, `scratch/`, and optionally staged inputs in the staged-inputs layout, plus a helper that validates a written document against the canonical schemas in the repository's `contracts/` (FR-022)

### Tests for User Story 1

- [ ] T017 [P] [US1] Add `src/analysts/sdk/tests/test_job_and_attempt.py`: valid 1.1.0 jobs and attempt contexts load; missing files, invalid documents, a newer minor or another major version, a 1.0.0 job without `workflow`, and a segment job without `segmentWindow` fail with their categories (FR-003, FR-005; User Story 2 scenario 3)
- [ ] T018 [P] [US1] Add `src/analysts/sdk/tests/test_result_writer.py`: segment and match results validate against the canonical schemas and equal the job's scope, attempt, provenance, lineage, window, and fact count; window boundaries (a fact ending exactly at the end is inside; starting at the end, ending after it, or empty is refused); window narrowing and refusal to extend; an empty fact list; artifact name, reserved-name, duplicate, and empty-content refusals; `confidence-missing` and `result-value-invalid`; an unexpected top-level file; a second finalization and additions after it; no partial file under a final name when a write fails (FR-010–FR-016, FR-022; SC-001, SC-003)
- [ ] T019 [P] [US1] Add `src/analysts/sdk/tests/test_entrypoint.py`: running `src/analysts/sdk/examples/minimal_analyst.py` and test Analysts in a subprocess yields exit 0 with the summary line, exit 1 for an exception in the Analyst's function or a refused fact (no manifest, failure line on standard error without payloads), and exit 2 for invalid inputs (FR-020, FR-021; SC-003, SC-004)

**Checkpoint**: An SDK-built Analyst writes acceptable results against prepared mounts

---

## Phase 4: User Story 2 - The Analyst Manager gives every Analyst the context its result needs (Priority: P1)

**Goal**: Job messages state workflow and segment window; the Manager writes the job verbatim and the attempt context

**Independent Test**: Platform tests show new segment jobs carry `workflow` and a `segmentWindow` equal to what
acceptance compares; Manager tests show `job.json` is byte-identical to the delivery and `attempt.json` names the
claimed attempt on both runtimes

### Platform and Manager implementation

- [ ] T020 [P] [US2] Add `src/platform/SocAlytics.Platform.Application/Analysis/SegmentWindows.cs` (segment id `{recordingVersionId}-{segmentNumber}` and the half-open window "`(n − 1)·D … n·D`" with `D` from the run snapshot's `segmentationPolicy.durationSeconds` × 1000), use it in `src/platform/SocAlytics.Platform.Application/Analysis/Results/ResultManifestVerifier.cs` and `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Analysis/UpstreamResultReader.cs` instead of their local computations, and in `src/platform/SocAlytics.Platform.Application/Analysis/RunReadinessCoordinator.cs` publish job messages with `contractVersion` `1.1.0`, `workflow` (`id` and the run's version as in run-state-changed), and `segmentWindow` for segment-scoped jobs only (research R10; FR-006)
- [ ] T021 [P] [US2] In `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Work/ActiveAttempt.cs` keep the delivered job message bytes, in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Work/AttemptRunner.cs` write those bytes unchanged to `job.json` and write `attempt.json` (`contractVersion` `1.0.0`, the claimed `attemptId` and `attemptNumber`; no lease, fencing token, grant, or address) beside it, in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Runtime/AttemptExecution.cs` add the attempt file to `AttemptLaunch`, and in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Runtime/Docker/DockerLaunchSpec.cs` bind it read-only at `/socalytics/attempt.json`; leave the outcome rules, environment, and other mounts unchanged (contracts/container-interface.md; research R10; FR-007, FR-008, FR-009)

### Tests for User Story 2

- [ ] T022 [P] [US2] Extend `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/ReadinessProgressionTests.cs`: published segment and match job messages validate against analyst-job 1.1.0, carry the run's workflow, and segment messages carry a `segmentWindow` whose id and window equal what `ResultManifestVerifier` and `UpstreamResultReader` use; match messages carry none (FR-006, FR-023)
- [ ] T023 [P] [US2] Extend `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Work/AttemptDirectoryTests.cs`, `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Runtime/Docker/DockerLaunchSpecTests.cs`, and `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Runtime/SimulatedRuntimeTests.cs`: `job.json` is byte-identical to the delivered message and valid against the job contract, `attempt.json` validates against `contracts/analysis/analyst-attempt/v1` and names the claimed (and, on retry, the new) attempt, the Docker bind is read-only, no environment variable is added, a job message of contract version 1.0.0 still runs with the attempt context, and the simulated runtime sees the same files (FR-007, FR-008, FR-009, FR-023; User Story 2 scenario 3; SC-005)

**Checkpoint**: Real executions provide everything an SDK-built Analyst needs

---

## Phase 5: User Story 3 - A high-level Analyst consumes verified upstream results and reports its coverage (Priority: P2)

**Goal**: Load and verify staged upstream results, expose typed manifests, facts, and coverage, and write
partial-coverage reports and derived match windows

**Independent Test**: A staged input mount with a partial-coverage dependency naming a failed segment loads into typed
results and coverage; the manifest's lineage and `inputCoverage` equal the input snapshot; tampered, missing, or extra
staged results fail with named errors and exit 2

### Implementation for User Story 3

- [ ] T024 [US3] Add `src/analysts/sdk/src/socalytics_analyst/inputs.py`: `StagedInputs`, `StagedDependency`, and `StagedResult` (data-model.md "SDK objects"), loading `/socalytics/inputs/{capabilityId}/coverage.json` (which states no contract version: validate its `upstream` and `coverage` parts against the `upstream` and `coverageEntry` definitions of the newest supported upstream-results schema) and `{acceptedResultId}/manifest.json` with schema validation; checks that the staged result set equals the input snapshot's accepted results, each manifest's SHA-256 equals its `manifestChecksum`, and each declared artifact exists with its declared size and digest (`input-staging-mismatch`, `input-artifact-mismatch`); typed facts from artifacts of the fact media type; no staged results for `absent`, `failed`, or `not-applicable` entries; read-only access only (research R8; FR-018, FR-019)
- [ ] T025 [US3] Wire staged inputs into `src/analysts/sdk/src/socalytics_analyst/context.py` (`inputs` populated; absent input mount means no inputs) and extend `src/analysts/sdk/src/socalytics_analyst/result.py`: `inputCoverage` written exactly as staged for each dependency whose staged coverage states mode `partial` and never for others; for match jobs without a stated window, the smallest window containing every staged result's `validityWindow`, else `validity-window-missing` (research R7; FR-013, FR-017)

### Tests for User Story 3

- [ ] T026 [P] [US3] Add `src/analysts/sdk/tests/test_inputs.py`: matching staged inputs load with typed manifests, facts, verified artifacts, coverage, and edge kinds; a changed artifact byte, a wrong size, a missing or extra staged result, a manifest checksum mismatch, and staged results for an `absent` entry fail with their categories and exit 2 through the entry point (FR-018, FR-019, FR-022)
- [ ] T027 [P] [US3] Add `src/analysts/sdk/tests/test_result_coverage.py`: lineage equals the input snapshot whatever the Analyst does; a partial-coverage dependency with a failed segment yields `inputCoverage` equal to the staged coverage and a result valid against result-manifest 1.1.0; complete-coverage dependencies get no report; the derived match window and `validity-window-missing` (FR-010, FR-013, FR-017, FR-022; SC-001)

**Checkpoint**: High-level Analysts consume verified inputs and report coverage

---

## Phase 6: User Story 4 - The SDK and the platform cannot drift apart (Priority: P2)

**Goal**: Executable conformance between the SDK models and validation and the canonical examples and schemas

**Independent Test**: The SDK test suite accepts every valid example, rejects every invalid example with its expected
keyword, and fails when a model field differs from its schema

### Tests for User Story 4

- [ ] T028 [P] [US4] Add `src/analysts/sdk/tests/test_contract_examples.py`: for analyst-job, analyst-attempt, result-manifest, and fact-artifact, every `examples/valid/*.json` in the repository's `contracts/` is accepted by schema validation and model parsing, and every `examples/invalid/*.json` is rejected by both, with the schema keyword equal to its `expectations.json` entry; for upstream-results (whole pages the SDK never reads), every example is checked by schema validation with its expected outcome, and the `upstream` and `coverage` parts and embedded manifests of the valid examples parse into the SDK's staged-coverage and manifest models (FR-022; SC-002)
- [ ] T029 [P] [US4] Add `src/analysts/sdk/tests/test_model_schema_fields.py`: walks each implemented schema's object definitions (resolving `$ref`) and compares property names, aliases, and required sets with the corresponding model fields, failing with the schema path and property on any difference (FR-002, FR-022; User Story 4 scenario 2)

**Checkpoint**: Contract drift fails SDK verification

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Documentation and final validation

### Documentation

- [ ] T030 [P] Add `src/analysts/sdk/README.md`: purpose, the container interface it implements, the minimal example from `src/analysts/sdk/examples/minimal_analyst.py`, exit statuses and error categories, supported contract versions, the supported commands (the environment's locked sequence and `uv run --no-sync pytest`), and the schema-copy refresh rule (`scripts/sync_contracts.py`) (FR-024)
- [ ] T031 [P] Make `docs/architecture/` true for the merged state: re-read it at implementation time and correct every narrative and status statement this feature affects, using plan.md "Architecture Updates" as examples rather than the whole job, including the SDK and container-interface paragraphs in `docs/architecture/analysts-models-and-hardware.md` and `docs/architecture/analyst-runtime-and-recovery.md` (present tense, the `/socalytics/attempt.json` mount), the SDK statements in `docs/architecture/platform-implementation.md` (source baseline, Analyst Technology, Acceptance Evidence), and the contract scope and Python-boundary statements in `docs/architecture/contracts-and-compatibility.md`, without presenting real capabilities, images, or production readiness as implemented (FR-024; constitution Principle I)
- [ ] T032 [P] Update `src/analysts/README.md` (the SDK is executable under `sdk/`; capabilities remain future), `README.md` (the supported SDK commands), and `AGENTS.md` (Current State: the SDK and the completed container interface; the SDK commands; that a change to an implemented analysis or common contract refreshes the SDK's schema copy with `src/analysts/sdk/scripts/sync_contracts.py` in the same change) (FR-024)

### Final validation

- [ ] T033 Run the validation of quickstart.md §1–§3 (SDK locked sequence and tests, `scripts/sync_contracts.py --check`, platform Contracts and full suites, Manager suite) and `node .github/scripts/check-markdown.mjs`, and fix any failure in the files of this feature (SC-001–SC-005)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: requires the merged environment feature (#143), Fact Indexing (#144), and Partial-Coverage
  Match Barriers.
- **Foundational (Phase 2)**: after Setup; blocks every story.
- **User Story 1 (Phase 3)**: after Foundational.
- **User Story 2 (Phase 4)**: after Foundational; independent of User Story 1 (platform and Manager code only).
- **User Story 3 (Phase 5)**: after User Story 1 (extends `context.py` and `result.py`).
- **User Story 4 (Phase 6)**: after Foundational; placed after User Story 3 so the tests cover the final models.
- **Polish (Phase 7)**: after all stories.

### Within Phases

- T002 and T003 run in parallel; T004 follows both (shared index and catalog).
- T005 → T006 → T007 (validation uses the copy; models use common types); T008–T011 run in parallel; T012 follows
  them (exports and the upstream model that embeds manifests).
- T013 → T014 → T015 → T016; T017–T019 run in parallel.
- T020 and T021 run in parallel (platform and Manager files); T022 and T023 run in parallel.
- T024 → T025; T026 and T027 run in parallel.
- T028 and T029 run in parallel.
- T030–T032 run in parallel; T033 last.

## Parallel Example: Phase 2 contract models

```text
T008 models/job.py     T009 models/attempt.py     T010 models/manifest.py     T011 models/facts.py
```

## Parallel Example: User Story 2

```text
T020 platform SegmentWindows, verifier, reader, readiness publisher
T021 Manager verbatim job.json and attempt.json
```

## Implementation Strategy

### MVP First

Setup, Foundational, and User Story 1 give Analyst authors a working SDK against prepared mounts; User Story 2 makes
real executions provide the same context. Both P1 stories together are the minimum for an accepted real result.

### Incremental Delivery

1. Setup + Foundational → contracts, schema copy, models.
2. User Story 1 → author-facing SDK (MVP for authors).
3. User Story 2 → complete container interface on the platform and Manager.
4. User Story 3 → high-level inputs, lineage verification, coverage reports.
5. User Story 4 → conformance gate against drift.
6. Polish → documentation and final validation.
