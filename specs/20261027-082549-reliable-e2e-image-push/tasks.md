# Tasks: Reliable End-to-End Analyst Image Push

**Input**: Design documents from `/specs/20261027-082549-reliable-e2e-image-push/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [quickstart.md](quickstart.md)

**Tests**: Required by FR-011: the digest determination, the retry classification, the limits, the bound, and the reuse have unit tests that run without Docker (no `[Collection("EndToEnd")]`, no fixed sleeps, a fake `TimeProvider`). The existing Docker-backed tests are not weakened (FR-010).

**Organization**: Tasks are grouped by user story. Paths are relative to `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/` unless they start with `src/`, `docs/`, or `specs/`. Only the end-to-end test support code, its unit tests, and documentation change (FR-013).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (US1 to US4)

## Phase 1: Setup, Foundation, and User Story 1 - A completed push is recognized however the engine reports it (Priority: P1) 🎯 MVP

**Purpose**: Prepare the engine client and the named limits, then fix the digest determination. This is one phase because the phase verification runs the whole Docker-backed end-to-end project, whose push-based tests fail with `The push did not report a manifest digest` until the digest fix (T007) landed; a phase that ends before T007 can never pass it. The cause evidence (FR-001, T001) follows the fix for the same reason.

**Goal**: The manifest digest of a completed push is taken from the progress record, the final status line, the local registry, or the engine's image record, in that order, and the push fails with the full response only when no source yields one.

**Independent Test**: `dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~ImagePush"` passes without Docker for each recorded response shape.

### Engine client move

- [x] T002 [P] Move `DockerEngine` out of `Support/TestAnalystImage.cs` into the new `Support/DockerEngine.cs` unchanged in behavior, and give it a constructor seam that accepts an `HttpMessageHandler` and a registry base address (defaults keep today's engine socket and registry use) so lookups can run against a fake endpoint

### Foundation (blocking prerequisites)

**⚠️ CRITICAL**: No later user story work can begin until this foundation is complete

- [x] T003 Create `Support/EngineCallPolicy.cs` with the named values of [data-model.md](data-model.md) in one place: `PushLimit` 15 minutes, `BuildLimit` 20 minutes, `RegistryLookupLimit` 30 seconds, `MaxAttempts` 3 (including the first), `RetryDelay` 2 seconds, each with a documenting comment of its meaning (execution logic follows in T009)

### Digest determination (before its tests, which name its types)

- [x] T006 [US1] Create `Support/PushDigest.cs`: a pure determination over (response body, repository, tag, registry and engine lookups as delegates) that returns the digest and its source (`ProgressRecord`, `StatusLine`, `RegistryManifest`, `EngineImageRecord`) or a failure carrying the body and the outcome of each tried source (`found`, `absent`, `unavailable: <reason>`); an error line fails first; a digest is valid only as `^sha256:[0-9a-f]{64}$`; a status line counts only for the pushed tag; the first valid source ends the determination except that the record and the status line, both read from the response, must agree; registry and engine are consulted only when the response yielded none and must agree when both were consulted The public API is `PushDigest.DetermineAsync(body, repository, tag, registryLookup, engineLookup, ct)` returning a `PushDigestResult` (`Succeeded`, `Digest`, `Source`, `FailureMessage`) with a `DigestSource` enum; the lookups are `Func<CancellationToken, Task<string?>>` that return `null` when the source has no digest and throw when it is unavailable, and `FailureMessage` lists one entry per tried source such as `EngineImageRecord: unavailable: boom`. It comes before T004 because the whole solution is built for every task verification, so tests that name `PushDigestResult` cannot compile, and cannot pass verification, before this type exists

### Tests for User Story 1

- [x] T004 [P] [US1] Write `ImagePush/PushDigestTests.cs` with recorded response bodies as string constants: progress record only, status line only, both agreeing, both disagreeing (fails with diagnostics, FR-004), neither with the registry reporting the digest, neither with only the engine record, none (fails with the full body and each source's result), an `errorDetail` or `error` line (fails with that error before any digest, no lookup), a status line for another tag (ignored), a malformed digest (not `^sha256:[0-9a-f]{64}$`, never accepted from any source), a malformed JSON line (deterministic failure with the body), a valid response digest causing no registry or engine lookup (delegates assert not called), and a failure message that never contains `X-Registry-Auth` values or credentials
- [x] T005 [P] [US1] Write `ImagePush/DockerEngineLookupTests.cs` against a fake `HttpMessageHandler` through the `DockerEngine` constructor seam: the registry manifest lookup (`GET /v2/<repository>/manifests/<tag>`, header `Docker-Content-Digest`, with the OCI index, OCI manifest, Docker manifest list, and Docker manifest v2 `Accept` types), the engine image-record lookup (`GET /images/<name>/json`, `RepoDigests` entry of the pushed repository only), an engine without the image record lookup not hiding the diagnostics of earlier sources, and registry and engine disagreeing when both were consulted

### Implementation for User Story 1

- [x] T007 [US1] Edit `Support/DockerEngine.cs`: `PushAsync` reads the whole response and uses `PushDigest`, adds the registry manifest lookup and the engine image-record lookup (each under `EngineCallPolicy.RegistryLookupLimit`), and throws the determination's failure with the full body; no lookup when the response already carries a valid digest

### Evidence after the fix

- [x] T001 [US1] After T007 (the task verification runs the whole end-to-end project for a non-`.cs` file, and its Docker-backed push tests fail until the digest fix lands), create `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/README.md` (the project's first documentation artifact) with a `## Cause` section that establishes the cause with evidence: read `git log` over `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests`, `src/analysts/`, `src/platform/Tests/SocAlytics.Platform.Integration.Tests` (registry fixture), and `.github/actions/environment-*` between the passing and the failing branch; record the push response shapes of the engine in the environment only where a Linux engine is reachable (a recorded body of a push, the status line, whether the `aux` record appears) and otherwise state that they are not recorded; record which timeout the Testcontainers build client applies (its own request timeout, separate from the `HttpClient` of `DockerEngine`) and its value, read from the restored package; name which suspected cause (digest from one source only, load and the shared five-minute timeout, a recent change on main, or another) is evidenced, and that the fix holds under either. Do not edit `research.md`: its `## Evidence` section already holds the code-level findings and the git-history result, and this task's agents may not change files under `specs/`

**Checkpoint**: Phase 1 ends with User Story 1 functional and testable without Docker, and the Docker-backed push tests passing again.

---

## Phase 2: User Story 2 - Pushes and builds are bounded, cancellable, and retried only for transient failures (Priority: P2)

**Goal**: A push or build ends at its named limit or the test's cancellation, and only transient failures are retried, at most `MaxAttempts` times, with every reason reported.

**Independent Test**: The `ImagePush` unit tests with a fake engine endpoint and fake `TimeProvider` show a stalled call ending at the limit or cancellation, a transient failure repeated exactly `MaxAttempts` times with each reason, and a deterministic failure tried once.

### Tests for User Story 2

- [x] T008 [P] [US2] Write `ImagePush/EngineCallPolicyTests.cs` with a fake `HttpMessageHandler` and fake `TimeProvider` (no real sleeps): a call longer than the former five-minute limit but within `PushLimit` completes, a stalled call ends at the named limit and is reported as that limit, cancellation of the test's token stops the call at once with no retry, a transient failure (`HttpRequestException`, `IOException`, a call timeout that was not a cancellation, HTTP 502, 503, 504, 429, a Testcontainers build exception with such an inner exception) is repeated exactly `MaxAttempts` times with the reason of every attempt in the final message, success on a later attempt stops the retries, a registry lookup against a registry that is not yet answering handled as a transient condition and repeated within `RegistryLookupLimit` rather than reported as a missing digest, and a deterministic failure (an error in the response, a malformed response, a digest conflict, another 4xx, a build exception without a transient cause) is tried once

### Implementation for User Story 2

- [x] T009 [US2] Edit `Support/EngineCallPolicy.cs`: add the execution of an engine call under a linked token (the test's token plus `CancelAfter(limit)`), the classification into `Succeeded`, `Transient(reason)`, `Deterministic(exception)`, and `Cancelled` per [research.md](research.md) D3, the bounded attempts with `RetryDelay` read through `TimeProvider`, and the final exception that names the limit or condition and lists every attempt's reason without credentials — verified without changes by the assessed verification at 5445577e (no change by this task)
- [x] T010 [US2] Edit `Support/DockerEngine.cs`: set `HttpClient.Timeout` to infinite so the shared client timeout no longer ends any call, run the push under `EngineCallPolicy` with `PushLimit` and the test's cancellation token, and run the registry and image-record lookups under `RegistryLookupLimit` with the transient handling tested by the registry-not-yet-answering case of T008

### Build helpers under the policy

- [x] T011 [P] [US2] Edit `Support/TestAnalystImage.cs`: run the image build and the push under `EngineCallPolicy` (`BuildLimit`, `PushLimit`, the test's cancellation token) and use the reworked `DockerEngine`. If T001 recorded that the Testcontainers build client applies a default shorter than `BuildLimit`, configure or run the build so that `BuildLimit` and the test's token are what end it, or document the limitation in the README (T028)
- [x] T012 [P] [US2] Edit `Support/ReferenceAnalystImage.cs`: run the image build and the push under `EngineCallPolicy` (`BuildLimit`, `PushLimit`, the test's cancellation token). If T001 recorded that the Testcontainers build client applies a default shorter than `BuildLimit`, configure or run the build so that `BuildLimit` and the test's token are what end it, or document the limitation in the README (T028)
- [x] T013 [P] [US2] Edit `Support/ShotBoundaryAnalystImage.cs`: run the image build and the push under `EngineCallPolicy` (`BuildLimit`, `PushLimit`, the test's cancellation token). If T001 recorded that the Testcontainers build client applies a default shorter than `BuildLimit`, configure or run the build so that `BuildLimit` and the test's token are what end it, or document the limitation in the README (T028)

**Checkpoint**: User Stories 1 and 2 work and are testable without Docker.

---

## Phase 3: User Story 3 - Repeated large pushes are avoided and builds and pushes stay bounded (Priority: P3)

**Goal**: One process-wide gate bounds concurrent builds and pushes, and one image variant is built and pushed once per test class.

**Independent Test**: The `ImagePush` unit tests with a concurrency probe show no more than `MaxConcurrentBuildsAndPushes` at once and one build per variant per class, with a failed build reported to every waiting test.

### Tests for User Story 3

- [x] T014 [P] [US3] Write `ImagePush/ImageBuildGateTests.cs`: a probe of concurrent builds on a fresh `ImageBuildGate` instance (never the process-wide static one, so the test neither waits for nor affects real builds) never exceeds that instance's limit, the gate is released on success, failure, and cancellation, a cancelled waiter never holds it, and code that does not build or push never waits for it
- [x] T015 [P] [US3] Write `ImagePush/AnalystImageCacheTests.cs`: concurrent tests (each using a fresh `ImageBuildGate` instance injected into the cache and the build delegate, never the static one) needing the same variant and inputs share one build and push, a different variant or other inputs gets its own image and is never served a mismatching one, a faulted build is reported to every waiting test and removed so a later test may retry (never cached as success), cancellation of one waiting test does not cancel the build for the others, a build whose waiters all cancelled stops, releases the gate, and has its entry removed, the per-key build count is observable, and a cached build cannot acquire the gate twice (no deadlock)

### Implementation for User Story 3

- [x] T016 [US3] Create `Support/ImageBuildGate.cs`: an instance type with a constructor limit, its own `SemaphoreSlim`, and its own observed-maximum concurrency counter that tests read; the process-wide gate is a single static instance created with the named `MaxConcurrentBuildsAndPushes` of 1 and its documenting comment; the gate is acquired with the test's token and released in `finally`
- [x] T017 [US3] Create `Support/AnalystImageCache.cs`: an xunit class fixture that owns the class's single `registry:2` through `OciRegistryContainerFixture`, a single-flight entry per key (helper, mode or variant, inputs) following the state machine of [data-model.md](data-model.md), a build running under the cache's own token bounded by the limits, which is cancelled when the class fixture is disposed or when the last waiting test has cancelled (the gate is then released and the entry removed), and calling the helper's uncached `BuildAndPushAsync`, a per-key build count, one log line of the build counts and the gate's observed maximum when the fixture is disposed, and removal of the class's images and disposal of the registry when the class ends

### Helpers use the gate and the cache

- [x] T018 [P] [US3] Edit `Support/TestAnalystImage.cs`: acquire `ImageBuildGate` in exactly one place, inside the uncached `BuildAndPushAsync`, around the build and the push of one image together (released in `finally`), and expose a cached entry point through `AnalystImageCache`
- [x] T019 [P] [US3] Edit `Support/ReferenceAnalystImage.cs`: acquire `ImageBuildGate` in exactly one place, inside the uncached `BuildAndPushAsync`, around the build and the push (released in `finally`), and expose a cached entry point through `AnalystImageCache`
- [x] T020 [P] [US3] Edit `Support/ShotBoundaryAnalystImage.cs`: acquire `ImageBuildGate` in exactly one place, inside the uncached `BuildAndPushAsync`, around the build and the push (released in `finally`), and expose a cached entry point through `AnalystImageCache`

### Call sites of the six classes that build and push

Each class takes the cached image where the reuse rule allows; a test that calls `RemoveAsync`, force-removes images to prove cleanup, or otherwise depends on the image being its own keeps its own build and push through the uncached helper. No assertion is removed or loosened.

- [x] T021 [P] [US3] Edit `ReferenceAnalystImageTests.cs` to use the class-scoped `AnalystImageCache` fixture for its image variants
- [x] T022 [P] [US3] Edit `ReferenceAnalystEndToEndTests.cs` to use the class-scoped `AnalystImageCache` fixture for its image variants
- [x] T023 [P] [US3] Edit `ShotBoundaryImageTests.cs` to use the class-scoped `AnalystImageCache` fixture for its image variants
- [x] T024 [P] [US3] Edit `ShotBoundaryEndToEndTests.cs` to use the class-scoped `AnalystImageCache` fixture for its image variants
- [x] T025 [P] [US3] Edit `DockerRuntimeEndToEndTests.cs` to use the class-scoped `AnalystImageCache` fixture where its tests need the same variant and inputs; acceptance: after the edit each test either takes the cached image or is named in a comment with the reason it keeps its own (for example it calls `RemoveAsync` or force-removes images), including the registries created in the nested helper
- [x] T026 [P] [US3] Edit `SegmentMediaEndToEndTests.cs` to use the class-scoped `AnalystImageCache` fixture where its tests need the same variant and inputs; acceptance: after the edit each test either takes the cached image or is named in a comment with the reason it keeps its own (for example it calls `RemoveAsync` or force-removes images)

**Checkpoint**: User Stories 1 to 3 work; builds and pushes are bounded and reused.

---

## Phase 4: User Story 4 - The cause and the fix are recorded and no test is weakened (Priority: P3)

**Goal**: The documentation names the evidenced cause, the digest sources in order, the limits, the retry rule, the bound, the reuse rule, and the run commands, and no Docker-backed test was weakened.

**Independent Test**: Read the README and compare assertions and attributes of the tests before and after with `git diff` against the base.

### Documentation

- [x] T027 [P] [US4] Only if the change made one of the two end-to-end evidence statements of `docs/architecture/platform-implementation.md` (the Docker-backed end-to-end test bullet of the evidence list) or `docs/architecture/analysts-models-and-hardware.md` (the sentence that the Docker-backed end-to-end tests register the development Analyst images) false, correct that statement; the plan expects none to be false (neither statement says how a test image is pushed), so the architecture stays unchanged otherwise and the task needs no file change — already done by a push to the implementation branch (verified; no change by this task)
- [x] T028 [P] [US4] Extend the `README.md` that T001 created in the end-to-end test project: keep its `## Cause` section from T001 (correct it only where the final design makes it false), the digest sources in order, the named limits and values of `EngineCallPolicy`, the retry rule (transient only, at most `MaxAttempts`, every reason reported, never after cancellation), the build and push bound with its value and why it is a guard, the per-class reuse rule and the tests that keep their own image, and the run commands of [quickstart.md](quickstart.md) (restore, build, the `ImagePush` filter, the full project, the Images tests), stating these are development values and not deployment support or production readiness
- [x] T029 [P] [US4] Edit `src/platform/README.md` to link to the end-to-end project's README and correct its end-to-end test description only where the change makes it false

### Verification of the rules

- [x] T030 [US4] Compare the tests of the end-to-end project and of `src/platform/Tests/SocAlytics.Platform.Images.Tests` before and after with `git diff` against the base: no assertion removed or loosened, no test skipped, no test's own behavior timeout lengthened, every Docker-backed test still requires a running engine, no fixed sleep added other than the named `RetryDelay`, list the tests that kept their own build and push and confirm that each has the reason named in a comment (T025, T026), and nothing changed outside the end-to-end support code, its tests, the documentation, and this spec folder (FR-010, FR-013, SC-006); fix any deviation found

**Checkpoint**: All user stories are complete.

---

## Phase 5: Polish & Cross-Cutting Concerns

- [x] T031 Run `node .github/scripts/check-markdown.mjs --files src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/README.md src/platform/README.md specs/20261027-082549-reliable-e2e-image-push/research.md` and fix every finding — verified without changes by the assessed verification at 9d5db6d4 (no change by this task)
- [x] T032 Run `dotnet restore src/platform/SocAlytics.Platform.slnx`, `dotnet build src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --no-restore`, and `dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --no-build --filter "FullyQualifiedName~ImagePush"` and fix every failure (quickstart step 1) — verified without changes by the assessed verification at 2df57419 (no change by this task)
- [x] T033 Run the full Docker-backed verification against a Linux engine, `dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --no-build` and `dotnet test src/platform/Tests/SocAlytics.Platform.Images.Tests`, and fix every failure caused by this change (quickstart step 2; the repeated-run stability evidence of SC-001 is recorded manually by a maintainer in `research.md` and is not a gate) — verified without changes by the assessed verification at ac74d91e (no change by this task)

---

## Dependencies & Execution Order

- **Phase 1** (T002, T003, US1 T004 to T007, then T001) has no dependencies outside itself and is one phase so that the phase verification, which runs the whole Docker-backed end-to-end project, only runs after the digest fix. T001 runs after T007 (a README change in the end-to-end project makes the task verification run all of its Docker-backed tests, which fail until the digest fix landed) and must finish before T011 to T013 and T028.
- **Foundation** (T003) is needed by every story; T007 and T010 use its values.
- **US1** (T004 to T007): T006 first, because T004 names its `PushDigestResult` and the solution build of every task verification fails without it (T004 failed twice on that); then tests T004 and T005 (T005 needs T002); T007 needs T006, T002, and T003; T001 needs T007.
- **US2** (T008 to T013) depends on US1 because T010 edits `Support/DockerEngine.cs` after T007; T009 before T010 to T013; T011 to T013 touch different files and are parallel.
- **US3** (T014 to T026) depends on US2 (the helpers are edited again): T016 before T017; T017 before T018 to T020; T018 to T020 before T021 to T026.
- **US4** (T027 to T030) depends on the final design; T028 needs T001; T030 runs last of the story.
- **Polish** (T031 to T033) depends on every story.

## Parallel Example

```text
Phase 1, after T007: T001 alone.
US1 tests, after T006: T004 and T005 together.
US3 call sites: T021 to T026 together (six different files).
```

## Implementation Strategy

- **MVP first**: Phase 1 (setup, foundation, and US1) fixes the failure that stops #335: a completed push is recognized however the engine reports it. Validate with the `ImagePush` unit tests.
- **Incremental delivery**: add US2 (limits, cancellation, bounded transient retries), then US3 (gate and per-class reuse), then US4 (documentation and the no-weakening comparison), then the Polish verification.
- **Stop rule**: do not weaken or skip a test, and do not change product code, Dockerfiles, images, contracts, or workflows (FR-010, FR-013).
