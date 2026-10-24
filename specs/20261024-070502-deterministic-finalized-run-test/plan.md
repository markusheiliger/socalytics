# Implementation Plan: Deterministic Finalized Recording Set Run Test

**Branch**: `20261024-070502-deterministic-finalized-run-test` | **Date**: 2026-10-24 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261024-070502-deterministic-finalized-run-test/spec.md`

## Summary

Make `RunCreationTests.FinalizedRecordingSetFlowsThroughNatsToExactlyOneRun` deterministic without weakening it. Code reading identifies the primary cause: `PublishOutboxBatch` publishes each claimed row to the broker and marks it `published` inside one unit of work that commits only after the whole batch, so the recordings-finalized consumer can already have created the run while the `published` state is not yet committed; the test's immediate `published` count then races that commit (the FR-001 hypothesis, to be demonstrated by a test-side delay of the commit). The fixed 4-second sleep before counting duplicates proves absence by elapsed time. The fix is test-side: wait for the `published` state as an observable condition, and wait for the durable consumer to have acknowledged both duplicate publications before counting. Production code stays unchanged because commit-after-batch is the existing behavior and the late state is eventually consistent by design.

## Technical Context

**Language/Version**: C# on .NET 10 (SDK pinned by `src/platform/global.json`), xUnit v3, Shouldly

**Primary Dependencies**: NATS.Net JetStream client (already used by the integration tests), Npgsql, the existing `FinalizationSetup` and `RecordingFinalizationHelper` helpers

**Storage**: PostgreSQL and NATS JetStream in disposable Docker containers (existing fixtures); no schema change

**Testing**: `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --filter "FullyQualifiedName~RunCreationTests"`

**Target Platform**: Linux with a Docker-compatible runtime (as for all integration tests)

**Project Type**: Test-only fix inside the existing platform monolith's integration test project

**Performance Goals**: N/A; waits are bounded polls of an observable condition (the existing `WaitAsync` helper), not fixed sleeps

**Constraints**: No retry, skip, fixed sleep, or longer timeout as the fix; no assertion removed or loosened; production behavior unchanged

**Scale/Scope**: One test method plus at most one new private helper in `RunCreationTests.cs`; the existing private `PublishAsync` helper also returns the acknowledgement sequence (call sites of the other tests unchanged)

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle / gate | Result |
| --- | --- |
| I. Architecture is the design authority | Pass: no design change; the outbox commit-after-batch behavior stays. `## Architecture Updates` is below. |
| II. Source-area ownership | Pass: only `src/platform/Tests/` changes; no new `src` child. |
| III. API-first control plane | Pass: no API or contract change. |
| IV. Evidence over claims | Pass: repeated runs alone and under load, a deliberate widening of the race, and two failing probes; development evidence only. |
| V. Focused, minimal changes | Pass: one test file plus this spec folder; no new dependency or abstraction. |
| Environment tooling | Pass: .NET, Docker, and the NATS and PostgreSQL fixtures already exist in `environment-setup`; no environment spec is needed. |

Post-design re-check: unchanged, all pass.

## Project Structure

### Documentation (this feature)

```text
specs/20261024-070502-deterministic-finalized-run-test/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── checklists/
└── tasks.md             # created by /speckit-tasks
```

No `contracts/`: no interface is added or changed.

### Source Code (repository root)

```text
src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/
└── RunCreationTests.cs   # the named test, plus a private helper that waits for the consumer to settle
```

**Structure Decision**: The cause lives in the test's assertions (read-then-assert and sleep-then-count), so the fix stays in `RunCreationTests.cs`, where the private `PublishAsync` helper also returns the acknowledgement sequence while the call sites of the other tests stay unchanged. `FinalizationSetup` and the production publisher and consumer are not changed; if the evidence shows a helper or production cause instead, only that file changes (spec FR-002, FR-006).

## Complexity Tracking

No constitution violations.

## Architecture Updates

None. The change is confined to one integration test in `src/platform/Tests/`: it waits for observable conditions instead of reading state once or sleeping. It changes no production code, contract, component, topology, or documented behavior of outbox publication, duplicate suppression, or run creation, and a search of `docs/architecture/` finds no statement about this test or its timing that the merge makes false. No planned, future-tense, or unimplemented statement in `docs/architecture/` changes, so no documentation task for `docs/architecture/` is required.
