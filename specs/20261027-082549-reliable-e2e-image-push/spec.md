# Feature Specification: Reliable End-to-End Analyst Image Push

**Feature Branch**: `20261027-082549-reliable-e2e-image-push`

**Created**: 2026-10-27

**Status**: Draft

**Fix for**: #335 (implementation pull request #345, `specs/20261025-233727-deterministic-payload-conformance-test`)

**Input**: User description: "Make the Docker-backed end-to-end tests of `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests` reliable when they push Analyst test images to the local test registry. In the full platform verification of a branch that merged main, 14 of 33 tests fail twice (for example `ShotBoundaryImageTests`, which fails in the engine push support of `Support/TestAnalystImage.cs` after the roughly 214 MB `media-context-classification` image was pushed completely), while the same project passed 33 of 33 on the unmerged branch. The push response ends with every layer pushed and a final status line `<tag>: digest: sha256:... size: ...`, yet the support code reads the manifest digest only from an `aux` record with a `Digest` property and otherwise throws 'The push did not report a manifest digest'. The spec `specs/20261025-233727-deterministic-payload-conformance-test` depends on this spec."

## Clarifications

### Session 2026-10-27

- Q: May the fix change production code? → A: No. The cause and the fix live in the end-to-end test support code (the engine client and the image build-and-push helpers) and in documentation; the product, its images, and the platform are not changed.
- Q: May a test be skipped, weakened, or loosened to make the project pass? → A: No. Every Docker-backed test keeps its meaning, its assertions, and its Docker requirement; a missing engine still fails visibly.
- Q: Is a retry an acceptable fix? → A: Only as a bounded, reported safeguard against transient failures of the engine or registry, never as the way a deterministic defect (a digest that is present but not recognized) is worked around.
- Q: When a push response already carries a valid manifest digest (a progress record or the final status line), must the support code still query the local registry and the engine's image record to cross-check it, or may it stop at the first valid digest? → A: Stop at the first valid digest (no extra lookups); the mismatch failure applies only to sources that were actually read, so the registry and engine are consulted only when the response yields none.
- Q: How is the stability criterion of at least 5 consecutive full end-to-end runs (including 2 after an engine restart and 1 under CPU load) to be evidenced, given that the implementation automation runs the verification once with a single rerun? → A: Only the unit tests without Docker and one full Docker-backed run (plus the Images tests) are required as automated verification; the 5-run, engine-restart, and CPU-load evidence is recorded manually by a maintainer in `research.md` and is not a task gate.
- Q: Which cause comes first, recent change on main or runner load? → A: Both are investigated with evidence before the fix is chosen (the code-level findings and the git-history result are already in `research.md`); only the recording of the cause in the README follows the digest fix, because a README change makes the task verification run the whole Docker-backed project, which fails until the fix landed. The fix has to hold under either.
- Q: All Docker-backed classes share one xunit collection and run serially in the assembly, so does a load cause from parallel tests still hold, and what does the build and push bound guard? → A: No concurrent load comes from tests of this assembly. The load cause is repeated large builds and pushes (one per test method) and the shared client timeout; reuse per class reduces them, and the bound is a guard for a changed collection structure. Load from other assemblies or the runner is out of scope. The `## Evidence` record of `research.md` settles this.
- Q: Must a final status line naming a digest for a different repository be rejected? → A: Only the tag can be checked from the status line (it carries no repository); the repository is checked for the registry and engine sources only.

## Problem

The Docker-backed end-to-end tests build Analyst test images, push them to the single local test registry that a test starts, and register them by manifest digest. When the push does not yield a manifest digest the test fails before it checks any behavior, and the failure is not about the feature under test.

Evidence from the stopped implementation of #335:

- In the full platform verification of a branch that merged `main`, 14 of 33 tests of the end-to-end project fail, and fail again on the one rerun. The same project passed 33 of 33 on the unmerged branch.
- A representative failure (`ShotBoundaryImageTests`) happens after the image of about 214 MB was pushed completely: every layer is reported as pushed and the response ends with the status line that names the manifest digest and size. The support code accepts the digest only from one kind of progress record and throws "The push did not report a manifest digest" otherwise, discarding the digest the response does carry.

Suspected causes, to be confirmed by evidence before they are fixed:

- **Digest taken from one source only** (most likely): the engine in the runner does not always emit the progress record the support code expects (a difference of engine or image-store version, or of how a push is served), although the digest is reported by the final status line, the registry, and the engine's image record.
- **Load and timeouts**: every test method builds and pushes its large image again to one engine and one registry, so a push or build exceeds the shared client timeout of five minutes, a connection is reset, or the registry is not ready yet. The Docker-backed classes of the project share one xunit collection and run one after another, so the load does not come from tests of this assembly running at the same time; load from other assemblies of a solution-wide run or from the runner is outside what this spec can bound.
- **A recent change on main**: a change merged to `main` (a larger image, a new image-building test, an engine setting, a base image) that increased the load or changed the push response. The implementation checks the history of the project and of the image inputs between the passing and the failing branch.

The implementation first reproduces the failure or demonstrates it with a recorded push response of each shape, identifies which cause (or another) produces it, and then removes it.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - A completed push is recognized however the engine reports it (Priority: P1)

A maintainer, or the verification of any implementation pull request, runs the end-to-end project and sees every test that pushes an Analyst test image get the pushed image's manifest digest whenever the push completed, whichever way the engine reported it, and fail with the full response only when no source yields a digest.

**Why this priority**: This is the failure that stops #335 and that fails 14 of 33 tests with the same cause; without it nothing else in the project can be relied on.

**Independent Test**: Feed the digest determination each response shape (a progress record with the digest, only the final status line, neither) and a registry or engine that reports the digest, and check the result; then run the end-to-end project against a Linux engine.

**Acceptance Scenarios**:

1. **Given** a push response with a progress record that carries the manifest digest, **When** the push completes, **Then** that digest is used, as before.
2. **Given** a push response without that record but with the final status line `<tag>: digest: sha256:<64 hex> size: <n>`, **When** the push completes, **Then** the digest of that line is used.
3. **Given** a push response that carries no digest at all, **When** the push completes, **Then** the digest is read from the pushed manifest in the local test registry, or failing that from the engine's image record for the pushed repository, and is used; the registry and the engine are consulted only in this case, never to cross-check a digest the response already carries.
4. **Given** the push response reports an error, **When** it ends, **Then** the push fails with that error and no digest is looked up to hide it.
5. **Given** no source yields a digest, **When** the push ends, **Then** it fails with the full response body and what each lookup returned.
6. **Given** more than one source that was actually read yields a digest (the progress record and the final status line of the response are read together), **When** they disagree, **Then** the push fails with the diagnostics instead of choosing one.

---

### User Story 2 - Pushes and builds are bounded, cancellable, and retried only for transient failures (Priority: P2)

A maintainer sees a slow push or build of a large image finish within an explicit, named, generous time limit tied to the test's cancellation, and a transient failure (a connection reset, a timeout, a registry that is not ready yet) repeated a bounded number of times with the reason of each failed attempt reported, instead of the test failing on a shared five-minute client timeout.

**Why this priority**: The roughly 214 MB image pushed under load is the case that exceeded or reset on shared limits; the digest fix alone would leave those failures.

**Independent Test**: With a fake engine endpoint that stalls, resets the connection, or answers "not ready" a configurable number of times, check that the call is cancelled by the test's token, retried at most the bounded number of times, and reports each reason; and that a deterministic failure is not retried.

**Acceptance Scenarios**:

1. **Given** a push or build that takes longer than the former shared limit but within the named limit, **When** it runs, **Then** it completes and the test continues.
2. **Given** the test's cancellation is signalled, **When** a push or build is running, **Then** it stops at once and no retry follows.
3. **Given** a push or build fails with a transient condition, **When** the bounded number of attempts is not used up, **Then** it is repeated and the failure reason of each failed attempt is part of the final message if all attempts fail.
4. **Given** a push or build fails deterministically (an error reported by the engine for the image itself, a digest conflict, a malformed response), **When** it fails, **Then** it is not retried.
5. **Given** the limits and the number of attempts, **When** they are read, **Then** each has a name and a documented value in one place, and not an anonymous client default.

---

### User Story 3 - Repeated large pushes are avoided and builds and pushes stay bounded (Priority: P3)

A maintainer sees an image that is already built and pushed for a test class reused instead of built and pushed again for every test, so the single engine and the single local registry carry far fewer large pushes, and sees image builds and pushes bounded in-process by one shared limit that guards against a changed collection structure (today the classes run one after another in one xunit collection). Load from other assemblies or the runner is out of scope.

**Why this priority**: Repeated large pushes are the second suspected cause and the reason a failure appears with the full project and not with a single test; reuse reduces the chance of any transient failure.

**Independent Test**: Check with a probe of concurrent builds that no more than the declared number of builds and pushes run at the same time, and with the recorded timings or counts that tests using the same image variant in one class share one build and push (reuse never serves an image that a test changes or that has another variant).

**Acceptance Scenarios**:

1. **Given** several tests would build or push images at the same time (for example if the collection structure changes), **When** they do, **Then** no more than the declared number run at once across all test classes of the run.
2. **Given** tests of one class need the same image variant with the same inputs, **When** they run, **Then** the image is built and pushed once for them, and each test still has the registry and digest it registers.
3. **Given** a test needs a different variant (another mode or another inputs), **When** it runs, **Then** it gets its own image and is never served one that does not match.
4. **Given** the bound or the reuse, **When** a test fails or is cancelled while it holds the bound or builds a shared image, **Then** the bound is released and no other test is left waiting or served a partial image.

---

### User Story 4 - The cause and the fix are recorded and no test is weakened (Priority: P3)

A maintainer reading the end-to-end tests' documentation finds the evidenced cause of the failure, the shapes of push response the support code accepts, the limits, retries, and bounds, and how to run the project, and finds that no Docker-backed test was skipped, weakened, or loosened.

**Why this priority**: The failure was triggered by a change in another area; the knowledge must stay with the tests so it is not rediscovered.

**Independent Test**: Read the documentation and compare the assertions and attributes of the tests before and after.

**Acceptance Scenarios**:

1. **Given** the fix is merged, **When** the documentation of the end-to-end tests is read, **Then** it names the cause, the accepted digest sources in order, the limits, the retry rule, and the build and push bound.
2. **Given** the tests before and after the change, **When** they are compared, **Then** no assertion is removed or loosened, no test is skipped, and every Docker-backed test still requires a running engine.

### Edge Cases

- A response whose last status line names a digest for a different tag than the one pushed MUST NOT be used (the status line carries no repository; the repository is checked only for the registry and engine sources).
- A response digest that is valid MUST end the determination without a registry or engine lookup, even if those would report another digest.
- A digest of a malformed form (not `sha256:` plus 64 hex characters) MUST NOT be accepted from any source.
- A registry that is not yet answering at the first lookup MUST be handled as a transient condition within the bound, not as a missing digest.
- An engine that does not support the image record lookup MUST NOT hide the diagnostics of the earlier sources.
- A reused image whose build or push failed MUST NOT be reused by later tests as if it succeeded; the failure is reported to every test that needs it.
- Cancellation or a failure in the middle of a build or push MUST NOT leave the bound held or a partially pushed image presented as complete.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The implementation MUST establish the cause with evidence before it chooses the fix (the investigation precedes the fix; only the recording of the result in the README may follow the digest fix, for the verification reason in `tasks.md` T001): the push responses the engine produces (a recorded body of a failing push and of a passing one), the history of the end-to-end project, its images, and the runner setup between the passing branch and the failing branch, and the load and time of the parallel builds and pushes. It MUST record the implementation-established cause in the `## Cause` section of the documentation of the end-to-end tests (FR-012); `research.md` carries the code-level evidence and the git-history result, and any later addition to it is evidence a maintainer records manually; the commit message, written by the person or automation that commits, repeats it.
- **FR-002**: Determining the manifest digest of a push MUST use, in this order, the progress record that carries the digest, the final status line `<tag>: digest: sha256:<64 hex> size: <n>` for the pushed repository and tag, the manifest digest reported by the local test registry for the pushed repository and tag, and the repository digests of the engine's image record. The first valid digest wins and ends the determination, except that the progress record and the final status line, both read from the response, are compared (FR-004): a later source (the registry, then the engine's image record) is consulted only when every earlier source yielded none, so a digest the response carries is never cross-checked against the registry or the engine.
- **FR-003**: An error reported in the push response MUST fail the push with that error before any digest is determined, and a digest MUST be accepted only in the form `sha256:` plus 64 hexadecimal characters.
- **FR-004**: If two sources that were actually read report different digests for the pushed image (the progress record and the final status line of the response, or the registry and the engine's image record when the response yielded none and both were consulted), or no source reports one, the push MUST fail with the full response body and the result of each source it tried; the message MUST NOT contain credentials.
- **FR-005**: The push and the image build MUST each have an explicit, named time limit that is generous for the largest test image, set in one place, and MUST be cancelled by the test's cancellation token (for a shared build, the test's token cancels it when no other waiting test remains); the shared client timeout MUST NOT be what ends a push or build.
- **FR-006**: A push or build that fails with a transient condition (a reset or closed connection, a timeout of the call, a registry or engine not yet ready) MUST be repeated at most a named, bounded number of times with a short bounded delay, MUST report the reason of every failed attempt in the final failure, and MUST NOT be repeated after cancellation. A failure the engine reports for the image itself, a malformed response, and a digest conflict MUST NOT be retried.
- **FR-007**: Image builds and pushes across all test classes of the run MUST be bounded by one shared limit on the number running at the same time, with a documented value, released on success, failure, and cancellation, and tests that do not build or push MUST NOT wait for it.
- **FR-008**: An image built and pushed for one test class MUST be reused within that class for tests that need the same variant and inputs, instead of built and pushed per test; tests that change, delete, or otherwise depend on the image being their own MUST keep their own, and a failed shared build MUST be reported to every test that needs it and not cached as a success.
- **FR-009**: The changes MUST apply to the engine client of the end-to-end support code and to the build-and-push helpers of the test analyst image, the reference Analyst image, and the shot-boundary Analyst image; the other Docker-backed support code (for example the harness and the registry start) MUST be changed only where the evidence shows the same defect.
- **FR-010**: No test of the end-to-end project or of the Images tests MAY be skipped, weakened, loosened, deleted, or made to pass without its Docker requirement, and no timeout of a test's own behavior checks (as distinct from the push and build calls) MAY be lengthened to make it pass.
- **FR-011**: The digest determination, the retry classification, the limits, and the bound MUST have unit tests that run without Docker (recorded responses of each shape, a fake engine and registry endpoint, and a probe of concurrent builds); the Docker-backed tests keep running against a Linux engine. The automated verification of this spec is these unit tests plus one full Docker-backed run of the end-to-end project and the Images tests; the repeated-run evidence of SC-001 is not a task gate.
- **FR-012**: The cause, the accepted digest sources in order, the named limits and values, the retry rule, the build and push bound, the reuse rule, and the commands to run the project MUST be recorded in the documentation of the end-to-end tests (a README of the project or the existing platform tests documentation, whichever exists or is the place its readers use), and every architecture or documentation statement the change makes false MUST be corrected.
- **FR-013**: The product code, the Dockerfiles, the images, the contracts, and the CI workflows MUST NOT be changed.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The end-to-end project passes 33 of 33 tests (or all of its tests if the count changed) in one full run against a Linux engine as automated verification, and in at least 5 consecutive full runs, including at least 2 runs started after an engine restart and 1 run under CPU load, with no test needing a rerun, as evidence a maintainer records manually in `research.md` (not verified by a task of this spec, like SC-007).
- **SC-002**: The digest determination returns the correct digest for each recorded response shape (record present, only the status line, neither with the registry reporting it, neither with only the engine record) and fails with diagnostics in the case of none, an error, a mismatch, or a malformed digest, in 100% of the unit cases, and no registry or engine lookup is made when the response yields a valid digest.
- **SC-003**: In the fake-endpoint tests, a stalled call ends at the named limit or the cancellation, a transient failure is repeated exactly up to the bound with every reason reported, and a deterministic failure is not repeated, in 100% of cases.
- **SC-004**: In the probe of concurrent builds no more than the declared number of image builds and pushes run at the same time (a guard, since the collection already serializes the tests of the assembly), and the number of builds and pushes of one image variant in one test class is one.
- **SC-005**: The Images tests (`SocAlytics.Platform.Images.Tests`) pass against the same engine in the same run, and no test of either project is skipped, weakened, or loosened (the comparison of the assertions and attributes before and after shows no removal).
- **SC-006**: The documentation names the evidenced cause and `research.md` carries the code-level evidence and git-history result (push responses and timings added later by a maintainer are maintainer-recorded evidence), the change touches only the end-to-end support code, its tests, the documentation, and this spec folder, and no fixed sleep is added other than the named bounded `RetryDelay` of `EngineCallPolicy`, and none in tests.
- **SC-007** (post-merge evidence, observed when #345 retries and not verified by a task of this spec): the full platform verification of #345 reports no failing end-to-end test of this cause.

## Assumptions

- **Cause**: The primary cause is presumed to be a push response without the progress record the support code needs, with load and the shared five-minute client timeout as contributing causes; this is a hypothesis, and the concrete cause is established during implementation (FR-001). If the evidence shows that a recent change on main caused it, the fix still makes the support code robust and the finding is recorded.
- **Dependencies**: The spec [Deterministic Payload Conformance Test](../20261025-233727-deterministic-payload-conformance-test/spec.md) (implementation #345, twin #335) depends on this spec. Related earlier work not to be repeated: the Docker-backed evidence of the reference and shot-boundary Analyst capabilities (see the merged specs under `specs/archive/`).
- **Environment**: A Docker-compatible engine on Linux (and the Compose-less registry the tests start) is available as for the existing end-to-end tests; Docker Desktop on Windows stays manual evidence.
- **Out of scope**: Other flaky tests, the content and size of the images, registry or engine configuration of the runner, publication of images, and production registry values. Passing tests are development evidence only and do not establish deployment support or production readiness.
