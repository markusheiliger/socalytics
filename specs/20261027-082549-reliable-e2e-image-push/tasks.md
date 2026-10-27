# Tasks: Reliable End-to-End Analyst Image Push

**Input**: Design documents from `specs/20261027-082549-reliable-e2e-image-push/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [quickstart.md](quickstart.md)

**Tests**: Requested by FR-011: the digest determination, the retry classification, the limits, and the bound have unit
tests that run without Docker. They are written before the code they cover.

**Organization**: Grouped by user story. All paths are relative to the repository root; `E2E` below is
`src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests`. No product code, Dockerfile, image, contract, or workflow
changes (FR-013); no existing test is skipped, weakened, or loosened (FR-010); no fixed sleep is added to wait out a
failure.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit (different files, no dependencies on each other)
- **[Story]**: The user story the task belongs to (US1 to US4)

## Phase 1: Setup (Evidence)

**Purpose**: Establish the cause with evidence before the fix is finalized (FR-001).

- [ ] T001 Establish the cause and record it in `specs/20261027-082549-reliable-e2e-image-push/research.md` under `## Evidence`: read the history of `E2E`, `src/analysts/`, and `.github/actions/environment-setup` and `.github/actions/environment-verify` with `git log`, state which push response shapes the engine produces (the final status line `<tag>: digest: sha256:… size: …` with and without an `aux` record) as recorded string bodies, note the timings of builds and pushes if an engine is available, and keep the fix valid under either suspected cause (missing digest source, shared five-minute client timeout); the stability runs of SC-001 stay a manual entry and are not a gate

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Give the engine client its own file so every story edits it in one place.

- [ ] T002 Move the `DockerEngine` class unchanged in behavior out of `E2E/Support/TestAnalystImage.cs` into the new `E2E/Support/DockerEngine.cs` (same namespace and accessibility, still usable by `E2E/DockerRuntimeEndToEndTests.cs` and the three image helpers) and check with `dotnet build src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests`

**Checkpoint**: The project builds and the engine client lives in `Support/DockerEngine.cs`.

---

## Phase 3: User Story 1 - A completed push is recognized however the engine reports it (Priority: P1) 🎯 MVP

**Goal**: A completed push yields the manifest digest whichever way the engine reports it, and fails with the full response only when no source yields one (FR-002 to FR-004).

**Independent Test**: `dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~PushDigest"` passes without Docker; then the Docker-backed classes that push an image no longer fail with "The push did not report a manifest digest".

### Tests for User Story 1

- [ ] T003 [P] [US1] Create `E2E/ImagePush/PushDigestTests.cs` (no `[Collection("EndToEnd")]`, no Docker) with recorded push-response bodies as string constants and delegate fakes for the registry and engine lookups: record with the digest; only the final status line; neither with the registry reporting it; neither with only the engine image record; a status line for another tag is ignored; a valid response digest causes no registry or engine lookup; record and status line that differ fail; registry and engine that differ fail when both were consulted; an `errorDetail` or `error` line fails before any digest; a malformed JSON line fails deterministically; no source fails; a digest not matching `^sha256:[0-9a-f]{64}$` is rejected; every failure message contains the full body and the outcome (`found`, `absent`, `unavailable: <reason>`) of each tried source and never the `X-Registry-Auth` value

### Implementation for User Story 1

- [ ] T004 [P] [US1] Create `E2E/Support/PushDigest.cs`: a pure determination over (response body, repository, tag, registry and engine lookups as delegates) with the sources in order `ProgressRecord`, `StatusLine`, `RegistryManifest` (`GET /v2/<repository>/manifests/<tag>`, header `Docker-Content-Digest`, with the OCI index, OCI manifest, Docker manifest list, and Docker manifest v2 `Accept` types), `EngineImageRecord` (`GET /images/<name>/json`, `RepoDigests` entry of the pushed repository), the `DigestResult` of data-model.md, and the rules of research.md D1 and D2 (digest valid only as `sha256:` plus 64 lowercase hexadecimal characters; first valid source wins; mismatches fail)
- [ ] T005 [US1] Change `PushAsync` in `E2E/Support/DockerEngine.cs` to use `PushDigest` instead of accepting the digest only from an `aux` record, with the registry manifest lookup and engine image-record lookup as the delegates (each in its own separately named `DockerEngine` method, and `DockerEngine` gets a constructor seam taking an `HttpMessageHandler` and the registry base address so the lookups can be tested without Docker), failing with the full response body and the per-source outcomes, and run the `PushDigest` tests

**Checkpoint**: User Story 1 is complete and testable without Docker; this is the MVP.

---

## Phase 4: User Story 2 - Pushes and builds are bounded, cancellable, and retried only for transient failures (Priority: P2)

**Goal**: A named push limit and build limit, cancellation by the test's token, and bounded retries of transient failures only (FR-005, FR-006).

**Independent Test**: `dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~EngineCallPolicy"` passes without Docker.

### Tests for User Story 2

- [ ] T006 [P] [US2] Create `E2E/ImagePush/EngineCallPolicyTests.cs` (no Docker) with a fake `HttpMessageHandler` and a fake `TimeProvider`: a stalled call ends at the named limit and is reported as that limit; cancellation of the test's token ends the call and is never retried; a reset connection, `IOException`, call timeout, HTTP 502, 503, 504, and 429, and a registry not yet answering are repeated exactly `MaxAttempts` times with every attempt's reason in the final failure; an error in the response, a malformed response, a digest conflict, other 4xx, and a build failure for the image's own content are tried once; the build-exception classification of T007 is covered with one case per exception type (transient `HttpRequestException`, `IOException`, Docker.DotNet connection and timeout exceptions, and Testcontainers build exceptions whose inner exception is one of those; deterministic Dockerfile or build-step failures and any other Testcontainers build exception without a transient cause); the retry delay is read through `TimeProvider` so the tests do not wait; also create `E2E/ImagePush/DockerEngineLookupTests.cs` (no Docker, through the `DockerEngine` constructor seam with a fake `HttpMessageHandler` and registry base address): the manifest request carries the four `Accept` types (OCI index, OCI manifest, Docker manifest list, Docker manifest v2) and the digest comes from `Docker-Content-Digest`, a 404 gives `absent`, a 503 or a refused connection is retried as transient, `RepoDigests` selects the entry of the pushed repository and ignores others, and a failed engine lookup keeps the diagnostics of the earlier sources

### Implementation for User Story 2

- [ ] T007 [P] [US2] Create `E2E/Support/EngineCallPolicy.cs` with the named values of data-model.md in one place (`PushLimit` 15 minutes, `BuildLimit` 20 minutes, `RegistryLookupLimit` 30 seconds, `MaxAttempts` 3, `RetryDelay` 2 seconds), the attempt outcomes `Succeeded`, `Transient(reason)`, `Deterministic(exception)`, and `Cancelled`, the transient classification of research.md D3 including the build-exception classification (transient: `HttpRequestException`, `IOException`, Docker.DotNet connection, timeout, and HTTP 502, 503, 504, and 429 exceptions, and a Testcontainers build exception whose inner exception chain contains one of these; deterministic: Dockerfile or build-step failures reported by the engine for the image's own content and every other exception), a linked token per attempt (the test's token plus `CancelAfter(limit)`), the delay between attempts through `TimeProvider`, and a final exception that names the limit or condition and lists every attempt's reason
- [ ] T008 [US2] Change `E2E/Support/DockerEngine.cs` so the `HttpClient` has an infinite timeout and every push, registry lookup, and image-record lookup runs through `EngineCallPolicy` with the test's cancellation token (the client timeout no longer ends any call), keeping deterministic push failures (an error in the response, no digest) out of the retries, keeping the two lookups in their separately named methods behind the constructor seam of T005, and run the `EngineCallPolicy`, `PushDigest`, and `DockerEngineLookup` tests
- [ ] T009 [P] [US2] Run the image build of `E2E/Support/TestAnalystImage.cs` through `EngineCallPolicy` with `BuildLimit` and the test's token
- [ ] T010 [P] [US2] Run the image build of `E2E/Support/ReferenceAnalystImage.cs` through `EngineCallPolicy` with `BuildLimit` and the test's token
- [ ] T011 [P] [US2] Run the image build of `E2E/Support/ShotBoundaryAnalystImage.cs` through `EngineCallPolicy` with `BuildLimit` and the test's token

**Checkpoint**: Pushes and builds end at named limits and are retried only for transient conditions.

---

## Phase 5: User Story 3 - Parallel tests do not overload the engine and the registry (Priority: P3)

**Goal**: One shared bound on concurrent builds and pushes, and one build and push per image variant per test class (FR-007, FR-008).

**Independent Test**: `dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~ImageBuildGate|FullyQualifiedName~AnalystImageCache"` passes without Docker; a full run builds and pushes one variant once per class.

### Tests for User Story 3

- [ ] T012 [P] [US3] Create `E2E/ImagePush/ImageBuildGateTests.cs` (no Docker) with a concurrency probe: no more than `MaxConcurrentBuildsAndPushes` acquirers run at once, the gate is released on success, failure, and cancellation, a cancelled waiter leaves it free for the next, a cancelled waiter on a cached build neither cancels the build nor holds the gate, a cached build (the cache passing its own token to the helper's uncached `BuildAndPushAsync`) completes with `MaxConcurrentBuildsAndPushes` 1 because the gate is acquired exactly once, and the observed-maximum concurrency counter never exceeds `MaxConcurrentBuildsAndPushes`
- [ ] T013 [P] [US3] Create `E2E/ImagePush/AnalystImageCacheTests.cs` (no Docker): one variant and inputs is built once for several concurrent requests; different variants or inputs are never served each other's entry; a failed build faults the entry for every waiting test and is removed so a later request builds again; cancelling one waiting test does not cancel the build for the others; disposing removes the built images and disposes the registry; the per-key build count reads 1 after several concurrent requests for one key and 2 after a faulted entry is requested again

### Implementation for User Story 3

- [ ] T014 [P] [US3] Create `E2E/Support/ImageBuildGate.cs`: one static `SemaphoreSlim` with `MaxConcurrentBuildsAndPushes` 1 (documented value), acquired with the token passed in around the build and push of one image together and released in `finally`; the gate is acquired in exactly one place, inside each helper's uncached `BuildAndPushAsync` (T016 to T018), and neither the cache nor any call site acquires it; it also keeps an observed-maximum concurrency counter that tests can read and that is written as one log line when the class fixture (T015) is disposed; only code that builds or pushes waits for it
- [ ] T015 [P] [US3] Create `E2E/Support/AnalystImageCache.cs`: a class-scoped, single-flight cache keyed by (helper, mode or variant, inputs) with the state machine of data-model.md (`absent`, `building`, `ready`, faulted and removed), built under its own token bounded by the limits (it passes that token to the helper's uncached `BuildAndPushAsync`, which acquires `ImageBuildGate`; the cache never acquires the gate itself), with a per-key build count that tests can read and that is written, together with the gate's observed maximum, as one log line when the class fixture is disposed, that also owns the class's single registry through `OciRegistryContainerFixture` and is usable as an xunit class fixture, removing its images and disposing the registry when the class ends

#### Helpers for User Story 3 (need T014 and T015)

- [ ] T016 [P] [US3] Acquire the gate around build and push together inside the uncached `BuildAndPushAsync` in `E2E/Support/TestAnalystImage.cs` (the only place the gate is acquired, with the token passed in), keep it and `RemoveAsync` for tests that need their own image, and add the cached entry point through `AnalystImageCache`, which calls the uncached `BuildAndPushAsync` with the cache's token and does not acquire the gate itself
- [ ] T017 [P] [US3] Acquire the gate around build and push together inside the uncached `BuildAndPushAsync` in `E2E/Support/ReferenceAnalystImage.cs` (the only place the gate is acquired, with the token passed in), keep it and `RemoveAsync`, and add the cached entry point through `AnalystImageCache`, which calls the uncached `BuildAndPushAsync` with the cache's token and does not acquire the gate itself
- [ ] T018 [P] [US3] Acquire the gate around build and push together inside the uncached `BuildAndPushAsync` in `E2E/Support/ShotBoundaryAnalystImage.cs` (the only place the gate is acquired, with the token passed in), keep it and `RemoveAsync`, and add the cached entry point through `AnalystImageCache`, which calls the uncached `BuildAndPushAsync` with the cache's token and does not acquire the gate itself

### Call sites for User Story 3

Read each class and keep an uncached image for any test that removes, mutates, or otherwise depends on the image being its own; every other test of the class that needs the same variant and inputs takes the cached image and the class's registry. No assertion, attribute, or `[Collection("EndToEnd")]` is removed or loosened.

- [ ] T019 [P] [US3] Use the cached image and the class registry in `E2E/ReferenceAnalystEndToEndTests.cs` (the `Baseline`, `PayloadValidated`, and `payload-invalid` variants)
- [ ] T020 [P] [US3] Use the cached image and the class registry in `E2E/ReferenceAnalystImageTests.cs`
- [ ] T021 [P] [US3] Use the cached image and the class registry in `E2E/ShotBoundaryImageTests.cs`
- [ ] T022 [P] [US3] Use the cached image and the class registry in `E2E/ShotBoundaryEndToEndTests.cs`
- [ ] T023 [P] [US3] Use the cached image and the class registry, per mode, in `E2E/DockerRuntimeEndToEndTests.cs`, keeping its own build where a test depends on owning the image
- [ ] T024 [P] [US3] Use the cached image and the class registry in `E2E/SegmentMediaEndToEndTests.cs`

**Checkpoint**: Builds and pushes are bounded and each variant is built and pushed once per class.

---

## Phase 6: User Story 4 - The cause and the fix are recorded and no test is weakened (Priority: P3)

**Goal**: The cause, digest sources, limits, retry rule, bound, reuse rule, and commands are documented, and no test is weakened (FR-010, FR-012).

**Independent Test**: Read the README and compare assertions and attributes with `git diff` against the base branch.

- [ ] T025 [P] [US4] Create `E2E/README.md` recording the evidenced cause (from research.md `## Evidence`), the digest sources in order, the named limits and their development values, the retry rule (transient only, bounded attempts, every reason reported), the build and push bound and its value, the per-class reuse rule and which tests keep their own image, and the commands from [quickstart.md](quickstart.md); state that the values are development values and that Docker Desktop on Windows stays manual evidence
- [ ] T026 [P] [US4] Edit `src/platform/README.md` to link to the end-to-end project README and keep its test description true
- [ ] T027 [P] [US4] Check `docs/architecture/platform-implementation.md` and `docs/architecture/analysts-models-and-hardware.md` for statements about the end-to-end tests that this change makes false and correct only those (the plan expects none; change nothing if all remain true)

---

## Phase 7: Polish and Verification

- [ ] T028 Run the whole verification of [quickstart.md](quickstart.md): `dotnet build src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests`, the `ImagePush` unit tests, the full `SocAlytics.Platform.EndToEnd.Tests` project and `SocAlytics.Platform.Images.Tests` against a Linux engine (every test passes, none skipped), `git diff` against the base showing no removed assertion or `[Collection]`, `[Fact]`, or `[Theory]` attribute and no change outside `E2E/`, `src/platform/README.md`, `docs/architecture/`, and this spec folder, `node .github/scripts/check-markdown.mjs --files src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/README.md src/platform/README.md`, a check that the full run built and pushed each image variant once per class and that the gate's observed maximum stayed within `MaxConcurrentBuildsAndPushes` (read from the per-key build counts of `AnalystImageCache` and the observed-maximum counter of `ImageBuildGate` that T014 and T015 write as one log line when the class fixture is disposed, as asserted in T012 and T013), a search of the diff for `Task.Delay` and `Thread.Sleep` outside `EngineCallPolicy` that finds none, and a `git diff` check that shows no changed timeout or delay literal in the existing test classes outside the push and build calls

---

## Dependencies and Execution Order

- Phase 1 (T001) has no code dependency but should precede the fix being finalized; Phase 2 (T002) blocks every story.
- US1 (T003 to T005) comes first; US2 (T006 to T011) follows because T008 edits the same `DockerEngine.cs` as T005; US3 (T012 to T024) follows US2 because the helpers (T016 to T018) were edited by T009 to T011 and the call sites need the helpers; US4 (T025 to T027) documents the finished behavior, T025 needs T001.
- T028 runs last.
- Within a phase, the consecutive `[P]` tasks under one heading touch different files and run in parallel (T009 to T011; T012 and T013; T014 and T015; T016 to T018, after T014 and T015; T019 to T024, after T016 to T018; T025 to T027); T005, T008, and the tasks without `[P]` run in order.

## Implementation Strategy

- **MVP**: Phases 1 to 3 (US1) remove the failure that fails 14 of 33 tests; verify with the `PushDigest` tests and one Docker-backed run.
- **Incremental**: add US2 (limits and retries), then US3 (bound and reuse), then US4 (documentation), verifying with the story's independent test after each.
