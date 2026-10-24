# Implementation Plan: Deterministic OpenAPI Document Host Tests

**Branch**: `20261024-070502-deterministic-openapi-host-tests` | **Date**: 2026-10-24 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261024-070502-deterministic-openapi-host-tests/spec.md`

## Summary

`AnalystRegistryOpenApiTests.OpenApiDocumentContainsAllAnalystRegistryOperationIds` is flaky because
of conditions around it. Every Host test that needs a stamp builds and starts its own complete
AppHost (PostgreSQL, Migrator, RustFS, NATS, Mailpit, API, Web), and xUnit runs distinct test
classes in parallel, so up to nine stamps start at once on one runner. The plan is to establish the
cause with evidence first (repeated runs alone and under load, the logs of the failed verifications
of #201, #225, and #275), then remove it in shared Host test support: one started stamp per test
project, shared by all tests that only read the running stamp or its OpenAPI document, held by an
xUnit collection fixture so that stamp-starting classes never run concurrently. Tests that need other
options or mutate the stamp keep their own AppHost, in the same collection so they stay serialized.
No assertion changes, no retries, sleeps, or longer limits; production code and the AppHost
composition are changed only if the evidence shows the cause lives there.

## Technical Context

**Language/Version**: C# 14 on .NET 10 (SDK pinned by `src/platform/global.json`)

**Primary Dependencies**: `Aspire.Hosting.Testing`, `xunit.v3` 3.2.2 (collection fixtures, `IAsyncLifetime`), Shouldly

**Storage**: Disposable PostgreSQL container per started stamp (`--SocAlytics:LocalDatabase:Persistent=false`); no schema change

**Testing**: `dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests` (Docker required)

**Target Platform**: Linux CI runner and developer machines with a Docker-compatible runtime

**Project Type**: Test project of the platform monolith (`src/platform/Tests/SocAlytics.Platform.Host.Tests`)

**Performance Goals**: The Host test project does not get slower than today; one shared stamp replaces up to nine concurrent ones

**Constraints**: Keep every test and assertion; keep the existing 5-minute and 1-minute limits; no retries, fixed sleeps, skips, or longer limits; a bounded wait on an observable condition is allowed; no test depends on state another test created

**Scale/Scope**: Eight OpenAPI test classes (nine tests starting a stamp) plus the stamp-starting tests of `PlatformHostTests`

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-checked after Phase 1 design.*

| Principle | Result |
| --- | --- |
| I. Architecture Is the Design Authority | Pass. Only test support changes unless the evidence points at covered code; see Architecture Updates. |
| II. Source-Area Ownership | Pass. All changes are in `src/platform/Tests/SocAlytics.Platform.Host.Tests` (or, if the evidence requires, the platform code the tests cover); no new `src/` child. |
| III. API-First Control Plane | Pass. No API or contract change; the committed OpenAPI snapshot stays unchanged unless evidence requires it (FR-006). |
| IV. Evidence Over Claims | Pass. The cause is proved by repeated runs and probes (SC-001, SC-002); results are development evidence only. |
| V. Focused, Minimal Changes | Pass. One support file plus the edits that route tests through it; no new dependency. |
| Tooling constraints | Pass. Uses only the documented `dotnet` commands; no environment action change, because xUnit v3 and Docker are already provided. |

Post-design re-check: unchanged, all gates pass. No Complexity Tracking entries.

## Project Structure

### Documentation (this feature)

```text
specs/20261024-070502-deterministic-openapi-host-tests/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── stamp-fixture.md
├── checklists/
└── tasks.md             # created by /speckit-tasks
```

### Source Code (repository root)

```text
src/platform/Tests/SocAlytics.Platform.Host.Tests/
├── StampCollection.cs                # new: collection definition and the shared stamp fixture
├── AgentCatalogOpenApiTests.cs       # use the shared stamp
├── AnalystRegistryOpenApiTests.cs    # use the shared stamp
├── OpenApiContractTests.cs           # use the shared stamp
├── OpenApiSnapshotTests.cs           # use the shared stamp
├── RecordingsOpenApiTests.cs         # use the shared stamp
├── RegistrationContractShapeTests.cs # use the shared stamp
├── RegistryOpenApiTests.cs           # use the shared stamp
├── WorkflowOperationsOpenApiTests.cs # use the shared stamp
└── PlatformHostTests.cs              # join the collection; keeps own stamps where it needs them
```

**Structure Decision**: Test support lives inside the existing Host test project; no new project.
Whether the fix also touches `SocAlytics.Platform.Api` or the AppHost is decided by the evidence in
task 1 and recorded in `research.md`.

## Architecture Updates

**Narratives**:

- [Platform Implementation](../../docs/architecture/platform-implementation.md), sections "Test And Observability
  Baseline" and "Acceptance Evidence", which describe the Host tests (xUnit v3, Shouldly, Aspire host tests that
  exercise `/alive`, `/health`, and `/openapi/v1.json`), and its project list entry
  `Tests/SocAlytics.Platform.Host.Tests`.
- [Contracts and Compatibility](../../docs/architecture/contracts-and-compatibility.md) and the Web UI section of
  Platform Implementation mention `OpenApiSnapshotTests` as the keeper of the committed OpenAPI snapshot; that stays
  true because the snapshot test keeps its behavior and the snapshot stays unchanged.

**Design updates**: None applied now. The shared started stamp (one xUnit collection fixture per Host test project,
stamp-starting classes serialized in one collection) is a test-support decision whose final shape depends on the
evidence (Decision 1 to 4 in [research.md](research.md)). The architecture does not state how Host tests start the
AppHost, so nothing is contradicted. If the evidence moves the fix into the API readiness or the AppHost composition,
the implementation updates the owning narrative in Platform Implementation ("Acceptance Evidence": start order and
`/health` readiness) in the documentation task.

**Status statements at merge**: No statement becomes false. The sentences "host tests exercise `/alive`, `/health`, and
`/openapi/v1.json`" and "Aspire starts PostgreSQL, the one-off Migrator, and the API in that order" stay true
(FR-005). Re-read at implementation time: if the Host tests then run against one shared started stamp, add one sentence
to "Test And Observability Baseline" stating that the Host tests that read the running stamp share one started
AppHost per test project, and that tests which change the stamp start their own.

**Documentation task requirement**: `/speckit-tasks` MUST include one documentation task referencing
`docs/architecture/` that makes the narratives above, and every status statement the final implementation affects,
true for the merged state, re-reading the documents at implementation time; the sentence above is an example, not the
whole job. If nothing in the architecture changes, the task records that outcome after re-reading the documents.

## Complexity Tracking

No violations.
