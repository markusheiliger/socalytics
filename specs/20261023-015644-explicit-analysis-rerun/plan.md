# Implementation Plan: Explicit Analysis Run Request

**Branch**: `20261023-015644-explicit-analysis-rerun` | **Date**: 2026-10-23 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261023-015644-explicit-analysis-rerun/spec.md`

## Summary

Add one member operation, `POST /api/v1/matches/{matchId}/analysis-runs` (`requestAnalysisRun`), through which a Club
Admin or Coach of the owning team asks the Scheduler for an analysis run of the match's current finalized
recording-set version. The run-creation body of `HandleRecordingsFinalizedNotificationHandler` is extracted into one
shared Application service (`AnalysisRunCreator`) that both the finalization consumer and the new handler call, so
snapshot, graph, segment requests, supersession, and readiness are identical by construction. The new handler adds the
member-API envelope (authorization, idempotency key, replay), the lookup of the match's current finalized set, a
durable receipt with the new message type `analysis-requested`, and exactly one security audit event. A new
idempotency table records each request's answer so replays, including recorded `rejected` refusals, never
re-evaluate. No Web UI, no new contract schema, and no change to supersession, cancellation, or result reads.

## Technical Context

**Language/Version**: C# on .NET 10 (SDK pinned by `src/platform/global.json`)

**Primary Dependencies**: existing ASP.NET Core minimal APIs (`MapMemberApi`), Dapper, DbUp migrations; no new packages

**Storage**: PostgreSQL; one forward-only migration (`0032_analysis_explicit_requests.sql`, numbered at implementation
time as the next free number): the new table `analysis_run_requests`, and the extension of the receipt table's
`message_type` and `rejection_category` check constraints

**Testing**: xUnit v3 Integration tests (Docker PostgreSQL, NATS, RustFS fixtures) in
`src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis` and Host tests for the committed OpenAPI snapshot;
the existing architecture tests are run in T022 and no new architecture test is added; Web UI receipt label mapping
checked with `npm test` and the regenerated Kiota client with `npm run build` in `src/clients/web`

**Target Platform**: Linux container, platform API host (no new process or worker)

**Project Type**: web-service (one monolith; layers Domain, Application, Infrastructure, Api)

**Performance Goals**: answered within 2 seconds under normal development load, defined as a single request on the standard test fixture (SC-001; the test asserts a generous fixed ceiling of 10 seconds); run creation is synchronous
and the same as the finalization path

**Constraints**: at most one run per recording-set version, workflow, and workflow version under concurrency (existing
unique constraint plus the per-match run lock); receipt, run, outcome, and audit are written in one unit of work;
Development and Test values only

**Scale/Scope**: one endpoint, one handler, one extracted service, one store, one migration, receipt reads unchanged in
shape

All items are resolved; see [research.md](research.md).

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Assessment |
| --------- | ---------- |
| I. Architecture is the design authority | Pass. The feature implements the "Explicit analysis demand" path already drawn in `docs/architecture/job-processing.md` and `overview.md`; the Architecture Updates section lists the narratives and status statements. |
| II. Source-area ownership | Pass. Platform layers plus a minimal Web UI mapping/client regeneration, no new control (no new project, no new `src/` child). |
| III. API-first control plane | Pass. One `/api/v1` operation in the committed OpenAPI snapshot; the Web UI gets no control here and the control plane moves no video bytes. |
| IV. Evidence over claims | Pass. Integration and Host (OpenAPI) tests are planned and the existing architecture tests are run (no new one); nothing claims production readiness. |
| V. Focused, minimal changes | Pass. One shared extraction instead of duplicated run creation; no new dependency; no speculative abstraction. |
| Tooling | Pass. No new framework, SDK, or tool; the existing `environment-setup` and `environment-verify` actions cover .NET, Docker-backed tests, and the Host OpenAPI snapshot check. No environment spec is needed. |

**Post-design re-check**: Pass. The design adds no project, dependency, or contract schema; the only new persisted table
is classified in `PersistedTableClassifications.cs`.

## Architecture Updates

### Narratives

- [Job Processing](../../docs/architecture/job-processing.md), analysis request flow (the "Explicit analysis demand"
  branch of the sequence diagram) and the Durable Workflow Model: the branch becomes an implemented member operation
  that creates the run through the same run creation as the finalization consumer.
- [Overview](../../docs/architecture/overview.md): the "Explicit demand" edge from the API to the Scheduler.
- [Platform Implementation](../../docs/architecture/platform-implementation.md): implementation evidence for analysis
  run creation (the consumer of `matches.recordings-finalized`, run cancellation, supersession) and its test
  coverage list.
- [Client Applications](../../docs/architecture/client-applications.md): the workflow operations area lists "the
  finalized-recordings notification receipts"; receipts now also cover explicit requests (the Web UI gains no
  control).
- [Contracts and Compatibility](../../docs/architecture/contracts-and-compatibility.md): the operation joins the
  committed OpenAPI contract; no new JSON Schema.

### Design updates

None applied to `docs/architecture/` now: the architecture already states the explicit demand path, and this plan's
decisions (the `POST /matches/{matchId}/analysis-runs` operation, the receipt message type `analysis-requested`, the
idempotency record table, and the shared run creation) are implementation-level. The implementation's documentation
task states them in present tense where the narratives describe run creation.

### Status statements at merge

- `job-processing.md`, sequence diagram: `API->>Scheduler: Request analysis` on the explicit branch has no named
  operation; it must name `requestAnalysisRun` and say it applies the complete-rerun rule and the same supersession.
- `job-processing.md`, Implementation Validation: "dependency-version validation and complete reruns" must state that
  the explicit request is implemented and evidenced.
- `overview.md`, "Analysis DAG completion, cancellation, and rerun semantics | Accepted" and the "Explicit demand"
  edge: must say the edge is implemented as a member operation without a Web UI control.
- `platform-implementation.md`, evidence lists around the analysis run tests: must add explicit requests, idempotent
  replay, duplicate and race protection, and operations visibility; any "not implemented" wording about explicit
  analysis demand must be removed.
- `client-applications.md`, workflow operations area: "the finalized-recordings notification receipts" must say
  receipts of finalization and explicit analysis requests, distinguished by message type.
- `README.md`, `src/platform/README.md`, and `AGENTS.md` current-state statements: must list the explicit analysis
  request as development evidence and keep "no Web UI control" explicit.

The documentation task must make the listed narratives and every status statement the feature affects true for the
merged state, re-reading `docs/architecture/` at implementation time, with these rewrites as examples rather than the
whole job.

## Project Structure

### Documentation (this feature)

```text
specs/20261023-015644-explicit-analysis-rerun/
├── plan.md              # This file (/speckit-plan command output)
├── research.md          # Phase 0 output (/speckit-plan command)
├── data-model.md        # Phase 1 output (/speckit-plan command)
├── quickstart.md        # Phase 1 output (/speckit-plan command)
├── contracts/
│   └── request-analysis-run.md   # Phase 1 output: operation contract
└── tasks.md             # Phase 2 output (/speckit-tasks command - NOT created by /speckit-plan)
```

### Source Code (repository root)

```text
src/platform/
├── SocAlytics.Platform.Application/
│   ├── Analysis/
│   │   ├── AnalysisRunCreator.cs                       # NEW: extracted shared run creation
│   │   ├── AnalysisAuditActions.cs                     # NEW (or extended): analysis.run.request event
│   │   ├── Commands/
│   │   │   ├── HandleRecordingsFinalizedNotification.cs  # CHANGED: calls AnalysisRunCreator, behavior unchanged
│   │   │   └── RequestAnalysisRun.cs                   # NEW: command, result, handler
│   │   └── Stores/
│   │       └── IAnalysisRunRequestStore.cs             # NEW: idempotency record store
│   ├── Recordings/IRecordingSetLookup.cs               # CHANGED: GetCurrentForMatchAsync
│   └── ApplicationServiceCollectionExtensions.cs       # CHANGED: registrations
├── SocAlytics.Platform.Infrastructure/
│   ├── Persistence/
│   │   ├── Analysis/AnalysisRunRequestStore.cs         # NEW
│   │   └── Migrations/0032_analysis_explicit_requests.sql   # NEW
│   ├── Recordings/RecordingSetLookup.cs                # CHANGED
│   └── InfrastructureServiceCollectionExtensions.cs    # CHANGED: registration
├── SocAlytics.Platform.Api/Analysis/AnalysisRunEndpoints.cs   # CHANGED: POST route and DTOs
└── Tests/
    ├── SocAlytics.Platform.Integration.Tests/
    │   ├── Analysis/ExplicitAnalysisRequestTests.cs    # NEW
    │   ├── Analysis/AnalysisSchemaTests.cs             # CHANGED: new type and categories
    │   └── Structure/PersistedTableClassifications.cs  # CHANGED: new table
    └── SocAlytics.Platform.Host.Tests/                 # CHANGED: OpenAPI snapshot
src/clients/web/src/                                    # CHANGED (minimal): receipt label/category mapping, unit test, regenerated Kiota client
docs/architecture/                                      # see Architecture Updates
README.md, src/platform/README.md, AGENTS.md            # CHANGED: current-state statements
```

**Structure Decision**: The existing layered monolith; the Analysis functional area folders in Application and
Infrastructure. The new handler sits beside `HandleRecordingsFinalizedNotification.cs`, and the shared creator sits at
the root of the Analysis namespace next to `RunSupersession`, which it calls.

## Complexity Tracking

No Constitution Check violations.
