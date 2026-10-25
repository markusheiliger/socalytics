# Implementation Plan: Deterministic Fact Payload Telemetry Test

**Branch**: `20261025-113505-deterministic-fact-payload-telemetry-test` | **Date**: 2026-10-25 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261025-113505-deterministic-fact-payload-telemetry-test/spec.md`

## Summary

`FactPayloadTelemetryTests.MetricsAndAuditRecordsCarryThePayloadReasonCodeAndNeverAPayloadValue` listens to the process-wide meter `SocAlytics.Platform.Analysis` without any scope, while the integration test project runs test collections in parallel in one process. A rejection counted by another test's host lands in the listener window and breaks the "exactly one measurement" and "no other rejection outcome" assertions. Code reading rules out a late metric (`CompleteAttempt` counts the rejection before it returns the response), so the cause is test-side. The fix puts the tests that listen to the analysis meter into one xUnit collection with `DisableParallelization = true`. xUnit runs such a collection only while no other collection runs, so no foreign host can raise a measurement during any listening test. No metric, tag, or production code changes and no assertion changes.

## Technical Context

**Language/Version**: C# on .NET 10 (`src/platform/global.json`).

**Primary Dependencies**: xUnit v3, Shouldly, `System.Diagnostics.Metrics.MeterListener` (existing). No new dependency.

**Storage**: None added (the tests use the existing disposable PostgreSQL, NATS, and RustFS containers).

**Testing**: `SocAlytics.Platform.Integration.Tests` (Docker): the named test repeatedly alone and with the whole analysis group, plus three probes.

**Target Platform**: Linux and developer machines with a Docker-compatible runtime.

**Project Type**: Test-only change to the platform monolith's integration test project.

**Performance Goals**: None; only the listening tests leave the parallel phase (FR-006).

**Constraints**: Assertions unchanged; no retry, skip, sleep, or longer timeout; metric names, tags, and values unchanged.

**Scale/Scope**: One new collection definition and the `[Collection]` attribute on the test classes that have the defect.

## Constitution Check

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | No design decision changes; see Architecture Updates. | Pass |
| II. Source-area ownership | Only `src/platform/Tests`; no new `src` child. | Pass |
| III. API-first control plane | Not affected. | Pass |
| IV. Evidence over claims | Cause shown by code path and a deliberate concurrent-rejection reproduction; repeated runs and probes. | Pass |
| V. Focused, minimal changes | One collection definition plus attributes; no abstraction, no production change. | Pass |
| Environment features | Needs only the existing .NET SDK and Docker. No environment spec. | Pass |

Post-design re-check: Pass.

## Design

- **Cause evidence (FR-001)**: `CompleteAttempt.cs` calls `AnalysisMetrics.CompletionRejected` before it commits and returns, so a late measurement is ruled out. `AnalysisMetrics.Meter` is a static, one per process. `MeterListener.InstrumentPublished` in the named test enables every instrument of that meter with no filter. xUnit runs collections in parallel, and `ValidatedAcceptanceTests`, `AnalysisTelemetryTests`, `RunSupersessionTests`, and others also reject completions. The implementation records a deliberate reproduction (another host rejects a completion with the same reason code during the window) in which the old test fails. All evidence is kept in the evidence record, a block comment in `AnalysisTelemetryCollection.cs`, because the implementation cannot write under `specs/`.
- **Fix (FR-002, FR-006)**: a collection `AnalysisTelemetryCollection` with `[CollectionDefinition(Name, DisableParallelization = true)]` beside the analysis test support, following the existing `TimingCollection` and `MigratorDiagnosticsRedactionCollection` pattern. The named test class carries `[Collection(AnalysisTelemetryCollection.Name)]`. Per-test attribution is rejected: identifiers must not become metric tags (FR-004), and an ambient context (`AsyncLocal` or `Activity`) does not reliably flow into the in-process host's request or worker threads.
- **Other tests (FR-005)**: the implementation reads the assertions of every Integration test that listens to the analysis meter (`ValidatedAcceptanceTests`, `AnalysisTelemetryTests`, `RunSupersessionTests`, `PartialCoverageReadinessTests`, `MinimumVersionEnforcementTests`) and moves a class into the collection only when it asserts counts or absence that a foreign measurement of its own instrument can break. The listeners in IdentityAccess and AgentOrchestration observe other meters and are not moved. The classification of a class that listens to the analysis meter covers every instrument it asserts on, including the messaging meter in `AnalysisTelemetryTests`; only classes that do not listen to the analysis meter at all are out of scope. Classes without the defect keep their code.
- **Own host unchanged**: measurements of the test's own host and its background workers are still observed, so a real extra rejection outcome still fails the test.
- **Probes (SC-003)**: a second `CompletionRejected` count, a different outcome, and a payload value in a tag are applied temporarily, must fail the test on its own assertions, and are removed.

## Project Structure

### Documentation (this feature)

```text
specs/20261025-113505-deterministic-fact-payload-telemetry-test/
├── plan.md
├── research.md
├── quickstart.md
├── spec.md
├── tasks.md             # /speckit-tasks
└── checklists/
    └── requirements.md
```

No `data-model.md` (no entities) and no `contracts/` (no external interface changes).

### Source Code (repository root)

```text
src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/
├── Support/AnalysisTelemetryCollection.cs   # new: the non-parallel collection and the evidence record comment
├── Results/FactPayloadTelemetryTests.cs     # changed: [Collection] and the evidence comment
└── (other listening test classes under Analysis/ and Registry/MinimumVersionEnforcementTests.cs)           # changed only where the defect is confirmed
```

**Structure Decision**: Keep the change inside the Integration test project; the collection lives with the analysis test support.

## Architecture Updates

None. The fix changes only how integration tests are scheduled and no architecture narrative, design decision, or implementation-status statement in `docs/architecture/`.
