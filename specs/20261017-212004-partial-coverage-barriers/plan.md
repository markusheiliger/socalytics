# Implementation Plan: Partial-Coverage Match Barriers

**Branch**: `20261017-212004-partial-coverage-barriers` | **Date**: 2026-10-17 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261017-212004-partial-coverage-barriers/spec.md`

## Summary

Let a match-scoped capability opt into partial upstream coverage per dependency and carry that decision through the
whole analysis path: Registry import, run snapshot, readiness, input snapshot, the Manager's upstream read and staging,
result acceptance, and the member run status read.

- **Declaration**: `analyst-manifest` 1.1.0 adds an optional `coverage` object to each `upstream` entry
  (`{ "mode": "partial", "minimumAcceptedFraction"?: number }`); absence means complete coverage. The Registry's
  `AnalystManifestValidator` accepts 1.0.0 and 1.1.0 and refuses partial coverage on segment-scoped capabilities, on
  `optional` dependencies, or with a fraction outside `(0, 1]` (refusal `manifest-invalid` with the validation error `partial-coverage` at `/upstream/<i>/coverage`).
  `ConfiguredWorkflowOptions` edges gain an optional `Coverage` so the development seed writes the same declaration.
- **Run snapshot**: `RegistryWorkflowDefinitionResolver` and `ConfiguredWorkflowDefinitionResolver` attach the
  dependent node's declaration to each resolved edge (matched by upstream capability id); `WorkflowSnapshotMapper`
  writes it into the run snapshot JSON (only for partial edges, so digests of unchanged workflows stay stable) and run
  creation stores it in two new columns of `analysis_dependency_edges` (`coverage_mode`, `minimum_accepted_fraction`).
- **Readiness** (`ReadinessEvaluator`, Domain): an edge is a *partial barrier* when its coverage mode is `partial`, the
  upstream node is segment-scoped, and the dependent node is match-scoped. A failed or blocked upstream node never
  blocks through a partial barrier; the barrier is satisfied when every upstream segment job is final (`Completed`,
  `Failed`, `Blocked`) and `accepted >= max(1, ceil(fraction × expected))`; when all are final and the minimum is not
  met, the dependent node and job become blocked with the new failure reason `upstream-coverage-below-minimum`.
  `Satisfiable` and therefore `RunOutcomePolicy` see a partial barrier as progressable while upstream jobs are not
  final. Run outcomes are otherwise unchanged: a failed segment node keeps the run from `Completed`.
- **Input snapshot**: `InputSnapshotEntry` gains `CoverageMode` and, for partial barriers, the ordered
  `Coverage` entries (segment id, state `accepted`/`failed`/`blocked`); its status is `accepted` when every segment was
  accepted and the new `partial` otherwise. Stored in the existing `analysis_logical_job_inputs.inputs` JSON.
- **Upstream read**: `ListUpstreamResults` takes partial-barrier coverage from the snapshot (not from current job
  state) and `upstream-results` 1.1.0 adds an optional `upstream.coverage` (`mode`, `minimumAcceptedFraction`). The
  Manager's `UpstreamStager` writes the mode into each `coverage.json` next to the coverage it already stages.
- **Result reporting and acceptance**: `result-manifest` 1.1.0 adds an optional `inputCoverage` array (per dependency:
  capability, schema version, and units with state). `ResultManifestVerifier` accepts both versions and adds the check
  `result-coverage-mismatch`: required for every partial barrier of the job and then equal to the snapshot; for other
  dependencies optional, with only its `accepted` units checked against the snapshot's accepted results. The Manager's
  own contract check accepts 1.1.0.
- **Run status read**: `AnalysisNodeStatusView`/`AnalysisNodeStatusDto` gain an additive nullable `inputCoverage` list
  (per upstream node key: `coverageMode`, `minimumAcceptedFraction`, and counts per state) computed from the node's job
  input snapshots; refreshed OpenAPI snapshot. The fact reads of the fact-indexing feature are untouched.
- **Simulation and seed**: `SimulatedCapabilityOptions` gains `FailSegmentStartsMs` (fail only the listed segment
  windows) and result modes `coverage-mismatch` and `coverage-omitted`; `SimulatedResultWriter` writes `inputCoverage`
  from the staged `coverage.json` files. `appsettings.Development.json` adds the requested node
  `dev-partial-match-node` (capability `dev-partial-match-capability`) with a `required` partial-coverage edge from
  `dev-segment-node` and no fraction.
- **Telemetry**: `AnalysisMetrics` gains counters `socalytics.analysis.partial_barriers_ready` and
  `socalytics.analysis.partial_barriers_blocked`, and coverage rejections are counted by the existing
  `completions_rejected` counter with the new reason; no new audit event.

Design rationale: [research.md](research.md). Data changes: [data-model.md](data-model.md). Contracts:
[contracts/](contracts/). Validation: [quickstart.md](quickstart.md).

## Technical Context

**Language/Version**: C# on .NET 10 (SDK pinned by `src/platform/global.json` and `src/analysts/manager/global.json`).

**Primary Dependencies**: existing platform stack (ASP.NET Core minimal APIs, Dapper and Npgsql, DbUp, NATS JetStream,
JSON Schema contract validation, OpenTelemetry metrics through `AnalysisMetrics`) and the existing Analyst Manager
Core (`UpstreamStager`, `AttemptRunner`, simulated runtime). No new package, framework, or tool.

**Storage**: PostgreSQL through one forward-only migration `NNNN_analysis_partial_coverage.sql` (number assigned at
implementation time as the next free number): `coverage_mode` (`complete` default, `partial`) and
`minimum_accepted_fraction` on `analysis_dependency_edges` with checks, and the extended failure-reason check of
`analysis_logical_jobs`. No new table, so `PersistedTableClassifications` does not change. Declarations stay in the
existing `analyst_capability_declaration.manifest` JSON; input snapshots in `analysis_logical_job_inputs.inputs`.

**Testing**:

- `SocAlytics.Platform.Contracts.Tests`: the three 1.1.0 schemas, their released 1.0.0 baselines and same-major
  compatibility, and new valid and invalid examples.
- `SocAlytics.Platform.Integration.Tests` (Docker): `Analysis/Domain/ReadinessEvaluatorTests` (waiting, satisfied,
  below minimum, fraction rounding, blocked upstream, all accepted, several dependencies, conditional, match-scoped
  upstream, outcome), Registry import and seed validation, run creation copying, readiness persistence and recovery,
  upstream read, acceptance and rejection, run status read, telemetry, and the migration.
- `SocAlytics.Platform.Host.Tests`: `OpenApiSnapshotTests` with the refreshed
  `src/platform/SocAlytics.Platform.Api/openapi/v1.json`.
- `SocAlytics.Analysts.Manager.Tests`: staging of the coverage mode, the simulated segment failures and coverage
  result modes, and the Manager's contract check of 1.1.0 manifests.
- `SocAlytics.Platform.EndToEnd.Tests` (Docker; SoftHSM2 on Linux): the development workflow with one failed segment.

**Target Platform**: Linux containers for the platform in development and test (Aspire AppHost); the Analyst Manager
Core with its simulated runtime.

**Project Type**: web service (platform monolith) plus the Analyst Manager worker.

**Performance Goals**: readiness stays one serialized evaluation per run; a partial barrier adds one pass over the
upstream node's segment jobs (bounded by the run's segment count). No new production value is adopted.

**Constraints**:

- Complete-coverage behavior, existing edge semantics, and node-level blocking between segment-scoped capabilities stay
  unchanged (FR-006).
- Input snapshots and accepted results are immutable; reads never cross runs.
- Contract changes are additive minor versions; released 1.0.0 baselines stay as immutable copies; `/api/v1` changes
  are additive.
- The migration must not change the digests or behavior of runs created before it (`coverage_mode` defaults to
  `complete`).
- The simulated runtime and the development seed stay Development and Test only.

**Scale/Scope**: one migration, three contract minor versions, Domain readiness changes, Registry validation and
resolution, run creation, upstream read, acceptance, one read model, Manager staging and simulated runtime changes, the
development workflow configuration, and tests across five test projects.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Evidence |
| --- | --- | --- |
| I. Architecture is the design authority | Pass | Implements the partial-coverage sentence of [Readiness And Dependencies](../../docs/architecture/job-processing.md#readiness-and-dependencies), the declaration of [Capability Declaration](../../docs/architecture/analysts-models-and-hardware.md#capability-declaration) and coverage of [Capability-Scoped Result Access](../../docs/architecture/analysts-models-and-hardware.md#capability-scoped-result-access), and follows [Versioning And Compatibility](../../docs/architecture/contracts-and-compatibility.md#versioning-and-compatibility); the decided details are applied to those narratives now (see Architecture Updates). |
| II. Source-area ownership | Pass | Changes under `src/platform/` and `src/analysts/manager/`, contracts under `contracts/analysis/`; no new `src/` child. |
| III. API-first control plane | Pass | The declaration travels in the contract-owned Analyst manifest; coverage reaches the Manager through the existing upstream read and members through the existing `/api/v1` run status read; no media passes the control plane. |
| IV. Evidence over claims | Pass | Contract, integration, host, Manager, and end-to-end tests (FR-021); nothing claims production readiness. |
| V. Focused, minimal changes | Pass | No new table, project, package, or endpoint; extends existing readiness, snapshot, read, and verifier code; the seed gains one node to exercise the behavior. |
| Technology and tooling | Pass | Needs the .NET SDK, Docker, and SoftHSM2 on Linux for end-to-end tests, all provided by `.github/actions/environment-setup` and `environment-verify`; no environment spec is needed. |

Post-design re-check: Pass (no violations; Complexity Tracking is empty).

## Architecture Updates

**Narratives**:

- [Job Processing → Readiness And Dependencies](../../docs/architecture/job-processing.md#readiness-and-dependencies):
  partial-coverage barriers (readiness rule, minimum, blocking, snapshot).
- [Job Processing → Run State And Recovery](../../docs/architecture/job-processing.md#run-state-and-recovery): run
  outcome with a match-level result built from partial coverage.
- [Analysts, Models, and Hardware → Capability Declaration](../../docs/architecture/analysts-models-and-hardware.md#capability-declaration):
  the per-dependency partial-coverage declaration and minimum.
- [Analysts, Models, and Hardware → Result Manifest And Lineage](../../docs/architecture/analysts-models-and-hardware.md#result-manifest-and-lineage):
  the reported input coverage and its acceptance check.
- [Analysts, Models, and Hardware → Capability-Scoped Result Access](../../docs/architecture/analysts-models-and-hardware.md#capability-scoped-result-access):
  snapshot-fixed coverage in the Manager's read and staging; members' input coverage in the run status read.
- [Contracts And Compatibility → Versioning And Compatibility](../../docs/architecture/contracts-and-compatibility.md#versioning-and-compatibility):
  followed unchanged for the three additive minor versions (no text change expected).
- [Platform Implementation](../../docs/architecture/platform-implementation.md): its list of implemented analysis
  evidence (development evidence summary).

**Design updates** (applied now, worded as decided and not yet implemented; the documentation task turns them into
present tense):

- `job-processing.md` → Readiness And Dependencies: a new paragraph after "Match-scoped foundational jobs wait…"
  decides the rule: the declaration is per dependency of a match-scoped capability on a segment-scoped upstream with a
  minimum of at least one accepted segment and an optional minimum accepted fraction; the barrier waits until every
  expected upstream segment job is final; failed or blocked upstream nodes do not block through it; below the minimum
  the dependent is blocked; the input snapshot records each segment's state; segment-to-segment dependencies keep
  node-level blocking; and a run whose match-level result used partial coverage never ends `Completed`.
- `analysts-models-and-hardware.md` → Capability Declaration: a bullet for the optional per-dependency partial-coverage
  declaration and its minimum (decided, not yet implemented).
- `analysts-models-and-hardware.md` → Result Manifest And Lineage: a bullet and sentence for the manifest's input
  coverage report, which acceptance compares with the job's input snapshot (decided, not yet implemented).
- `analysts-models-and-hardware.md` → Capability-Scoped Result Access: the Manager's read reports, for partial-coverage
  dependencies, the coverage recorded in the input snapshot and the coverage mode, and the member run status read
  states each node's input coverage counts (decided, not yet implemented).

**Status statements at merge** (the merged feature makes these false or incomplete):

- `job-processing.md` → Readiness And Dependencies, the new paragraph "Partial-coverage barriers are decided as follows
  and not yet implemented." → present tense, implemented as development evidence against the simulated Analyst.
- `analysts-models-and-hardware.md` → Capability Declaration, Result Manifest And Lineage, and Capability-Scoped Result
  Access: the "(decided, not yet implemented)" wording added for partial coverage → implemented.
- `analysts-models-and-hardware.md` → Result Manifest And Lineage, "The result write grants, the result-manifest
  contract, and this validation are implemented as development evidence…" → also names the coverage check.
- `platform-implementation.md`: the evidence bullet "integration and end-to-end tests cover the Manager-mediated
  upstream read (current-attempt and input-snapshot scoping, refusals without disclosure, per-object read grants,
  coverage), digest-verified staging…" → also names partial-coverage barriers, snapshot-fixed coverage, and the
  coverage check of result acceptance as development evidence.
- `job-processing.md` → Readiness And Dependencies, scheduler step 4 "Waits until accepted, API-indexed foundational
  results exist at the declared versions for all required match segments" → still true for complete-coverage
  dependencies; at merge it must name the partial-coverage exception or link to the partial-coverage paragraph.
- `AGENTS.md` "Current State" (not under `docs/architecture/`, kept consistent): the analysis evidence sentence →
  mentions partial-coverage match barriers.
- The archived Durable Analysis Workflow spec's deferral ("capability-permitted partial-coverage barriers … deferred")
  is history and stays unchanged.

**Documentation task requirement**: `/speckit-tasks` MUST generate an outcome-based documentation task that makes the
listed narratives and every status statement this feature affects anywhere in `docs/architecture/` true for the merged
state, re-reading the documents at implementation time; the rewrites listed above are examples, not the whole job.

## Project Structure

### Documentation (this feature)

```text
specs/20261017-212004-partial-coverage-barriers/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output
│   ├── analysis-contracts.md
│   └── run-status-input-coverage.md
└── tasks.md             # Phase 2 output (/speckit-tasks)
```

### Source Code (repository root)

```text
contracts/
├── README.md                                         # versions 1.1.0
└── analysis/
    ├── analyst-manifest/v1/                          # schema 1.1.0, releases/1.1.0.schema.json, examples
    ├── result-manifest/v1/                           # schema 1.1.0, releases/1.1.0.schema.json, examples
    └── upstream-results/v1/                          # schema 1.1.0, releases/1.1.0.schema.json, examples

src/platform/
├── SocAlytics.Platform.Domain/Analysis/
│   ├── CoveragePolicy.cs                             # new: mode, minimum fraction, minimum accepted count
│   ├── ReadinessEvaluator.cs                         # partial barriers, snapshot coverage
│   ├── RunGraphState.cs                              # edge coverage policy
│   └── AnalysisPersistedNames.cs                     # coverage mode and failure reason names
├── SocAlytics.Platform.Domain/Registry/CapabilityDeclaration.cs  # DeclaredUpstream coverage policy
├── SocAlytics.Platform.Application/
│   ├── Analysis/WorkflowSnapshotMapper.cs            # edge coverage in snapshot
│   ├── Analysis/RunReadinessCoordinator.cs           # blocked reason, metrics
│   ├── Analysis/Results/CoverageBuilder.cs           # snapshot-fixed coverage
│   ├── Analysis/Results/ListUpstreamResults.cs       # coverage mode in the read
│   ├── Analysis/Results/ResultManifestVerifier.cs    # 1.1.0 and coverage check
│   ├── Analysis/Queries/AnalysisRunViews.cs          # node input coverage
│   ├── Registry/ResolvedWorkflowDefinition.cs        # edge coverage
│   └── Telemetry/AnalysisMetrics.cs                  # partial-barrier counters
├── SocAlytics.Platform.Infrastructure/
│   ├── Persistence/Migrations/NNNN_analysis_partial_coverage.sql
│   ├── Persistence/Analysis/{AnalysisRunStore,LogicalJobStore,UpstreamResultReader,AnalysisRunQueries}.cs
│   └── Registry/{AnalystManifestValidator,RegistryWorkflowDefinitionResolver,ConfiguredWorkflowDefinitionResolver,ConfiguredWorkflowSeeder}.cs  # ConfiguredWorkflowOptions lives in the resolver file
├── SocAlytics.Platform.Api/
│   ├── Analysis/AnalysisRunEndpoints.cs              # inputCoverage on node status
│   ├── appsettings.Development.json                  # dev-partial-match-node
│   └── openapi/v1.json                               # refreshed snapshot
└── Tests/
    ├── SocAlytics.Platform.Contracts.Tests/
    ├── SocAlytics.Platform.Integration.Tests/Analysis/ and Registry/
    ├── SocAlytics.Platform.Host.Tests/
    └── SocAlytics.Platform.EndToEnd.Tests/

src/analysts/manager/
├── SocAlytics.Analysts.Manager.Core/Work/{UpstreamStager,AttemptRunner}.cs
├── SocAlytics.Analysts.Manager.Core/Runtime/Simulated/{SimulatedAnalystOptions,SimulatedRuntimeAdapter,SimulatedResultWriter}.cs
└── Tests/SocAlytics.Analysts.Manager.Tests/
```

**Structure Decision**: The layered platform monolith keeps its layers: the rule lives in the Domain readiness
evaluator, orchestration and reads in Application, persistence and Registry in Infrastructure, and the DTO in Api. The
Manager changes stay in its Core project. Contracts change in their existing major-version directories.

## Complexity Tracking

No violations.
