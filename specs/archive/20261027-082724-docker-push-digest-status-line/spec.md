# Feature Specification: Docker Push Digest from the Status Line

**Feature Branch**: `20261027-082724-docker-push-digest-status-line`

**Created**: 2026-10-27

**Status**: Draft

**Fix for**: the phase verification of `specs/20261025-003459-deterministic-agent-workload-test` (14 failing End-to-End tests)

**Input**: User description: "Make the Docker push helper of the End-to-End test support accept the manifest digest from the push status line. `DockerEngine.PushAsync` in `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/Support/TestAnalystImage.cs` reads the pushed manifest digest only from an `aux.Digest` entry of the JSON progress stream of the engine's push call. The Docker engine of the GitHub Actions runner completes the push but reports the digest only in a status line of the form `<tag>: digest: sha256:<64 hex> size: <n>` and emits no `aux` entry, so all 14 End-to-End tests that build and push a Test Analyst image fail. The spec `specs/20261025-003459-deterministic-agent-workload-test` depends on this spec."

## Clarifications

### Session 2026-10-27

- Q: Which pushed digest wins when both sources report one? → A: The `aux.Digest` entry stays authoritative; the status line is used only when no `aux` digest was reported. Both name the same manifest for a successful push.
- Q: May the fix add retries, sleeps, or timeout changes? → A: No. It only widens what the helper accepts as the digest report.
- Q: May production code change? → A: No. Only the End-to-End test support and its tests change.

## Problem

The End-to-End tests build a Test Analyst image, push it to the registry of the test stamp, and register it by the manifest digest the push reports. The helper reads the digest from the engine's JSON progress stream. Two engines report it differently:

- Engines that emit an `aux` entry with a `Digest` field (the development engines the helper was written against).
- The Docker engine of the GitHub Actions runner, which completes the push (`Pushed`) but reports the digest only in a `status` line of the form `<tag>: digest: sha256:<64 hex> size: <n>` and emits no `aux` entry.

On the second engine the helper finds no digest and throws `The push did not report a manifest digest`, so every End-to-End test that calls `TestAnalystImage.BuildAndPushAsync` fails (`ReferenceAnalystImage`, `ShotBoundaryImage`, and the other end-to-end tests that build and push a Test Analyst image, 14 in total). The failures are unrelated to the changes of the pull requests that run them, and block the stopped implementation of `specs/20261025-003459-deterministic-agent-workload-test`, whose phase verification fails in these tests although it only adds comments to a test file of the Integration tests.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Image-pushing End-to-End tests pass on either engine (Priority: P1)

A maintainer, or the verification of an implementation pull request on a GitHub Actions runner, runs the End-to-End tests that build and push a Test Analyst image and sees them pass whether the engine reports the pushed digest in an `aux` entry, in the status line, or in both, and still sees a clear failure when the push really failed or reported no digest.

**Why this priority**: Fourteen End-to-End tests fail on the runner's engine, and a dependent implementation cannot be verified until they pass.

**Independent Test**: Run `dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --filter "FullyQualifiedName~ReferenceAnalystImage|FullyQualifiedName~ShotBoundaryImage"` on a Linux engine; the tests pass. The deterministic parsing tests pass without Docker.

**Acceptance Scenarios**:

1. **Given** a push progress stream with an `aux` entry carrying a `Digest`, **When** the helper reads it, **Then** it returns that digest.
2. **Given** a push progress stream with no `aux` entry and a `status` line `<tag>: digest: sha256:<64 hex> size: <n>`, **When** the helper reads it, **Then** it returns `sha256:<64 hex>`.
3. **Given** a stream with both, **When** the helper reads it, **Then** it returns the `aux` digest.
4. **Given** a stream with neither, **When** the helper reads it, **Then** it throws `InvalidOperationException` whose message contains the engine output.
5. **Given** a stream with an `errorDetail` entry (before or after a digest report), **When** the helper reads it, **Then** it fails the push with `InvalidOperationException` naming the error.
6. **Given** a status line whose digest is malformed (not `sha256:`, fewer or more than 64 hex characters, non-hex characters, or extra text around the digest), **When** the helper reads it, **Then** the digest is not accepted and the push fails as reporting no digest.
7. **Given** the End-to-End tests run on a Linux engine, **When** they build and push a Test Analyst image, **Then** `ReferenceAnalystImage` and `ShotBoundaryImage` pass.

### Edge Cases

- A progress line that is not related to the digest (layer progress, `Pushed`, `Preparing`) MUST NOT be mistaken for a digest report.
- A `status` field that is absent or not a string MUST be ignored, not fail the parsing.
- A line that is not JSON keeps throwing `JsonException`; a JSON line that is not an object, an `aux` value that is not an object, an `aux.Digest` that is not a string, and an `aux.Digest` that is JSON null throw `InvalidOperationException`; a null never overwrites an earlier `aux` digest; the helper does not skip such lines.
- A status line with a different tag than the pushed one is still a well-formed report; the digest is accepted by form, not by tag.
- Upper-case hexadecimal characters are not accepted, because the engine reports lower-case digests.
- When several status lines match, the last one wins, as with the `aux` entry.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The push helper MUST keep using the `Digest` of an `aux` entry of the push progress stream when present; an `aux` value that is not an object, or a `Digest` that is not a string (including JSON null), fails with `InvalidOperationException` and never overwrites an earlier `aux` digest.
- **FR-002**: The helper MUST additionally accept the digest from a `status` field matching `<tag>: digest: sha256:<hex> size: <n>`, anchored so that only a well-formed `sha256:` digest of exactly 64 lower-case hexadecimal characters and a numeric size is accepted.
- **FR-003**: When both sources report a digest, the `aux` digest MUST win.
- **FR-004**: The helper MUST keep failing the push with `InvalidOperationException` on an `errorDetail` entry.
- **FR-005**: The helper MUST still throw `InvalidOperationException` with the engine output when neither source reports a digest, including when the only candidate is malformed.
- **FR-006**: The parsing MUST be separable from the engine call so that deterministic unit-level tests cover aux only, status line only, both, neither, an error entry, and a malformed digest without Docker, a socket, or a registry. Each test MUST fail on its assertions if the behavior it covers is broken.
- **FR-007**: The change MUST touch only the End-to-End test support and its tests: no production code, no sleeps, no retries, and no timeout changes.
- **FR-008**: The change MUST be verified with the `ReferenceAnalystImage` and `ShotBoundaryImage` End-to-End tests on a Linux engine.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The six parsing cases (aux only, status line only, both, neither, error entry, malformed digest) pass in a run that needs no Docker, in under 5 seconds in total.
- **SC-002**: The `ReferenceAnalystImage` and `ShotBoundaryImage` End-to-End tests pass on a Linux engine, and the other End-to-End tests that build and push a Test Analyst image no longer fail with `The push did not report a manifest digest`.
- **SC-003**: Each of two probes (ignoring the status line, and accepting a 63-character digest) makes at least one parsing test fail on its assertions; the probes are removed afterward.
- **SC-004**: The change touches only files under `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/` plus this spec folder; no production file, sleep, retry, or timeout changes.
- **SC-005** (post-merge evidence, observed when `specs/20261025-003459-deterministic-agent-workload-test` is implemented and not verified by a task of this spec): its phase verification reports no failing End-to-End test caused by the missing digest.

## Assumptions

- **Scope**: The helper is test support code from the Docker Desktop runtime adapter, materialized segments, and capability fact payload schema features. The registry is the one of the test stamp; the digest names the manifest the engine pushed.
- **Dependencies**: `specs/20261025-003459-deterministic-agent-workload-test` depends on this spec and implements only after it is merged. No other spec is a prerequisite.
- **Environment**: The verification needs a Linux Docker engine (the runner's engine reproduces the status-line form); the parsing tests need none.
- **Out of scope**: Other flaky or failing End-to-End tests, a change of how the digest is used after the push, real-registry evidence beyond the test stamp, and production values. Passing tests are development evidence only and do not establish deployment support or production readiness.
