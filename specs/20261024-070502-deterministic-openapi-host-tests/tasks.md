---

description: "Task list for Deterministic OpenAPI Document Host Tests"
---

# Tasks: Deterministic OpenAPI Document Host Tests

**Input**: Design documents from `/specs/20261024-070502-deterministic-openapi-host-tests/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/stamp-fixture.md](contracts/stamp-fixture.md), [quickstart.md](quickstart.md)

**Tests**: The feature changes tests. No new test tasks are added; the existing tests keep every assertion (FR-003), and verification tasks run them as the spec's success criteria require.

**Organization**: Tasks are grouped by user story. All source changes are in `src/platform/Tests/SocAlytics.Platform.Host.Tests/` unless the evidence of T001 places the cause in covered platform code (then T002 and the documentation task follow the evidence, keeping the start order PostgreSQL, Migrator, API).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies); consecutive `[P]` tasks under one heading run in parallel, each from the same commit
- **[Story]**: Which user story this task belongs to (US1, US2)

## Phase 1: Setup (Establish the Cause)

**Purpose**: FR-001: prove or rule out each hypothesis before changing anything.

- [ ] T001 Establish the cause with evidence per `specs/20261024-070502-deterministic-openapi-host-tests/research.md` and `quickstart.md` step 1: restore and build `src/platform/SocAlytics.Platform.slnx`; read the failed verification logs of #201, #225, and #275 with `gh` (exception type, elapsed time, failing assertion); run `dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --no-build` repeatedly, alone and under processor load, and with the OpenAPI document requested in a loop right after `api` is healthy (uncommitted probe); then append an "Evidence" section to `specs/20261024-070502-deterministic-openapi-host-tests/research.md` with a verdict (proved, ruled out, or not reproducible) for each of H1 to H4 and the evidenced cause. Before any change, also record the number of Host tests (from `dotnet test --list-tests`) and the assertion-line count of the OpenAPI test files in that section as the SC-003 baseline. T002 may start only if T001 records a proved cause, or logs showing a failure mode the shared stamp removes; if no hypothesis reproduces and the logs show no such failure mode, record "not reproducible", keep investigating, and make no source change.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: The shared stamp support every Host test that starts a stamp joins.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [ ] T002 Create `src/platform/Tests/SocAlytics.Platform.Host.Tests/StampCollection.cs` per `contracts/stamp-fixture.md` and `data-model.md`: `[CollectionDefinition("Stamp")]` class `StampCollection : ICollectionFixture<StampFixture>`, and `StampFixture : IAsyncLifetime` that builds and starts `Projects.SocAlytics_Platform_AppHost` with `--SocAlytics:LocalDatabase:Persistent=false` and `--SocAlytics:ApiHttpsEndpoint=false`, waits for `api` healthy under one bounded 5-minute `CancellationTokenSource` (and, only if T001 proved H2, until `GET /openapi/v1.json` returns 200 under that token), records any start exception and rethrows it from `CreateApiClient()` (an `HttpClient` for `api`, endpoint `http`) for every test without caching a success it did not observe, and stops and disposes the stamp once in `DisposeAsync`. Put a short comment on the fixture naming the evidenced cause from T001. No fixed delay, retry, skip, or longer limit.
- [ ] T002a If T001 proves H2 in covered code (the API reports healthy before `GET /openapi/v1.json` is served), fix API readiness in `src/platform/SocAlytics.Platform.Api` or the AppHost composition in `src/platform/SocAlytics.Platform.AppHost`, keeping the start order PostgreSQL, Migrator, API, and include the change in the scope of T014 and T015; otherwise record in the "Evidence" section of `research.md` that no covered code changes.

**Checkpoint**: The fixture builds (`dotnet build src/platform/Tests/SocAlytics.Platform.Host.Tests`).

---

## Phase 3: User Story 1 - The registry operation test fails only when the document is wrong (Priority: P1) 🎯 MVP

**Goal**: `OpenApiDocumentContainsAllAnalystRegistryOperationIds` passes whenever the document contains every Analyst Registry operation id, however loaded the runner is, and fails only on its assertion.

**Independent Test**: Run the test 20 times in a row alone; every run passes. Probes make it fail on its assertion or on the start error within the existing limits.

- [ ] T003 [US1] In `src/platform/Tests/SocAlytics.Platform.Host.Tests/AnalystRegistryOpenApiTests.cs` add `[Collection("Stamp")]` and a constructor taking `StampFixture`; make both tests (`OpenApiDocumentContainsAllAnalystRegistryOperationIds` and `AnalystRegistryOperationsDeclareJsonSchemasForSuccessResponsesAndRequestBodies`) read the document through the fixture's `CreateApiClient()` and keep a bounded 5-minute token for the read; remove only the per-test AppHost build, start, and wait; keep every assertion and expected value textually unchanged.
- [ ] T004 [US1] Verify SC-001 and SC-002 for the registry tests: run `dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --no-build --filter "FullyQualifiedName~AnalystRegistryOpenApiTests"` at least 20 consecutive times (all pass), and at least 5 consecutive runs of the project under load generated with `stress-ng --cpu $(nproc)` (or `$(nproc)` busy-loop processes) running for the whole duration of the runs; then with temporary, uncommitted probes (remove one Analyst Registry operation from the served document; make the stamp unable to become ready, for example through a failing Migrator environment value) confirm the first fails on its `ShouldContain` assertion and the second fails every dependent test with the fixture's start error, both within the existing limits and without hanging; remove the probes and confirm `git status` shows no probe change. Record the run counts and outcomes in the "Evidence" section of `specs/20261024-070502-deterministic-openapi-host-tests/research.md`.

**Checkpoint**: User Story 1 is verified independently.

---

## Phase 4: User Story 2 - The other Host tests stay deterministic (Priority: P2)

**Goal**: Every other Host test that starts the AppHost keeps every assertion and passes at least as reliably; no two stamp-starting classes run concurrently.

**Independent Test**: The whole Host test project passes five runs in a row under load, with an unchanged number of tests and assertions.

### Route the OpenAPI classes through the shared stamp

- [ ] T005 [P] [US2] In `src/platform/Tests/SocAlytics.Platform.Host.Tests/AgentCatalogOpenApiTests.cs` add `[Collection("Stamp")]`, a `StampFixture` constructor parameter, and read the document through `CreateApiClient()` instead of building and starting an AppHost; keep all assertions unchanged.
- [ ] T006 [P] [US2] In `src/platform/Tests/SocAlytics.Platform.Host.Tests/OpenApiContractTests.cs` add `[Collection("Stamp")]`, a `StampFixture` constructor parameter, and read the document through `CreateApiClient()` instead of building and starting an AppHost; keep all assertions unchanged.
- [ ] T007 [P] [US2] In `src/platform/Tests/SocAlytics.Platform.Host.Tests/OpenApiSnapshotTests.cs` add `[Collection("Stamp")]`, a `StampFixture` constructor parameter, and read the document through `CreateApiClient()` instead of building and starting an AppHost; keep the `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1` compare-or-write behavior and the snapshot path unchanged.
- [ ] T008 [P] [US2] In `src/platform/Tests/SocAlytics.Platform.Host.Tests/RecordingsOpenApiTests.cs` add `[Collection("Stamp")]`, a `StampFixture` constructor parameter, and read the document through `CreateApiClient()` instead of building and starting an AppHost; keep all assertions unchanged.
- [ ] T009 [P] [US2] In `src/platform/Tests/SocAlytics.Platform.Host.Tests/RegistrationContractShapeTests.cs` add `[Collection("Stamp")]`, a `StampFixture` constructor parameter, and read the document through `CreateApiClient()` instead of building and starting an AppHost; keep all assertions unchanged.
- [ ] T010 [P] [US2] In `src/platform/Tests/SocAlytics.Platform.Host.Tests/RegistryOpenApiTests.cs` add `[Collection("Stamp")]`, a `StampFixture` constructor parameter, and read the document through `CreateApiClient()` instead of building and starting an AppHost; keep all assertions unchanged.
- [ ] T011 [P] [US2] In `src/platform/Tests/SocAlytics.Platform.Host.Tests/WorkflowOperationsOpenApiTests.cs` add `[Collection("Stamp")]`, a `StampFixture` constructor parameter, and read the document through `CreateApiClient()` instead of building and starting an AppHost; keep all assertions unchanged.

### Keep stamps that need other options out of concurrency

- [ ] T012 [US2] In `src/platform/Tests/SocAlytics.Platform.Host.Tests/PlatformHostTests.cs` add `[Collection("Stamp")]` to `PlatformHostTests` so its own AppHosts (`AppHostStartsHealthyApiWithOperationalOpenApiSurface`, which asserts start order and per-resource readiness, and `AppHostDoesNotStartApiWhenMigratorFails`, which changes the Migrator environment) run one at a time next to the one shared stamp (at most two stamps at once) and never overlap other classes; record this residual overlap, measured under load, in the "Evidence" section of `research.md` (T013); keep every test and assertion; `AppHostStartsHealthyApiWithOperationalOpenApiSurface` and `AppHostDoesNotStartApiWhenMigratorFails` keep their own start (Decision 2 of `research.md`) and the three build-only tests start nothing, so no `PlatformHostTests` test reads the shared stamp unless the evidence of T001 says otherwise.

### Verification

- [ ] T013 [US2] Verify SC-001, SC-003, and FR-006: run `dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --no-build` at least 5 consecutive times under processor load generated with `stress-ng --cpu $(nproc)` (or `$(nproc)` busy-loop processes) running for the whole duration of the runs (all pass, test count unchanged from the baseline of T001); run the quickstart step 4 commands (`git diff --stat -- src/platform/SocAlytics.Platform.Api/openapi/v1.json` is empty and no assertion line is only removed in `git diff -U0 -- src/platform/Tests/SocAlytics.Platform.Host.Tests`); with `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT` unset confirm `OpenApiSnapshotTests` still compares. Record the outcomes, the load generator and level, and the residual overlap of the `PlatformHostTests` stamps with the shared stamp under load in the "Evidence" section of `specs/20261024-070502-deterministic-openapi-host-tests/research.md`.

**Checkpoint**: Both user stories are verified.

---

## Phase 5: Polish & Cross-Cutting Concerns

- [ ] T014 Make `docs/architecture/` true for the merged state: re-read `docs/architecture/platform-implementation.md` ("Test And Observability Baseline", "Acceptance Evidence", and the `Tests/SocAlytics.Platform.Host.Tests` project entry) and `docs/architecture/contracts-and-compatibility.md` against the final implementation; add the sentence that the Host tests that read the running stamp share one started AppHost per test project, held by one xUnit collection, and that tests which change the stamp or need other options start their own, correcting any statement that this or a covered-code change makes false (including planned, future-tense, or unimplemented wording); if nothing needs to change, record that outcome after re-reading. Keep the statements "host tests exercise `/alive`, `/health`, and `/openapi/v1.json`" and "Aspire starts PostgreSQL, the one-off Migrator, and the API in that order" true.
- [ ] T015 Run `node .github/scripts/check-markdown.mjs` and fix any finding in the files this spec changed; then write the final summary naming the evidenced cause and each hypothesis verdict (FR-001, SC-004) for the commit message and pull request, and confirm the changed files are only the Host test support, the evidenced cause's files, `docs/architecture/`, and this spec folder.

---

## Dependencies & Execution Order

- T001 (evidence) → T002 (fixture) → T003 → T004 (User Story 1, the MVP).
- T005 to T011 need T002 only and touch different files, so they run in parallel; T012 follows them to keep one `Stamp` collection decision in one place; T013 needs T003 to T012.
- T014 needs T013 (final shape known); T015 is last.
- User Story 2 does not depend on T003 or T004 but benefits from the fixture proven by them; implement it after User Story 1.

## Parallel Example: User Story 2

```text
Task: T005 AgentCatalogOpenApiTests.cs, T006 OpenApiContractTests.cs, T007 OpenApiSnapshotTests.cs,
      T008 RecordingsOpenApiTests.cs, T009 RegistrationContractShapeTests.cs,
      T010 RegistryOpenApiTests.cs, T011 WorkflowOperationsOpenApiTests.cs
```

## Implementation Strategy

- **MVP**: T001 to T004: the cause is proved, the shared stamp exists, and the flaky test reads it.
- **Incremental delivery**: T005 to T013 move the remaining classes into the collection and prove them under load; T014 and T015 finish documentation and the summary.
- **No weakening**: no retry, fixed sleep, skip, longer limit, deleted test, or changed expected value at any step (FR-002, FR-003).
