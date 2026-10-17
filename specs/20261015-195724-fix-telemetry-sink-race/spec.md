# Feature Specification: Race-Free Analysis Telemetry Test

**Feature Branch**: `20261015-195724-fix-telemetry-sink-race`

**Created**: 2026-10-15

**Input**: User description: "AnalysisTelemetryTests.Instruments_are_emitted_without_ids_or_secrets enumerates the shared sink list without a lock while MeterListener callbacks on background threads still add to it (InvalidOperationException: Collection was modified, line 118). Take a snapshot of the sink under lock (sink) after listener.RecordObservableInstruments() and use the snapshot for the instrument name and tag assertions. Do not change the assertions themselves."

**Fix for**: #113 (implementation of `specs/archive/20261015-145134-oidc-sign-in`)

## Problem

The integration test `Instruments_are_emitted_without_ids_or_secrets` (Analysis, introduced by [Durable Analysis Workflow](../archive/20261005-130703-durable-analysis-workflow/spec.md)) collects measurements into a shared `sink` list through a `MeterListener`. The listener callbacks add to the list under `lock (sink)` and run on whichever thread records a measurement, including background workers of the test host (outbox publisher, workers, Job Monitor). After the scenario the test enumerates `sink` twice, once for the instrument names and once for the tags, without taking that lock. A callback that runs meanwhile modifies the list during enumeration and the test fails with `InvalidOperationException: Collection was modified`.

Evidence: phase 1 verification run 38774758876 of #113: Integration failed 1 of 1036 (1034 passed, 1 skipped) with this exception in `AnalysisTelemetryTests.cs`, line 118. The test file is unchanged on `main` and the stopped implementation touches nothing under `Tests/.../Analysis`, so the failure is timing dependent and can recur in every later full verification, including the one before the merge.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The telemetry test passes reliably (Priority: P1)

A maintainer runs the Integration tests. The telemetry test reads the collected measurements from a consistent snapshot taken under the same lock the listener callbacks use, so concurrent background measurements can no longer break its enumeration, while it still proves that the expected instruments are emitted with only allowed, non-identifying tags.

**Why this priority**: The intermittent failure blocks the verification and merge of other features.

**Independent Test**: Run the test repeatedly, also while background workers keep recording measurements, and it never fails with `Collection was modified`.

**Acceptance Scenarios**:

1. **Given** measurements still arriving on background threads, **When** the test evaluates the instrument names and tags, **Then** it reads a snapshot taken under `lock (sink)` and no enumeration of the live list happens.
2. **Given** the snapshot, **When** the assertions run, **Then** they check the same instrument names, allowed tag keys, and tag values as before.

### Edge Cases

- A measurement that arrives after the snapshot is not evaluated; the expected instruments were all recorded before the snapshot by the scenario itself and by `RecordObservableInstruments()`.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: After `listener.RecordObservableInstruments()`, the test MUST copy `sink` into a local snapshot while holding `lock (sink)`.
- **FR-002**: The instrument name assertions and the tag assertions MUST use only the snapshot, never the live `sink`.
- **FR-003**: Every existing assertion (the eight expected instrument names, the allowed tag keys `capability`, `message_type`, `outcome`, `failure_category`, and the checks that no tag value is a GUID or contains `https://`) MUST stay unchanged.
- **FR-004**: No production code, option, default, helper, or other test changes; no assertion is weakened, removed, or skipped.

### Key Entities

- **Telemetry sink**: the test's list of recorded measurements (instrument name, value, tags), filled by `MeterListener` callbacks under `lock (sink)`.

## Success Criteria *(mandatory)*

- **SC-001**: The test passes in 20 consecutive runs.
- **SC-002**: The Integration suite's verification in the implementation of #113 passes without this test failing.
- **SC-003**: The test contains the same set of assertions as before (FR-003).

## Assumptions

- The smallest fix is in the test only: `List<T>` copy via `lock (sink) { snapshot = sink.ToList(); }` directly after `listener.RecordObservableInstruments()`, with the two `sink.Select(...)` enumerations reading `snapshot`. The listener callbacks and the `Listen` helper already lock correctly and stay unchanged.
- Dependencies: the feature owning the test is [Durable Analysis Workflow](../archive/20261005-130703-durable-analysis-workflow/spec.md); the implementation this fix unblocks is #113 (`specs/archive/20261015-145134-oidc-sign-in`).
