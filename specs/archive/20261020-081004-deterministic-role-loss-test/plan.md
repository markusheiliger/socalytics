# Implementation Plan: Deterministic Club Admin Role-Loss Notification Test

**Branch**: `20261020-081004-deterministic-role-loss-test` | **Date**: 2026-10-20 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/archive/20261020-081004-deterministic-role-loss-test/spec.md`

## Summary

`AClubAdminWhoLosesTheRoleBeforeTheSendReceivesNothing` races the hosted email delivery worker. The test starts its harness with the background workers disabled so that only its explicit `DeliverAsync` sends, which makes the role removal always precede delivery. The assertion is unchanged.

## Technical Context

**Language/Version**: C# on .NET 10 (`src/platform/global.json`).

**Primary Dependencies**: xUnit v3, Shouldly (existing). No new dependency.

**Storage**: None.

**Testing**: `SocAlytics.Platform.Integration.Tests` (Docker-backed, Mailpit), the test run repeatedly and with its class.

**Target Platform**: Linux and developer machines with a Docker-compatible runtime.

**Project Type**: Test-only change to a platform monolith's test project.

**Performance Goals**: None.

**Constraints**: No assertion weakened, removed, or skipped; no production code change.

**Scale/Scope**: One test file.

## Constitution Check

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | No design decision changes; see Architecture Updates. | Pass |
| II. Source-area ownership | Only `src/platform/Tests`; no new `src` child. | Pass |
| III. API-first control plane | Not affected. | Pass |
| IV. Evidence over claims | Repeated runs and a probe that must still fail the test. | Pass |
| V. Focused, minimal changes | One test; no package or abstraction. | Pass |
| Environment features | Needs only the existing .NET SDK and Docker. No environment spec. | Pass |

Post-design re-check: Pass.

## Design

- `RegistrySettings` is shared by the class; the role-loss test builds its own settings (a copy of `RegistrySettings` plus `Analysis:Workers:Enabled` = `false`) and passes them to `EmailApiHarness.StartAsync`. Other tests keep `RegistrySettings`.
- The submission, the role deletion, and `CountAsync` (which calls `h.DeliverAsync`) stay as they are; with the worker disabled the message can only be sent by that explicit delivery after the role is gone.
- Fallback if disabling the workers breaks the submission path: keep the workers and hold the queued message back instead (set its due time in the future, delete the role, then make it due) so that delivery still happens only after the role is gone; the assertion stays.

## Project Structure

### Documentation (this feature)

```text
specs/archive/20261020-081004-deterministic-role-loss-test/
├── plan.md
├── spec.md
├── tasks.md
└── checklists/
    └── requirements.md
```

### Source Code (repository root)

```text
src/platform/Tests/SocAlytics.Platform.Integration.Tests/
└── Notifications/PendingRequestNotificationTests.cs
```

**Structure Decision**: Edit the failing test in place.

## Architecture Updates

None. The fix changes only how one integration test controls delivery timing and no architecture narrative, design decision, or status statement in `docs/architecture/`.
