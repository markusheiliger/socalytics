---

description: "Task list for Deterministic Upload Session Expiry Test"
---

# Tasks: Deterministic Upload Session Expiry Test

**Input**: Design documents from `specs/20261014-114032-fix-upload-expiry-test/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md)

**Tests**: The task changes a test; its verification is running it.

## Format: `[ID] [P?] [Story] Description`

## Phase 1: User Story 1 - The expiry test passes reliably (Priority: P1) 🎯 MVP

- [ ] T001 [US1] In `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Recordings/UploadSessionExpiryTests.cs`, change `SessionLifetime` from `00:00:02` to `00:30:00`; after the first part PUT and before reading the session's ETag, expire the session deterministically: `UPDATE socalytics.recording_upload_sessions SET expires_at = now() - interval '1 second' WHERE id = '<id>'` through the existing `MigratorConnectionString` helper and `factory.Time.Advance(TimeSpan.FromHours(1))`; remove the `Task.Delay`. Keep every other assertion unchanged. Verify: `dotnet build src/platform/SocAlytics.Platform.slnx --no-restore` then `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter-class "*UploadSessionExpiryTests"` passes repeatedly (at least 5 times), also with a temporary 10 s delay before the PUT that is not committed.

## Dependencies & Execution Order

T001 is the only task.
