# Feature Specification: Deterministic OpenAPI Document Host Tests

**Feature Branch**: `20261024-070502-deterministic-openapi-host-tests`

**Created**: 2026-10-24

**Status**: Draft

**Fix for**: #275 (implementation of `specs/20261023-015644-explicit-analysis-rerun`)

**Input**: User description: "Make the platform Host test `SocAlytics.Platform.Host.Tests.AnalystRegistryOpenApiTests.OpenApiDocumentContainsAllAnalystRegistryOperationIds` deterministic. It failed and then passed on its one rerun in the verifications of implementation pull requests #201, #225 and #275, so it is flaky. Suspected causes to investigate and prove or rule out: every Host test class and even each test method builds and starts its own full AppHost with `DistributedApplicationTestingBuilder`, and distinct test classes run in parallel without a shared fixture or collection, so many complete stamps start at the same time on one runner and contend for ports, containers, CPU and Docker; and the readiness the tests wait for versus what they read next. Find the actual cause and remove it in the test or in the code it covers, for example by sharing one started AppHost per test project through a fixture used by all OpenAPI document tests, or by whatever the evidence shows, while every test keeps all of its assertions. No weakening, skipping, deleting, retries, fixed sleeps, or longer timeouts."

## Clarifications

### Session 2026-10-24

- Q: Where may the fix live? → A: Where the evidence shows the cause: the test, the shared test support of the Host test project, or the platform code and AppHost composition the tests cover. No production behavior may be weakened.
- Q: Are retries, fixed sleeps, or longer limits acceptable? → A: No. A bounded wait on an observable condition is allowed; nothing else.
- Q: May a shared started AppHost be used? → A: Yes, if the evidence supports it, provided every test keeps its assertions and no test depends on state left behind by another.
- Q: Which tests are in scope? → A: The Host tests that start the AppHost and read the OpenAPI document or the running stamp (`AnalystRegistryOpenApiTests`, `AgentCatalogOpenApiTests`, `OpenApiContractTests`, `OpenApiSnapshotTests`, `RecordingsOpenApiTests`, `RegistrationContractShapeTests`, `RegistryOpenApiTests`, `WorkflowOperationsOpenApiTests`) and `PlatformHostTests` where it shares the changed support. Other flaky tests and the Docker-backed Integration and EndToEnd projects are out of scope.

## Problem

`OpenApiDocumentContainsAllAnalystRegistryOperationIds` starts the full local stamp (PostgreSQL, the Migrator, object storage, NATS, the mail catcher, and the API), waits until the API reports healthy, reads the API's OpenAPI document, and expects every Analyst Registry operation id to be present. It failed and passed on its one rerun in the verifications of three implementation pull requests (#201, #225, #275), none of which changed the Analyst Registry operations. The test therefore passes or fails depending on conditions around it, not on the behavior it covers.

The Host test project starts a complete stamp for every test method, and distinct test classes run concurrently with no shared fixture or collection. Many complete stamps can therefore start at the same moment on one runner, contending for ports, containers, processor time, and the container engine, while each test waits for its own stamp to become healthy and then reads the document. Which of these, or another cause (readiness reached before the state the test reads, a start-up step that races another stamp, an external dependency), produces the failure is not yet established.

The implementation first reproduces the failure and identifies the cause with evidence, then removes it where it lives.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The registry operation test fails only when the document is wrong (Priority: P1)

A maintainer running the Host tests, or the verification of any implementation, sees `OpenApiDocumentContainsAllAnalystRegistryOperationIds` pass whenever the API document contains every Analyst Registry operation id, however loaded the runner is, and fail only when an operation id is missing.

**Why this priority**: The test failed in three unrelated verifications, and the Explicit Analysis Rerun implementation (#275) depends on this fix; a person wants the cause removed instead of rerunning.

**Independent Test**: Run the Host test project repeatedly, alone and under processor load; every run passes. Then remove an Analyst Registry operation from the document with a probe and confirm the test fails on its assertion within the existing limits, not hangs or passes.

**Acceptance Scenarios**:

1. **Given** a loaded runner, **When** the Host tests run, **Then** the document is read only after the API is ready to serve it, and the test passes when every operation id is present.
2. **Given** the document lacks an Analyst Registry operation id, **When** the test runs, **Then** it fails on its unchanged assertion.
3. **Given** the stamp cannot become ready, **When** the bounded wait ends, **Then** the test fails with the unchanged limit and does not hang.

---

### User Story 2 - The other Host tests stay deterministic (Priority: P2)

A maintainer sees every other Host test that starts the AppHost, including the other tests of `AnalystRegistryOpenApiTests`, keep every assertion and pass at least as reliably after the fix.

**Why this priority**: The cause is expected to be shared by all Host tests that start a stamp, so a fix in shared support must not trade one flaky test for another.

**Independent Test**: Run the whole Host test project five times in a row under load; every test passes in every run and the number of tests and assertions is unchanged.

**Acceptance Scenarios**:

1. **Given** the fix changes shared Host test support, **When** the whole Host test project runs, **Then** every test passes and no test depends on state left behind by another.
2. **Given** a test needs a stamp with different options than the shared one, **When** it runs, **Then** it still gets the options it needs.
3. **Given** the OpenAPI snapshot refresh mode is requested, **When** `OpenApiSnapshotTests` runs, **Then** it still reads and compares or writes the snapshot as before.

### Edge Cases

- A test that stops, restarts, or reconfigures the stamp must not leave a shared stamp unusable for the others.
- A stamp that fails to start must fail every test that needs it visibly, not hang them or report a pass.
- Tests that read the document must not depend on the order in which classes or methods run.
- Concurrent classes must not contend for fixed ports or shared resource names.
- A failed shared start must not hide behind a cached success for later tests.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The implementation MUST first establish the cause of the intermittent failure with evidence (repeated runs with and without load, and the logs of failing runs or the failed verification runs of #201, #225, and #275) and record it in a code comment next to the fix and in the task's final summary, which the commit message and pull request quote. Suspected causes (concurrent full stamps contending for resources, and the gap between the readiness the test waits for and the state it reads next) MUST each be proved or ruled out.
- **FR-002**: The cause MUST be removed where it lives (the test, shared Host test support, or the covered platform code and AppHost composition), not by retries, fixed sleeps, skipping, or lengthening a limit. A bounded wait on an observable condition is allowed.
- **FR-003**: Every test and every assertion of the Host tests MUST remain, and none MAY be removed, loosened, skipped, or have an expected value changed.
- **FR-004**: If tests share a started stamp, the sharing MUST be isolated from the concurrency of other classes, MUST NOT let a test observe state another test created unless it already tolerated it, and MUST surface a failed start to every test that needs it.
- **FR-005**: Production behavior and the AppHost composition MUST NOT be weakened: a change to covered code MUST keep the start order (PostgreSQL, Migrator, API), readiness, and documented behavior in `docs/architecture/`.
- **FR-006**: The other Host tests that share the changed support MUST stay passing and deterministic, and the committed OpenAPI snapshot MUST remain unchanged unless the evidence requires it.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: `OpenApiDocumentContainsAllAnalystRegistryOperationIds` passes in 100% of at least 20 consecutive runs alone and of at least 5 consecutive runs of the whole Host test project under processor load.
- **SC-002**: A probe that removes an Analyst Registry operation from the document, and a probe that prevents the stamp from becoming ready, each make the test fail within the existing limits; the probes are removed afterward.
- **SC-003**: The number of Host tests and assertions is unchanged, and no assertion is textually removed.
- **SC-004**: The pull request names the evidenced cause and changes only the files it lives in, plus this spec folder, and confirms the architecture statements stay true or corrects them.
- **SC-005** (post-merge evidence, observed when #275 retries and not verified by a task of this spec): the verification of #275 reports no flaky Host test.

## Assumptions

- **Cause**: The test belongs to the Analyst Registry and platform host features (merged specs under `specs/archive/`); every Host test class starts its own full stamp and classes run in parallel, which is the leading hypothesis, to be proved or ruled out (FR-001).
- **Dependencies**: [Explicit Analysis Rerun](../20261023-015644-explicit-analysis-rerun/spec.md) (#275) depends on this spec. The related fixes [Deterministic Integration Races](../archive/20261020-144627-deterministic-integration-races/spec.md) and [Deterministic Stall Expiry of the Simulated Agent Workload](../archive/20261023-150804-deterministic-stall-expiry-test/spec.md) concern other projects and must not be repeated.
- **Out of scope**: Other flaky tests, the Docker-backed Integration and EndToEnd projects, and production values.
- **Evidence**: Passing tests are development evidence only and do not establish deployment support or production readiness.
