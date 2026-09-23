# Durable Analysis Workflow Specification

## Purpose

Define recoverable, PostgreSQL-authoritative analysis workflow behavior from immutable run creation through dependency evaluation, leased attempts, and accepted-result lineage.

## ADDED Requirements

### Requirement: Analysis runs preserve immutable resolved snapshots

The system SHALL persist each Analysis Run with its Team, Match, finalized recording-set version, requested capability set, workflow identity and version, completion policy, state, timestamps, and immutable resolved workflow snapshot. Each Workflow Node SHALL preserve its tier, execution scope, required inputs, resolved Analyst profile, OCI image, model, policy, schema, resource, retry, lease, stale-timeout, and execution-timeout identities and values as they existed when the run was created.

#### Scenario: Source or execution inputs change after run creation

- **WHEN** a source input, capability, Analyst profile, image, model, policy, schema, resource, retry, lease, or timeout definition changes after an Analysis Run is created
- **THEN** the existing run and node snapshots remain unchanged and a request using the changed input creates a distinct Analysis Run rather than reinterpreting accepted history

#### Scenario: Hierarchy round trip

- **WHEN** a persisted Analysis Run is reloaded
- **THEN** its Workflow Nodes, dependency edges, Logical Jobs, Execution Attempts, and accepted-result references reproduce the same identities, values, ordering, and relationships that were committed

### Requirement: Workflow graphs are valid and dependency readiness is authoritative

The system SHALL reject a workflow graph containing a cycle before creating schedulable work. It SHALL persist run-local dependency edges as required, optional, or conditional and SHALL derive readiness only from persisted graph state and accepted upstream results at the pinned versions.

#### Scenario: Cyclic graph is submitted

- **WHEN** run creation receives a workflow graph containing a direct or indirect cycle
- **THEN** the complete run creation is rejected without persisting an Analysis Run, Workflow Node, Logical Job, or outgoing ready-work record

#### Scenario: Required dependency is incomplete

- **WHEN** a node has a required dependency without an accepted result at every declared input version
- **THEN** its Logical Jobs are not ready and no ready-work notification is recorded

#### Scenario: Optional dependency is absent or failed

- **WHEN** an optional dependency has no accepted result or has failed
- **THEN** the dependent node may become ready from its remaining requirements and its job snapshot records the absent or failed optional input

#### Scenario: Conditional dependency does not apply

- **WHEN** a conditional dependency's pinned predicate evaluates false
- **THEN** that dependency does not delay or block the dependent node and the non-applicable outcome is preserved in the job snapshot

#### Scenario: Required dependency fails permanently

- **WHEN** a required upstream Logical Job permanently fails
- **THEN** its dependent nodes become Blocked, independent branches retain their accepted results, and no blocked descendant is published as ready work

### Requirement: Logical Jobs are stable and hardware-neutral

The system SHALL persist one stable Logical Job identity per schedulable realization of a Workflow Node. A Logical Job SHALL contain execution requirements and immutable pinned inputs but SHALL NOT identify a runtime product, Manager, host, accelerator model, or other selected execution hardware.

#### Scenario: Logical Job is retried on another Manager

- **WHEN** a retry is later claimed by a different compatible Manager
- **THEN** the Logical Job identity and pinned inputs, image digest, model digest, resource requirements, and attempt budget remain unchanged

#### Scenario: Segment-level job is created

- **WHEN** a segment-scoped capability is materialized for a run
- **THEN** its Logical Job records the finalized recording-set version and complete validated analysis-segment reference without converting preprocessing tiles or container internals into workflow identities

### Requirement: Execution Attempts are numbered, leased, and fenced

Each successful claim SHALL create exactly one Execution Attempt with the next positive attempt number for its Logical Job and a new opaque lease/fencing token. Claim, heartbeat, expiry, retry, failure, and completion transitions SHALL use optimistic entity versions and SHALL reject stale versions without changing current state.

#### Scenario: Concurrent claims target one Logical Job

- **WHEN** two claim requests race for the same ready Logical Job and entity version
- **THEN** at most one creates the next numbered Execution Attempt and the other receives a concurrency conflict without consuming an attempt number

#### Scenario: Lease expires with retry budget remaining

- **WHEN** the current attempt lease expires and the immutable Logical Job attempt budget has remaining capacity
- **THEN** the attempt becomes stale, its token is fenced, and the same Logical Job becomes eligible for republication and a later consecutively numbered attempt

#### Scenario: Lease expires at attempt-budget exhaustion

- **WHEN** the current attempt lease expires after consuming the final permitted attempt
- **THEN** the Logical Job fails permanently, is not republished, and normal descendant-blocking and run-completion rules apply

#### Scenario: Late heartbeat or completion arrives

- **WHEN** a heartbeat or completion carries an expired, superseded, fabricated, or wrong-attempt fencing token
- **THEN** it is rejected or reported obsolete without renewing a lease, accepting a result, or changing workflow state

### Requirement: Completion is idempotent and accepts one immutable result

The system SHALL accept at most one completion for a Logical Job. Acceptance SHALL atomically record the successful attempt outcome, one immutable accepted-result reference, the exact run, node, job, attempt, contract, input, upstream-result, artifact, schema, image, model, and policy lineage, and any newly ready workflow state.

#### Scenario: Current completion is accepted

- **WHEN** a completion matches the current attempt, fencing token, entity version, pinned job snapshot, declared contract version, and validated result manifest
- **THEN** exactly one immutable accepted-result reference is committed and dependency evaluation may use only that accepted reference

#### Scenario: Identical completion is replayed

- **WHEN** the same completion idempotency key and canonical request are replayed
- **THEN** the original outcome and accepted-result identity are returned without creating another result or advancing workflow state again

#### Scenario: Idempotency key is reused with different completion content

- **WHEN** a completion idempotency key is reused in the same scope with a different canonical request
- **THEN** the request conflicts and the previously recorded outcome remains unchanged

#### Scenario: Losing attempt completes after another result was accepted

- **WHEN** an obsolete attempt submits a validly shaped result after a different attempt has been accepted
- **THEN** the completion cannot replace, mutate, or supplement the accepted-result reference

### Requirement: PostgreSQL reconstructs workflow truth after restart

The system SHALL reconstruct run state, dependency readiness, active and expired leases, retry eligibility, accepted results, blocked descendants, and terminal outcomes solely from PostgreSQL. NATS contents, consumer offsets, or queue depth SHALL NOT determine these states.

#### Scenario: Control plane restarts with NATS empty

- **WHEN** the control plane restarts while PostgreSQL contains nonterminal runs and NATS contains no corresponding notifications or ready work
- **THEN** reconciliation derives the same workflow state from PostgreSQL and records missing eligible publications without duplicating accepted results or attempts

#### Scenario: Control plane restarts while NATS is unavailable

- **WHEN** PostgreSQL is available but NATS is unavailable during startup reconciliation
- **THEN** authoritative workflow recovery completes, publication remains durably pending or failed according to policy, and no run or job is advanced from assumed broker state

#### Scenario: Run reaches a partial terminal outcome

- **WHEN** at least one useful required branch has an accepted result and another independent branch permanently fails
- **THEN** the run terminates as PartiallyCompleted while accepted results and failed or blocked branch evidence remain queryable and immutable
