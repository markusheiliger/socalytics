# Analysis Messaging Specification

## Purpose

Define atomic Analysis outbox evidence and recoverable at-least-once JetStream transport without transferring workflow authority away from PostgreSQL.

## ADDED Requirements

### Requirement: Analysis state and outgoing publications are atomic

Every Analysis notification or ready-work publication SHALL originate from an Analysis-owned outbox record committed in the same PostgreSQL transaction as the authoritative state transition that requires publication. A failed transaction SHALL commit neither the state transition nor its outbox record.

#### Scenario: Ready work becomes authoritative

- **WHEN** dependency evaluation commits a Logical Job as ready
- **THEN** exactly one canonical outbox record for that readiness generation is committed in the same transaction

#### Scenario: Transaction fails after preparing publication

- **WHEN** an injected failure causes the state transaction to roll back
- **THEN** neither the state transition nor an outgoing outbox record is observable and nothing is eligible for publication

#### Scenario: State mutation is replayed

- **WHEN** the same idempotent command or readiness generation is processed more than once
- **THEN** durable uniqueness converges on one authoritative outcome and one canonical outbox record

### Requirement: Outbox publication is at least once and restart-safe

A background publisher SHALL publish eligible Analysis outbox records to NATS JetStream with stable message and causation identifiers. It SHALL record acknowledged publication durably, SHALL tolerate publication after an acknowledgment is lost, and SHALL resume all unpublished eligible records after publisher or process restart.

#### Scenario: Publish acknowledgment is recorded

- **WHEN** JetStream acknowledges an outbox message
- **THEN** the publisher records a known published outcome and publication timestamp without altering the associated workflow transition

#### Scenario: Publisher stops after broker acceptance but before recording success

- **WHEN** JetStream accepts a message and the publisher stops before its PostgreSQL success update commits
- **THEN** restart may publish the same stable message again and consumers process the duplicate idempotently

#### Scenario: Publisher restarts with pending records

- **WHEN** the publisher restarts with eligible unpublished outbox records
- **THEN** it resumes them without requiring the originating command or state transition to run again

### Requirement: Publication retries are bounded and observable

The publisher SHALL apply an explicitly configured finite retry policy and durably expose each record's pending, published, or failed outcome, attempt count, last attempt time, next eligible attempt, sanitized failure category, age, and publication lag. Exhausting the configured attempt limit SHALL produce an observable failed outcome and SHALL NOT discard or falsely acknowledge the authoritative workflow state.

#### Scenario: Broker is temporarily unavailable

- **WHEN** publication fails because JetStream is unavailable and retry capacity remains
- **THEN** the record remains unpublished, its attempt evidence advances, and it becomes eligible again only according to the configured retry policy

#### Scenario: Retry budget is exhausted

- **WHEN** the configured final publication attempt fails
- **THEN** the record becomes observably failed, is retained for operator reconciliation, and its associated PostgreSQL workflow state remains authoritative

#### Scenario: Published-record retention runs

- **WHEN** outbox retention evaluates old records
- **THEN** it removes only records whose publication outcome is known and whose approved retention obligations are satisfied

### Requirement: Consumers are idempotent and validate authoritative context

Every Analysis event or ready-work consumer SHALL deduplicate by a stable message identity and SHALL validate referenced identities, scope, versions, current state, readiness generation, and fencing context against owning typed boundaries and PostgreSQL before causing a state transition. Broker possession alone SHALL confer no authority.

#### Scenario: JetStream redelivers an accepted notification

- **WHEN** a consumer receives the same stable message more than once
- **THEN** notification deduplication returns the original processing outcome and no Analysis Run, Logical Job, attempt, accepted result, or downstream outbox record is duplicated

#### Scenario: Fabricated recording-finalized notification is delivered

- **WHEN** a syntactically valid notification references nonexistent, mismatched, cross-Team, or nonfinalized recording lineage
- **THEN** the consumer rejects it without creating or advancing an Analysis Run and records a sanitized observable rejection outcome

#### Scenario: Stale ready-work message is delivered

- **WHEN** a ready-work message references a cancelled, blocked, completed, superseded, already claimed, or different-readiness-generation Logical Job
- **THEN** claim validation rejects it without creating an Execution Attempt or changing current workflow state

### Requirement: JetStream can be reconstructed from PostgreSQL

Loss, empty restoration, or unavailability of NATS SHALL NOT lose or invent workflow work. Reconciliation SHALL use current PostgreSQL state and Analysis outbox outcomes to republish only currently eligible notifications and ready jobs with stable identities.

#### Scenario: NATS is restored empty

- **WHEN** streams and consumers are recreated without prior messages
- **THEN** reconciliation republishes eligible missing work from PostgreSQL and the outbox while omitting completed, blocked, cancelled, exhausted, or otherwise ineligible jobs

#### Scenario: NATS retains stale messages after PostgreSQL recovery

- **WHEN** the broker contains work that no longer matches authoritative PostgreSQL eligibility
- **THEN** consumers reject or acknowledge the stale transport copy without reverting or advancing PostgreSQL state
