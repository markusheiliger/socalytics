# Feature Specification: Deterministic Simulated Agent Workload Invocation Test

**Feature Branch**: `20261025-003459-deterministic-agent-workload-test`

**Created**: 2026-10-25

**Status**: Draft

**Fix for**: #283 (implementation of `specs/archive/20261023-022148-shot-boundary-analyst`)

**Input**: User description: "Make the Docker-backed integration test `SocAlytics.Platform.Integration.Tests.AgentOrchestration.Catalog.SeedTests.TheSimulatedAgentWorkloadCompletesAnInvocationOfTheSeededVersionWithItsPinnedReferences` deterministic. It lives in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Catalog/SeedTests.cs` and is owned by the agent catalog registry feature of Agent Orchestration. It covers a private conversation and a direct invocation submitted through the `/api/v1` operations against a seeded catalog, the delivery of the work message through NATS JetStream (the test's `AgentOrchestrationFixture` stream and consumer with `Messaging:Nats:ProvisionTopology`), the simulated agent workload runner `src/platform/Tests/SocAlytics.Platform.SimulatedAgentWorkload` (`SimulatedAgentWorkloadRunner`, fenced claim, lease, checkpoint, and completion with `private_key_jwt` and DPoP) running against the in-process API with the fixture's fake time (`f.Factory.Time`), and the invocation read that the test polls until it is `succeeded`. It failed and then passed on its one rerun in the verifications of implementation pull requests #221, #259, and #283, so it is flaky, and the flakiness is not caused by the stopped spec `specs/archive/20261023-022148-shot-boundary-analyst`, whose changes touch no agent orchestration code. Find the actual cause and remove it in the test, its support, or in the code it covers, whatever the evidence shows. Keep every assertion of the test and the other tests of `SeedTests` and of the shared support they use passing and deterministic. Do not weaken, skip, or delete the test or its assertions, and do not add retries, fixed sleeps, or longer timeouts as the fix. Other flaky tests and the Web UI and Host test projects are out of scope. The spec `specs/archive/20261023-022148-shot-boundary-analyst` (implementation pull request #283) depends on this spec."

## Clarifications

### Session 2026-10-25

- Q: May the fix change production code if the cause lives there? → A: Yes, but only where the evidence puts the cause, and without changing the documented behavior of invocation submission, work delivery, fenced claim, lease, checkpoint, completion, or authentication. Causes in the test or its support are fixed there.
- Q: Are retries, fixed sleeps, or longer timeouts acceptable? → A: No. Every wait MUST be on an observable condition, and time that the test controls MUST come from one clock.
- Q: Which tests are in scope? → A: The named test, the other tests of `SeedTests`, and any test that uses a shared helper the fix changes; all must stay passing and deterministic. Other flaky tests are out of scope.

## Problem

The test seeds a catalog, submits a private conversation and a direct invocation through the member operations, starts the simulated agent workload runner, and polls the invocation until it is `succeeded`. It then asserts the advice text, the pinned version, the pinned digest, and the four pinned artifact kinds. In the verifications of three implementation pull requests it failed and then passed on its single rerun, so the failure depends on timing and not on the behavior under test.

Suspected causes, to be confirmed or ruled out by evidence before any is fixed:

- **Wall-clock versus fake-time mixing**: the runner uses the fixture's fake time while the API, the token and DPoP proof validity, the lease, or the poll loop may use the wall clock (or the reverse), so a token, proof, or lease can appear expired or not yet valid depending on how fast the runner runs.
- **Start order**: the runner can start before the work message is published or before the stream and consumer are provisioned, and a fetch that finds nothing or a consumer that does not exist yet is treated as final or loses the message.
- **Polling loop**: the test's poll for `succeeded` can end on a bound that is a guess about elapsed time, or can read a terminal state other than `succeeded` (for example a failure, a lease loss, or a redelivery) without surfacing why.
- **Shared state**: streams, durable consumers, ports, or settings shared with test classes that run in parallel, or left over from a previous test.
- **Covered production code**: fencing, claim, lease expiry, or redelivery handling that behaves differently under load.

The implementation first reproduces the failure (repeated runs alone and under CPU contention) or demonstrates the suspected race by deliberately widening its window test-side, proves or rules out each suspected cause, and then removes the cause or causes that the evidence shows.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The invocation test fails only when behavior is wrong (Priority: P1)

A maintainer running the integration tests, or the verification of any implementation pull request, sees the test pass whenever the simulated agent workload completes an invocation of the seeded version with its pinned references, however loaded the runner is, and fail only when that behavior is broken.

**Why this priority**: The test failed in the verifications of three implementations, which pass only on a rerun and report a flaky test that none of those features caused; the stopped implementation of #283 needs it reliable.

**Independent Test**: Run the test repeatedly alone and under CPU load; every run passes. Then break the behavior with probes (for example a different advice text, a wrong pinned digest, or a missing artifact kind) and confirm the test still fails on its assertions.

**Acceptance Scenarios**:

1. **Given** an invocation submitted and its work message published, **When** the runner is started before, during, or after the publication and the provisioning of the consumer, **Then** the invocation reaches `succeeded` and the assertions hold, whatever the order.
2. **Given** the runner claims, leases, checkpoints, and completes the invocation with `private_key_jwt` and DPoP, **When** the machine is heavily loaded, **Then** no token, proof, or lease is judged expired or not yet valid because of the load, and the invocation still reaches `succeeded`.
3. **Given** the invocation reaches a terminal state other than `succeeded` or never leaves a non-terminal state, **When** the test observes it, **Then** it fails on the observed state with a message that names the state and the reason the platform recorded, and does not hang.
4. **Given** the test finished or failed in the middle, **When** another test of `SeedTests` or of a parallel class runs, **Then** it starts without stream, consumer, port, or database state left over from this test.

### Edge Cases

- A work message delivered more than once, or a fetch that finds no message yet, MUST NOT fail the test while the platform behaves correctly.
- A failure in the middle of the test MUST NOT leave state that affects another test.
- The fix MUST NOT depend on the order in which tests or test classes run.
- A passing test MUST NOT hide a wrong advice text, version, digest, or artifact kind.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The implementation MUST first establish the cause of the intermittent failure with evidence (repeated runs of the test with and without CPU load, and for each suspected cause a demonstration that proves or rules it out, for example a deliberate test-side delay, clock skew, or reordering under which the old test fails and the fixed test passes) and record it in a code comment next to the fix (the evidence comment block above the named test); `research.md` keeps the decisions, because implementation agents cannot change files under `specs/`.
- **FR-002**: The cause MUST be removed where it lives (the test, its shared support, the simulated agent workload runner, or the covered production code) by waiting on an observable condition, controlling the racing input, using one consistent clock, or isolating shared state, not by retrying, skipping, adding a fixed sleep, or lengthening a timeout.
- **FR-003**: Every assertion of the named test (advice text, pinned version, pinned digest, and the four pinned artifact kinds) MUST remain unchanged, and no assertion, expected value, or step MAY be removed, loosened, or skipped.
- **FR-004**: When the invocation does not reach `succeeded`, the test MUST fail on that observation with the `state` and the `failureReason` of the invocation read, and the wait for it MUST be bounded by an observable condition (such as a terminal state) and not by a guess about elapsed time. The only time-based limit is the linked token of the test (`run.Token`, with its existing `CancelAfter`) or an explicit `[Fact(Timeout = ...)]`, which the poll loop and its delay MUST use, kept solely so a hung platform cannot block the run; it is not the success condition, and when it fires the resulting `OperationCanceledException` is caught and the test fails with the last observed state and the recorded `failureReason`.
- **FR-005**: The other tests of `SeedTests`, and any test that uses a shared helper the fix changes, MUST keep every assertion and pass deterministically.
- **FR-006**: Production behavior MUST NOT change: any change to the covered production code MUST keep the behavior documented in `docs/architecture/`, and a change to the simulated agent workload MUST keep it a development-only simulation that is not an agent runtime.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The named test passes in 100% of at least 30 consecutive runs alone and in at least 10 consecutive runs under CPU load (one busy-loop process per core for the duration of the runs). If the failure does not reproduce naturally, the evidence additionally contains a deliberate reproduction in which the old test fails and the fixed test passes.
- **SC-002**: Every test of `SeedTests` passes in the same consecutive runs, and the other integration tests that use a changed helper still pass.
- **SC-003**: Each of three probes (a wrong advice text, a wrong pinned digest, and a missing pinned artifact kind) makes the named test fail on its assertions; the probes are removed afterward.
- **SC-004**: The evidence comment block above the named test names the evidenced cause and the suspected causes ruled out; the change touches only the files the cause lives in plus this spec folder; no assertion is removed and no fixed wait or retry is added.
- **SC-005** (post-merge evidence, observed when #283 retries and not verified by a task of this spec): the verification of #283 reports no flaky `TheSimulatedAgentWorkloadCompletesAnInvocationOfTheSeededVersionWithItsPinnedReferences` test.

## Assumptions

- **Cause**: The test belongs to the agent catalog registry and Agent Orchestration development evidence (see the merged specs [Agent Catalog Registry](../20261018-101517-agent-catalog-registry/spec.md) and [Agent Orchestration Invocations](../20261017-204332-agent-orchestration-invocations/spec.md)). The concrete cause is unknown; the suspected causes above are hypotheses established or ruled out during implementation (FR-001).
- **Dependencies**: [Shot Boundary Analyst](../20261023-022148-shot-boundary-analyst/spec.md) (implementation #283) depends on this spec. Related specs that must not be repeated: [Deterministic Finalized Recording Set Run Test](../../20261024-070502-deterministic-finalized-run-test/spec.md) and [Deterministic Stall Expiry Test](../20261023-150804-deterministic-stall-expiry-test/spec.md) (different tests).
- **Out of scope**: Other flaky tests (such as `RunCreationTests` and the evidence audit tests), the Web UI and Host test projects, and production values (lease durations, poll intervals, and token lifetimes are development values that stay as they are).
- **Evidence**: Passing tests are development evidence only and do not establish deployment support or production readiness.
