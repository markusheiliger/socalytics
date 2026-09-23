# Add Durable Analysis Workflow Proposal

## Why

The platform cannot yet preserve or recover analysis workflow truth, publish ready work reliably, or exchange versioned Analyst job and completion payloads. This change establishes the durable Analysis foundation now so PostgreSQL authority, attempt fencing, result lineage, and at-least-once transport are proven before Analyst execution is introduced.

## What Changes

- Add Analysis-owned PostgreSQL persistence for Analysis Runs, immutable run and node snapshots, Workflow Nodes and run-local dependency edges, hardware-neutral Logical Jobs, numbered Execution Attempts, immutable accepted-result references, idempotency outcomes, notification deduplication, and the Analysis transactional outbox.
- Add durable graph validation and readiness evaluation that rejects cyclic graphs, creates only dependency-ready work, preserves required, optional, and conditional dependency semantics, and reconstructs state entirely from PostgreSQL after restart.
- Preserve optimistic entity versions, attempt budgets and numbering, lease expiry, fencing tokens, idempotent completion, one accepted result per Logical Job, and immutable lineage from accepted results to run, node, job, attempt, inputs, and pinned versioned snapshots.
- Add an Analysis background outbox publisher for NATS JetStream with at-least-once publication, bounded retry attempts, durable restart recovery, observable pending/published/failed state and lag, and retention only after a known publication outcome.
- Add idempotent consumers and notification validation so duplicate, stale, cross-scope, or fabricated notifications cannot create or advance workflow state; PostgreSQL remains authoritative when NATS is empty, unavailable, restored, or contains duplicate work.
- Add the first versioned machine-readable JSON Schema contracts for hardware-neutral Analyst jobs and fenced completion submissions, including stable identifiers, attempt numbering, fencing, immutable run snapshots, input and accepted-result lineage, and compatibility validation.
- Add PostgreSQL and NATS Testcontainers evidence for transactional atomicity, duplicate delivery, broker outage, bounded retry, publisher restart, expired leases, fabricated notifications, acyclic graphs, completion fencing, and complete recovery with NATS empty or unavailable.
- Depend on the active `add-platform-persistence-foundation` change for PostgreSQL composition, migrations, module-scoped Dapper access, explicit transactions, optimistic concurrency, schema isolation, and PostgreSQL Testcontainers support. This change does not modify that change's artifacts or duplicate its shared infrastructure.
- Depend on the active `add-recording-lineage-and-upload` change for typed finalized recording-set lineage and its canonical finalized-event evidence. Analysis consumes that boundary and event without reading the Recordings schema or changing Recordings-owned outbox state.
- Keep Analyst Manager/container execution, segment encoding or materialization, model inference, capability implementation, production JetStream stream/consumer values, backpressure thresholds, production retention and alert values, client UI, and deployment configuration out of scope.
- Preserve production credentials, subject taxonomy details beyond contract-owned stable families, retry timing and count values, retention periods, queue capacity, operational objectives, and disaster-recovery values as unresolved production decisions.

## Capabilities

### New Capabilities

- `durable-analysis-workflow`: Defines PostgreSQL-authoritative Analysis Runs, DAG snapshots, dependency readiness, Logical Jobs, Execution Attempts, fencing, accepted-result lineage, idempotency, and restart recovery.
- `analysis-messaging`: Defines the Analysis transactional outbox, reliable JetStream publication, observable delivery state, duplicate-safe consumption, broker-outage behavior, and PostgreSQL-led transport recovery.
- `analyst-job-contracts`: Defines versioned machine-readable hardware-neutral job and fenced completion contracts, compatibility rules, and validation evidence.

### Modified Capabilities

None.

## Impact

- Affected product areas: `SocAlytics.Platform.Analysis`, API and background-service composition, Analysis-owned PostgreSQL migrations and Dapper handlers, a NATS JetStream development resource, repository-root machine-readable contracts, Analysis integration tests, host tests, and architecture tests.
- Active-change dependency: the persistence foundation must be applied first or supplied as equivalent accepted behavior. Recording finalization lineage and its typed query/event contract must be available before finalized-notification consumption can be completed; this change remains the sole owner of Analysis state and publication behavior.
- Dependency impact: add centrally managed NATS .NET client and NATS Testcontainers support. PostgreSQL, Dapper, DbUp, and PostgreSQL Testcontainers support come from the persistence foundation and must not be duplicated.
- Contract impact: add initial JSON Schema draft 2020-12 job and completion artifacts, examples, offline reference and compatibility validation, and .NET boundary validation. No Analyst runtime or generated client is introduced.
- Documentation impact: apply synchronizes `docs/architecture/job-processing.md`, `docs/architecture/analyst-runtime-and-recovery.md`, `docs/architecture/contracts-and-compatibility.md`, `docs/architecture/platform-implementation.md`, repository/platform development guidance, and component-local Analysis contract guidance justified by implementation.
- Security and governance impact: durable actor/correlation evidence, minimized payloads, fencing, deduplication, immutable lineage, and observable publication state add development control evidence without resolving production identity, credential, audit, retention, encryption, backup, or recovery approvals.
- UX and client applications are unaffected.
