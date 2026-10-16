---

description: "Task list for Race-Free Analysis Telemetry Test"
---

# Tasks: Race-Free Analysis Telemetry Test

**Input**: Design documents from `specs/20261015-195724-fix-telemetry-sink-race/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md)

**Tests**: The task changes a test; its verification is running it.

## Format: `[ID] [P?] [Story] Description`

## Phase 1: User Story 1 - The telemetry test passes reliably (Priority: P1) 🎯 MVP

- [ ] T001 [US1] In `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/AnalysisTelemetryTests.cs`, in `Instruments_are_emitted_without_ids_or_secrets`, directly after `listener.RecordObservableInstruments();` take a snapshot under the lock the listener callbacks use (`List<(string Name, long Value, Dictionary<string, object?> Tags)> snapshot; lock (sink) { snapshot = sink.ToList(); }`), and use `snapshot` instead of `sink` in `snapshot.Select(s => s.Name).ToHashSet()` and in `foreach (var tags in snapshot.Select(s => s.Tags))`. Keep every assertion, the `Listen` helper, and all other code unchanged. Verify: `dotnet build src/platform/SocAlytics.Platform.slnx --no-restore` then `dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter-class "*AnalysisTelemetryTests"` passes repeatedly (at least 5 times).

## Dependencies & Execution Order

T001 is the only task.
