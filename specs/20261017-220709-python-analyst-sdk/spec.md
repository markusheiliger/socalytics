# Feature Specification: Python Analyst SDK

**Feature Branch**: `20261017-220709-python-analyst-sdk`

**Created**: 2026-10-17

**Status**: Draft

**Input**: User description: "Python Analyst SDK: the architecture plans an internal Analyst SDK that exposes Pydantic models and validates input and output at the container boundary, while the platform independently validates the same canonical JSON Schemas (docs/architecture/platform-implementation.md#analyst-technology, docs/architecture/analysts-models-and-hardware.md, docs/architecture/tenancy-and-technology.md#analytics, docs/architecture/contracts-and-compatibility.md); no Analyst SDK exists (src/analysts/README.md). Analyst authors need one library that implements the fixed Analyst container interface (docs/architecture/analyst-runtime-and-recovery.md: the Logical Job read-only at `/socalytics/job.json`, staged upstream results read-only at `/socalytics/inputs/`, the result mount `/socalytics/result/` for the result manifest and artifacts, scratch at `/socalytics/scratch/`; no network, no environment variables, no credentials). This feature creates the SDK as the independent Python project `src/analysts/sdk/` (per the environment spec's project contract): typed Pydantic models for the Logical Job, the staged upstream results and their coverage, the result manifest, and fact artifacts, generated from or verified against the canonical schemas under `contracts/analysis/` and `contracts/common/v1/` with the same strictness (unknown properties, digests, half-open windows, timestamps); helpers that load and validate the job and staged inputs (including digest verification of staged artifacts against their manifests), write fact artifacts and the result manifest atomically into the result mount with correct digests, sizes, lineage, validity window, fact count, and coverage, and refuse to write facts outside the nominal half-open window; a small entry-point helper that maps exceptions to the exit behavior the Manager expects; and contract-conformance tests that validate the SDK against every valid and invalid example and golden fixture in `contracts/` so the SDK and the platform cannot drift. It includes no capability, no model, no network client, and no platform credentials; versioning follows the contracts' major versions and supports the contract minor versions present on main at implementation time (partial coverage 1.1.0 when spec 5 has merged). Out of scope: real capabilities and models, container images and publishing, a Python transport client for the platform API, media decoding (spec 9), Hermes. Depends on `specs/archive/20261017-163801-analyst-media-environment` (#143, environment, must merge first), Analysis Result Manifests and Validated Acceptance, Analysis Result Read for Jobs, Durable Analysis Workflow, Docker Desktop Runtime Adapter for Analyst Execution (container interface) (merged; archived), and `specs/archive/20261017-175648-fact-indexing-queries` (#144, fact-artifact contract). Implements docs/architecture/platform-implementation.md#analyst-technology (Analyst SDK), the SDK parts of docs/architecture/analysts-models-and-hardware.md, and src/analysts ownership (docs/architecture: Analyst SDK planned under src/analysts)."

## Clarifications

### Session 2026-10-17

- Q: Should this feature depend on Partial-Coverage Match Barriers, so the SDK writes the result manifest version with the input coverage report, instead of making coverage reporting conditional on what has merged? → A: Yes; `specs/archive/20261017-212004-partial-coverage-barriers` MUST merge first, and the SDK supports result manifests 1.0.0 and 1.1.0 and writes 1.1.0.
- Q: Which exit statuses should the entry point use? → A: 0 for a finalized result, 1 for a failure of the Analyst's own code or a refused fact or artifact, 2 for invalid, unsupported, or unverifiable inputs.
- Q: What should the SDK write to the container's output besides the failure line? → A: One bounded summary line on success with the number of facts and artifacts; no telemetry export and no content.
- Q: Should the SDK put all facts of a result into one fact artifact or let authors write several? → A: One fact artifact per result, named `facts.json`; supporting artifacts are separate.

## User Scenarios & Testing *(mandatory)*

The users are Analyst authors (people who build Analyst capabilities as containers) and the maintainers of the
platform and the Analyst Manager, who need Analyst results to pass acceptance without each author re-implementing
the container interface and the result contracts.

### User Story 1 - An Analyst author writes an acceptable result without knowing the contract details (Priority: P1)

An Analyst author writes only the capability logic: a function that receives the typed Logical Job and returns facts.
The SDK reads and validates the job and its attempt context from the container mounts, lets the author add facts and
supporting artifacts, refuses any fact outside the job's nominal half-open window, and writes the fact artifacts and
the result manifest into the result mount so that every value the platform checks on acceptance (scope, attempt,
provenance, lineage, artifact digests and sizes, validity window, fact count, output type) is correct. A small entry
point runs the function and ends the container with the exit behavior the Analyst Manager expects: success only with
a complete result, failure without a result otherwise.

**Why this priority**: This is the reason the SDK exists. Without it every Analyst author re-implements the same
contract details, and any mistake fails acceptance only at run time.

**Independent Test**: Run a test Analyst function through the entry point against prepared mount directories for a
segment-scoped and a match-scoped job; the result mount then holds exactly the manifest and the declared artifacts,
the manifest and fact artifacts validate against the canonical schemas, and every value the platform compares on
acceptance equals the job's. Raising an error in the function, or adding a fact outside the window, ends with a
non-zero exit and no manifest.

**Acceptance Scenarios**:

1. **Given** a valid segment-scoped job and attempt context in the mounts, **When** the Analyst function adds facts
   inside the segment's nominal window and returns, **Then** the entry point writes one fact artifact and the result
   manifest with the job's scope and segment window, the attempt identity, the run-pinned provenance, empty lineage,
   the artifact's digest and size, the validity window, and the number of facts, and the process exits successfully.
2. **Given** a match-scoped job, **When** the Analyst function adds soccer concepts within the validity window it
   states, **Then** the manifest carries the match scope, that window, and the matching fact count.
3. **Given** any job, **When** the Analyst function adds a fact whose range is empty or not inside the result's
   half-open validity window, **Then** the SDK refuses the fact with a named error, nothing is written as a result,
   and the process exits with a failure.
4. **Given** the Analyst function raises an error, or the job or attempt context is missing or invalid, **When** the
   entry point ends, **Then** no manifest exists in the result mount and the process exits with a failure; standard
   error states the failure category without job content.
5. **Given** the Analyst function writes a supporting artifact through the SDK, **When** the result is finalized,
   **Then** the artifact appears in the manifest with its name, media type, digest, and size, and a file the SDK did
   not write in the result mount prevents finalization.

---

### User Story 2 - The Analyst Manager gives every Analyst the context its result needs (Priority: P1)

Today the Analyst container receives a reduced copy of the Logical Job without its contract version, and neither the
attempt identity, the run's workflow, nor the segment's nominal window, all of which the platform checks on
acceptance; a real Analyst can only guess them. The Analyst Manager now writes the Logical Job message exactly as the
platform published it, and writes a read-only attempt context beside it; the platform's job message states the run's
workflow and, for segment jobs, the segment identity and nominal window. The SDK reads these, so an SDK-built Analyst
never guesses an identity.

**Why this priority**: Without the complete container interface no real Analyst, SDK-built or not, can produce a
result the platform accepts; User Story 1 depends on it for real executions.

**Independent Test**: Run a Manager attempt with the simulated and the Docker runtime; the job file in the attempt
directory is byte-identical to the received message and validates against the job contract, the attempt context
names the claimed attempt, and a newly published segment job states its workflow, segment identity, and window equal
to the values acceptance compares.

**Acceptance Scenarios**:

1. **Given** a ready Logical Job published by the platform, **When** the message is built, **Then** it states the
   run's workflow id and version and, for a segment-scoped job, the segment identity and the half-open nominal window
   that acceptance derives from the run's segmentation policy, and it still validates against the job contract.
2. **Given** a claimed attempt, **When** the Manager prepares the container, **Then** `/socalytics/job.json` holds the
   received job message unchanged and `/socalytics/attempt.json` holds the attempt identity and number, both
   read-only, and no environment variable, credential, or platform address is added.
3. **Given** a job message of the previous contract version, **When** the Manager runs it, **Then** it runs as today
   with the attempt context, and an SDK-built Analyst ends with a failure that names the missing job fields instead of
   guessing them.
4. **Given** a retried attempt, **When** the Manager prepares it, **Then** the attempt context names the new attempt
   and number, and the job file is the retry's message.

---

### User Story 3 - A high-level Analyst consumes verified upstream results and reports its coverage (Priority: P2)

A match-level Analyst depends on segment-level results. The SDK loads the staged upstream results from the read-only
input mount, verifies each staged manifest against the job's input snapshot and each staged artifact against its
manifest's digest and size, gives the author typed upstream manifests, facts, and per-dependency coverage, writes the
upstream lineage exactly as the input snapshot requires, and writes the coverage report that partial-coverage
dependencies require.

**Why this priority**: High-level capabilities are the next real capabilities; lineage and coverage mistakes are
the most common acceptance failures for them. It builds on User Stories 1 and 2.

**Independent Test**: Prepare an input mount laid out as the Manager stages it, with one partial-coverage dependency
whose coverage names a failed segment; the SDK returns the typed results, facts, and coverage, and the written
manifest's lineage and coverage equal the input snapshot. A staged artifact with a changed byte, a missing staged
result, or an extra one fails loading with a named error.

**Acceptance Scenarios**:

1. **Given** staged upstream results that match the job's input snapshot, **When** the Analyst loads its inputs,
   **Then** it receives every accepted result with its typed manifest, its fact artifacts as typed facts, its other
   artifacts as verified files, and the dependency's coverage.
2. **Given** a staged artifact whose digest or size differs from its manifest, a staged manifest whose checksum
   differs from the input snapshot, or a staged result set that differs from the input snapshot, **When** the
   Analyst loads its inputs, **Then** loading fails with a named error and no result is written.
3. **Given** a job with upstream dependencies, **When** the result is finalized, **Then** the manifest's lineage
   names exactly the accepted results of the input snapshot with their capabilities, schema versions, and manifest
   checksums, whatever the Analyst function did.
4. **Given** a partial-coverage dependency, **When** the result is finalized, **Then** the manifest reports that
   dependency's coverage exactly as staged; for other dependencies no coverage report is written.

---

### User Story 4 - The SDK and the platform cannot drift apart (Priority: P2)

A maintainer changes a canonical analysis contract or adds an example. The SDK's conformance tests validate the SDK
models and validators against every valid and invalid example and golden fixture of the contracts it implements, and
compare the models' fields with the canonical schemas, so a contract change that the SDK does not follow fails the
SDK's verification, which the environment reruns for every shared contract change.

**Why this priority**: The architecture keeps the JSON Schemas canonical and lets the platform and the SDK validate
them independently; only executable conformance evidence keeps the two aligned.

**Independent Test**: Run the SDK test suite; every valid example of the implemented contracts is accepted by both
the models and the schema validation, every invalid example is rejected with its expected reason, and a model field
missing from or extra to its schema fails a test.

**Acceptance Scenarios**:

1. **Given** the examples under `contracts/analysis/` and `contracts/common/v1/` for the contracts the SDK
   implements, **When** the SDK tests run, **Then** every valid example is accepted and every invalid example is
   rejected for the reason its expectations name.
2. **Given** a canonical schema gains, loses, or renames a property, **When** the SDK tests run without a matching
   model change, **Then** a conformance test fails and names the schema and property.
3. **Given** a document the SDK writes in its tests, **When** it is validated against the canonical schema, **Then**
   it is valid.

### Edge Cases

- A fact ending exactly at the window's end is inside; a fact starting at the window's end, ending after it, or
  with an end not after its start is refused.
- A segment-scoped Analyst may state a validity window narrower than the segment's nominal window but never one
  outside it; a match-scoped Analyst must state its validity window or let the SDK derive it from the staged upstream
  results' windows, and a match job without upstream results and without a stated window fails finalization.
- No facts at all: the result is written with a fact artifact holding an empty list and a fact count of zero.
- Facts are added after finalization, or finalization runs twice: refused; the first result stays intact.
- The result mount already contains files when the Analyst starts (for example after a crash of a previous run in
  the same directory): finalization refuses files it did not write rather than declaring them.
- An artifact name that the result contract does not allow, an empty artifact, or a name used twice: refused when the
  artifact is added.
- The job has no upstream dependencies: the input mount is absent and the SDK reports no inputs.
- An optional dependency that was absent, failed, or not applicable when the job became ready: the SDK reports its
  status from the input snapshot and expects no staged results for it.
- The process is killed while writing: no complete manifest exists, so the Manager fails the attempt as today.
- A contract minor version newer than the SDK supports appears in an input: loading fails with a named
  unsupported-version error instead of ignoring unknown properties.
- Upstream fact artifacts of a capability whose payload the Analyst does not understand: the SDK returns the payload
  as an untyped object; payload semantics stay with the capability owners.

## Requirements *(mandatory)*

### Functional Requirements

#### SDK project and models

- **FR-001**: The SDK MUST be an internal, independently locked Python project under the Analysts source area that
  follows the Python project convention of the environment feature and contains no capability, model, network
  client, platform credential, or container image.
- **FR-002**: The SDK MUST provide typed models for the Logical Job, the attempt context, the staged upstream results
  and their coverage, the result manifest, and fact artifacts, with the same strictness as the canonical schemas:
  unknown properties, identifier and digest patterns, semantic and contract version formats, UTC timestamps,
  half-open millisecond windows, enumerations, and bounds.
- **FR-003**: The SDK MUST validate every document it reads or writes against the canonical schemas of the contract
  version it states (staged coverage files state no version and are validated against the corresponding definitions
  of the newest supported upstream-results version), in addition to its typed models, and MUST refuse documents of a
  major version it does not implement or of a minor version newer than it supports with a named error.
- **FR-004**: The SDK MUST support every released minor version of each implemented contract present in `contracts/`
  when it is implemented, including result manifests 1.0.0 and 1.1.0, and MUST write the newest supported minor
  version of the result manifest (1.1.0) and fact artifact contracts.

#### Container interface

- **FR-005**: The SDK MUST read the Logical Job from `/socalytics/job.json`, the attempt context from
  `/socalytics/attempt.json`, and staged inputs from `/socalytics/inputs/`, and MUST write only into
  `/socalytics/result/` and, for temporary data, `/socalytics/scratch/`; it MUST NOT read environment variables,
  open network connections, or require credentials. Tests MAY redirect the mount root through an explicit parameter.
- **FR-006**: The platform's Logical Job message MUST gain an additive minor version that states the run's workflow
  id and version and, for segment-scoped jobs, the segment identity and half-open nominal window, with exactly the
  values that result acceptance compares; the platform MUST publish this version for new readiness occurrences, and
  messages of the previous version MUST remain valid.
- **FR-007**: The Analyst Manager MUST write the Logical Job message it received unchanged as the container's job
  file, so the file validates against the job contract.
- **FR-008**: The Analyst Manager MUST write, beside the job file and equally read-only, an attempt context with the
  claimed attempt's identity and number, following a new versioned attempt-context contract in
  `contracts/analysis/`; it MUST contain no credential, grant, lease or fencing token, or platform address.
- **FR-009**: The simulated runtime of the Manager MUST provide the same job file and attempt context as the Docker
  runtime, and the existing Analyst container outcome rules (exit status and `manifest.json`) MUST stay unchanged.

#### Writing results

- **FR-010**: The SDK MUST derive the manifest's scope, attempt, provenance (capability, schema version, Analyst
  profile, image and model digests, policy versions, workflow), and upstream lineage from the job and attempt
  context; the Analyst author MUST NOT be able to override them.
- **FR-011**: The SDK MUST let the author state the output type, the overall confidence, and evidence references, and
  MUST write all facts of a result into exactly one fact artifact named `facts.json`, with the job's capability and
  schema version and that output type, under the fact media type of the fact-artifact contract; supporting
  artifacts are separate artifacts with other names.
- **FR-012**: The SDK MUST refuse any fact whose range is empty or not within the result's half-open validity
  window, and any fact or payload the fact-artifact contract rejects: when the fact is added, against the window known
  at that time, and again at finalization against the final window.
- **FR-013**: For segment-scoped jobs the validity window MUST default to the segment's nominal window and MAY be
  narrowed but never extended; for match-scoped jobs the author MUST state it or the SDK MUST derive it as the
  smallest window containing the staged upstream results' validity windows, and finalization MUST fail without one.
- **FR-014**: The SDK MUST compute each artifact's digest and size from the bytes it wrote, set the fact count to the
  number of facts in the fact artifact, and write the manifest only after all artifacts are complete.
- **FR-015**: The SDK MUST write artifacts and the manifest atomically, so the result mount never holds a partially
  written artifact or manifest under its final name, and the manifest exists only after a successful finalization.
- **FR-016**: At finalization the result mount MUST contain exactly the manifest and the artifacts the manifest
  declares; the SDK MUST refuse to finalize otherwise, refuse a second finalization, and refuse additions after it.
- **FR-017**: For every partial-coverage dependency of the job the SDK MUST write the dependency's coverage report
  exactly as staged; for other dependencies it MUST NOT write one.

#### Reading inputs

- **FR-018**: The SDK MUST load the staged upstream results laid out as the Manager stages them, verify each staged
  manifest's checksum against the job's input snapshot, each staged artifact's digest and size against its manifest,
  and that the staged results are exactly the accepted results of the input snapshot, and MUST fail with a named
  error otherwise.
- **FR-019**: The SDK MUST give the author typed upstream manifests, upstream facts parsed from fact artifacts, other
  artifacts as verified read-only files, the coverage and edge kind per dependency, and the input-snapshot status of
  dependencies without staged results.

#### Entry point and failure behavior

- **FR-020**: The SDK MUST provide an entry point that loads the inputs, runs the author's function, finalizes the
  result, and exits with status 0 only after a successful finalization, writing one bounded summary line with the
  number of facts and artifacts to standard output.
- **FR-021**: On any failure the entry point MUST leave no manifest in the result mount and exit with status 2 for
  invalid, unsupported, or unverifiable inputs (job, attempt context, or staged inputs) and status 1 for every other
  failure (an error raised by the author's function, a refused fact or artifact, or failed finalization), and MUST
  write a bounded, single-line failure category to standard error without job content, payloads, or file contents.
  The SDK MUST NOT export telemetry.

#### Conformance evidence and documentation

- **FR-022**: Automated SDK tests MUST validate the models and the schema validation against every valid and invalid
  example and golden fixture of the contracts the SDK implements, MUST detect fields that differ between a model and
  its canonical schema, and MUST cover writing, atomicity, window and fact-count rules, input verification, lineage,
  coverage, and the entry point's exit behavior.
- **FR-023**: Platform and Manager tests MUST cover the new job-message fields and their equality with the values
  acceptance compares, the unchanged job file, the attempt context in both runtimes, and the new and changed
  contracts with examples under the repository's contract rules (released copies, index rows, expectations).
- **FR-024**: The SDK MUST ship author documentation with a minimal example Analyst function and the supported
  commands, and the Analysts area documentation MUST describe the SDK as implemented.

### Key Entities

- **Logical Job**: The run-pinned description of one unit of work the Analyst receives read-only; it now also states
  the run's workflow and, for segment jobs, the segment identity and nominal window.
- **Attempt Context**: The read-only statement of which Execution Attempt the container runs: attempt identity and
  number.
- **Staged Input**: One accepted upstream result in the input mount: its manifest, verified artifacts, and the
  coverage and edge kind of its dependency.
- **Fact Artifact**: A result artifact of the fact media type holding facts with capability-owned kinds, half-open
  match-time ranges, confidences, and payloads.
- **Result**: The manifest and artifacts the SDK writes atomically into the result mount for one attempt.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In automated tests, 100% of results written by the SDK for segment-scoped and match-scoped test jobs,
  with and without upstream dependencies, validate against the canonical schemas and match every value the platform
  compares on acceptance.
- **SC-002**: 100% of valid examples of the implemented contracts are accepted and 100% of invalid examples are
  rejected for their expected reason by the SDK.
- **SC-003**: 0 results are written when a fact lies outside the window, an input fails verification, or the author's
  function fails; every such run ends with a non-zero exit in 100% of tests.
- **SC-004**: An Analyst author can produce an acceptable segment-level result with a function of at most 20 lines
  using the documented example.
- **SC-005**: In the Manager tests, the job file is byte-identical to the received message and the attempt context
  names the claimed attempt for 100% of attempts on both runtimes, and existing Manager, platform, and end-to-end tests
  pass unchanged.

## Assumptions

### Dependencies

- [Analyst and Media Verification Environment](../archive/20261017-163801-analyst-media-environment/spec.md) (twin #143)
  provides CPython, `uv`, and the locked verification of `src/analysts/sdk/`; it MUST merge before this feature.
- [Accepted Fact Indexing and Capability-Scoped Fact Queries](../archive/20261017-175648-fact-indexing-queries/spec.md)
  (twin #144) provides the fact-artifact contract and media type; it MUST merge before this feature.
- [Partial-Coverage Match Barriers](../archive/20261017-212004-partial-coverage-barriers/spec.md) provides the result
  manifest 1.1.0 input coverage report and the partial-coverage statement in the staged coverage; it MUST merge
  before this feature.
- [Durable Analysis Workflow](../archive/20261005-130703-durable-analysis-workflow/spec.md),
  [Analysis Result Manifests and Validated Acceptance](../archive/20261013-125502-analysis-result-manifests/spec.md),
  [Analysis Result Read for Jobs](../archive/20261013-134112-analysis-result-read/spec.md),
  [Analyst Manager Work Execution](../archive/20261012-141233-manager-work-execution/spec.md), and
  [Docker Desktop Runtime Adapter for Analyst Execution](../archive/20261015-092159-docker-runtime-adapter/spec.md)
  are implemented and define the job message, the result contracts, the staged-inputs layout, and the container
  interface this feature completes.

### Architecture Alignment

- Implements the Analyst SDK of [Analyst Technology](../../docs/architecture/platform-implementation.md#analyst-technology),
  the SDK parts of [Analysts, Models, and Hardware](../../docs/architecture/analysts-models-and-hardware.md#implementation-profile),
  [Analytics](../../docs/architecture/tenancy-and-technology.md#analytics), and the container interface of
  [Analyst Runtime and Recovery](../../docs/architecture/analyst-runtime-and-recovery.md#runtime-preflight-and-status),
  under the canonical-schema rules of [Contracts and Compatibility](../../docs/architecture/contracts-and-compatibility.md).
- The JSON Schemas stay canonical; the SDK's models are a consumer and never a cross-language authority.

### Scope Boundaries

- Real capabilities and models, Analyst container images and their publication, a Python transport client for the
  platform API, media decoding and segment media staging, validation of the Analyst image manifest, Hermes, and
  production values are out of scope.
- The SDK does not enforce platform-configured size limits for fact artifacts and payloads; the platform remains the
  authority for them at acceptance.
- The existing end-to-end test Analyst keeps working unchanged; adopting the SDK in it is not required.
