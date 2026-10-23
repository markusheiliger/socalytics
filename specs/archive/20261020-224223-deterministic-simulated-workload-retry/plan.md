# Implementation Plan: Deterministic Fail-Once Retry of the Simulated Agent Workload

**Branch**: `20261020-224223-deterministic-simulated-workload-retry` | **Date**: 2026-10-20 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/archive/20261020-224223-deterministic-simulated-workload-retry/spec.md`

## Summary

`FailOnceSucceedsOnTheSecondAttempt` passes only on its rerun in some verifications. The implementation reproduces the failure, establishes which uncontrolled race causes it, and removes that cause where it lives (the test helper `RunToEndAsync`, the simulated workload runner, or platform code) without weakening assertions or adding retries or longer limits.

## Technical Context

**Language/Version**: C# on .NET 10 (`src/platform/global.json`).

**Primary Dependencies**: xUnit v3, Shouldly, NATS JetStream client (existing). No new dependency.

**Storage**: PostgreSQL in disposable test containers (existing).

**Testing**: `SocAlytics.Platform.Integration.Tests` (Docker-backed), `SimulatedAgentWorkloadBehaviorTests` and `SimulatedAgentWorkloadTests` run repeatedly and under load.

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

1. **Establish the cause.** Run the class repeatedly, alone and with CPU load (for example parallel CPU burners), with the runner's logger and the platform log captured for failing runs. Compare the failing evidence with the candidate races of the spec's Problem section: the two independent real-time limits of the poll loop and the runner, the outbox publication interval before the retry notification, the fetch expiry and unsettled-notification redelivery, the count reads right after the runner is cancelled, and the shared fake clock. T002 removed the two-limit race and added the failure diagnostics, yet under full CPU load the invocation still stayed `accepted`/`queued` with 0 attempts and an empty runner log, so the hop from the submitted invocation through the outbox publication and the queue delivery to the runner's first claim was a further candidate that the earlier attempts at the investigation examined; step 4 evidences it as a notification stolen through the shared queue, and T005 and T006 remove it.
2. **Remove the cause where it lives.** The fixes that fit the candidates, applied only for the evidenced cause: derive both limits from one deadline so the runner outlives the wait; wait for the runner to stop and the invocation to be terminal before reading the counts (the code appears already ordered; T001 verifies it, and no change is made when it holds); make the runner settle a notification only after its effect is committed; keep the fake clock from being advanced for non-stall behaviors. A bounded wait on a condition is allowed; a longer fixed limit or a retry of the test is not.
3. **Keep the shared code behavior.** Changes to `RunToEndAsync` or the runner keep the other `SimulatedAgentWorkloadBehaviorTests` and `SimulatedAgentWorkloadTests` assertions and behaviors unchanged.
4. **Remove the stalled claim.** Two attempts at the former investigation task reproduced the load failure (every core busy) with temporary probes and evidenced the hop: the ready notification is published and reaches the stream, but a runner of another concurrently running test fetches it from the shared `AGENT_WORK` stream and `agent-workload` consumer (one NATS container per assembly, one database per test), its claim answers `404 not-found`, and it acknowledges the message, so the owning runner never sees it. T005 gives each fixture instance its own stream, subject, and consumer through the existing `Messaging:Nats` options; T006 points the runners of the two test classes at them. Claim, fencing, lease, retry, and outbox semantics stay unchanged, and no longer limit or test retry is added; only then does the proof below apply.
5. **Prove it.** The proof is evidence, not a committed change, so the workflow cannot run it as a task; it was made once in the pull request (see `research.md`) and is checked again by the phase verification and the convergence. Run the test at least 20 times alone and `SimulatedAgentWorkloadBehaviorTests` together with `SimulatedAgentWorkloadTests` 5 times under load; use a probe that makes the second attempt fail to show the test still fails on its assertions; remove the probe.

## Project Structure

### Documentation (this feature)

```text
specs/archive/20261020-224223-deterministic-simulated-workload-retry/
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
├── SocAlytics.Platform.Integration.Tests/AgentOrchestration/SimulatedAgentWorkloadTests.cs
├── SocAlytics.Platform.Integration.Tests/AgentOrchestration/Support/AgentOrchestrationFixture.cs
└── SocAlytics.Platform.SimulatedAgentWorkload/SimulatedAgentWorkloadRunner.cs
```

**Structure Decision**: Edit the files the evidenced cause lives in; the four files above are the expected set (the fixture holds the shared work queue the evidenced cause lives in), and platform code and the runner change only when a run proves the isolation insufficient.

## Architecture Updates

None. The fix removes a race in test code or the Development and Test simulated workload and changes no architecture narrative, design decision, or status statement in `docs/architecture/`; if the evidence puts the cause in platform code, the fix keeps the documented Agent Orchestration semantics and the implementation re-checks this section.
