# Implementation Plan: Analysis Run Cancellation

**Branch**: `20261017-182600-analysis-run-cancellation` | **Date**: 2026-10-17 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261017-182600-analysis-run-cancellation/spec.md`

## Summary

Add the missing transition into `Cancelled` for analysis runs, enforce it
through publication, claim, and attempt fencing, and offer it to Club Admins and
Coaches in the Web UI's match page.

- **Member operation**: `POST /api/v1/analysis/runs/{runId}/cancellation`
  (`cancelAnalysisRun`) on `MapMemberApi()` with `IfMatchHeader` on the run's
  `version`, an optional plain-text `reason` (at most 500 characters), and
  `IAccessAuthorizer.AuthorizeTeamResourceAsync(("analysis-run", runId), TeamPermission.Write)`;
  returns the run status (`AnalysisRunStatusDto`) or `ProblemResults`
  (400, 403, 404, 409 `analysis-run-terminal`, 412, 428).
- **One transaction** (`IUnitOfWork`, run lock first, the same lock order as
  readiness and stale handling): run `Requested|Running → Cancelled` with
  `terminated_at`, `cancelled_by_account_id`, and `cancellation_reason`; every
  unfinished node (`Waiting`, `Active`) and logical job (`Waiting`, `Ready`,
  `Claimed`) → new terminal `Cancelled`; every `Active` attempt → new terminal
  attempt state `Cancelled` with close reason `run-cancelled` (fencing token no
  longer authorizes anything, no budget consumed, no requeue); pending or
  failed `analysis.ready-work` outbox messages of the run → `discarded`; one
  `analysis.run-state-changed` outbox message (existing contract, no reason);
  one audit event `analysis.run-cancelled` through `IAuditTrail`.
- **Enforcement by existing checks**: claims already require a `Ready` job
  (`claim-obsolete`), and heartbeats, result grants, upstream reads, and
  completions already require an `Active` attempt (`attempt-obsolete`); the
  Job Monitor already scans only `Active` attempts and the publisher only
  `pending` outbox rows. The plan adds guards so readiness and publication
  reconciliation skip cancelled runs and jobs, and tests prove every path.
- **Idempotency**: a cancel request for a `Cancelled` run succeeds unchanged
  when `If-Match` names the current version or the version before the
  cancellation (current − 1, because a cancelled run never changes again).
- **Reads**: additive fields on the run summary and status
  (`cancelledBy { memberId, accountName }`, `cancellationReason`) and an
  additive `cancelled` job count per node; the account name is joined at read
  time from `member_account`.
- **Manager**: no production change. The Analyst Manager already stops the
  container through its runtime adapter, deletes the attempt directory, and
  reports nothing when a heartbeat, grant, upstream read, or completion is
  refused as obsolete (`AttemptRunner`, `HeartbeatLoop`); the end-to-end test
  proves it for cancellation.
- **Web UI**: `AnalysisRunStates` gains a cancel action (Club Admin or Coach
  via `canEditTeamMatches`) for `Requested`/`Running` runs, a confirmation
  dialog with an optional reason, and the cancelled member, time, and reason;
  `analysisRunAdapter` gains `cancel` over the regenerated Kiota client.

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
`NNNN_analysis_run_cancellation.sql` (number assigned at implementation time):
cancellation columns on `analysis_runs`, `Cancelled` added to the state checks
of `analysis_workflow_nodes`, `analysis_logical_jobs`, and
`analysis_execution_attempts`. No new table, so no new
`PersistedTableClassifications` entry; the versioned `analysis_runs` keeps its
`version` trigger.

**Testing**:

- `SocAlytics.Platform.Integration.Tests` (Docker: PostgreSQL, NATS, object
  storage): domain transitions, the cancel endpoint (roles, preconditions,
  idempotency, problem responses, audit, outbox), fencing of claims,
  heartbeats, grants, upstream reads, and completions, the
  completion-versus-cancellation race, discarded and skipped publication,
  worker behavior (Job Monitor, reconciliation, readiness), immutable accepted
  results, and the additive read fields.
- `SocAlytics.Platform.Host.Tests`: `OpenApiSnapshotTests` with the refreshed
  `src/platform/SocAlytics.Platform.Api/openapi/v1.json`.
- `SocAlytics.Platform.Contracts.Tests`: unchanged `run-state-changed`
  contract; a new valid example `cancelled.json` proves the transition shape.
- `SocAlytics.Platform.EndToEnd.Tests`: cancel a running run while the
  simulated runtime holds an active attempt; the Manager stops and cleans up,
  and nothing of the run is accepted afterwards.
- Web UI: `npm test` (adapter, action visibility, dialog, cancelled display),
  `npm run test:e2e -- --project simulated` (`run-cancellation.spec.ts`), and
  the platform chain (`platform/run-cancellation.spec.ts` in a new chained
  project after `platform-upload`).

**Target Platform**: Linux containers for the platform in development and
test (Aspire AppHost); Chromium for the Web UI tests.

**Project Type**: web service (platform monolith) plus single-page web
application.

**Performance Goals**: cancellation is one transaction over one run's nodes,
jobs, attempts, and outbox rows; the Manager stops its container within two
heartbeat intervals (SC-004), whose production value stays unresolved.

**Constraints**:

- Accepted results, manifests, and lineage are never changed or deleted
  (FR-015).
- The `run-state-changed` contract stays unchanged (`Cancelled` is already in
  its enum); the reason never enters a notification (FR-006).
- `/api/v1` additions are additive; existing clients ignore new fields.
- Production heartbeat, timeout, and queue-retention values remain unresolved.

**Scale/Scope**: one migration, one command handler, one endpoint, guards in
the readiness, monitor, reconciliation, and publication paths, two read
models, one Web UI component change with one dialog, one adapter method, and
tests across five test projects.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Evidence |
| --- | --- | --- |
| I. Architecture is the design authority | Pass | Implements the accepted cancellation semantics of [Run State And Recovery](../../docs/architecture/job-processing.md#run-state-and-recovery) and the late-call rejection of [Execution Attempts, Heartbeats, And Recovery](../../docs/architecture/analyst-runtime-and-recovery.md#execution-attempts-heartbeats-and-recovery); the cancel action extends [Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui). Design updates are applied now (see Architecture Updates). |
| II. Source-area ownership | Pass | Changes under `src/platform/` and `src/clients/web/`; the Manager under `src/analysts/manager/` gets no production change; no new `src/` child. |
| III. API-first control plane | Pass | Cancellation is a member `/api/v1` operation in the OpenAPI document; the Web UI only calls it through an adapter; no media passes the control plane. |
| IV. Evidence over claims | Pass | Integration, host, contract, end-to-end, unit, and browser tests (FR-021); nothing claims production readiness. |
| V. Focused, minimal changes | Pass | No new project, package, table, or contract; reuses fencing, outbox discard, audit, and Manager cleanup. |
| Technology and tooling | Pass | Needs the .NET SDK, Docker, SoftHSM2 on Linux for end-to-end tests, Node.js 24, Kiota 1.36.0, and Playwright 1.63.0 Chromium, all provided by `.github/actions/environment-setup` and `environment-verify`; no environment spec is needed. |

Post-design re-check: Pass (no violations; Complexity Tracking is empty).

## Project Structure

### Documentation (this feature)

```text
specs/20261017-182600-analysis-run-cancellation/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output
│   ├── cancel-analysis-run-api.md
│   └── web-ui-cancel-action.md
└── tasks.md             # Phase 2 output (/speckit-tasks)
```

### Source Code (repository root)

```text
src/platform/
├── SocAlytics.Platform.Domain/Analysis/
│   ├── AnalysisStateTransitions.cs          # Requested|Running → Cancelled; node, job, attempt → Cancelled
│   ├── WorkflowNodeState.cs                 # + Cancelled
│   ├── LogicalJobState.cs                   # + Cancelled
│   ├── ExecutionAttemptState.cs             # + Cancelled
│   └── RunCancellation.cs                   # reason rules (length, plain text), idempotency rule
├── SocAlytics.Platform.Application/Analysis/
│   ├── Commands/CancelAnalysisRun.cs        # command, handler, audit, outbox
│   ├── RunReadinessCoordinator.cs           # skip cancelled runs
│   ├── Commands/ExpireStaleAttempts.cs      # only active attempts (unchanged scan), cancelled runs untouched
│   ├── Commands/ReconcileAnalysisPublication.cs # never recreate or republish work of cancelled runs
│   ├── Queries/AnalysisRunViews.cs          # cancelledBy, cancellationReason, cancelled job count
│   ├── Results/AnalysisAuditEvents.cs       # + analysis.run-cancelled
│   └── Stores/I*Store.cs                    # cancellation store operations
├── SocAlytics.Platform.Infrastructure/Persistence/
│   ├── Analysis/AnalysisRunStore.cs, LogicalJobStore.cs, ExecutionAttemptStore.cs, AnalysisRunQueries.cs
│   ├── Messaging/PublicationReconciliationStore.cs # discard ready-work of a run
│   └── Migrations/NNNN_analysis_run_cancellation.sql
├── SocAlytics.Platform.Api/
│   ├── Analysis/AnalysisRunEndpoints.cs     # cancelAnalysisRun, additive DTO fields
│   └── openapi/v1.json                      # refreshed snapshot
└── Tests/
    ├── SocAlytics.Platform.Integration.Tests/Analysis/
    │   ├── Domain/AnalysisValueObjectTests.cs        # transitions
    │   ├── RunCancellationTests.cs                    # endpoint, roles, preconditions, audit, outbox
    │   └── RunCancellationFencingTests.cs             # claims, heartbeats, grants, reads, completions, workers, race
    ├── SocAlytics.Platform.Host.Tests/OpenApiSnapshotTests.cs
    └── SocAlytics.Platform.EndToEnd.Tests/RunCancellationEndToEndTests.cs
contracts/analysis/run-state-changed/v1/examples/valid/cancelled.json
src/clients/web/
├── src/
│   ├── platform/adapters/analysisRunAdapter.ts      # + cancel, cancelled fields
│   ├── platform/models/recordings.ts                # AnalysisRunSummary + version, cancelledBy, cancellationReason
│   ├── features/lineage/AnalysisRunStates.tsx       # action, cancelled details
│   ├── features/lineage/CancelRunDialog.tsx         # confirmation with optional reason
│   └── i18n/en/lineage.json                         # new keys
├── tests/unit/...                                   # adapter, dialog, run list
├── tests/e2e/support/simulatedApi.ts                # cancel route
├── tests/e2e/simulated/run-cancellation.spec.ts
├── tests/e2e/platform/run-cancellation.spec.ts
└── playwright.config.ts                             # chained platform-cancellation project
```

**Structure Decision**: cancellation lives in the existing `Analysis` folders
of each platform layer and in the Web UI's `lineage` feature that already lists
a match's runs; no new project or feature folder.

## Architecture Updates

### Narratives

- [Job Processing → Run State And Recovery](../../docs/architecture/job-processing.md#run-state-and-recovery): implements member-initiated cancellation with the accepted semantics; adds the terminal `Cancelled` state for nodes and logical jobs.
- [Analyst Runtime and Recovery → Execution Attempts, Heartbeats, And Recovery](../../docs/architecture/analyst-runtime-and-recovery.md#execution-attempts-heartbeats-and-recovery): cancellation fences active attempts without consuming budget; the Manager's existing best-effort stop and cleanup apply.
- [Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui): the cancel action in the match page's run list.
- [Contracts and Compatibility](../../docs/architecture/contracts-and-compatibility.md): unchanged; the `run-state-changed` contract already includes `Cancelled` (no contract change).

### Design updates (applied now, worded as decided but not implemented)

- `job-processing.md`, Run State And Recovery: the node-state sentence now says a node may also end `Cancelled` when its run is cancelled (decided, not yet implemented), and a new paragraph states that member-initiated cancellation is decided as follows and not yet implemented: Club Admins and Coaches of the owning team cancel a `Requested` or `Running` run with the run's version; the run records who, when, and an optional reason, audited and announced without the reason; unfinished nodes and logical jobs become `Cancelled`, active attempts are closed as `Cancelled` without consuming budget or being republished, pending ready-work publications are discarded, queued claims and later attempt calls are refused as obsolete, accepted results stay immutable history, cancelled runs never transition again, other terminal runs cannot be cancelled, repeats are idempotent, and supersession stays unimplemented. Implementation turns it into present tense.
- `analyst-runtime-and-recovery.md`, Execution Attempts, Heartbeats, And Recovery: a new paragraph that run cancellation (decided, not yet implemented) closes active attempts in its transaction without republishing or consuming budget, and that the AM stops the container and removes staged inputs and result data as best effort once a call is refused as obsolete. Implementation turns it into present tense.
- `client-applications.md`, Web UI: the match-workflow bullet now says that the match page's run list will offer Club Admins and Coaches of the owning team a confirmed cancel action with an optional reason for requested and running runs and shows cancelled runs with who, when, and why (decided, not yet implemented). Implementation turns it into present tense.

### Status statements at merge

- `job-processing.md`, Run State And Recovery: "Member-initiated cancellation is decided as follows and not yet implemented." and "(decided, not yet implemented)" in the node-state sentence → implemented, in present tense; "Automatic supersession of older runs remains unimplemented." stays true until the supersession feature.
- `job-processing.md`, Implementation Validation ("partial failure, cancellation, supersession, and duplicate completion"): keep the requirement list, but make sure no statement implies that cancellation evidence is missing.
- `analyst-runtime-and-recovery.md`, Execution Attempts, Heartbeats, And Recovery: "Cancelling an analysis run (decided, not yet implemented; …)" → present tense.
- `client-applications.md`, Web UI: "… will offer … (decided, not yet implemented)" → implemented as part of the connected match workflow.
- `platform-implementation.md`, Acceptance Evidence: "and analysis run creation, readiness, claims, fenced completion, state-plus-outbox atomicity, …" gains run cancellation with fencing of queued and active work; the Web UI browser-test bullet ("the match workflow and recording upload …") gains the cancel action; the end-to-end evidence gains cancellation during an active simulated attempt.
- `platform-implementation.md`, Architecture Reassessment: re-check the durable analysis workflow sentence and add cancellation to the development evidence where the workflow's implemented behavior is listed.
- `overview.md` ("Analysis DAG completion, cancellation, and rerun semantics | Accepted"): unchanged (decision status), re-check only.
- `AGENTS.md` "Current State" ("the durable analysis workflow as development evidence (transactional outbox, NATS JetStream publication, run creation from finalized recording sets, readiness, fenced attempts, …)") gains run cancellation, and the Web UI foundation description gains the cancel action; `src/platform/README.md` and `src/clients/README.md` are re-checked for statements that the `Cancelled` state is only representable or that runs cannot be controlled.
- Any other statement in `docs/architecture/` that presents run cancellation, the revocation of queued work, or the `Cancelled` transition as planned, deferred, or only representable.

The documentation task MUST make these narratives and every status statement
this feature affects true for the merged state, re-reading
`docs/architecture/` (and `AGENTS.md` "Current State", `src/platform/README.md`,
`src/clients/README.md`, and the root `README.md`) at implementation time; the
rewrites above are examples, not the whole job.

## Complexity Tracking

No violations.
