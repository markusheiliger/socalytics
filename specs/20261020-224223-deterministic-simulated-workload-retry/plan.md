# Implementation Plan: Deterministic Fail-Once Retry of the Simulated Agent Workload

**Branch**: `20261020-224223-deterministic-simulated-workload-retry` | **Date**: 2026-10-20 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261020-224223-deterministic-simulated-workload-retry/spec.md`

## Summary

`FailOnceSucceedsOnTheSecondAttempt` passes only on its rerun in some verifications. The implementation reproduces the failure, establishes which uncontrolled race causes it, and removes that cause where it lives (the test helper `RunToEndAsync`, the simulated workload runner, or platform code) without weakening assertions or adding retries or longer limits.

## Technical Context

**Language/Version**: C# on .NET 10 (`src/platform/global.json`).

**Primary Dependencies**: xUnit v3, Shouldly, NATS JetStream client (existing). No new dependency.

**Storage**: PostgreSQL in disposable test containers (existing).

**Testing**: `SocAlytics.Platform.Integration.Tests` (Docker-backed), the class run repeatedly and under load.

**Target Platform**: Linux and developer machines with a Docker-compatible runtime.

**Project Type**: Test-support change in a platform monolith's test projects; platform code only if the evidence puts the cause there.

**Performance Goals**: None.

**Constraints**: No assertion weakened, removed, or skipped; no retries or longer fixed limits as the fix; no weakening of claim, fencing, lease, retry, or outbox semantics.

**Scale/Scope**: One test, its shared helper, and the simulated runner it drives.

## Constitution Check

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | No design decision changes; see Architecture Updates. | Pass |
| II. Source-area ownership | Only `src/platform` (tests and the simulated workload test project); no new `src` child. | Pass |
| III. API-first control plane | The runner keeps using the public workload operations. | Pass |
| IV. Evidence over claims | The cause is evidenced by repeated runs and logs; a probe must still fail the test. | Pass |
| V. Focused, minimal changes | One test path; no package or abstraction. | Pass |
| Environment features | Needs only the existing .NET SDK and Docker. No environment spec. | Pass |

Post-design re-check: Pass.

## Design

1. **Establish the cause.** Run the class repeatedly, alone and with CPU load (for example parallel CPU burners), with the runner's logger and the platform log captured for failing runs. Compare the failing evidence with the candidate races of the spec's Problem section: the two independent real-time limits of the poll loop and the runner, the outbox publication interval before the retry notification, the fetch expiry and unsettled-notification redelivery, the count reads right after the runner is cancelled, and the shared fake clock. T002 removed the two-limit race and added the failure diagnostics, yet under full CPU load the invocation still stayed `accepted`/`queued` with 0 attempts and an empty runner log, so the hop from the submitted invocation through the outbox publication and the queue delivery to the runner's first claim is a further candidate that T005 examines.
2. **Remove the cause where it lives.** The fixes that fit the candidates, applied only for the evidenced cause: derive both limits from one deadline so the runner outlives the wait; wait for the runner to stop and the invocation to be terminal before reading the counts (the code appears already ordered; T001 verifies it, and no change is made when it holds); make the runner settle a notification only after its effect is committed; keep the fake clock from being advanced for non-stall behaviors. A bounded wait on a condition is allowed; a longer fixed limit or a retry of the test is not.
3. **Keep the shared code behavior.** Changes to `RunToEndAsync` or the runner keep the other `SimulatedAgentWorkloadBehaviorTests` and `SimulatedAgentWorkloadTests` assertions and behaviors unchanged.
4. **Examine the stalled claim.** T005 reproduces the load failure (every core busy) and, with the diagnostics and temporary probes, finds the hop at which a submitted invocation is never claimed; T006 removes that cause where it lives by controlling the racing input or waiting on the awaited condition, not by a longer limit, and only then does the proof below apply.
5. **Prove it.** Run the test at least 20 times alone and the class 5 times under load; use a probe that makes the second attempt fail to show the test still fails on its assertions; remove the probe.

## Project Structure

### Documentation (this feature)

```text
specs/20261020-224223-deterministic-simulated-workload-retry/
├── plan.md
├── research.md
├── spec.md
├── tasks.md
└── checklists/
    └── requirements.md
```

### Source Code (repository root)

```text
src/platform/Tests/
├── SocAlytics.Platform.Integration.Tests/AgentOrchestration/SimulatedAgentWorkloadBehaviorTests.cs
└── SocAlytics.Platform.SimulatedAgentWorkload/SimulatedAgentWorkloadRunner.cs
```

**Structure Decision**: Edit the files the evidenced cause lives in; the two files above are the expected set, and platform code changes only when the evidence (T005) requires it.

## Architecture Updates

None. The fix removes a race in test code or the Development and Test simulated workload and changes no architecture narrative, design decision, or status statement in `docs/architecture/`; if the evidence puts the cause in platform code, the fix keeps the documented Agent Orchestration semantics and the implementation re-checks this section.
