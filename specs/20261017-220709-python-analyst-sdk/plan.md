# Implementation Plan: Python Analyst SDK

**Branch**: `20261017-220709-python-analyst-sdk` | **Date**: 2026-10-17 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261017-220709-python-analyst-sdk/spec.md`

## Summary

Create the internal Python Analyst SDK at `src/analysts/sdk/` and complete the Analyst container interface it needs.
The SDK reads and validates the Logical Job and a new attempt context, verifies staged upstream inputs, lets authors
add facts and supporting artifacts, and atomically writes `facts.json` and a result manifest 1.1.0 whose scope,
attempt, provenance, lineage, window, fact count, and partial-coverage reports equal what platform acceptance checks.
The platform publishes analyst-job 1.1.0 with the run's workflow and the segment window; the Manager writes the
received job message verbatim and a read-only `/socalytics/attempt.json` (new `analysis/analyst-attempt` 1.0.0).
Conformance tests validate the SDK against every example of the contracts it implements and against a verified copy
of their schemas (research R1–R10).

## Technical Context

**Language/Version**: Python 3.12 (CPython 3.12.15 in verification, from the environment feature) for the SDK; C# on
.NET 10 for the platform and Manager changes.

**Primary Dependencies**: SDK: `pydantic` 2.x and `jsonschema` 4.x at runtime; `hatchling` and `pytest` in the locked
`dev` group; exact versions fixed by `src/analysts/sdk/uv.lock` (research R2). Platform and Manager: existing
dependencies only.

**Storage**: Files in the container mounts only; no database change, no migration, no OpenAPI change.

**Testing**: pytest for the SDK (conformance, writer, reader, entry point); xUnit v3 for the platform Contracts and
Integration tests and the Manager tests.

**Target Platform**: Linux x64 OCI Analyst containers (the SDK is pure Python and also runs on Windows and macOS for
author tests); the platform and Manager targets are unchanged.

**Project Type**: Internal library (SDK) plus additive contract, platform message, and Manager changes.

**Performance Goals**: Digests are computed streaming in fixed-size chunks; staged inputs are read once. No throughput
target beyond the existing execution timeouts.

**Constraints**: No network, environment variables, or credentials in the SDK; external dependencies only with
CPython 3.12 manylinux wheels; no shared uv workspace; existing outcome rules, staged-inputs layout, and acceptance
rules unchanged; previous contract versions stay valid.

**Scale/Scope**: One SDK project; five implemented contracts plus common definitions; result manifests with at most
256 artifacts and one fact artifact.

## Constitution Check

| Gate | Before research | After design |
| --- | --- | --- |
| I. Architecture authority | Pass: implements the planned Analyst SDK and fixes a container-interface gap the architecture leaves unstated | Pass: design updates applied to the owning narratives now, worded as decided (see Architecture Updates) |
| II. Source-area ownership | Pass: SDK under `src/analysts/sdk/` (approved Analysts area); platform and Manager changes in their own areas | Pass: no new first-level area; the SDK directory starts with its first meaningful artifacts |
| III. API-first, contract authority | Pass: JSON Schemas stay canonical; the SDK is a consumer | Pass: verified schema copy and conformance tests; additive contract versions; no data through the control plane |
| IV. Evidence over claims | Pass: SDK, contract, platform, and Manager tests required | Pass: no image, publication, or production claim; real capability execution stays out of scope |
| V. Focused changes | Pass: only the interface fields the SDK needs | Pass: no transport client, media handling, or capability |
| Environment prerequisite | Pass: [Analyst and Media Verification Environment](../archive/20261017-163801-analyst-media-environment/spec.md) (#143) provides CPython 3.12.15, `uv` 0.13.1, and locked verification of `src/analysts/sdk/` | Pass: all other SDK dependencies are project-locked wheels allowed by that environment's project contract; .NET and Docker already provided |

No violation; Complexity Tracking is empty.

## Architecture Updates

**Narratives**:

- [Analysts, Models, and Hardware → Implementation Profile](../../docs/architecture/analysts-models-and-hardware.md#implementation-profile):
  the Analyst SDK and boundary validation.
- [Analysts, Models, and Hardware → Result Manifest And Lineage](../../docs/architecture/analysts-models-and-hardware.md#result-manifest-and-lineage):
  what the Analyst writes into the result mount.
- [Analyst Runtime and Recovery → Runtime Preflight And Status](../../docs/architecture/analyst-runtime-and-recovery.md#runtime-preflight-and-status):
  the Analyst container interface.
- [Platform Implementation Profile → Source And Runtime Baseline](../../docs/architecture/platform-implementation.md#source-and-runtime-baseline)
  and [Analyst Technology](../../docs/architecture/platform-implementation.md#analyst-technology): the SDK's place and
  role.
- [Contracts and Compatibility → Scope And Dependencies](../../docs/architecture/contracts-and-compatibility.md#scope-and-dependencies)
  and its contract test paragraph: new and changed contracts and Python boundary validation.
- [Technology Stack → Analytics](../../docs/architecture/tenancy-and-technology.md#analytics): Pydantic models in the
  Analyst SDK (already stated; stays true).

**Design updates applied now** (worded as decided, not yet implemented; the documentation task turns them into
present tense):

- `analysts-models-and-hardware.md` (Implementation Profile): new paragraph defining the SDK project, its verified
  schema copy, validation, input verification, one fact artifact, window refusal, atomic writing, and exit statuses.
- `analyst-runtime-and-recovery.md` (Runtime Preflight And Status): new paragraph completing the container interface:
  verbatim `job.json`, analyst-job 1.1.0 workflow and segment window, read-only `/socalytics/attempt.json`.
- `contracts-and-compatibility.md` (Scope And Dependencies): the `analyst-attempt` contract and analyst-job 1.1.0.

**Status statements at merge** (each must become true for the merged state):

- `analysts-models-and-hardware.md`, Implementation Profile: "The shared internal Analyst SDK will expose Pydantic
  models and validate both boundaries; it and cross-runtime schema agreement remain future work." → the SDK exists and
  validates both boundaries; cross-runtime agreement is evidenced by the shared examples.
- `analysts-models-and-hardware.md`, Implementation Profile: the new "decided as follows and not yet implemented"
  paragraph → present tense.
- `analyst-runtime-and-recovery.md`, the new "completed as follows (decided, not yet implemented …)" paragraph →
  present tense, and the mount sentence before it names `/socalytics/attempt.json`.
- `platform-implementation.md`, Source And Runtime Baseline: "and the future Analyst SDK and Analyst capability
  implementations" → the SDK under `src/analysts/sdk`; capabilities remain future.
- `platform-implementation.md`, Analyst Technology: "The planned internal Analyst SDK exposes Pydantic models and
  validates input and output at the container boundary" → implemented.
- `platform-implementation.md`, Acceptance Evidence: "JSON Schema validation agrees at .NET and Python boundaries (the
  platform and the Analyst Manager already agree on result manifests at the .NET boundary)" → the Python boundary is
  evidenced by the SDK's conformance tests over the same examples; real Analyst images remain open.
- `contracts-and-compatibility.md`, contract tests: "The same fixture corpus will also be validated independently at
  .NET and Python boundaries." → done for the analysis contracts the SDK implements; Kiota Python clients stay planned.
- `contracts-and-compatibility.md`, Scope And Dependencies: "The Python Analyst SDK will add … (decided, not yet
  implemented)" → present tense.
- Any other statement found at implementation time that says no Analyst SDK exists or that the container receives
  only the job, inputs, result, and scratch mounts.

The documentation task MUST make the listed narratives and every status statement the feature affects true for the
merged state, re-reading `docs/architecture/` at implementation time; the rewrites above are examples, not the whole
job. It also updates `src/analysts/README.md`, `README.md`, and `AGENTS.md` (Current State, SDK commands, and the
schema-copy refresh rule).

## Project Structure

### Documentation (this feature)

```text
specs/20261017-220709-python-analyst-sdk/
├── spec.md
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── analysis-contracts.md
│   ├── container-interface.md
│   └── sdk-api.md
├── checklists/requirements.md
└── tasks.md                 # /speckit-tasks
```

### Source Code (repository root)

```text
contracts/
├── README.md                                            # index rows: analyst-attempt; analyst-job 1.1.0
└── analysis/
    ├── analyst-attempt/v1/                              # new schema, releases/1.0.0, examples
    └── analyst-job/v1/                                  # 1.1.0, releases/1.1.0, new examples

src/platform/
├── SocAlytics.Platform.Application/
│   ├── Abstractions/ContractSchemaIds.cs                # analyst-attempt id
│   └── Analysis/
│       ├── SegmentWindows.cs                            # new: segment id and nominal window helper
│       ├── RunReadinessCoordinator.cs                   # job payload 1.1.0: workflow, segmentWindow
│       └── Results/ResultManifestVerifier.cs            # uses SegmentWindows
├── SocAlytics.Platform.Infrastructure/Persistence/Analysis/UpstreamResultReader.cs   # uses SegmentWindows
└── Tests/
    ├── SocAlytics.Platform.Contracts.Tests/             # ContractCatalog, AnalysisExampleTests
    └── SocAlytics.Platform.Integration.Tests/Analysis/ReadinessProgressionTests.cs

src/analysts/manager/
├── SocAlytics.Analysts.Manager.Core/
│   ├── Work/ActiveAttempt.cs, AttemptRunner.cs          # verbatim job bytes, attempt.json
│   └── Runtime/AttemptExecution.cs, Docker/DockerLaunchSpec.cs   # attempt file mount
└── Tests/SocAlytics.Analysts.Manager.Tests/
    ├── Work/AttemptDirectoryTests.cs
    └── Runtime/Docker/DockerLaunchSpecTests.cs, Runtime/SimulatedRuntimeTests.cs

src/analysts/sdk/                                        # new independent project
├── pyproject.toml, uv.lock, README.md, .gitignore
├── scripts/sync_contracts.py
├── examples/minimal_analyst.py                          # documented example (SC-004)
├── src/socalytics_analyst/
│   ├── __init__.py, errors.py, entrypoint.py, context.py
│   ├── schemas.py                                       # offline registry and validation
│   ├── models/ (__init__.py, common.py, job.py, attempt.py, upstream.py, manifest.py, facts.py)
│   ├── inputs.py                                        # staged-input loading and verification
│   ├── result.py                                        # ResultWriter and atomic finalization
│   └── contracts/                                       # verified schema copy (research R3)
└── tests/
    ├── conftest.py, fixtures/mounts.py
    ├── test_package.py, test_contract_copy.py, test_schemas.py
    ├── test_contract_examples.py, test_model_schema_fields.py
    ├── test_job_and_attempt.py, test_result_writer.py, test_entrypoint.py
    └── test_inputs.py, test_result_coverage.py
```

**Structure Decision**: The SDK is a new independent project at the path the environment feature recognizes; platform
and Manager changes stay in their existing layers and folders. `src/analysts/README.md` gains the SDK as executable.

## Complexity Tracking

No violations.
