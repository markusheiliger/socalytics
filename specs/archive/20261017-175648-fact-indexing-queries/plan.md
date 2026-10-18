# Implementation Plan: Accepted Fact Indexing and Capability-Scoped Fact Queries

**Branch**: `20261017-175648-fact-indexing-queries` | **Date**: 2026-10-17 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/archive/20261017-175648-fact-indexing-queries/spec.md`

## Summary

Turn accepted Analyst results into queryable facts and let authorized members
read them per run and capability.

- **Contract**: a new `fact-artifact` v1 schema in `contracts/analysis/` for
  the platform-owned fact envelope; fact artifacts are the result artifacts
  with media type `application/vnd.socalytics.facts+json`.
- **Platform (`src/platform`)**:
  - **Validation**: `ResultManifestVerifier` gains a twelfth, ordered check
    that reads every fact artifact (outside the unit of work, like the
    manifest), validates it against the contract, matches its capability,
    schema version, and output type to the manifest and node snapshot, and
    checks ranges, the declared fact count, and the configured limits. Failures
    end the attempt with new named reasons.
  - **Indexing**: `CompleteAttemptHandler` inserts the parsed facts into a new
    immutable `analysis_indexed_facts` table in the same unit of work that
    inserts the accepted result and its manifest record, so acceptance and
    indexing are atomic; the existing completion idempotency makes it
    idempotent.
  - **Member reads**: `GET /api/v1/analysis/runs/{runId}/fact-streams` and
    `GET /api/v1/analysis/runs/{runId}/facts`, mapped with `MapMemberApi()`,
    authorized through `IAccessAuthorizer` with the existing `analysis-run`
    team scope (`TeamPermission.Read`), with keyset pagination in time order,
    coverage windows derived from the run's logical jobs, and provenance from
    the node snapshot; weak ETags as for `getAnalysisRun`.
- **Analyst Manager (`src/analysts/manager`)**: the simulated Analyst writes a
  deterministic fact artifact in its valid mode and gains an `invalid-facts`
  result mode; nothing else in the Manager changes.
- **Evidence**: contract tests, verifier and indexing integration tests, member
  endpoint tests (ordering, paging, ranges, coverage, provenance, run
  isolation, authorization), the OpenAPI snapshot, and an end-to-end read-back
  of the development workflow's facts.

Design rationale: [research.md](research.md). Entities and mappings:
[data-model.md](data-model.md). Contracts: [contracts/](contracts/).
Validation: [quickstart.md](quickstart.md).

## Technical Context

**Language/Version**: C# on .NET 10 (SDK pinned by `src/platform/global.json`
and `src/analysts/manager/global.json`); nullable enabled, warnings as errors.

**Primary Dependencies**: existing `JsonSchema.Net` through
`JsonSchemaContractValidator` (`IContractValidator`), `IObjectStorage.ReadObjectAsync`,
Dapper and Npgsql, DbUp migrations, `IAccessAuthorizer`/`ITeamScopeSource`
(`AnalysisRunScopeSource`), `MapMemberApi()`, `ProblemResults`, the built-in
OpenAPI document; Manager `SimulatedResultWriter`. No new packages.

**Storage**: one new forward-only migration
`NNNN_analysis_indexed_facts.sql` (number assigned at implementation as the
next free number on an up-to-date default branch) creating the immutable table
`socalytics.analysis_indexed_facts` with an immutability trigger and revoked
`UPDATE`/`DELETE`; it has no `version` column and gets an entry in
`PersistedTableClassifications`. Object storage is read once per fact artifact
during acceptance.

**Testing**: xUnit v3; `SocAlytics.Platform.Contracts.Tests` (schema, examples,
released copy, OpenAPI), `SocAlytics.Platform.Integration.Tests` (PostgreSQL,
RustFS, NATS containers), `SocAlytics.Platform.Host.Tests` (`OpenApiSnapshotTests`),
`SocAlytics.Platform.Architecture.Tests`, `SocAlytics.Platform.EndToEnd.Tests`,
and `SocAlytics.Analysts.Manager.Tests`.

**Target Platform**: platform in Linux containers through the Aspire AppHost;
Manager simulated runtime on any supported host; automated verification on
Linux.

**Project Type**: distributed system (web service plus desktop/server worker).

**Performance Goals**: no load targets (production limits out of scope); a
page of at most 500 facts is served by one indexed range scan.

**Constraints**:

- Acceptance and indexing are one unit of work (FR-007); fact artifacts are
  read and validated before it, like the manifest.
- Development limits: 16 MiB per fact artifact, 16 KiB per payload, 50,000
  facts per result, page size default 100 and maximum 500 (FR-004, FR-011),
  configurable and validated at start.
- Responses expose no storage, segment, recording, mapping, or policy
  identifiers (FR-016) and never mix runs (FR-017).
- The control plane handles metadata only; payloads are returned as stored.

**Scale/Scope**: one contract, one migration, two member operations, one
verifier check, one simulated-Analyst change.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Evidence |
| --- | --- | --- |
| I. Architecture is the design authority | Pass | Implements [Segment And Match Analysis Scope](../../../docs/architecture/match-data-pipeline.md#segment-and-match-analysis-scope), [Result Manifest And Lineage](../../../docs/architecture/analysts-models-and-hardware.md#result-manifest-and-lineage), and [Capability-Scoped Result Access](../../../docs/architecture/analysts-models-and-hardware.md#capability-scoped-result-access); the member read and the fact-artifact contract are added to the narratives now (see Architecture Updates). |
| II. Source-area ownership | Pass | Platform code in `src/platform/`, contract in `contracts/analysis/`, simulated Analyst in `src/analysts/manager/`; no new `src/` child. |
| III. API-first control plane | Pass | Members read only through `/api/v1`; the fact envelope is a JSON Schema 2020-12 contract; no video bytes; payloads are metadata. |
| IV. Evidence over claims | Pass | Contract, integration, host, end-to-end, and Manager tests (FR-024); nothing is presented as production readiness. |
| V. Focused, minimal changes | Pass | Reuses verifier, completion, authorization, paging, and coverage patterns; no new packages or projects. |
| Technology and tooling | Pass | Needs only .NET 10, Docker-backed PostgreSQL, RustFS, and NATS, and SoftHSM2 for the existing end-to-end tests, all provided by `.github/actions/environment-setup` and `environment-verify`; no environment spec is needed. |

Post-design re-check: Pass (no violations; Complexity Tracking is empty).

## Architecture Updates

### Narratives

- [Match Data Pipeline → Segment And Match Analysis Scope](../../../docs/architecture/match-data-pipeline.md#segment-and-match-analysis-scope): fact indexing at acceptance and the ordered, match-wide fact stream, now also read by members.
- [Analysts, Models, and Hardware → Result Manifest And Lineage](../../../docs/architecture/analysts-models-and-hardware.md#result-manifest-and-lineage): fact artifacts, their validation, and indexing in stamp metadata.
- [Analysts, Models, and Hardware → Capability-Scoped Result Access](../../../docs/architecture/analysts-models-and-hardware.md#capability-scoped-result-access): the member-facing capability-scoped fact read with coverage and provenance.
- [Contracts And Compatibility → Contract Authority and Scope And Dependencies](../../../docs/architecture/contracts-and-compatibility.md#scope-and-dependencies): the new `fact-artifact` contract.
- [Platform Implementation Profile → Data Organization and Acceptance Evidence](../../../docs/architecture/platform-implementation.md#data-organization): indexed facts as Analysis data and the new evidence.
- [Job Processing](../../../docs/architecture/job-processing.md) step 4 and the completion sequence ("Accept completion and index facts"), which become true without design change.

### Design updates (applied now, worded as decided but not implemented)

- `analysts-models-and-hardware.md`, Result Manifest And Lineage: fact artifacts are result artifacts with the media type `application/vnd.socalytics.facts+json` following the `fact-artifact` contract; the API validates their envelope (not capability-specific payload fields), ranges inside the validity window, the declared fact count, and size limits, and stores each fact in the same transaction as the acceptance. Implementation turns "is decided" into present tense.
- `analysts-models-and-hardware.md`, Capability-Scoped Result Access: members with team read access read a run's facts per capability and schema version with coverage as match-time windows and run-pinned provenance, without segment or storage identifiers. Implementation turns it into present tense.
- `match-data-pipeline.md`, Segment And Match Analysis Scope: the ordered fact stream is also exposed to authorized members through the member API; high-level ACs keep receiving their inputs through Manager staging. Implementation turns it into present tense.
- `contracts-and-compatibility.md`, Scope And Dependencies: the planned `fact-artifact` envelope under `contracts/analysis/`. Implementation turns it into present tense.

### Status statements at merge

- `analysts-models-and-hardware.md`, Result Manifest And Lineage: "fact indexing remains future work." → fact validation and indexing are implemented as development evidence against the simulated Analyst; capability-specific payload validation remains future work.
- `match-data-pipeline.md`, Segment And Match Analysis Scope: "The API validates accepted low-level result manifests (implemented) and indexes their facts (future)" → indexing implemented; the member read implemented as development evidence.
- `analysts-models-and-hardware.md`, Capability-Scoped Result Access: the implementation-status sentence ("This Manager-mediated read, the staging, and the coverage are implemented …") gains the member fact read.
- `platform-implementation.md`, Data Organization: "Analysis data covers durable runs, DAG state, logical jobs, attempts, and accepted-result references." → also indexed facts.
- `platform-implementation.md`, Acceptance Evidence (current evidence list) and Architecture Reassessment ("result write grants with validated result acceptance … and the Manager-mediated read and staging of accepted upstream results are implemented"): add fact indexing and the member fact reads; the Web UI analysis review remains unimplemented.
- `contracts-and-compatibility.md`, Scope And Dependencies: "Analysis Result Read for Jobs added the upstream-results envelope …" → add the fact-artifact contract.
- `terminology-and-principles.md`: "High-level ACs obtain facts through match-scoped API queries" stays the target design; re-check that it does not read as implemented for ACs, since ACs still receive staged inputs.
- `client-applications.md`: "(analysis review and agent conversations are not implemented yet)" stays true (no Web UI in this feature); re-check at merge.

The documentation task MUST make the listed narratives and every status
statement this feature affects true for the merged state, re-reading
`docs/architecture/` (and `AGENTS.md` "Current State", `README.md`, and
`contracts/README.md`) at implementation time; the rewrites above are examples,
not the whole job.

## Project Structure

### Documentation (this feature)

```text
specs/archive/20261017-175648-fact-indexing-queries/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output
│   ├── fact-artifact.md
│   └── member-fact-api.md
└── tasks.md             # Phase 2 output (/speckit-tasks)
```

### Source Code (repository root)

```text
contracts/
├── README.md                                        # index row for fact-artifact
└── analysis/fact-artifact/v1/
    ├── fact-artifact.schema.json                    # 1.0.0
    ├── releases/1.0.0.schema.json
    └── examples/{valid,invalid}/

src/platform/
├── SocAlytics.Platform.Domain/Analysis/
│   └── FactRange.cs                                 # half-open range, overlap, containment
├── SocAlytics.Platform.Application/
│   ├── Abstractions/ContractSchemaIds.cs            # FactArtifactV1
│   └── Analysis/
│       ├── Results/ResultManifestVerifier.cs        # check 12: facts; new reasons
│       ├── Results/AnalysisResultOptions.cs         # fact limits
│       ├── Commands/CompleteAttempt.cs              # insert facts in the acceptance unit of work
│       ├── Stores/IIndexedFactStore.cs
│       └── Facts/
│           ├── FactReadOptions.cs                   # page sizes
│           ├── IFactQueries.cs
│           ├── FactCoverageBuilder.cs
│           ├── ListRunFacts.cs
│           └── ListRunFactStreams.cs
├── SocAlytics.Platform.Infrastructure/Persistence/
│   ├── Analysis/IndexedFactStore.cs
│   ├── Analysis/FactQueries.cs
│   └── Migrations/NNNN_analysis_indexed_facts.sql
├── SocAlytics.Platform.AppHost/Program.cs            # Analysis:Results and Analysis:Facts development values
├── SocAlytics.Platform.Api/
│   ├── Analysis/AnalysisFactEndpoints.cs
│   ├── Program.cs                                   # endpoint mapping
│   └── openapi/v1.json                              # refreshed snapshot
└── Tests/
    ├── SocAlytics.Platform.Contracts.Tests/         # fact-artifact examples
    ├── SocAlytics.Platform.Integration.Tests/Analysis/Facts/
    ├── SocAlytics.Platform.Integration.Tests/Analysis/Results/   # verifier and acceptance cases
    ├── SocAlytics.Platform.Integration.Tests/Structure/PersistedTableClassifications.cs
    └── SocAlytics.Platform.EndToEnd.Tests/FactReadEndToEndTests.cs

src/analysts/manager/
├── SocAlytics.Analysts.Manager.Core/Runtime/Simulated/
│   ├── SimulatedResultWriter.cs                     # deterministic facts
│   └── SimulatedAnalystOptions.cs                   # SimulatedResultModes: invalid-facts
└── Tests/SocAlytics.Analysts.Manager.Tests/Runtime/Simulated/SimulatedResultModeTests.cs
```

**Structure Decision**: the layered platform monolith keeps the Analysis area
as folders in each layer; the new `Facts` folder sits beside `Results`. The
contract follows the `contracts/<area>/<name>/v<major>/` convention. The
Manager change stays in the simulated runtime.

## Complexity Tracking

No violations.
