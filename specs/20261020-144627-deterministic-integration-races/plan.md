# Implementation Plan: Bounded Wait for the Simulated Agent Workload Advice Test

**Branch**: `20261020-144627-deterministic-integration-races` | **Date**: 2026-10-20 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261020-144627-deterministic-integration-races/spec.md`

## Summary

`RunnerClaimsAndCompletesASubmittedInvocationSoTheOwnerReadsSimulatedAdvice` gives the simulated runner and the polling loop 30 real seconds. The test raises both limits to one generous shared limit, so it passes as soon as the advice is readable and still fails with the same assertions when it never is.

## Technical Context

**Language/Version**: C# on .NET 10 (`src/platform/global.json`).

**Primary Dependencies**: xUnit v3, Shouldly (existing). No new dependency.

**Storage**: None.

**Testing**: `SocAlytics.Platform.Integration.Tests` (Docker-backed), the test run repeatedly and under load.

**Target Platform**: Linux and developer machines with a Docker-compatible runtime.

**Project Type**: Test-only change to a platform monolith's test project.

**Performance Goals**: None.

**Constraints**: No assertion weakened, removed, or skipped; no production code change.

**Scale/Scope**: One test method.

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

- Introduce one local limit (120 seconds) in the test, used for the runner's `CancelAfter` and the polling deadline, and keep the 250 ms poll interval. The deadline uses the same limit so the runner outlives the wait.
- The advice assertions stay as they are.
- The fake clock is untouched: the runner's lease logic uses it, and the test does not advance it.

## Project Structure

### Documentation (this feature)

```text
specs/20261020-144627-deterministic-integration-races/
├── plan.md
├── spec.md
├── tasks.md
└── checklists/
    └── requirements.md
```

### Source Code (repository root)

```text
src/platform/Tests/SocAlytics.Platform.Integration.Tests/
└── AgentOrchestration/SimulatedAgentWorkloadTests.cs
```

**Structure Decision**: Edit the failing test in place.

## Architecture Updates

None. The fix changes only the wait limits of one integration test and no architecture narrative, design decision, or status statement in `docs/architecture/`.
