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

- [ ] T001 Confirm the recorded cause per `specs/20261024-070502-deterministic-openapi-host-tests/research.md` ("Evidence", recorded by the amendment from the first T001 attempt, because tasks cannot write under `specs/`): restore and build `src/platform/SocAlytics.Platform.slnx`; confirm the SC-003 baseline stated there (the number of Host tests from `dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --no-build --list-tests` and the count of lines matching `Should` (Shouldly calls) and `Assert\.` in the Host test files) and run the single test `OpenApiDocumentContainsAllAnalystRegistryOperationIds` alone once with `dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --no-build --filter "FullyQualifiedName~OpenApiDocumentContainsAllAnalystRegistryOperationIds"`; change no file at all (research.md and every other file under `specs/` are read-only for tasks) and write the final summary with the confirmed numbers and the verdicts of H1 to H4 from the "Evidence" section (H1 proved, H2 ruled out, H3 and H4 not reproducible), which the pull request shows. Evidence for FR-001 and SC-001 is the recorded Evidence; there is no open-ended investigation and no further bounded run is required. T002 proceeds because H1 is proved.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: The shared stamp support every Host test that starts a stamp joins.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [ ] T002 Create `src/platform/Tests/SocAlytics.Platform.Host.Tests/StampCollection.cs` per `contracts/stamp-fixture.md` and `data-model.md`: `[CollectionDefinition("Stamp")]` class `StampCollection : ICollectionFixture<StampFixture>`, and `StampFixture : IAsyncLifetime` that builds and starts `Projects.SocAlytics_Platform_AppHost` with `--SocAlytics:LocalDatabase:Persistent=false` and `--SocAlytics:ApiHttpsEndpoint=false`, waits for `api` healthy under one bounded 5-minute `CancellationTokenSource` (H2 is ruled out in the "Evidence" section of `research.md`, so no wait for the document is added), records any start exception and rethrows it from `CreateApiClient()` (an `HttpClient` for `api`, endpoint `http`) for every test without caching a success it did not observe, and stops and disposes the stamp once in `DisposeAsync`. Put a short comment on the fixture naming the evidenced cause (H1 in the "Evidence" section of `research.md`). No fixed delay, retry, skip, or longer limit. If T001 proves H2 in covered code (the API reports healthy before `GET /openapi/v1.json` is served), also fix API readiness in `src/platform/SocAlytics.Platform.Api` or the AppHost composition in `src/platform/SocAlytics.Platform.AppHost`, keeping the start order PostgreSQL, Migrator, API, and include the change in the scope of T014 and T015, and add a focused Host test in `src/platform/Tests/SocAlytics.Platform.Host.Tests` that fails when `api` reports healthy before `GET /openapi/v1.json` is served (without removing or weakening any existing assertion), counted as an added test in the T013 baseline comparison; otherwise state in the final summary that no covered code changed.

**Checkpoint**: The fixture builds (`dotnet build src/platform/Tests/SocAlytics.Platform.Host.Tests`).

---

## Phase 3: User Story 1 - The registry operation test fails only when the document is wrong (Priority: P1) 🎯 MVP

**Goal**: `OpenApiDocumentContainsAllAnalystRegistryOperationIds` passes whenever the document contains every Analyst Registry operation id, however loaded the runner is, and fails only on its assertion.

**Independent Test**: Run the test 20 times in a row alone; every run passes. Probes make it fail on its assertion or on the start error within the existing limits.

- [ ] T003 [US1] In `src/platform/Tests/SocAlytics.Platform.Host.Tests/AnalystRegistryOpenApiTests.cs` add `[Collection("Stamp")]` and a constructor taking `StampFixture`; make both tests (`OpenApiDocumentContainsAllAnalystRegistryOperationIds` and `AnalystRegistryOperationsDeclareJsonSchemasForSuccessResponsesAndRequestBodies`) read the document through the fixture's `CreateApiClient()` and keep a bounded 5-minute token for the read; remove only the per-test AppHost build, start, and wait; keep every assertion and expected value textually unchanged.
- [ ] T004 [US1] Verify SC-001 and SC-002 for the registry tests: run `dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --no-build --filter "FullyQualifiedName~AnalystRegistryOpenApiTests"` at least 20 consecutive times (all pass), and at least 5 consecutive runs of the same registry-class filter under load generated with `stress-ng --cpu $(nproc)` (or `$(nproc)` busy-loop processes when `stress-ng` is missing) running for the whole duration of the runs (the whole project under load is run once, in T013); drive the repeated runs with one shell loop that stops at the first failure and records the counts, and run the loaded runs exactly the minimum number of times in SC-001 (no more); then with temporary, uncommitted probes (remove one Analyst Registry operation from the served document; make the stamp unable to become ready, for example through a failing Migrator environment value) confirm the first fails on its `ShouldContain` assertion and the second fails every dependent test with the fixture's start error, both within the existing limits and without hanging; remove the probes and confirm `git status` shows no probe change. Record the run counts and outcomes in the "Evidence" section of `specs/20261024-070502-deterministic-openapi-host-tests/research.md`.

**Checkpoint**: User Story 1 is verified independently.

---

## Phase 4: User Story 2 - The other Host tests stay deterministic (Priority: P2)

**Goal**: Every other Host test that starts the AppHost keeps every assertion and passes at least as reliably; no two stamp-starting classes run tests concurrently; at most two stamps exist at once (the shared stamp and one `PlatformHostTests` stamp).

**Independent Test**: The whole Host test project passes five runs in a row under load, with an unchanged number of tests and assertions.

### Route the OpenAPI classes through the shared stamp

- [ ] T005 [P] [US2] in `src/platform/Tests/SocAlytics.Platform.Host.Tests/AgentCatalogOpenApiTests.cs` add `[Collection("Stamp")]`, a `StampFixture` constructor parameter, and read the document through `CreateApiClient()` instead of building and starting an AppHost; keep all assertions unchanged; keep a bounded 5-minute CancellationTokenSource for the document read, as in T003.
- [ ] T006 [P] [US2] In `src/platform/Tests/SocAlytics.Platform.Host.Tests/OpenApiContractTests.cs` add `[Collection("Stamp")]`, a `StampFixture` constructor parameter, and read the document through `CreateApiClient()` instead of building and starting an AppHost; keep all assertions unchanged; keep a bounded 5-minute CancellationTokenSource for the document read, as in T003.
- [ ] T007 [P] [US2] In `src/platform/Tests/SocAlytics.Platform.Host.Tests/OpenApiSnapshotTests.cs` add `[Collection("Stamp")]`, a `StampFixture` constructor parameter, and read the document through `CreateApiClient()` instead of building and starting an AppHost; keep the `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1` compare-or-write behavior and the snapshot path unchanged; keep a bounded 5-minute CancellationTokenSource for the document read, as in T003.
- [ ] T008 [P] [US2] In `src/platform/Tests/SocAlytics.Platform.Host.Tests/RecordingsOpenApiTests.cs` add `[Collection("Stamp")]`, a `StampFixture` constructor parameter, and read the document through `CreateApiClient()` instead of building and starting an AppHost; keep all assertions unchanged; keep a bounded 5-minute CancellationTokenSource for the document read, as in T003.
- [ ] T009 [P] [US2] In `src/platform/Tests/SocAlytics.Platform.Host.Tests/RegistrationContractShapeTests.cs` add `[Collection("Stamp")]`, a `StampFixture` constructor parameter, and read the document through `CreateApiClient()` instead of building and starting an AppHost; keep all assertions unchanged; keep a bounded 5-minute CancellationTokenSource for the document read, as in T003.
- [ ] T010 [P] [US2] In `src/platform/Tests/SocAlytics.Platform.Host.Tests/RegistryOpenApiTests.cs` add `[Collection("Stamp")]`, a `StampFixture` constructor parameter, and read the document through `CreateApiClient()` instead of building and starting an AppHost; keep all assertions unchanged; keep a bounded 5-minute CancellationTokenSource for the document read, as in T003.
- [ ] T011 [P] [US2] In `src/platform/Tests/SocAlytics.Platform.Host.Tests/WorkflowOperationsOpenApiTests.cs` add `[Collection("Stamp")]`, a `StampFixture` constructor parameter, and read the document through `CreateApiClient()` instead of building and starting an AppHost; keep all assertions unchanged; keep a bounded 5-minute CancellationTokenSource for the document read, as in T003.

### Keep stamps that need other options out of concurrency

- [ ] T012 [US2] In `src/platform/Tests/SocAlytics.Platform.Host.Tests/PlatformHostTests.cs` add `[Collection("Stamp")]` to `PlatformHostTests` so its own AppHosts (`AppHostStartsHealthyApiWithOperationalOpenApiSurface`, which asserts start order and per-resource readiness, and `AppHostDoesNotStartApiWhenMigratorFails`, which changes the Migrator environment) run one at a time next to the one shared stamp (at most two stamps at once) and never overlap other classes; keep every test and assertion; `AppHostStartsHealthyApiWithOperationalOpenApiSurface` and `AppHostDoesNotStartApiWhenMigratorFails` keep their own start (Decision 2 of `research.md`) and the three build-only tests start nothing, so no `PlatformHostTests` test reads the shared stamp unless the evidence of T001 says otherwise.

### Verification

- [ ] T013 [US2] Verify SC-001, SC-003, and FR-006: run `dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --no-build` at least 5 consecutive times under processor load generated with `stress-ng --cpu $(nproc)` (or `$(nproc)` busy-loop processes when `stress-ng` is missing) running for the whole duration of the runs (all pass, test count unchanged from the baseline in the "Evidence" section of `research.md` except for the focused test T002 added when it proved H2 in covered code), driven by one shell loop that stops at the first failure and records the counts, with exactly the minimum number of loaded runs in SC-001 (no more); compare the count of lines matching `Should` and `Assert\.` in the Host test files and the number from `dotnet test --list-tests` with the baseline in the "Evidence" section of `research.md`; record the wall-clock duration of one unloaded whole-project run after the change next to the baseline duration in the "Evidence" section of `research.md` and compare them in the final summary; run the quickstart step 4 commands (`git diff --stat -- src/platform/SocAlytics.Platform.Api/openapi/v1.json` is empty and no assertion line is only removed in `git diff -U0 -- src/platform/Tests/SocAlytics.Platform.Host.Tests`); with `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT` unset confirm `OpenApiSnapshotTests` still compares. Record the outcomes, the load generator and level, and the residual overlap of the `PlatformHostTests` stamps with the shared stamp under load in the final summary (research.md is read-only for tasks).

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
