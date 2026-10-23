# Implementation Plan: Deterministic Pending-Notification Recovery Test

**Branch**: `20261021-010051-deterministic-workflow-availability-test` | **Date**: 2026-10-21 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261021-010051-deterministic-workflow-availability-test/spec.md`

## Summary

`WorkflowAvailabilityTests.Pending_notifications_wait_while_unresolvable_and_produce_one_run_after_recovery` passes only on its rerun in some verifications. The implementation reproduces the failure, establishes which uncontrolled race causes it, and removes that cause where it lives (the test, its NATS fixture or support, or platform code) without weakening assertions or adding retries or longer limits.

## Technical Context

**Language/Version**: C# on .NET 10 (`src/platform/global.json`).

**Primary Dependencies**: xUnit v3, Shouldly, NATS JetStream client (existing). No new dependency.

**Storage**: PostgreSQL and NATS in disposable test containers (existing).

**Testing**: `SocAlytics.Platform.Integration.Tests` (Docker-backed), the class run repeatedly and under load.

**Target Platform**: Linux and developer machines with a Docker-compatible runtime.

**Project Type**: Test change in a platform monolith's test project; platform code only if the evidence puts the cause there.

**Performance Goals**: None.

**Constraints**: No assertion weakened, removed, or skipped; no retries or longer fixed limits as the fix; no weakening of postponement, redelivery, idempotency, or single-run semantics.

**Scale/Scope**: One test, its support, and the code it covers.

## Constitution Check

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | No design decision changes; see Architecture Updates. | Pass |
| II. Source-area ownership | Only `src/platform`; no new `src` child. | Pass |
| III. API-first control plane | Not affected. | Pass |
| IV. Evidence over claims | The cause is evidenced by repeated runs and logs; probes must still fail the test. | Pass |
| V. Focused, minimal changes | One test path; no package or abstraction. | Pass |
| Environment features | Needs only the existing .NET SDK and Docker. No environment spec. | Pass |

Post-design re-check: Pass.

## Design

1. **Establish the cause.** Run the class repeatedly, alone and with CPU load, capturing the consumer and platform logs of failing runs. Compare the evidence with the candidates of the spec's Problem section: the fixed sleep, the cached availability status, shared NATS streams across the collection, separate count reads, and real-time redelivery.
2. **Remove the cause where it lives.** Fixes that fit the candidates, applied only for the evidenced cause: replace the fixed sleep with a wait for an observable sign that the consumer postponed the notification (for example the stream's redelivery count or consumer state) before asserting; isolate the stream or consumer state per test (purge before start or use a test-specific stream and consumer name through options); read the receipt and run state after the awaited condition in one consistent read; ensure the availability status is refreshed before the health assertion. A bounded wait on a condition is allowed; a longer fixed limit or a retry of the test is not.
3. **Keep the shared code behavior.** Changes to shared support keep the other tests of the class and of the `NatsFaultInjection` collection unchanged.
4. **Prove it.** Run the test at least 20 times alone and the class 5 times under load; use probes (a run created while unresolvable; two runs after recovery) to show the test still fails; remove the probes.

## Project Structure

### Documentation (this feature)

```text
specs/20261021-010051-deterministic-workflow-availability-test/
├── plan.md
├── research.md
├── spec.md
├── tasks.md
├── verification.md   # created by T002
└── checklists/
    └── requirements.md
```

### Source Code (repository root)

```text
src/platform/Tests/SocAlytics.Platform.Integration.Tests/
├── Analysis/WorkflowAvailabilityTests.cs
└── Infrastructure/NatsContainerFixture.cs
```

**Structure Decision**: Edit the files the evidenced cause lives in; the two files above are the expected set, and platform code changes only when the evidence requires it.

## Architecture Updates

None. The fix removes a race in test code and changes no architecture narrative, design decision, or status statement in `docs/architecture/`; if the evidence puts the cause in platform code, the fix keeps the documented notification semantics and the implementation re-checks this section.
