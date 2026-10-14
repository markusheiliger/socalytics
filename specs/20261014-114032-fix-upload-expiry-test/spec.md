# Feature Specification: Deterministic Upload Session Expiry Test

**Feature Branch**: `20261014-114032-fix-upload-expiry-test`

**Created**: 2026-10-14

**Input**: User description: "UploadSessionExpiryTests.ExpiredSessionIsRefusedImmediatelyAndReleasedBySweep fails intermittently: SessionLifetime is 2 s and the first part PUT can run after the presigned grant expired (403 Request has expired). Make the setup independent of that window while keeping all expiry and sweep assertions."

**Fix for**: #72 (implementation of `specs/20261012-141233-manager-work-execution`)

## Problem

The integration test `ExpiredSessionIsRefusedImmediatelyAndReleasedBySweep` (Recordings, introduced by [Recording Lineage and Upload](../archive/20261005-130702-recording-lineage-upload/spec.md)) starts an upload session with `SessionLifetime` of 2 seconds and then uploads the first part to the presigned URL, which is valid only until the session expires. On a loaded runner the session start and the PUT take longer than 2 s, object storage answers `403 AccessDenied: Request has expired`, and the setup assertion fails before any expiry behavior is checked.

Evidence: run 38549094900 (phase verification of #72): Integration failed 1 of 829 with `part PUT returned 403: AccessDenied: Request has expired`; an earlier diagnosis round recorded the same test as a flake. The test file is unchanged on `main`, so the stopped implementation cannot fix it and its final merge verification will fail intermittently again.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The expiry test passes reliably (Priority: P1)

A maintainer runs the Integration tests on a slow or loaded machine. The expiry test's setup (starting the session and uploading the first part) no longer depends on any wall-clock window, while the test still proves that an expired session is refused immediately and released by the sweep.

**Why this priority**: The intermittent failure blocks the verification and merge of other features.

**Independent Test**: Run the test repeatedly, also with an artificial delay between session start and part upload, and it passes every time.

**Acceptance Scenarios**:

1. **Given** a started upload session with a lifetime (10 minutes) far longer than the test takes, **When** the first part is uploaded however slowly, **Then** the upload succeeds.
2. **Given** that session is made expired afterwards, **When** grants or completion are requested, **Then** both are refused with the `upload-session-expired` conflict, and the session reads as `expired` with an unchanged ETag.
3. **Given** the expired session, **When** the expiry sweep runs twice, **Then** the outcome is exactly as before: state `expired`, storage released once, no recording version, one `recording.upload.expire` audit event, and the multipart upload and object are gone from storage.

### Edge Cases

- A delay between session start and part upload of many seconds must not change the result.
- The test must not wait on real time to pass for the session to expire.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The test setup MUST NOT depend on the first part PUT happening within a short wall-clock window after the session start.
- **FR-002**: The test MUST keep every existing assertion: immediate refusal of grants and completion as `upload-session-expired`, reported state `expired` with an unchanged ETag (the baseline is the ETag read after the setup update, which the test first asserts differs from the pending ETag; refused grants, refused completion, and the derived `expired` read must not change it), an idempotent double sweep, storage release count, no recording version, the audit event, and the storage-side `NoSuchUpload` and not-found results.
- **FR-003**: The session MUST be made expired by deterministic means (moving the test's time provider and the stored expiry), not by waiting for real time.
- **FR-004**: No production code, option, default, or other test changes; no assertion is weakened, removed, or skipped.

### Key Entities

- **Upload session**: the existing pending upload session with its `expires_at`; the test sets that value directly in its own disposable database.

## Success Criteria *(mandatory)*

- **SC-001**: The test passes in 20 consecutive runs, including runs with an added 10 s delay between session start and first part upload.
- **SC-002**: The Integration suite's verification in the implementation of #72 passes without this test failing.
- **SC-003**: The test file contains the same set of expiry and sweep assertions as before (FR-002).

## Assumptions

- The smallest fix is in the test only: a 10 minute session lifetime for the setup (far above the 10 s delay of SC-001), then the session is expired through the test time provider advanced by 11 minutes (below the 30 minute BFF session idle timeout, so the admin session stays valid) and a direct update of the session's `expires_at` in the test's disposable database, after the pending ETag is read. The setup update bumps the version through the trigger, which the test asserts explicitly, so the unchanged-ETag assertion remains equivalent: it still proves the derived expired state, refusals, and report write nothing.
- Dependencies: the feature owning the test is [Recording Lineage and Upload](../archive/20261005-130702-recording-lineage-upload/spec.md); the implementation this fix unblocks is #72 (`specs/20261012-141233-manager-work-execution`).
