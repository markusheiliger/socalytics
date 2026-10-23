# Implementation Plan: Analysis Run Supersession

**Branch**: `20261017-183953-analysis-run-supersession` | **Date**: 2026-10-17 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/archive/20261017-183953-analysis-run-supersession/spec.md`

## Summary

When the run for a newer finalized recording-set version of a match is
created, supersede the match's older runs: cancel those still `Requested` or
`Running` through the Analysis Run Cancellation feature's effects with cause
`superseded` and no member, record an immutable supersession for every older
run, and expose current and superseded runs in the run reads and the Web UI's
match run list.

- **Version order**: `analysis_runs.recording_set_finalized_at` (copied from
  the recording-set lineage at creation, backfilled by the migration) and the
  recording-set version id give one deterministic per-match order; the
  current run is the first run of the match in the order
  `(recording_set_finalized_at, recording_set_version_id, created_at, id)`
  descending.
- **Supersession record**: new insert-only table
  `analysis_run_supersessions` (superseded run, superseding run, match, time,
  previous state), `Immutable` in `PersistedTableClassifications`; it never
  changes the superseded run's `version`, so the cancellation feature's
  idempotency rule stays valid.
- **One rule** (`RunSupersession.ApplyAsync(matchId)` in the Analysis
  application folder): under a per-match transaction advisory lock, every run
  without a supersession row whose version is older than the current run's
  version is superseded by the current run. Run creation
  (`HandleRecordingsFinalizedNotification`) calls it after inserting the run
  and runs readiness only when the new run is still `Requested`; the analysis
  reconciliation calls it per match before resetting ready work.
- **Cancellation reuse**: the cancellation feature's in-transaction effects
  are factored into `RunCancellationEffects` and called with cause
  `superseded`; a new `analysis_runs.cancellation_cause` (`member` or
  `superseded`) replaces the "cancelled ⇔ member" check. Audit event
  `analysis.run-superseded` (system actor) and one `analysis.run-state-changed`
  message only for active runs; a new `runs_superseded` counter.
- **Reads**: additive `isCurrent`, `supersededBy { runId,
  recordingSetVersionId, recordingSetFinalizedAt }`, `supersededAt`, and
  `cancellationCause` on `AnalysisRunSummaryDto` and `AnalysisRunStatusDto`;
  refreshed OpenAPI snapshot.
- **Web UI**: `AnalysisRunStates` marks the current run and superseded runs,
  shows a superseded cancellation without a member; `analysisRunAdapter` maps
  the new fields.
- **Manager**: no change; the existing obsolete-call handling stops and
  cleans up superseded attempts.

Design rationale: [research.md](research.md). Data changes:
[data-model.md](data-model.md). Contracts: [contracts/](contracts/).
Validation: [quickstart.md](quickstart.md).

## Technical Context

**Language/Version**: C# on .NET 10 (SDK pinned by `src/platform/global.json`)
for the platform; TypeScript 5.9 (strict) on Node.js 24 for the Web UI.

**Primary Dependencies**: existing platform stack (ASP.NET Core minimal APIs,
Dapper and Npgsql, DbUp, NATS JetStream client, OpenTelemetry metrics through
`AnalysisMetrics`); existing Web UI stack (React 19, React Router 7, TanStack
Query 5, MUI 7, i18next, `@microsoft/kiota-bundle`, Vitest 4, Playwright
1.63.0, `@axe-core/playwright`). No new package.

**Storage**: PostgreSQL through one forward-only migration
`NNNN_analysis_run_supersession.sql` (number assigned at implementation time,
after the cancellation feature's migration): `recording_set_finalized_at`
and `cancellation_cause` on `analysis_runs` with backfills and replaced
cancellation checks, `uq_analysis_runs_id_match`, the ordering index, and the
new immutable table `analysis_run_supersessions` (new
`PersistedTableClassifications` entry).

**Testing**:

- `SocAlytics.Platform.Integration.Tests` (Docker: PostgreSQL, NATS, object
  storage): supersession of active and ended runs, cancellation effects and
  fencing, audit and outbox, metrics, version order and tie-break, late and
  duplicate events, concurrent finalizations and member cancellation, same
  version and other matches, reconciliation, read fields and current run,
  readable results of superseded runs, and the table classification.
- `SocAlytics.Platform.Host.Tests`: `OpenApiSnapshotTests` with the refreshed
  `src/platform/SocAlytics.Platform.Api/openapi/v1.json`.
- `SocAlytics.Platform.EndToEnd.Tests`: supersede a running run while the
  simulated runtime holds an active attempt.
- Web UI: `npm test` (adapter mapping, run-list markers),
  `npm run test:e2e -- --project simulated` (`run-supersession.spec.ts`), and
  a chained platform project after the cancellation feature's
  `platform-cancellation` project.

**Target Platform**: Linux containers for the platform in development and
test (Aspire AppHost); Chromium for the Web UI tests.

**Project Type**: web service (platform monolith) plus single-page web
application.

**Performance Goals**: supersession is one transaction per match over its
runs (typically a handful); the current-run answer uses one indexed lookup per
match; the Manager stops a superseded attempt within two heartbeat intervals
(SC-007), whose production value stays unresolved.

**Constraints**:

- Accepted results, manifests, facts, and lineage are never changed or
  deleted; reads never cross runs (FR-017).
- The `run-state-changed` contract and all Manager contracts stay unchanged.
- `/api/v1` additions are additive.
- Lock order is always the match advisory lock, then run locks in id order;
  paths that do not supersede never take the match lock.
- Production heartbeat and timeout values and the retention of superseded
  results remain unresolved.

**Scale/Scope**: one migration, one new table, one application service plus
the factored cancellation effects, changes to run creation and
reconciliation, two read models, one Web UI component and adapter change, and
tests across three platform test projects (Integration, Host, EndToEnd) and the Web UI.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Evidence |
| --- | --- | --- |
| I. Architecture is the design authority | Pass | Implements the "new run for changed source input" rule of [Run State And Recovery](../../../docs/architecture/job-processing.md#run-state-and-recovery) and the recording-set revision of [Upload](../../../docs/architecture/match-data-pipeline.md#upload), keeps [Capability-Scoped Result Access](../../../docs/architecture/analysts-models-and-hardware.md#capability-scoped-result-access) run-scoped, and extends [Client Applications → Web UI](../../../docs/architecture/client-applications.md#web-ui); the undecided supersession behavior is decided now in those narratives (see Architecture Updates). |
| II. Source-area ownership | Pass | Changes under `src/platform/` and `src/clients/web/`; no Manager production change; no new `src/` child. |
| III. API-first control plane | Pass | Supersession is exposed through additive fields of the existing `/api/v1` reads in the OpenAPI document; the Web UI only reads them through its adapter; no media passes the control plane. |
| IV. Evidence over claims | Pass | Integration, host, end-to-end, unit, and browser tests (FR-022); nothing claims production readiness. |
| V. Focused, minimal changes | Pass | One table justified by the immutability requirement (research R2); reuses cancellation effects, advisory-lock pattern, audit trail, metrics, and reconciliation; no new project, package, or contract. |
| Technology and tooling | Pass | Needs the .NET SDK, Docker, SoftHSM2 on Linux for end-to-end tests, Node.js 24, Kiota 1.36.0, and Playwright 1.63.0 Chromium, all provided by `.github/actions/environment-setup` and `environment-verify`; no environment spec is needed. Depends on the batch sibling Analysis Run Cancellation, named under Assumptions → Dependencies. |

Post-design re-check: Pass (no violations; Complexity Tracking is empty).

## Project Structure

### Documentation (this feature)

```text
specs/archive/20261017-183953-analysis-run-supersession/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output
│   ├── analysis-run-reads-api.md
│   └── web-ui-supersession-markers.md
└── tasks.md             # Phase 2 output (/speckit-tasks)
```

### Source Code (repository root)

```text
src/platform/
├── SocAlytics.Platform.Domain/Analysis/
│   ├── CancellationCause.cs                     # member | superseded
│   └── RecordingSetVersionOrder.cs              # version key, comparison, current-run order
├── SocAlytics.Platform.Application/
│   ├── Analysis/RunCancellationEffects.cs       # factored from CancelAnalysisRunHandler (cancellation feature)
│   ├── Analysis/RunSupersession.cs              # the supersession rule
│   ├── Analysis/Commands/CancelAnalysisRun.cs   # uses RunCancellationEffects with cause member
│   ├── Analysis/Commands/HandleRecordingsFinalizedNotification.cs # match lock, finalized time, supersession, readiness only if Requested
│   ├── Analysis/Commands/ReconcileAnalysisPublication.cs          # supersession pass first, RunsSuperseded
│   ├── Analysis/Commands/RunAnalysisReconciliation.cs             # audit detail runsSuperseded
│   ├── Analysis/Queries/AnalysisRunViews.cs     # IsCurrent, SupersededBy, SupersededAt, CancellationCause
│   ├── Analysis/Results/AnalysisAuditEvents.cs  # analysis.run-superseded
│   ├── Analysis/Stores/IAnalysisRunStore.cs, IRunSupersessionStore.cs
│   ├── Telemetry/AnalysisMetrics.cs             # runs_superseded
│   └── ApplicationServiceCollectionExtensions.cs
├── SocAlytics.Platform.Infrastructure/Persistence/
│   ├── Analysis/AnalysisRunStore.cs, RunSupersessionStore.cs, AnalysisRunQueries.cs
│   └── Migrations/NNNN_analysis_run_supersession.sql
├── SocAlytics.Platform.Infrastructure/InfrastructureServiceCollectionExtensions.cs # register RunSupersessionStore
├── SocAlytics.Platform.Infrastructure/Workers/PublicationReconciliationWorker.cs # log RunsSuperseded
├── SocAlytics.Platform.Api/
│   ├── Analysis/AnalysisRunEndpoints.cs         # additive DTO fields
│   └── openapi/v1.json                          # refreshed snapshot
└── Tests/
    ├── SocAlytics.Platform.Integration.Tests/
    │   ├── Analysis/Domain/AnalysisValueObjectTests.cs
    │   ├── Analysis/RunSupersessionTests.cs           # creation, effects, audit, outbox, metrics, reads
    │   ├── Analysis/RunSupersessionOrderingTests.cs   # order, late/duplicate events, races, reconciliation
    │   └── Structure/PersistedTableClassifications.cs
    ├── SocAlytics.Platform.Host.Tests/OpenApiSnapshotTests.cs
    └── SocAlytics.Platform.EndToEnd.Tests/RunSupersessionEndToEndTests.cs
src/clients/web/
├── src/
│   ├── platform/models/recordings.ts            # additive fields
│   ├── platform/adapters/analysisRunAdapter.ts  # mapping
│   ├── features/lineage/AnalysisRunStates.tsx   # markers
│   └── i18n/en/lineage.json                     # new keys
├── tests/unit/platform/adapters/analysisRunAdapter.test.ts
├── tests/unit/features/lineage/runs/runSupersession.test.tsx
├── tests/e2e/support/simulatedApi.ts
├── tests/e2e/simulated/run-supersession.spec.ts
├── tests/e2e/platform/run-supersession.spec.ts
└── playwright.config.ts                         # chained platform-supersession project
```

**Structure Decision**: supersession lives in the existing `Analysis` folders
of each platform layer next to the cancellation it reuses, and in the Web
UI's `lineage` feature that lists a match's runs; no new project or feature
folder.

## Architecture Updates

### Narratives

- [Job Processing → Run State And Recovery](../../../docs/architecture/job-processing.md#run-state-and-recovery): implements automatic supersession of older runs by the run of a newer recording-set version; decides what happens to active and ended older runs and how reads identify the current run.
- [Match Data Pipeline → Upload](../../../docs/architecture/match-data-pipeline.md#upload) and [Recording-Lineage Foundation](../../../docs/architecture/match-data-pipeline.md#recording-lineage-foundation): the recording-set revision now supersedes the match's older runs; the version order is finalization time with the version identity as tie-break.
- [Analysts, Models, and Hardware → Capability-Scoped Result Access](../../../docs/architecture/analysts-models-and-hardware.md#capability-scoped-result-access): "supersession applies only across runs" is made concrete; superseded runs' accepted results stay readable through their own run-scoped reads.
- [Client Applications → Web UI](../../../docs/architecture/client-applications.md#web-ui): current and superseded markers in the match page's run list.
- [Contracts and Compatibility](../../../docs/architecture/contracts-and-compatibility.md): unchanged; no contract changes (the `run-state-changed` contract is reused as is).
- [Security and Data Governance](../../../docs/architecture/security-and-data-governance.md) (`POL-005`, "Active, superseded, and source-deletion dependency periods"): unchanged; this feature defines when a run's results become superseded but sets no retention value, which stays `Open / Blocking`.

### Design updates (applied now, worded as decided but not implemented)

- `job-processing.md`, Run State And Recovery: the sentence "Automatic supersession of older runs remains unimplemented." is replaced by a paragraph stating that automatic supersession is decided as follows and not yet implemented: when the run of a newer finalized recording-set version of a match is created, every older run of that match that has not been superseded is superseded by the match's current run in the same transaction; runs still `Requested` or `Running` are cancelled with cause `superseded`, without a member, through the cancellation mechanics, audited as a system event and announced through the run-state-changed notification; runs that already ended keep their state and only gain the immutable supersession record; the current run is the newest run of the latest finalized recording-set version that has a run; a run that arrives for an older version is created already superseded and never publishes work; supersession is serialized per match, idempotent under duplicate or reordered notifications, never crosses matches or recording-set versions in the wrong direction, and is re-applied by the analysis reconciliation. Implementation turns it into present tense.
- `match-data-pipeline.md`, Upload: after "A later recording addition or correction creates a new recording-set version and a new run rather than overwriting accepted evidence.", a sentence that the new run supersedes the match's older runs once it exists (decided, not yet implemented; see Run State And Recovery). Recording-Lineage Foundation: a sentence that recording-set versions of a match are ordered by finalization time with the version identity as tie-break, the order supersession uses (decided, not yet implemented). Implementation turns both into present tense.
- `analysts-models-and-hardware.md`, Capability-Scoped Result Access: after "supersession applies only across runs, which a job's read never crosses.", a sentence that a superseded run keeps its accepted results as readable history through reads of that run only (decided, not yet implemented). Implementation turns it into present tense.
- `client-applications.md`, Web UI: the match-workflow bullet now says that the match page's run list will mark the match's current run and each superseded run with the run that superseded it, and shows a run cancelled because it was superseded without a member (decided, not yet implemented). Implementation turns it into present tense.

### Status statements at merge

- `job-processing.md`, Run State And Recovery: "Automatic supersession of older runs is decided as follows and not yet implemented." → implemented, in present tense.
- `job-processing.md`, Run State And Recovery, last paragraph ("Any changed source input … creates an entirely new analysis run … Prior runs and accepted results remain immutable history"): re-check that it stays consistent with supersession (prior runs stay history; active older runs of a revised recording set are now cancelled).
- `job-processing.md`, Implementation Validation ("partial failure, cancellation, supersession, and duplicate completion"): keep the requirement list, but make sure no statement implies that supersession evidence is missing.
- `match-data-pipeline.md`, Upload and Recording-Lineage Foundation: "(decided, not yet implemented …)" → present tense; the foundation's test list ("revision through a new recording-set identity") may gain supersession of the older run.
- `analysts-models-and-hardware.md`, Capability-Scoped Result Access: "(decided, not yet implemented)" on the superseded-results sentence → present tense.
- `client-applications.md`, Web UI: "will mark … (decided, not yet implemented)" → implemented as part of the connected match workflow.
- `platform-implementation.md`, Acceptance Evidence: "and analysis run creation, readiness, claims, fenced completion, state-plus-outbox atomicity, …" gains run supersession on recording-set revision (with the cancellation feature's run cancellation already listed); the Web UI browser-test bullet ("the match workflow and recording upload …") gains the current and superseded markers; the end-to-end evidence gains supersession during an active simulated attempt.
- `platform-implementation.md`, Architecture Reassessment: re-check the durable analysis workflow sentence and add supersession where the workflow's implemented behavior is listed.
- `overview.md` ("Analysis DAG completion, cancellation, and rerun semantics | Accepted"): unchanged (decision status), re-check only.
- `security-and-data-governance.md`, `POL-005` and `GOV-POL-005`: unchanged (`Open / Blocking`); re-check that nothing implies superseded results are purged or have a retention value.
- `AGENTS.md` "Current State" ("the durable analysis workflow as development evidence (transactional outbox, NATS JetStream publication, run creation from finalized recording sets, readiness, fenced attempts, …)") gains run supersession, and the Web UI foundation description gains the run-list markers; `src/platform/README.md` and `src/clients/README.md` are re-checked for statements that older runs keep running or that supersession is deferred.
- Any other statement in `docs/architecture/` that presents supersession of analysis runs as planned, deferred, or unimplemented.

The documentation task MUST make these narratives and every status statement
this feature affects true for the merged state, re-reading
`docs/architecture/` (and `AGENTS.md` "Current State", `src/platform/README.md`,
`src/clients/README.md`, and the root `README.md`) at implementation time; the
rewrites above are examples, not the whole job.

## Complexity Tracking

No violations.
