# Research: Reliable End-to-End Analyst Image Push

All values below are development values for tests, not production values.

## Evidence

Read from the code at planning time (the failing run itself is not reproducible from the spec folder):

- `DockerEngine.PushAsync` (`Support/TestAnalystImage.cs`) reads the whole push response, throws on `errorDetail`, and
  keeps a digest only from an `aux` record with a `Digest` property; otherwise it throws "The push did not report a
  manifest digest". A final status line `<tag>: digest: sha256:… size: …` is ignored. This explains a failure after a
  complete push.
- The engine client has one `HttpClient.Timeout` of five minutes for every call, including builds' neighbours and the
  push; builds go through Testcontainers' own client.
- All Docker-backed classes in the project share the xunit collection `EndToEnd`, so they run one after another inside
  this assembly; concurrent load on the single engine therefore comes from other assemblies of a solution-wide
  `dotnet test` (for example `SocAlytics.Platform.Images.Tests`) and from the runner, which an in-process bound cannot
  see. Each test method starts its own `registry:2` and builds and pushes its image again (6 classes call the three
  helpers).

Established by the first implementation attempt (git history): no commit changed `DockerEngine.PushAsync` or the
registry fixture between the passing and the failing branch, so a recent change on main is not the cause. The remaining
suspects are the single-source digest (explains a failure after a complete push) and load with the shared five-minute
timeout (explains an incomplete push); no recording evidences either, and the fix holds under both.

Still to be established by the implementation (FR-001; T001 runs after the digest fix T007, because the task verification runs the whole Docker-backed project for a README change in it, and that project fails until the fix landed), recorded in the `## Cause` section of the end-to-end project's
README (implementation agents may not change files under `specs/`): the Testcontainers build client's own request
timeout and its value, read from the restored package (the attempt could only read the 4.15.0 source, which shows no
timeout of its own). A recorded push response of a failing and of a passing push and the timings of builds and pushes
need a reachable Linux engine; where none is, the README says they are not recorded, and a maintainer adds them here
with the stability runs. Stability runs (SC-001: five
consecutive full runs, two after an engine restart, one under CPU load) are recorded here by a maintainer and are not
a task gate.

## Decisions

### D1 Digest sources and order

- **Decision**: progress record (`aux.Digest`) → final status line for the pushed tag → registry manifest
  (`GET /v2/<repository>/manifests/<tag>`, header `Docker-Content-Digest`, with the OCI index, OCI manifest, Docker
  manifest list, and Docker manifest v2 `Accept` types) → engine image record (`GET /images/<name>/json`,
  `RepoDigests` entry of the pushed repository). The first valid digest wins; later sources are consulted only if
  earlier ones yielded none. The record and the status line are both read from the response, so they are compared; the
  registry and the engine are compared with each other only when both were consulted because the response yielded none.
- **Rationale**: matches FR-002 to FR-004 and the clarification "stop at the first valid digest". A pure function over
  (response body, repository, tag, lookups as delegates) is unit-testable without Docker.
- **Alternatives considered**: always cross-check with the registry (rejected by the clarification, adds load and a new
  failure source); take only the registry (the registry is not the response's authority and may be unready).

### D2 Validity and error handling

- **Decision**: digest form `^sha256:[0-9a-f]{64}$`; a status line counts only when its prefix equals the pushed tag
  (a line for another tag is ignored, not an error); `errorDetail` or `error` in any line fails before any digest;
  malformed JSON lines of the response are a deterministic failure with the body. Failure messages contain the full
  body and the result of each tried source; the `X-Registry-Auth` value (`{}`) and any credentials are never printed.
- **Rationale**: FR-003, FR-004, and the edge cases.

### D3 Time limits, cancellation, and retries

- **Decision**: `EngineCallPolicy` names `PushLimit` 15 minutes, `BuildLimit` 20 minutes, `MaxAttempts` 3, `RetryDelay`
  2 seconds (a bounded backoff between attempts, not a sleep to wait out a failure; read through `TimeProvider` so tests
  do not wait), and `RegistryLookupLimit` 30 seconds. `HttpClient.Timeout` becomes infinite; each call runs under a
  linked token (the test's token plus `CancelAfter(limit)`), and the limit ending the call is reported as the named
  limit. Transient: `HttpRequestException` or `IOException` (reset or closed connection, refused connection), a Testcontainers build exception whose inner exception chain contains any of these, a call
  timeout that was not a cancellation, HTTP 502, 503, 504, and 429, and a registry that is not yet answering. Not
  transient and never retried: an error in the response, a malformed response, a digest conflict, other 4xx, and a
  build failure Testcontainers reports for the image's own content (a Dockerfile or build-step failure, or any build
  exception without a transient cause in its inner exception chain). Cancellation of the test's token is never retried.
  The final failure lists every attempt's reason. Builds go through Testcontainers' own Docker client, whose request
  timeout is not touched by the `HttpClient` change; T001 (run after the digest fix, T007) records the value it applies in the end-to-end README, and if it is shorter than
  `BuildLimit` the build is configured or run so that `BuildLimit` and the test's token end it, or the limitation is
  documented in the README.
- **Rationale**: FR-005, FR-006; the 214 MB image under load exceeded or reset on the shared limit.
- **Alternatives considered**: raising only the client timeout (still one anonymous limit and no cancellation tie);
  retrying every exception (would hide the deterministic digest defect).

### D4 Bound across builds and pushes

- **Decision**: `ImageBuildGate`, an instance type with a constructor limit, its own `SemaphoreSlim`, and its own
  observed-maximum counter; the process-wide gate is a single static instance created with
  `MaxConcurrentBuildsAndPushes` 1 (builds and pushes of the whole assembly run one at a time; the collection already serializes the tests, so this is a guard if the
  collection structure changes), acquired in exactly one place: inside each helper's uncached `BuildAndPushAsync`,
  around the build and the push of one image together, with the token passed in, and released in `finally`. The cache
  and the call sites never acquire it, so a cached build cannot acquire it twice and deadlock. Only code that builds
  or pushes waits for it. Each gate instance keeps an observed-maximum concurrency counter that tests read (the static
  gate's is logged once when the class fixture is disposed). Unit tests (`ImageBuildGateTests`, `AnalystImageCacheTests`)
  use fresh gate instances injected into the cache and the build delegate, never the static one, so they neither wait
  for nor are polluted by real builds.
- **Rationale**: FR-007; a value of 1 keeps the single engine and registry unloaded and is simplest to document.
- **Alternatives considered**: a cross-process named lock (would also bound `Images.Tests`, but it adds a lock file and
  a hang risk, and the other assembly's code is out of scope); a higher value (no evidence it helps).

### D5 Reuse within a test class

- **Decision**: `AnalystImageCache` per test class (an xunit class fixture that also owns the class's single
  `registry:2` through `OciRegistryContainerFixture`), keyed by helper, mode or variant, and inputs, holding a
  single-flight entry that follows the state machine of [data-model.md](data-model.md). A failed build or push faults the entry for every
  waiting test and is removed so a later test may retry; cancellation of one waiting test does not cancel the build for
  the others, but the cache's build token is cancelled when the class fixture is disposed or when the last waiting test
  has cancelled, which stops the build, releases the gate, and removes the entry (the build runs under the cache's own token bounded by the limits, which the cache passes to the helper's uncached
  `BuildAndPushAsync` that takes the gate; the cache keeps a per-key build count that is logged with the gate's
  observed maximum), and the fixture removes the images and
  disposes the registry at the end of the class. A test that deletes, mutates, or otherwise depends on the image being
  its own (to be identified by reading each class: for example tests that call `RemoveAsync` or force-remove images to
  prove cleanup) keeps its own build and push through the uncached helper.
- **Rationale**: FR-008 and SC-004: one build and push of a variant per class. Moving the registry to the class keeps
  "each test has the registry and digest it registers" true for reused images.
- **Alternatives considered**: reuse across classes (more savings, but the registry lifetime is per class and
  cross-class state makes failures harder to attribute; deferred); reusing only the local build and pushing again
  (still repeats the large push).

### D6 Documentation location

- **Decision**: a new `README.md` in the end-to-end test project documents the cause, sources, limits, retry rule,
  bound, reuse rule, and commands; `src/platform/README.md` links to it and its test description stays otherwise true.
  `docs/architecture/platform-implementation.md` changes only if its end-to-end evidence bullet becomes false.
- **Rationale**: FR-012; the project README is where readers of the tests look.

### D7 Test strategy without Docker

- **Decision**: unit tests use recorded response bodies of each shape as string constants, a fake
  `HttpMessageHandler` for the engine and the registry (stall, reset, 503 a configurable number of times), a fake
  `TimeProvider`, and a probe that counts concurrent builds under the gate. The Docker-backed tests keep running
  unchanged in meaning against a Linux engine. The classes carry no `[Collection("EndToEnd")]`, need no Docker, and
  therefore do not skip.
- **Rationale**: FR-011, SC-002, SC-003.
