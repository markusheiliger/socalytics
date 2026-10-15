# Implementation Plan: Race-Free Analysis Telemetry Test

**Branch**: `20261015-195724-fix-telemetry-sink-race` | **Date**: 2026-10-15 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261015-195724-fix-telemetry-sink-race/spec.md`

## Summary

Remove the unsynchronized enumeration from `AnalysisTelemetryTests.Instruments_are_emitted_without_ids_or_secrets`: after `listener.RecordObservableInstruments()`, copy `sink` into a snapshot under `lock (sink)` and use the snapshot for the instrument name and tag assertions. All assertions stay.

## Technical Context

**Language/Version**: C# on .NET 10 (xUnit v3, Shouldly).

**Primary Dependencies**: existing `AnalysisTestHost`, `MeterListener`; no new packages.

**Storage**: None changed.

**Testing**: `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter-class "*AnalysisTelemetryTests"` (Docker required).

**Constraints**: test-only change; the callbacks lock `sink`, so the snapshot must use the same lock object.

## Constitution Check

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | No design decision changes. | Pass |
| II. Source-area ownership | Only `src/platform/Tests`. | Pass |
| III. API-first control plane | Not affected. | Pass |
| IV. Evidence over claims | The race is removed and verified by repeated runs. | Pass |
| V. Focused, minimal changes | One test method in one file. | Pass |

## Project Structure

```text
specs/20261015-195724-fix-telemetry-sink-race/
├── spec.md
├── plan.md
├── tasks.md
└── checklists/requirements.md

src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/
└── AnalysisTelemetryTests.cs
```

## Architecture Updates

None. The change is limited to a test's evaluation step and alters no design decision or architecture statement.
