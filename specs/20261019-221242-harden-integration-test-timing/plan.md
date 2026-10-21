# Implementation Plan: Timing Margins for Intermittent Integration Tests

**Branch**: `20261019-221242-harden-integration-test-timing` | **Date**: 2026-10-19 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261019-221242-harden-integration-test-timing/spec.md`

## Summary

Five Integration test classes fail at random on loaded runners because their real-time margins are too small. Each is hardened without touching an assertion: the upload session lifetime of `SecretHygieneTests` gets ample margin and the expiry is driven by the factory's fake clock, NATS calls get larger client timeouts or a bounded retry of the timed-out call, and fixed short waits become condition waits with a generous limit.

## Technical Context

**Language/Version**: C# on .NET 10 (`src/platform/global.json`).

**Primary Dependencies**: xUnit v3, Shouldly, NATS.Net (existing). No new dependency.

**Storage**: None.

**Testing**: `SocAlytics.Platform.Integration.Tests` (Docker-backed), each class run repeatedly and with the whole suite.

**Target Platform**: Linux and developer machines with a Docker-compatible runtime.

**Project Type**: Test-only change to a platform monolith's test project.

**Performance Goals**: None; limits only rise, and condition waits return early.

**Constraints**: No assertion weakened, removed, or skipped; no production code change.

**Scale/Scope**: Five test files, plus at most one shared wait helper.

## Constitution Check

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | No design decision changes; see Architecture Updates. | Pass |
| II. Source-area ownership | Only `src/platform/Tests`; no new `src` child. | Pass |
| III. API-first control plane | Not affected. | Pass |
| IV. Evidence over claims | Repeated runs under load and probes that must still fail the tests. | Pass |
| V. Focused, minimal changes | Test files only; no package or abstraction beyond a small wait helper if needed. | Pass |
| Environment features | Needs only the existing .NET SDK and Docker. No environment spec. | Pass |

Post-design re-check: Pass.

## Design

- `SecretHygieneTests`: raise `SessionLifetime` well above the setup time (for example minutes) and replace the `Task.Delay(6s)` with `factory.Time.Advance` past the lifetime, so the first session is valid at the re-grant and the second is expired by the sweep handler deterministically. Keep all expiry and secret assertions.
- `ManagerConsumerTopologyTests`, `TransportRecoveryTests`: where the NATS connection options are built in the test, raise command, request, and connect timeouts; where a single call times out (`NatsJSApiNoResponseException`, publish timeout), wrap only that call in a bounded retry with a generous overall limit. Existing deadlines of condition polls (30 s, 60 s) are raised where they are the failing limit. Assertions on results, durations configured on the stream or consumer, and statuses stay.
- `SimulatedAgentWorkloadBehaviorTests.StallExpiresTheFirstAttemptAndSucceedsOnTheSecond`, `PayloadConformanceTests`: raise the overall limits (the 60-second run and deadline, the 120 iterations of `WaitForReadyWorkAsync`) and keep polling on the condition, advancing the fake clock only as today.
- A shared helper `WaitFor.UntilAsync(condition, timeout, message)` in the Integration test support is added only if three or more call sites share the shape.

## Project Structure

### Documentation (this feature)

```text
specs/20261019-221242-harden-integration-test-timing/
├── plan.md
├── spec.md
├── tasks.md
└── checklists/
    └── requirements.md
```

### Source Code (repository root)

```text
src/platform/Tests/SocAlytics.Platform.Integration.Tests/
├── Recordings/SecretHygieneTests.cs
├── Messaging/ManagerConsumerTopologyTests.cs
├── Analysis/TransportRecoveryTests.cs
├── Analysis/PayloadConformanceTests.cs
└── AgentOrchestration/SimulatedAgentWorkloadBehaviorTests.cs
```

**Structure Decision**: Edit the five failing files in place.

## Architecture Updates

None. The fix changes only timing margins of integration tests and no architecture narrative, design decision, or status statement in `docs/architecture/`.
