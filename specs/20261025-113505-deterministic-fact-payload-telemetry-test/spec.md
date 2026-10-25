# Feature Specification: Deterministic Fact Payload Telemetry Test

**Feature Branch**: `20261025-113505-deterministic-fact-payload-telemetry-test`

**Created**: 2026-10-25

**Status**: Draft

**Fix for**: #294 (implementation of `specs/20261018-104924-agent-evidence-tools`)

**Input**: User description: "Make the Integration test `SocAlytics.Platform.Integration.Tests.Analysis.Results.FactPayloadTelemetryTests.MetricsAndAuditRecordsCarryThePayloadReasonCodeAndNeverAPayloadValue` deterministic. It lives in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/Results/FactPayloadTelemetryTests.cs` and covers the analysis metrics of `src/platform/SocAlytics.Platform.Application/Telemetry/AnalysisMetrics.cs` (the counter `socalytics.analysis.completions_rejected`). It failed and then passed on its one rerun in the verifications of three implementation pull requests (#259, #284, #294), so it is flaky. Find and confirm the cause, then remove it in the test or in the code it covers, without weakening, skipping, or deleting the test or its assertions, and without retries or longer timeouts as the fix. The spec `specs/20261018-104924-agent-evidence-tools` depends on this spec."

## Clarifications

### Session 2026-10-25

- Q: May the fix change production code if the cause lives there? → A: Yes, but only where the evidence puts the cause, without changing which metrics are recorded, their names, their tags, or their values. Test-only causes are fixed in the test or its shared helpers.
- Q: Are retries, longer timeouts, or fixed sleeps acceptable? → A: No. The fix removes the cause; it never waits out or repeats the failure.
- Q: Which tests are in scope? → A: The named test, and any other Integration test that observes the same process-wide analysis metrics and has the same defect, which is fixed only where the evidence shows the defect. Tests without the defect are not changed.

## Problem

The named test submits one result whose fact payload is invalid (or whose fact kind is undeclared), and asserts that the rejection is counted exactly once with the bare reason code as the metric tag, that no other rejection outcome is counted, that no metric tag carries the payload value, and that the audit record and the attempt close reason carry the reason code and no payload value.

The analysis instruments live on one process-wide meter, `SocAlytics.Platform.Analysis`, created once per process. The test listens to every instrument of that meter without any scope. The integration test project runs its test collections in parallel in one process, so measurements raised by the hosts of other tests land in the same listener window. Several of those tests also reject completions, some with the same reason codes. Whenever such a measurement lands while the named test listens, the assertion of exactly one measurement with the reason code, or of no other rejection outcome, fails. The same test passes on a rerun because the overlap is timing dependent. It failed in the verifications of three unrelated implementations.

Suspected causes, to be confirmed by evidence before they are fixed:

- **Shared, process-wide meter observed without scope** (most likely): measurements of tests that run in parallel are recorded by the unscoped listener. Candidates named in the report are `ValidatedAcceptanceTests`, `AnalysisTelemetryTests`, `RunSupersessionTests`, and `PartialCoverageReadinessTests`; the implementation determines which tests actually overlap and which of the others listen the same way.
- **Metric recorded after the response**: the rejection could be counted after the HTTP response is returned, so the assertion would read before the measurement exists (a missing measurement rather than an extra one). The implementation rules this in or out from the code of the completion path.
- **Ordering and shared state**: leftover state from a previous test in the same process, or measurements recorded by background workers of the test's own host (for example a monitor or retention worker raising a different rejection outcome).
- **External dependencies**: asynchronous publication or container startup that a wait does not observe.

The implementation first reproduces the failure, or demonstrates the overlap with a deliberate concurrent rejection, and identifies which cause (or another) produces it, and then removes that cause.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The payload telemetry test fails only when its behavior is wrong (Priority: P1)

A maintainer running the integration tests, or the verification of any implementation pull request, sees the payload telemetry test pass whenever the rejection of an invalid or undeclared fact payload is counted once with the bare reason code, free of payload values, no matter which other tests run at the same time, and fail only when that behavior is broken.

**Why this priority**: The test failed in the verifications of three implementations that pass only on a rerun and report a flaky test none of them caused; the stopped implementation of #294 needs it reliable.

**Independent Test**: Run the named test repeatedly alone, together with the whole analysis test group in parallel, and under CPU load; every run passes. Then break the behavior with a probe (for example a second rejection count for one completion, or a payload value in a tag) and confirm the test still fails on its assertions.

**Acceptance Scenarios**:

1. **Given** other tests in the same process reject completions with the same reason codes at the same time, **When** the named test runs, **Then** its measurements contain only those raised by its own completion, and its assertions of exactly one measurement with the reason code and no other rejection outcome hold.
2. **Given** the completion of the named test is rejected, **When** the response is returned, **Then** the test asserts the measurement only after it is observable, without sleeping, retrying, or lengthening a timeout.
3. **Given** the rejection were counted twice, counted with a different outcome, or tagged with a payload value, **When** the test runs, **Then** the unchanged assertions fail.
4. **Given** other tests observe the same meter, **When** they have the same defect (an unscoped listener whose assertions another test's measurements can break), **Then** they are made deterministic the same way, and tests without the defect keep their code.

### Edge Cases

- A measurement raised by another test's host while the named test listens MUST NOT be counted for the named test, and MUST NOT make it fail.
- A measurement of the test's own completion MUST NOT be hidden by the scoping, so a missing or duplicated own measurement still fails the test.
- The fix MUST NOT depend on the order or the degree of parallelism in which tests run, and a failure in the middle of the test MUST NOT leave listener or shared state that affects another test.
- Measurements raised by background workers of the test's own host MUST be treated the same as before, so a real extra rejection outcome of the test's own host is still detected.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The implementation MUST first establish the cause of the intermittent failure with evidence (the code path of the rejection counting, the parallel execution of the test project, and a demonstration in which a concurrent rejection of another test host makes the old test fail and the new test pass) and record it in a code comment next to the fix and in the `## Evidence` section of `research.md` (the commit message, written by the person or automation that commits, repeats it).
- **FR-002**: The cause MUST be removed where it lives, by attributing each measurement to the test's own run, team, or host (a per-test discriminator that other tests cannot reach, or a measurement scope they cannot observe), or by isolating the telemetry-observing tests from parallel execution without hiding the assertions. Retries, skips, fixed sleeps, and longer timeouts MUST NOT be the fix.
- **FR-003**: Every assertion of the named test MUST remain with the same meaning: exactly one rejection measurement with the bare reason code, no other rejection outcome, no tag containing the payload value, one audit event with the bare reason code and no payload content, and no payload value in the attempt close reason.
- **FR-004**: If the fix adds a discriminator to metric tags or changes how metrics are recorded, metric names, the documented tag set (capability, message type, outcome, failure category), tag cardinality, and recorded values MUST stay as documented: identifiers MUST NOT become metric tags, and production behavior MUST NOT change.
- **FR-005**: Other Integration tests that observe the process-wide analysis meter MUST be checked for the same defect; only those that have it, with assertions another test's measurement can break, MUST be fixed the same way, and the others MUST keep their code. No assertion of any of these tests MAY be removed, loosened, or skipped.
- **FR-006**: The fix MUST NOT make the test suite slower by serializing it as a whole: any isolation from parallel execution MUST be limited to the telemetry-observing tests that need it.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The named test passes in 100% of at least 30 consecutive runs alone and in at least 10 consecutive runs together with the other analysis tests in parallel (including those that reject completions with the same reason codes). If the failure does not reproduce naturally, the evidence additionally contains a deliberate reproduction (a concurrent rejection raised by another host during the window) in which the old test fails and the new test passes.
- **SC-002**: Every other test changed by this fix passes in the same consecutive runs, and the tests of the integration project that were not changed still pass.
- **SC-003**: Each of three probes (a second rejection count for one completion, a rejection counted with a different outcome, and a payload value in a metric tag) makes the named test fail on its assertions; the probes are removed afterward.
- **SC-004**: The code comment and the `## Evidence` section of `research.md` name the evidenced cause, and the change touches only the files the cause lives in plus this spec folder; no assertion is textually removed and no fixed wait or retry is added.
- **SC-005** (post-merge evidence, observed when #294 retries and not verified by a task of this spec): the verification of #294 reports no flaky `MetricsAndAuditRecordsCarryThePayloadReasonCodeAndNeverAPayloadValue` test.

## Assumptions

- **Cause**: The test belongs to the validated result acceptance evidence for fact payload schemas (see the merged specs under `specs/archive/`). The primary cause is presumed to be the unscoped listener on the process-wide meter in parallel test collections; this is a hypothesis, and the concrete cause is established during implementation (FR-001).
- **Dependencies**: The spec [Agent Evidence Tools](../20261018-104924-agent-evidence-tools/spec.md) (implementation #294) depends on this spec. Related specs that must not be repeated: [Deterministic Finalized Recording Set Run Test](../20261024-070502-deterministic-finalized-run-test/spec.md) and [Deterministic Integration Races](../archive/20261020-144627-deterministic-integration-races/spec.md) (different tests).
- **Out of scope**: Other flaky tests, listeners of meters other than the analysis meter unless they share the defect and the same fix, and production metric values and tag sets. Passing tests are development evidence only and do not establish deployment support or production readiness.
