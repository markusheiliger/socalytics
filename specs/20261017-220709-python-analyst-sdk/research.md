# Research: Python Analyst SDK

**Date**: 2026-10-17

## R1 - The container interface is incomplete today

**Decision**: Complete the container interface in this feature: the Manager writes the received Logical Job message
verbatim to `/socalytics/job.json`, writes an attempt context to `/socalytics/attempt.json`, and the platform's job
message (analyst-job 1.1.0) states the run's workflow and, for segment jobs, the segment identity and nominal window.

**Evidence** (repository at authoring time):

- `AttemptRunner` deserializes the NATS delivery into the partial record `AnalystJobMessage` and writes
  `JsonSerializer.SerializeToUtf8Bytes(attempt.Job, …)` to `job.json`. The record omits `contractVersion`,
  `readinessSequence`, `nodeKey`, `tier`, `attemptBudget`, `timeouts`, `createdAt`, and others, so the container's
  job file does not validate against `contracts/analysis/analyst-job/v1`, contrary to the Docker runtime adapter's
  `contracts/analyst-container.md`.
- `ResultManifestVerifier` rejects a manifest whose `attempt.attemptId`/`attemptNumber` differ from the completing
  attempt, whose `provenance.workflow` differs from the run's workflow, or whose segment window differs from
  `(n − 1)·D … n·D` with `D` from the run's segmentation policy. None of these values reaches the container: the end-to-end
  test Analyst (`src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/TestAnalyst/analyst.py`) reads the attempt
  identity from `/proc/self/mountinfo` and hard-codes the workflow; the simulated Analyst takes them from Manager
  options.
- The platform names segments `{recordingVersionId}-{segmentNumber}` in upstream reads and coverage
  (`UpstreamResultReader.SegmentIdOf`).

**Rationale**: An SDK cannot write an acceptable manifest from the documented interface; guessing (mount tables,
hard-coded workflows, a copied segment-id convention) would make every Analyst depend on undocumented host details.
The job message is the run-pinned description, so run-level facts (workflow, segment window) belong there; the
attempt identity exists only after the claim, so only the Manager can provide it.

**Alternatives considered**:

- Let the Manager rewrite `attempt`/`workflow` into the Analyst's manifest before upload: the Analyst would write
  manifests that are invalid against the canonical contract, and the checksum would no longer be the Analyst's.
- Put the attempt identity into `job.json`: the file would no longer be the platform's message and would not validate.
- Environment variables: the architecture forbids them for Analyst containers.
- Defer the interface completion to a separate spec: the SDK would have no executable path to an accepted result.

## R2 - SDK project shape

**Decision**: One independently locked project at `src/analysts/sdk/` with distribution name `socalytics-analyst-sdk`,
import package `socalytics_analyst` (src layout), `requires-python = "==3.12.*"`, build backend `hatchling`, and
development group `dev` with `pytest` and `hatchling` locked, exactly as the environment contract
(`specs/archive/20261017-163801-analyst-media-environment/contracts/environment-interface.md`) requires. Runtime
dependencies: `pydantic` 2.x and `jsonschema` 4.x (with its `referencing` registry). Exact versions are the newest
releases that ship CPython 3.12 manylinux x86_64 wheels at implementation time, fixed by `uv.lock`.

**Rationale**: The architecture names Pydantic for the SDK models; `jsonschema` validates the canonical Draft
2020-12 schemas offline with a local registry, so the SDK checks the very documents the platform checks. Both and
their transitive dependencies (`pydantic-core`, `rpds-py`, `attrs`, `referencing`, `jsonschema-specifications`,
`typing-extensions`, `annotated-types`) ship wheels, as the environment requires. `hatchling` is pure Python and
builds without isolation from the locked environment.

**Alternatives considered**: Pydantic alone (cannot express every schema rule identically and would make Python types
the authority); `fastjsonschema` (code generation, weaker 2020-12 support); `setuptools` (heavier, no benefit); a
shared uv workspace (rejected by the environment feature).

## R3 - Canonical schemas inside the SDK

**Decision**: The SDK carries a byte-identical copy of the schemas it implements under
`src/analysts/sdk/src/socalytics_analyst/contracts/` (same relative layout as the repository's `contracts/`, so
relative `$ref`s resolve), refreshed by `src/analysts/sdk/scripts/sync_contracts.py` (standard library only). A
conformance test compares every copied file with its canonical file and fails with the refresh command when they
differ or a canonical file of an implemented contract is missing from the copy.

Copied: `common/v1/common.schema.json`; `analysis/analyst-job/v1/`, `analysis/analyst-attempt/v1/`,
`analysis/result-manifest/v1/`, `analysis/upstream-results/v1/`, and `analysis/fact-artifact/v1/` schemas and their
`releases/` copies (examples are read from the repository by the tests only).

**Rationale**: An Analyst image installs the SDK wheel and has no repository checkout, so the schemas must be package
data. `uv sync` installs the project editable, where Hatch `force-include` from outside the project root does not
apply, so a build-time include would need a second, repository-walking lookup path. A verified copy works identically
in editable and wheel installs, and the environment reruns the SDK tests on every `contracts/analysis/` or
`contracts/common/` change, so a contract change without a refreshed copy fails verification instead of drifting.

**Alternatives considered**: Hatch `force-include` of `../../../contracts` plus a development fallback (two lookup
paths, editable-mode surprises); reading schemas from the repository at run time (unavailable in images); embedding
schemas as Python literals (unreviewable diffs).

## R4 - Typed models and strictness

**Decision**: Hand-written Pydantic v2 models, one module per contract, `ConfigDict(extra="forbid", frozen=True,
populate_by_name=True)` with camelCase aliases, `Annotated` string constraints mirroring the common definitions
(identifier, key, semanticVersion, contractVersionV1, timestamp with `Z`, digest, safeLabel, objectName), integer
bounds, enumerations, and `oneOf` scopes as discriminated unions. Every read document is validated first against the
canonical schema of its `contractVersion` and then parsed into models; every written document is dumped by alias,
validated against the canonical schema, then written.

Conformance tests: (1) every `examples/valid/*.json` of the implemented contracts is accepted by schema validation and
model parsing; every `examples/invalid/*.json` is rejected by both, and the schema error keyword equals its
`expectations.json` entry; (2) a structural test walks each schema's object definitions (resolving `$ref`) and compares
property names and required sets with the corresponding model fields, failing with schema path and property; (3)
documents written by SDK tests validate against the canonical schemas.

**Rationale**: Hand-written models stay readable for Analyst authors; the structural test and dual validation give
the "same strictness" guarantee without making generated Python types authoritative.

**Alternatives considered**: `datamodel-code-generator` (an extra tool; unstable output for `oneOf`, `if/then`, and
shared `$defs`; generated code would still need the same tests).

## R5 - Supported versions

**Decision**: The SDK supports major version 1 of each implemented contract and these minor versions:
analyst-job 1.0.0 and 1.1.0 (reads; an Analyst needs 1.1.0 fields to finalize, R1), analyst-attempt 1.0.0,
upstream-results 1.0.0 and 1.1.0 (staged `coverage.json` and staged manifests), result-manifest 1.0.0 and 1.1.0
(reads staged upstream manifests; writes 1.1.0), fact-artifact 1.0.0. A document stating a newer minor or another
major version fails with `unsupported-contract-version`. The package version starts at `1.0.0` and follows the
contracts' major version.

**Rationale**: Partial-Coverage Match Barriers (a hard dependency) introduces result-manifest and upstream-results
1.1.0 and the platform and Manager accept both. Writing 1.1.0 always is safe because the coverage report is optional
there and written only for partial-coverage dependencies.

## R6 - Writing results atomically

**Decision**: The result writer writes each artifact into a private staging directory
`/socalytics/result/.socalytics-staging/` (a directory, which the Manager's top-level file scan ignores), flushes and
`fsync`s it, and moves it with `os.replace` to its final top-level name only at finalization; the manifest is written
the same way and moved last; the staging directory is removed before the manifest is moved. Before finalizing, the
writer lists the top-level files of the result mount and refuses any name it did not write.

**Rationale**: `os.replace` is atomic only within one filesystem; `/socalytics/scratch/` is a different bind mount, so
staging must live inside the result mount. Top-level files are uploaded by the Manager (`ResultPublisher` enumerates
top-level files only), so staging files must never be top-level.

## R7 - Windows, facts, lineage, and coverage

**Decision**:

- Segment job: validity window defaults to `segmentWindow` (1.1.0 job field); the author may narrow it within those
  bounds. Match job: the author states it, or the SDK uses the smallest window containing every staged upstream
  result's `validityWindow`; without either, finalization fails `validity-window-missing`.
- A fact is accepted only when `startMs < endMs`, `startMs >= window.startMs`, and `endMs <= window.endMs`, checked
  when the fact is added against the window known at that time (a match job's window may be unknown until the author
  states it) and again at finalization against the final window (facts outside a window narrowed later fail
  finalization).
- All facts go into `facts.json` (`application/vnd.socalytics.facts+json`), in insertion order; `factCount` is the
  number of facts; an empty list is valid.
- `upstreamLineage` is built from the job's `inputs[].acceptedResults` (acceptedResultId, capability, schemaVersion,
  manifestChecksum), in input-snapshot order; the author cannot change it.
- `inputCoverage` (result-manifest 1.1.0) is written for each dependency whose staged `coverage.json` states
  `upstream.coverage.mode = partial` (upstream-results 1.1.0), with the staged entries unchanged; never for others.
- Provenance: capability and schemaVersion from the job; `analystProfile` from `pinned.analystProfileId`/
  `analystProfileDigest`; `imageDigest`; `modelDigest` only when not null; `policyVersions` from `pinned.policies`
  (`policyId` → `id`); `workflow` from the 1.1.0 job field.

## R8 - Staged input verification

**Decision**: For each `inputs[]` entry with status `accepted`, the SDK expects `inputs/{capabilityId}/coverage.json`
and one directory per accepted result; it checks that the directory set equals the input snapshot's accepted result
ids, that each staged `manifest.json`'s SHA-256 equals the snapshot's `manifestChecksum`, that each declared artifact
exists with the declared size and digest, and parses fact artifacts (by media type) into typed facts. `coverage.json`
states no contract version, so the SDK validates its `upstream` and `coverage` parts against the `upstream` and
`coverageEntry` definitions of the newest supported upstream-results schema (1.1.0, whose additions are optional, so
1.0.0 content passes too). Entries with
status `absent`, `failed`, or `not-applicable` must have no staged results. Any mismatch fails with a named
`input-*` error (exit 2). Files are opened read-only and never modified.

## R9 - Entry point, exit statuses, and output

**Decision**: `socalytics_analyst.run(analyst, *, output_type, root=Path("/socalytics")) -> NoReturn` loads inputs,
calls `analyst(context)`, finalizes, prints one summary line (`socalytics-analyst: finalized facts=<n> artifacts=<m>`)
to standard output, and exits 0. `InputError` subclasses (invalid, unsupported, unverifiable inputs) exit 2; every
other exception exits 1. Before exiting non-zero the entry point removes the staging directory and any manifest it
wrote, and prints one line `socalytics-analyst: failed <category>` (at most 200 characters, category only) to
standard error. No environment variable is read; `root` exists only for tests.

**Rationale**: The Manager treats every non-zero exit as `analyst-error` and needs a manifest only on success
(`DockerAttemptExecution`); distinct statuses help operators reading the bounded container log without leaking
content.

## R10 - Platform and Manager changes

**Decision**:

- analyst-job 1.1.0: optional `workflow` `{ id: key, version: semanticVersion }` (same shape as run-state-changed) and
  optional `segmentWindow` `{ segmentId: identifier, startMs, endMs }`, with a rule that `segmentWindow` appears only
  for segment-scoped jobs. The platform emits 1.1.0 with both fields for every new readiness occurrence; the segment
  id and window come from one Application helper that `UpstreamResultReader` and `ResultManifestVerifier` also use,
  so the three agree by construction.
- analyst-attempt 1.0.0: `{ contractVersion, attemptId: identifier, attemptNumber: attemptNumber }`, closed object.
- Manager: `ActiveAttempt` keeps the delivery bytes; `AttemptRunner` writes them unchanged to `job.json` and writes
  `attempt.json`; `AttemptLaunch` gains the attempt file path; `DockerLaunchSpec` binds it read-only at
  `/socalytics/attempt.json`. The simulated runtime reads the same files.

## Evidence Boundary

Package versions, wheel availability, and editable-install behavior are planning knowledge, not executed evidence.
The implementation must prove them through the environment's locked sync, wheel build, and pytest run, and through
the platform and Manager test suites.
