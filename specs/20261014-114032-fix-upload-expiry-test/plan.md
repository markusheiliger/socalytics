# Implementation Plan: Deterministic Upload Session Expiry Test

**Branch**: `20261014-114032-fix-upload-expiry-test` | **Date**: 2026-10-14 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261014-114032-fix-upload-expiry-test/spec.md`

## Summary

Remove the wall-clock race from `UploadSessionExpiryTests.ExpiredSessionIsRefusedImmediatelyAndReleasedBySweep`: start the session with a long lifetime, upload the first part, then expire the session deterministically (advance the test `MutableTimeProvider` and set the session's `expires_at` in the past in the test database) instead of `Task.Delay`. All assertions stay.

## Technical Context

**Language/Version**: C# on .NET 10 (xUnit v3, Shouldly).

**Primary Dependencies**: existing `PlatformApiFactory`, `MutableTimeProvider`, `IsolatedDatabase`, `RustFsContainerFixture`; no new packages.

**Storage**: PostgreSQL test database; one `UPDATE` of `expires_at` in the test (the `version` trigger bumps the version before the ETag is read).

**Testing**: `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --filter-class "*UploadSessionExpiryTests"` (Docker required).

**Constraints**: test-only change; the API reads expiry from the injected `TimeProvider`, while the sweep and completion SQL compare with the database `now()`, so both must be moved.

## Constitution Check

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | No design decision changes. | Pass |
| II. Source-area ownership | Only `src/platform/Tests`. | Pass |
| III. API-first control plane | Behavior is still exercised through the API. | Pass |
| IV. Evidence over claims | The test is made deterministic and verified repeatedly. | Pass |
| V. Focused, minimal changes | One test file. | Pass |

## Project Structure

```text
specs/20261014-114032-fix-upload-expiry-test/
├── spec.md
├── plan.md
├── tasks.md
└── checklists/requirements.md

src/platform/Tests/SocAlytics.Platform.Integration.Tests/Recordings/
└── UploadSessionExpiryTests.cs
```

## Architecture Updates

None. The change is limited to a test's setup and alters no design decision or architecture statement.
