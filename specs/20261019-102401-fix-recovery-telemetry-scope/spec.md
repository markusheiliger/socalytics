# Feature Specification: Deterministic Telemetry Capture in the Password Recovery Secret Test

**Feature Branch**: `20261019-102401-fix-recovery-telemetry-scope`

**Created**: 2026-10-19

**Status**: Draft

**Fix for**: #186 (implementation of `specs/20261018-094332-compose-stamp`)

**Input**: User description: "SelfServicePasswordRecoveryLimitsTests.AuditTrailHoldsOneEventPerStepAndNothingSecretIsRecorded in src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess fails intermittently: its process-wide ActivityListener and MeterListener collect unrelated telemetry (Npgsql pool tags) that can contain the weak password 'short'. Scope the capture to this test's own host and keep all secret assertions unchanged."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The secret-leak test fails only when a secret leaks (Priority: P1)

A maintainer running the Integration test suite, or the verification of any implementation, sees the password recovery audit test pass whenever no secret is recorded in audit events, logs, or telemetry of the host under test, and fail only when one is. Today it also fails at random when unrelated telemetry of the same test process happens to contain the weak password `short` as a substring.

**Why this priority**: The flaky failure stopped the verification of the implementation in #186 twice, on a branch that changes nothing under the Integration tests; it will stop any other implementation the same way.

**Independent Test**: Run the test repeatedly, alone and together with the whole Integration suite, and confirm it passes every time; then make the test host record one of the secrets in a log line or a span tag and confirm the test still fails and names the channel.

**Acceptance Scenarios**:

1. **Given** other tests of the same process emit database-driver telemetry whose tags contain connection details (for example `db.client.connection.pool.name=Host=127.0.0.1;Port=32898;Database=socalytics;Username=postgres ...`), **When** the test runs, **Then** that telemetry is not part of the text it checks and the test passes.
2. **Given** the host under test records any checked secret (address, normalized address, token, either password, the default password, the message subject text, or the message body) in an activity tag, an activity event, or a metric tag, **When** the test runs, **Then** it fails with the same assertion and message as today.
3. **Given** the audit and log checks, **When** the test runs, **Then** they are unchanged.

### Edge Cases

- Telemetry that is emitted before the test's host starts or after it stops must not influence the result.
- A secret that appears in the tags of the host's own request-handling telemetry (for example a token in a URL) must still be detected.
- Other tests running in parallel in the same process must not change the outcome, in either direction.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The telemetry capture of the test MUST record only telemetry of the host under test and the clients it drives, and MUST NOT record telemetry of other hosts, fixtures, or tests of the same process, including database-driver pool and connection telemetry that carries connection strings.
- **FR-002**: The capture MUST still include the request-handling, outgoing-call, messaging, and metric telemetry the host under test emits, so that a secret in any of their tags or events is detected.
- **FR-003**: The list of checked secrets, the case-insensitive comparison, the audit-event and log checks, the audit-event count and masking assertions, and the failure message format MUST stay unchanged. No secret may be removed from the list, shortened, or exempted.
- **FR-004**: The capture MUST NOT depend on timing or test order: whether a given emitted tag is part of the checked text depends only on which host emitted it.
- **FR-005**: The change MUST be limited to the test file `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/SelfServicePasswordRecoveryLimitsTests.cs` (and a shared test support helper if the plan needs one); no production code, product behavior, other test's assertion, or threshold changes.

### Key Entities

- **Telemetry capture**: The listeners of the test that collect activity and metric tags into the text checked for secrets.
- **Host under test**: The platform API instance started by the test's harness, including the clients and workers it runs.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The audit test passes in 100% of at least 20 consecutive runs of its test class on one machine and in a run of the whole Integration suite, in which other tests emit database-driver telemetry containing the text `short`.
- **SC-002**: A deliberate probe that makes the host under test record each checked secret in an activity tag or a metric tag makes the test fail in 100% of probes (verified during implementation, probe removed afterward).
- **SC-003** (post-merge evidence, observed when #186 retries and not verified by a task of this spec): The verification of #186 (combined verification of its tasks T012 and T013) passes the Integration suite without a retry.
- **SC-004**: No file outside the test project and this spec folder changes, and the audit, log, and secret-list assertions are textually unchanged.

## Assumptions

- **Cause**: The test registers an `ActivityListener` with `ShouldListenTo = _ => true` and a `MeterListener` that enables every instrument of the process, then asserts that the collected text never contains the secrets. Database-driver telemetry of other tests in the process carries connection strings (host, port, database, user) that can contain the weak password `short` as a substring by chance (the diagnosis of #186; runs 39224866049 and 39197471333). The defect is in the test, not in the platform.
- **Smallest fix**: The capture is scoped by what the host under test emits, for example by the instrumentation scopes the host and its clients use (excluding the database driver, whose tags are connection details and never request content) and, where the platform offers it, by an identity of the host under test. The plan chooses the mechanism; the requirements above decide acceptance.
- **Dependencies**: The owning feature is [Self-Service Password Recovery](../archive/20261017-200823-password-recovery/spec.md). The fix unblocks the implementation of [Compose Stamp Composition for Self-Hosted Evaluation](../20261018-094332-compose-stamp/spec.md) in #186.
- **Out of scope**: Changing production telemetry, other tests that use process-wide listeners, the secret list, or any threshold; disabling or skipping the test.
- **Evidence**: Passing tests are development evidence only and do not establish deployment support or production readiness.
