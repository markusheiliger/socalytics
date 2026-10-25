# Implementation Plan: Deterministic Simulated Agent Workload Invocation Test

**Branch**: `20261025-003459-deterministic-agent-workload-test` | **Date**: 2026-10-25 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261025-003459-deterministic-agent-workload-test/spec.md`

## Summary

`SeedTests.TheSimulatedAgentWorkloadCompletesAnInvocationOfTheSeededVersionWithItsPinnedReferences` fails
intermittently and passes on rerun. The plan is to establish the cause with evidence first (repeated runs alone and
under CPU load, then a deliberate test-side widening of each suspected race), and then remove it where it lives.
The code reading in [research.md](research.md) ranks one hypothesis first: the fixture's `MutableTimeProvider` has a
frozen `GetUtcNow()` (set once by `AgentOrchestrationFixture.StartAsync`) but real timers, while the test's poll bound
uses `DateTime.UtcNow`. A stored-timestamp retry gate (outbox `next_attempt_at <= @now`, with the retry computed from
the frozen clock) therefore never opens once the first publication attempt fails, for example because the JetStream
topology is not provisioned yet, and the invocation stays queued until the 120 s bound ends the test. The fix is chosen
by the evidence: control the racing input (provision the stream and consumer, or make the fixture clock advance with
real time), wait on an observable condition (a terminal invocation state, with the recorded reason in the failure
message), and isolate shared state. No assertion changes, no retries, fixed sleeps, or longer timeouts.

## Technical Context

**Language/Version**: C# 14 on .NET 10 (SDK pinned by `src/platform/global.json`)

**Primary Dependencies**: xUnit v3, Shouldly, NATS.Net JetStream client, Testcontainers (PostgreSQL, NATS), `Microsoft.AspNetCore.Mvc.Testing`; no new dependency

**Storage**: Disposable PostgreSQL database per test through `PostgresContainerFixture`; no schema change

**Testing**: `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --filter "FullyQualifiedName~AgentOrchestration"` (Docker required)

**Target Platform**: Linux CI runner and developer machines with a Docker-compatible runtime

**Project Type**: Test support of the platform monolith (`SocAlytics.Platform.Integration.Tests`, `SocAlytics.Platform.SimulatedAgentWorkload`)

**Performance Goals**: The test is not slower than today (about 5 to 10 s); the wait ends when the invocation is terminal

**Constraints**: No retries, fixed sleeps, skips, or longer limits; every wait is on an observable condition; one clock for time the test controls; production behavior and development values (lease 2 s, token lifetime, poll intervals) stay; the simulated workload stays a development-only simulation

**Scale/Scope**: One test, its four sibling tests in `SeedTests`, and the tests that use a changed helper (`AgentOrchestrationFixture`, `PlatformApiFactory`/`MutableTimeProvider`, the runner)

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-checked after Phase 1 design.*

| Principle | Result |
| --- | --- |
| I. Architecture Is the Design Authority | Pass. Test support changes; production changes only where the evidence puts the cause and without changing documented behavior. See Architecture Updates. |
| II. Source-Area Ownership | Pass. Changes stay in `src/platform/Tests/` (or the platform code the evidence names); no new `src/` child. |
| III. API-First Control Plane | Pass. No API or contract change; the OpenAPI snapshot stays unchanged. |
| IV. Evidence Over Claims | Pass. The cause is proved by repeated runs and probes (SC-001 to SC-003); results are development evidence only. |
| V. Focused, Minimal Changes | Pass. The smallest change in the files the cause lives in; no new dependency or abstraction beyond what the cause needs. |
| Tooling constraints | Pass. Only documented `dotnet` commands; Docker and xUnit v3 are already provided by the environment actions, so no environment spec is needed. |

Post-design re-check: unchanged, all gates pass. No Complexity Tracking entries.

## Project Structure

### Documentation (this feature)

```text
specs/20261025-003459-deterministic-agent-workload-test/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── checklists/
└── tasks.md             # created by /speckit-tasks
```

No `contracts/` folder: the feature exposes no interface and changes none.

### Source Code (repository root)

```text
src/platform/Tests/
├── SocAlytics.Platform.Integration.Tests/
│   ├── AgentOrchestration/Catalog/SeedTests.cs          # the test: observable wait with state and reason
│   ├── AgentOrchestration/Support/AgentOrchestrationFixture.cs  # topology and clock handling, per-test isolation
│   └── IdentityAccess/Support/                          # PlatformApiFactory / MutableTimeProvider, only if the clock is the cause
└── SocAlytics.Platform.SimulatedAgentWorkload/
    └── SimulatedAgentWorkloadRunner.cs                  # only if the evidence names the runner
```

**Structure Decision**: No new project or file is planned; the final file list is decided by the evidence recorded
in `research.md` (`## Evidence`). Production code (`SocAlytics.Platform.Infrastructure` outbox, `WorkerLoop`,
`AgentAttemptMonitorWorker`) changes only if the evidence shows the cause there (FR-006).

## Architecture Updates

**Narratives**:

- [Platform Implementation](../../docs/architecture/platform-implementation.md), "Test And Observability Baseline",
  which describes the Testcontainers-backed `SocAlytics.Platform.Integration.Tests`.
- [Intelligence and Agents](../../docs/architecture/intelligence-and-agents.md), which describes the simulated agent
  workload that executes invocations in Development and Test.

**Design updates**: None applied now. The fix concerns how a test controls time and waits; the architecture does not
state how the Integration tests fake the clock or provision NATS topology. If the evidence moves the fix into
production code, the owning narrative is updated in the documentation task.

**Status statements at merge**: No statement becomes false; the simulated agent workload stays a development-only
simulation that is not an agent runtime. Re-read both narratives at implementation time and, if the shared fixture now
advances its clock with real time, add one sentence to "Test And Observability Baseline" stating that.

**Documentation task requirement**: `/speckit-tasks` MUST include one documentation task referencing
`docs/architecture/` that makes the narratives above, and every status statement the final implementation affects,
true for the merged state, re-reading the documents at implementation time. If nothing changes, the task records
that outcome after re-reading them.

## Complexity Tracking

No violations.
