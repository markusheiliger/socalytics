# Implementation Plan: Remove the Flakiness of the Member Invitation Audit Trail Test

**Branch**: `20261027-051220-fix-invitation-audit-flake` | **Date**: 2026-10-27 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261027-051220-fix-invitation-audit-flake/spec.md`

## Summary

`AuditTrailHoldsOneEventPerStepAndNothingSecretIsRecorded` counts `invitation.issued` events by the masked address of the first invitee. Every address the harness generates (`EmailApiHarness.NewAddress()`, `member-<guid>@example.test`) masks to the same value `m***@e***.test`, so the count also matches the link of the third invitation (`audit-c`), which the test creates and revokes without ever delivering it. The platform behaves correctly: the message of `audit-c` is queued by the creation, and the host's own background email delivery worker (its first iteration runs once the database is healthy, on a frozen test clock, so a loaded runner can place it after the creation and before the revoke) may claim it, issue its link, and record a legitimate `invitation.issued` event. That event carries the masked address shared with the first invitee, so the count is 3. The plan reproduces this first (research R1), then removes the collision at its source in the test: the first invitee gets an address whose masked form is unique among the test's invitees, so the unchanged assertion counts exactly the links issued to that invitee (one invitation and one resend) whatever the background worker does with the other invitations. The platform code is not changed, because the processor claims a message under `FOR UPDATE SKIP LOCKED` and issues and audits it inside that one transaction (research R2).

## Technical Context

**Language/Version**: C# on .NET 10 (SDK pinned by `src/platform/global.json`); xUnit v3 and Shouldly

**Primary Dependencies**: existing integration-test support only (`EmailApiHarness`, `PlatformApiFactory`, `MutableTimeProvider`, PostgreSQL and Mailpit container fixtures); no new dependency

**Storage**: PostgreSQL in a disposable container (existing tables `socalytics.security_audit_event`, `socalytics.email_messages`, `socalytics.member_invitation_links`); no schema change, no migration

**Testing**: `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --filter` on `MemberInvitationSafetyTests`, repeated alone and under CPU load; one full run of the project

**Target Platform**: Linux runner with a Docker-compatible container runtime

**Project Type**: platform test fix (one test project, executable evidence only)

**Performance Goals**: N/A

**Constraints**: no assertion removed or loosened, no retry, fixed delay, or longer timeout; the stopped spec `specs/20261025-113505-deterministic-fact-payload-telemetry-test` is not touched

**Scale/Scope**: one test file (the harness and platform code stay unchanged)

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- **I. Architecture Is the Design Authority**: pass. No design changes; the documented invitation and email delivery behavior stays as it is. The plan has an `## Architecture Updates` section.
- **II. Source-Area Ownership**: pass. Changes are in `src/platform/Tests/SocAlytics.Platform.Integration.Tests`; no new `src/` child, no new directory.
- **III. API-First Control Plane**: pass. No API, contract, or OpenAPI change.
- **IV. Evidence Over Claims**: pass. The fix is backed by a reproduction, repeated runs alone and under load, and three probes; the evidence is development evidence only.
- **V. Focused, Minimal Changes**: pass. One test file is changed; no abstraction, no dependency.
- **Tooling constraints**: pass. The environment actions already provide the .NET SDK and Docker-backed integration tests; no environment spec is needed.

Post-design re-check: unchanged, pass.

## Project Structure

### Documentation (this feature)

```text
specs/20261027-051220-fix-invitation-audit-flake/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── checklists/
└── tasks.md             # created by /speckit-tasks
```

There are no `contracts/`: the change adds no interface.

### Source Code (repository root)

```text
src/platform/Tests/SocAlytics.Platform.Integration.Tests/
└── IdentityAccess/
    └── MemberInvitationSafetyTests.cs   # test of the audit trail: unique masked address for the first invitee, evidence comment
```

**Structure Decision**: The cause lives in the test's data (colliding masked addresses) combined with a legitimate background delivery, so the fix is local to the test file. The shared harness (`EmailApiHarness`) and the platform code (`EmailDeliveryProcessor`, stores) stay unchanged unless the reproduction (R1) shows otherwise; in that case the cause found there is fixed where it lives and this plan is amended through the Spec Kit flow.

## Architecture Updates

None. The change only makes an integration test deterministic and does not alter the documented behavior of member invitations, email delivery, audit events, or any other narrative in `docs/architecture/`; no implementation-status statement there becomes true or false, so no documentation task for `docs/architecture/` is needed.

## Complexity Tracking

No constitution violations.
