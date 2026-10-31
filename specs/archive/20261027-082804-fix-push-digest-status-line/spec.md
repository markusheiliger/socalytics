# Feature Specification: Read the Pushed Manifest Digest from Every Engine's Push Output

**Feature Branch**: `20261027-082804-fix-push-digest-status-line`

**Created**: 2026-10-27

**Status**: Draft

**Fix for**: the merge verification of the implementation of `specs/archive/20261023-085421-cv-inference-environment` (twin #256, pull request #318), which fails in unrelated platform end-to-end tests; that spec depends on this spec.

**Input**: User description: "Make the Docker-backed end-to-end test helper read the pushed manifest digest from every engine's push output. Where it lives: `DockerEngine.PushAsync` in `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/Support/TestAnalystImage.cs` (test support of the Analyst end-to-end evidence, used by `ShotBoundaryAnalystImage.BuildAndPushAsync` and the Analyst image tests). Today it accepts the digest only as the `aux.Digest` property of a JSON line of the push stream and otherwise throws `The push did not report a manifest digest`; engines on the CI runner report the digest only in a status line such as `{\"status\":\"<tag>: digest: sha256:<hex> size: <n>\"}`, so 14 of 33 EndToEnd tests (among them `ShotBoundaryImageTests`) fail with that exception. Required change: keep the `aux.Digest` path and the `errorDetail` handling (a pushed image that reports an error still fails visibly), and additionally accept the status line by parsing the `sha256:` digest (64 lowercase hex characters) from it with a regular expression; the returned digest keeps the `sha256:<hex>` form; when neither source yields a digest the helper still throws the same descriptive error with the push body. Verify with the EndToEnd tests that push Analyst images (`ShotBoundaryImageTests`, `ReferenceAnalystImageTests`, and the other tests that use the helper) on a Linux engine; no production code, contract, or documentation of the platform changes beyond what the test-support behaviour requires."

## Problem

The Docker-backed end-to-end tests push a locally built Analyst image to the test registry and need the manifest digest of the pushed image to register it by digest. The test helper reads that digest only from the structured `aux` record (`Digest` property) of the engine's push stream. Engines on the CI runner do not emit that record; they report the digest only in a plain status line of the form `<tag>: digest: sha256:<hex> size: <n>`. The helper then throws `The push did not report a manifest digest`, and 14 of the 33 end-to-end tests fail, among them `ShotBoundaryImageTests`, although the image was pushed correctly.

The failure lives in test support only. It blocks the merge verification of the implementation of `specs/archive/20261023-085421-cv-inference-environment` (pull request #318), which changes nothing but environment actions and its own spec folder.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Pushed Analyst images work on every engine (Priority: P1)

A maintainer, or the verification of any implementation pull request, runs the Docker-backed end-to-end tests on a Linux engine of either push-output style, and every test that pushes an Analyst image obtains the pushed manifest digest and proceeds.

**Why this priority**: 14 of 33 end-to-end tests fail on the CI runner and hold back the merge of an unrelated implementation.

**Independent Test**: Run `ShotBoundaryImageTests`, `ReferenceAnalystImageTests`, and the other tests that use the helper on a Linux engine; all obtain a digest and pass. Feed the helper recorded push streams of each style and compare the returned digest.

**Acceptance Scenarios**:

1. **Given** a push stream that carries the digest only in a status line `<tag>: digest: sha256:<64 lowercase hex> size: <n>`, **When** the helper reads it, **Then** it returns `sha256:<hex>`.
2. **Given** a push stream that carries the digest in the `aux` record's `Digest` property, **When** the helper reads it, **Then** it returns that digest exactly as before.
3. **Given** a stream with both, **When** the helper reads it, **Then** it returns the `aux` digest (the `aux` digest wins and a differing status-line digest is ignored without a new failure).
4. **Given** a stream with an `errorDetail` line, **When** the helper reads it, **Then** it fails visibly with the existing push-failure error, even if a digest line appears too.
5. **Given** a stream with neither source, **When** the helper reads it, **Then** it throws the same descriptive error, `The push did not report a manifest digest`, followed by the push body.

### Edge Cases

- A status line that mentions a `sha256:` value that is not a push digest (for example a layer progress or "Layer already exists" line) MUST NOT be taken as the digest; only a status of the form `<tag>: digest: sha256:<hex> size: <n>` counts.
- A value of fewer or more than 64 hex characters, or with uppercase hex, MUST NOT be accepted.
- Lines of the stream that are not JSON objects with the expected properties MUST NOT hide a digest on a later line or turn the helper's error into an unrelated parsing failure where it was not one before.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The helper MUST keep reading the digest from the `aux` record's `Digest` property of a push stream line, unchanged.
- **FR-002**: The helper MUST additionally accept a digest from a `status` line of the form `<tag>: digest: sha256:<hex> size: <n>`, where `<hex>` is exactly 64 lowercase hexadecimal characters, parsed with a regular expression.
- **FR-003**: The returned digest MUST keep the `sha256:<hex>` form on both paths.
- **FR-004**: The helper MUST keep failing visibly with the existing push-failure error when a stream line carries `errorDetail`.
- **FR-005**: When neither source yields a digest, the helper MUST throw the same descriptive error as today, `The push did not report a manifest digest`, with the push body.
- **FR-006**: The change MUST be confined to the end-to-end test support (`DockerEngine.PushAsync` in `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/Support/TestAnalystImage.cs`, plus tests of the parsing where useful); it MUST NOT change production code, contracts, or platform documentation beyond what the test-support behaviour requires, and MUST NOT weaken or skip any end-to-end test or assertion.
- **FR-007**: The callers (`ShotBoundaryAnalystImage.BuildAndPushAsync`, the Test Analyst image build and the Analyst image tests) MUST need no change.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: On a Linux engine, `ShotBoundaryImageTests` and `ReferenceAnalystImageTests` pass, and no end-to-end test fails with `The push did not report a manifest digest`.
- **SC-002**: Parsing of recorded push streams returns the expected digest in 100% of the cases for the `aux` style, the status-line style, both together, an `errorDetail` stream, and a stream without a digest.
- **SC-003**: No production source file, contract, or platform documentation file changes.
- **SC-004** (post-merge evidence, not verified by a task of this spec): the merge verification of `specs/archive/20261023-085421-cv-inference-environment` no longer fails in these end-to-end tests.

## Assumptions

- **Cause**: The push succeeds on the CI runner's engine; only the digest's location in the stream differs. The status line format is `<tag>: digest: sha256:<hex> size: <n>`, as reported in the guidance of the person who asked.
- **Scope**: Test support of the Analyst end-to-end evidence only; this is not a platform feature, and `docs/architecture/` states nothing the change makes false.
- **Dependencies**: A Docker-compatible Linux engine is needed for the end-to-end verification. The spec `specs/archive/20261023-085421-cv-inference-environment` depends on this spec.
- **Out of scope**: Other end-to-end failures, pull behaviour, registry authentication, and production readiness.
- **Evidence**: Passing tests are development evidence only and do not establish deployment support or production readiness.
