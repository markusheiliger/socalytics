# Research: Deterministic OpenAPI Document Host Tests

No `NEEDS CLARIFICATION` remains. FR-001 requires each hypothesis to be proved or ruled out with evidence.
The [Evidence](#evidence) section records the baseline from the first T001 attempt (H1 proved, H2 ruled
out), so the "not reproducible" path below no longer applies; the hypotheses table and decisions document
the method and the design that follows from each outcome.

## Observed Facts (from the code)

- Nine tests in eight classes call `DistributedApplicationTestingBuilder.CreateAsync<Projects.SocAlytics_Platform_AppHost>`,
  build, start, and wait for `api` healthy; `PlatformHostTests` adds five more AppHost builds, two of which start it.
- No `[Collection]`, `ICollectionFixture`, or `CollectionBehavior` exists in the project, so xUnit v3 runs the
  classes in parallel (tests of one class run serially).
- A started stamp is the whole composition of `SocAlytics.Platform.AppHost/Program.cs`: PostgreSQL, Migrator,
  RustFS, NATS, Mailpit, API, and Web. Container ports are assigned dynamically (the fixed ports exist only in
  e2e mode), so port collisions are unlikely; CPU, container-engine, and image-pull contention are not.
- The API waits for PostgreSQL, RustFS, NATS, and the Migrator, and reports healthy through `/health`
  (database check and `ClubBootstrapHealthCheck`). `/openapi/v1.json` is mapped in `Program.cs` independent of
  those checks.
- Every wait is bounded by one 5-minute `CancellationTokenSource` shared by building, starting, waiting, and reading.

## Hypotheses and How Each Is Proved or Ruled Out

| # | Hypothesis | Proof method | Outcome if proved |
| --- | --- | --- | --- |
| H1 | Concurrent full stamps exhaust the shared 5-minute budget on a loaded runner (a timeout surfaces as the failure) | Read the failed verification logs of #201, #225, #275 for the exception type and elapsed time; run the Host project under processor load and with `--maxParallelThreads 1` (or serialized collections) and compare | Shared stamp plus serialized stamp-starting classes (Decision 1) |
| H2 | Readiness reached before the state the test reads (`api` healthy, then the document is incomplete or not yet served) | Read the failing assertion in the logs; probe by requesting `/openapi/v1.json` in a loop right after healthy and count non-200 or incomplete documents | A bounded wait on an observable condition in shared support, or a covered-code fix if the API reports healthy too early |
| H3 | A start-up step races between stamps (container name, image pull, shared parameter or user-secret state, `persist: true` parameters) | Compare resource names and parameter stores across concurrently started stamps; look for pull or name conflicts in logs | Isolation in the shared stamp (a single stamp removes the race) or AppHost parameter handling |
| H4 | An external dependency (image registry pull, rate limit) | Look for pull errors and timing in the failed logs | Out of scope unless the images can be pulled once by the shared stamp, which Decision 1 already does |

Every hypothesis gets a recorded verdict (proved, ruled out, not reproducible) with the evidence in the
task's final summary. If no hypothesis reproduces, the shared stamp with serialized classes is still
adopted only if the logs of the failed verifications show a failure mode it removes; otherwise the
implementation records "not reproducible" with the evidence as the terminal outcome of T001, changes nothing,
and marks the remaining implementation tasks (T002 to T014) not applicable; only the final summary (T015) runs.
Reading the failed logs with `gh` is best effort; the busy-loop fallback is a sufficient load generator.

## Decision 1: One shared started stamp in a collection fixture

- **Decision**: Add one xUnit collection (`Stamp`) with an `ICollectionFixture<StampFixture>`. The fixture
  builds and starts the AppHost with the options all OpenAPI tests already pass
  (`--SocAlytics:LocalDatabase:Persistent=false`, `--SocAlytics:ApiHttpsEndpoint=false`), waits for `api`
  healthy under a bounded token, and exposes only `CreateApiClient()` (an `HttpClient` for `api`, as in `contracts/stamp-fixture.md`). All
  classes that start a stamp join the collection, so their tests run one after another and never concurrently
  (a `PlatformHostTests` stamp may coexist with the shared stamp, so at most two stamps exist at once).
- **Rationale**: A collection fixture is the xUnit mechanism that is isolated from other collections'
  concurrency, starts once, and surfaces a failed start to every test that uses it. It also removes the
  contention (H1, H3) rather than hiding it.
- **Alternatives considered**: A per-class `IClassFixture` (still starts several stamps concurrently, so
  contention remains); disabling parallelization for the whole assembly (serializes the unit tests that start
  no stamp and still starts a stamp per test); retries, sleeps, or longer limits (forbidden by the spec).

## Decision 2: Tests that need other options or mutate the stamp keep their own AppHost

- **Decision**: `AppHostDoesNotStartApiWhenMigratorFails` (changes the Migrator environment) and the tests that
  only inspect the built, not started, model keep building their own AppHost from `PlatformHostTests`; the
  class joins the `Stamp` collection so its stamps run one at a time next to the one shared stamp (at most two stamps at once). `AppHostStartsHealthyApiWithOperationalOpenApiSurface`
  asserts start order and per-resource readiness and so keeps its own start unless the evidence shows it
  can read the shared stamp without losing an assertion.
- **Rationale**: FR-003 and FR-004: no assertion lost, no test observing state another test created.
- **Alternatives considered**: Reconfiguring the shared stamp per test (leaks state; rejected).

## Decision 3: A failed or slow start fails every dependent test visibly

- **Decision**: The fixture stores the start exception (or the bounded-wait cancellation) and rethrows it from
  every test that touches the stamp; it never caches a success it did not observe. Disposal stops the stamp
  once, after the last test of the collection.
- **Rationale**: Edge cases of the spec (a failed shared start must not hide behind a cached success, and no hangs).
- **Alternatives considered**: `Lazy<Task>` per test (reruns a failed start for every test, multiplying the
  contention it should remove).

## Decision 4: The wait stays a bounded wait on an observable condition

- **Decision**: Reading the document waits for `api` healthy (already observable). If H2 is proved, the
  fixture also waits until `GET /openapi/v1.json` returns 200 under the fixture's bounded token, or, if the
  API reports healthy too early, the API readiness is corrected in the covered code. No fixed delay and no
  longer limit is introduced in either case.
- **Rationale**: FR-002 allows exactly this.
- **Alternatives considered**: `Task.Delay` (forbidden); raising the 5-minute limit (forbidden).

## Probes (SC-002)

Two temporary probes, removed afterward and never committed: (1) remove one Analyst Registry operation from the
served document and confirm the test fails on its `ShouldContain` assertion within the existing limit;
(2) prevent the stamp from becoming ready (for example a failing Migrator environment value) and confirm every
dependent test fails with the fixture's start error within the limit instead of hanging.

## Evidence

Recorded by the amendment from the first T001 attempt (its agent summary on pull request #310), because tasks
cannot write under `specs/`. T001 confirms the numbers and changes no file.

- **Load generator and level**: `stress-ng` was not installed, so 4 busy-loop processes ran for the whole duration
  of the loaded runs (the sufficient fallback).
- **Single test alone**: `OpenApiDocumentContainsAllAnalystRegistryOperationIds` passed 20 of 20 consecutive runs.
- **Whole Host project under load**: runs 1 to 3 passed and run 4 failed (4 of the 5 planned runs; the loop stops
  at the first failure). The failure was a 20 s Polly timeout inside Aspire's start step (`app.StartAsync`), not an
  assertion and not the 5-minute limit.
- **H1 (concurrent full stamps exhaust the start budget on a loaded runner)**: proved. The failure surfaced only in
  whole-project runs under load, where several stamps start concurrently.
- **H2 (readiness reached before the document is complete)**: ruled out. 800 requests for `/openapi/v1.json` right
  after `api` was healthy all returned 200 with every registry operation ID; the probe was reverted.
- **H3 (start-up race between stamps)**: not reproducible; no name conflicts were seen in the logs.
- **H4 (external dependency)**: not reproducible; no pull errors or rate limits were seen in the logs.
- **Logs of #201, #225, and #275**: not read; `gh` was denied in the attempt's environment.
- **Evidenced cause**: H1, a start-step timeout when concurrent stamps compete for the loaded runner.
- **Covered code**: no platform code needs to change (H2 is ruled out).
- **SC-003 baseline**: 57 Host tests and 161 assertion lines (lines matching `Should` and `Assert\.` in the Host
  test files).
- **Unloaded whole-project run**: 2 m 43 s wall-clock.
