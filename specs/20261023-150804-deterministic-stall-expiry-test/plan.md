# Implementation Plan: Deterministic Stall Expiry of the Simulated Agent Workload

**Branch**: `20261023-150804-deterministic-stall-expiry-test` | **Date**: 2026-10-23 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261023-150804-deterministic-stall-expiry-test/spec.md`

## Summary

`StallExpiresTheFirstAttemptAndSucceedsOnTheSecond` passes only on its rerun in some verifications. The implementation reproduces the failure, establishes with evidence which uncontrolled race around the shared fake clock causes it, and removes that cause where it lives: the unsynchronized "one attempt row, then advance the clock by 3 seconds" step of `RunToEndAsync`, the simulated workload runner, or platform code. The expected fix replaces the blind jump with a wait on observable conditions (the first attempt is claimed, leased, and observed as stalled by the runner; the clock then moves just past that attempt's recorded lease expiry; the attempt is observed as `expired` exactly once) without weakening any assertion, adding retries, fixed sleeps, or longer limits.

## Technical Context

**Language/Version**: C# on .NET 10 (`src/platform/global.json`).

**Primary Dependencies**: xUnit v3, Shouldly, NATS JetStream client (existing). No new dependency.

**Storage**: PostgreSQL and NATS in disposable test containers (existing).

**Testing**: `SocAlytics.Platform.Integration.Tests` (Docker-backed), the test run repeatedly alone and its class under CPU load.

**Target Platform**: Linux and developer machines with a Docker-compatible runtime.

**Project Type**: Test-support change in a platform monolith's test projects; platform code only if the evidence puts the cause there.

**Performance Goals**: None.

**Constraints**: No assertion weakened, removed, or skipped; no retries, fixed sleeps, or longer limits as the fix; no weakening of claim, fencing, lease, expiry, retry, or outbox semantics; no repetition of #209 or the archived timing spec.

**Scale/Scope**: One test, its shared helper, and the simulated runner it drives.

## Constitution Check

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | No design decision changes; see Architecture Updates. | Pass |
| II. Source-area ownership | Only `src/platform` (tests and the simulated workload test project); no new `src` child. | Pass |
| III. API-first control plane | The runner keeps using the public workload operations; observation reads use test-side SQL as the helper already does. | Pass |
| IV. Evidence over claims | The cause is evidenced by repeated runs and logs; probes must still fail the test. | Pass |
| V. Focused, minimal changes | One test path; no package or abstraction. | Pass |
| Environment features | Needs only the existing .NET SDK and Docker. No environment spec. | Pass |

Post-design re-check: Pass.

## Design

1. **Establish the cause (FR-001).** Run the test at least 20 times alone and its class repeatedly with CPU burners, capturing the runner logger and the platform log of failing runs. Compare against the candidates in [research.md](research.md): the clock jump before the stall is in effect, the jump racing the runner's DPoP proof timestamps and the platform's validation, the jump racing the lease monitor, the second attempt's lease and retry timing overlapping the jump, and late or duplicate ready notifications. Record the evidenced cause in a code comment next to the fix and in the implementation task's final summary, which the commit message and pull request quote (an implementation task cannot write under `specs/`).
2. **Remove the cause where it lives (FR-002).** Only for the evidenced cause, and within these shapes: wait (bounded, on a condition) until the stall is in effect (the runner has read the claim response and decided to stall for attempt 1, the first attempt row is `active`, and its `lease_expires_at` is recorded; the acknowledgement is not part of the condition); advance the fake clock once by `lease_expires_at` (read from the first attempt row) minus the fake clock's current time plus a fixed margin of 100 ms instead of a blind 3 seconds (the second attempt's lease is computed from the advanced fake clock at its claim, so it is not overshot); wait until the first attempt is observed as `expired` (`end_reason` `lease-expired`, and exactly one attempt row has that reason) before waiting for the end; and, if the `active` row and `lease_expires_at` are not enough to show the stall is in effect, make the runner expose the stall as an observable event (decided in T001 from the evidence) rather than the test inferring it from a row count. The `fail-once`, `fail-retryable`, and `fail-permanent` paths never advance the clock and keep their helper behavior.
3. **Keep assertions and shared behavior (FR-003, FR-005).** `succeeded`, exactly two attempts, and at least one checkpoint stay verbatim and are read only after the invocation is terminal and the runner has stopped. Changes to `RunToEndAsync` or the runner keep the other `SimulatedAgentWorkloadBehaviorTests` and `SimulatedAgentWorkloadTests` assertions and behavior unchanged.
4. **Platform code (FR-004).** Changed only if the evidence shows a real defect in claim, lease, or expiry; the change then keeps the documented Agent Orchestration semantics and gets its own focused test.
5. **Prove it (SC-001, SC-002).** Run the test 20 times alone and the class 5 times under load; use probes (never expire the first attempt; make the second attempt fail) to show the test still fails within its existing limits; remove the probes.

## Project Structure

### Documentation (this feature)

```text
specs/20261023-150804-deterministic-stall-expiry-test/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── spec.md
└── checklists/
    └── requirements.md
```

`contracts/` is not created: the feature adds no external interface.

### Source Code (repository root)

```text
src/platform/Tests/
├── SocAlytics.Platform.Integration.Tests/AgentOrchestration/SimulatedAgentWorkloadBehaviorTests.cs
└── SocAlytics.Platform.SimulatedAgentWorkload/SimulatedAgentWorkloadRunner.cs
```

**Structure Decision**: Edit the files the evidenced cause lives in; the two files above are the expected set, and `Support/AgentOrchestrationFixture.cs` or platform code change only when the evidence requires it.

## Architecture Updates

None. The fix removes a race in test code or the Development and Test simulated workload and changes no architecture narrative, design decision, or status statement in `docs/architecture/`; if the evidence puts the cause in platform code, the fix keeps the documented Agent Orchestration claim, lease, expiry, and retry semantics (see [Intelligence and Agents](../../docs/architecture/intelligence-and-agents.md)) and the implementation re-checks this section.
