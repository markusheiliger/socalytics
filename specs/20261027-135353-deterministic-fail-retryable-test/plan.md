# Implementation Plan: Deterministic Retryable Failure of the Simulated Agent Workload

**Branch**: `20261027-135353-deterministic-fail-retryable-test` | **Date**: 2026-10-27 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261027-135353-deterministic-fail-retryable-test/spec.md`

## Summary

`SimulatedAgentWorkloadBehaviorTests.FailRetryableEndsWithAttemptsExhausted` is flaky for an unestablished cause. The plan is evidence first: repeat the test alone and its class without synthetic CPU load, capturing the runner log, attempt rows, lineage entries, and outbox and NATS state on each failure, to choose between the candidate causes in [research.md](research.md). CPU-loaded runs are optional supplemental evidence. The fix then goes where the cause lives, by awaiting an observable condition or controlling the racing input; the most likely homes are the shared helper `RunToEndAsync` and the `SimulatedAgentWorkloadRunner`. Platform behavior, assertions, limits, and the other behaviors stay unchanged.

## Technical Context

**Language/Version**: C# on .NET 10 (SDK pinned by `src/platform/global.json`)

**Primary Dependencies**: xUnit v3, Shouldly, NATS.Net JetStream client, Testcontainers (PostgreSQL and NATS fixtures), the platform's fake `TimeProvider` (`Factory.Time`)

**Storage**: PostgreSQL (`agent_attempts`, `agent_lineage_entries`, outbox) and NATS JetStream (`AGENT_WORK` work-queue stream, durable explicit-ack consumer, `MaxDeliver` unlimited) in disposable containers

**Testing**: `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --filter "FullyQualifiedName~SimulatedAgentWorkloadBehaviorTests"`; 20 repeated runs alone and at least 5 repeated class runs without synthetic CPU load (CPU-loaded runs optional); two removable probes (stop redelivery after attempt 2, make attempt 3 succeed)

**Target Platform**: Linux CI runner and developer machines with a Docker-compatible runtime

**Project Type**: Test support and test code of the platform monolith (plus, only if the evidence demands it, platform code)

**Performance Goals**: N/A (no new limits; the existing 3 minute bounded wait, 2 s lease, 2 s fetch expiry, and 200 ms runner retry delay stay)

**Constraints**: No retries, fixed sleeps, skips, or longer limits; assertions (`failed`, `attempts-exhausted`, exactly 3 attempts) unchanged; documented Agent Orchestration behavior unchanged; fail-once, fail-permanent, and stall behaviors and helper behavior unchanged

**Scale/Scope**: One test, the shared helper `RunToEndAsync`, and at most the runner and one platform operation

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- **I. Architecture authority**: Pass. No design change; the Agent Orchestration semantics in `docs/architecture/` stay as they are (see Architecture Updates).
- **II. Source-area ownership**: Pass. All changes stay under `src/platform/` (`Tests/` and, only if evidenced, the platform layers); no new `src/` child.
- **III. API-first control plane**: Pass. The test keeps using the `/api/v1` operations and the workload operations; no direct database access beyond the test's existing read-only evidence queries.
- **IV. Evidence over claims**: Pass. The fix is backed by repeated-run evidence, probes that prove the test still fails on broken behavior, and a focused test for any platform fix. Results are development evidence only.
- **V. Focused, minimal changes**: Pass. Only the file the evidenced cause lives in changes; no new abstractions or dependencies.
- **Environment**: Pass. Docker-backed Integration tests already run through the existing `environment-setup` and `environment-verify` actions; no new tool is needed, so no environment spec is required.

Post-design re-check: unchanged, all gates pass; no Complexity Tracking entry.

## Project Structure

### Documentation (this feature)

```text
specs/20261027-135353-deterministic-fail-retryable-test/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── checklists/requirements.md
└── tasks.md             # Created by /speckit-tasks
```

No `contracts/` folder: the feature adds and changes no interface.

### Source Code (repository root)

```text
src/platform/Tests/
├── SocAlytics.Platform.Integration.Tests/AgentOrchestration/
│   ├── SimulatedAgentWorkloadBehaviorTests.cs   # test and shared helper RunToEndAsync (likely home of the fix)
│   └── Support/AgentOrchestrationFixture.cs     # fixture, only if the evidence points at it
└── SocAlytics.Platform.SimulatedAgentWorkload/
    └── SimulatedAgentWorkloadRunner.cs          # only if the evidence points at the runner
src/platform/SocAlytics.Platform.Application/AgentOrchestration/Execution/
├── FailAgentAttempt.cs, ClaimAgentInvocation.cs, MonitorAgentAttempts.cs  # only if the evidence shows a platform defect
```

**Structure Decision**: No new files in source areas. The fix edits the file the evidenced cause lives in (see [research.md](research.md) for the decision procedure); a platform fix adds a focused test under `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/`.

## Architecture Updates

None. The feature changes test support code only (or, if the evidence shows a platform defect, makes the code match the Agent Orchestration semantics that `docs/architecture/` already documents for failure, retry, redelivery, and attempt exhaustion). A search of `docs/architecture/` for the test, its helper, and the `simulate:*` markers finds no statement about them, and the feature adds no component, technology, topology, protocol, contract, or term, so it makes no planned, future-tense, or unimplemented statement true or false. No documentation task is required.

## Complexity Tracking

No constitution violations; nothing to justify.
