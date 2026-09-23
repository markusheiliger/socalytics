# Durable Analysis Workflow Design

## Context

See `proposal.md` for motivation and the three delta specs for required behavior. The current .NET 10 host composes an Analysis project containing only one public dependency-injection method and an internal marker. The API has no domain routes or workers, AppHost starts only the API, and the solution has no NATS or contract artifacts. The active `add-platform-persistence-foundation` change plans shared PostgreSQL, Npgsql, Dapper, DbUp, transaction, optimistic-concurrency, schema-isolation, and Testcontainers behavior; `add-recording-lineage-and-upload` plans immutable finalized recording lineage and a Recordings-owned outbox event but explicitly defers transport publication.

The adopted architecture assigns Analysis Runs, workflow state, attempts, accepted results, notification receipts, and outgoing Analysis events to the Analysis module's `analysis` PostgreSQL schema. NATS JetStream is a reconstructable transport for lifecycle notifications and ready work. It cannot create readiness, validate claims, or supersede PostgreSQL state. Production subjects, credentials, capacities, retry values, retention, objectives, and alerts remain unresolved profile inputs.

Impact categories:

- **UX:** Unaffected. No client, page, user journey, or accessibility behavior is added.
- **Architecture:** Analysis gains its first domain model, durable DAG evaluator, attempt and completion boundary, module outbox, publisher, JetStream consumers, and reconciliation worker. AppHost gains local NATS composition. No new product service or capability-to-capability project reference is introduced.
- **Security and governance:** Adds fencing, minimized immutable lineage, deduplication, sanitized failure evidence, and correlation/audit inputs. Production broker identity, credentials, TLS, retention, encryption, backup, recovery, and audit approvals remain unresolved.
- **Implementation:** Adds Analysis-owned migrations and internal Dapper handlers, background workers hosted by the existing API process, centrally versioned NATS client and NATS Testcontainers dependencies, repository-root JSON Schemas, an Analysis integration-test project, and focused host and architecture coverage.
- **Documentation:** Apply synchronizes `docs/architecture/job-processing.md`, `docs/architecture/analyst-runtime-and-recovery.md`, `docs/architecture/contracts-and-compatibility.md`, `docs/architecture/platform-implementation.md`, root/platform development guidance, and component-local Analysis contract/validation guidance.

## Goals / Non-Goals

**Goals:**

- Make every workflow transition, readiness decision, attempt, and accepted result reconstructable from PostgreSQL alone.
- Preserve immutable run snapshots and accepted-result lineage while permitting bounded attempt retries and at-least-once physical delivery.
- Make outgoing Analysis publication atomic with its causing state transition and recoverable across broker and process failure.
- Establish canonical v1 job and completion schemas before runtime implementations can create incompatible payloads.
- Prove failure behavior with real PostgreSQL and JetStream instances without relying on timing-only assertions or developer services.

**Non-Goals:**

- Running Analyst containers, selecting Managers or hardware, encoding segments, invoking models, indexing capability-specific result facts, or implementing any Analyst capability.
- Publishing the active Recordings change's module-owned outbox. This change consumes and validates the canonical finalized notification when delivered but does not access or mutate Recordings outbox rows. A Recordings publisher or a later shared publication orchestration change remains required for the end-to-end automatic trigger.
- A user-facing run-request API, client workflow, generated SDK, AsyncAPI document, or production deployment profile.
- Choosing production subject values, stream topology values, credentials, retry intervals/counts, retention periods, queue capacities, backpressure thresholds, alerts, RPO, or RTO.
- Descendant-only recomputation, cross-run result reuse, dynamic graph mutation, or importing prior accepted nodes into a new run.

## Decisions

### 1. Keep orchestration inside the existing Analysis module and API host

Analysis owns internal application handlers, persistence, DAG evaluation, attempt state, completion acceptance, outbox records, notification consumption, and reconciliation. Its existing public composition method remains the primary module entry point and may be joined only by narrow endpoint/contract ports justified by host composition. The API hosts background services for outbox publication, notification consumption, lease monitoring, and startup reconciliation.

This preserves the modular-monolith boundary and current peer-project graph. A separate Scheduler executable is rejected because this slice has no independent scaling or deployment evidence and would add distributed coordination before the durable model exists. Putting workflow logic in API endpoints is rejected because broker delivery, restart recovery, and lease expiry are not request-scoped concerns.

### 2. Gate apply on the persistence foundation and isolate the recording dependency

Implementation starts only after `add-platform-persistence-foundation` is applied or equivalent accepted behavior exists. Analysis uses its module-scoped connection, explicit transaction, migration, and optimistic-concurrency contracts rather than copying them. The Recording finalized consumer depends on a narrow typed lineage validation port composed by the API host; it never references Recordings persistence or SQL.

The finalized event contract and typed lineage boundary from `add-recording-lineage-and-upload` are required for that consumer slice. Other Analysis work can compile and be tested independently, but the tasks must not mark finalized-notification behavior complete until equivalent accepted Recordings contracts exist. Direct cross-schema reads and edits to either active change's artifacts are rejected.

### 3. Persist the Analysis hierarchy with database-enforced identity and immutability

The `analysis` schema adds tables for runs, workflow nodes, workflow edges, logical jobs, execution attempts, accepted results, idempotency outcomes, notification receipts, and outbox records. Stable opaque primary identities, explicit foreign keys, unique natural constraints, check constraints, timestamps, and `BIGINT` entity versions enforce hierarchy and concurrency. Accepted snapshots and lineage have insert-only application privileges; mutable workflow rows expose only explicit state transitions with expected versions.

Runs contain scope, source lineage, workflow identity/version, requested capability set, completion policy, and state. Nodes copy every resolved execution identity and value. Edges record dependency kind plus a versioned conditional predicate representation. Jobs copy effective attempt, lease, stale, execution-timeout, resource, and input snapshots. Attempts carry consecutive numbers, Manager identity when claimed, token digest rather than a recoverable bearer token, lease times, state, and entity version. Accepted results reference exactly one successful attempt and preserve complete immutable input and artifact lineage.

One generic serialized aggregate is rejected because relational constraints, conflict diagnostics, recovery queries, and lineage inspection would become weaker. A generic repository is rejected in favor of purpose-built Dapper SQL owned by Analysis.

### 4. Validate and freeze a DAG before making work visible

Run creation canonicalizes node and edge identities, validates endpoints and dependency kinds, evaluates a deterministic topological ordering, and rejects cycles before committing. The transaction inserts the complete immutable run snapshot, nodes, edges, initial Logical Jobs, idempotency outcome, and any initially ready outbox records. No partially created graph becomes visible.

Persisted topological ordinals support deterministic evaluation, but they are not trusted as the only validation: integration tests reload the edge set and prove acyclicity and endpoint closure. Dynamic edge insertion is unsupported. Database foreign keys prevent dangling edges; application graph validation prevents cycles that ordinary constraints cannot express cleanly.

Incremental graph mutation is rejected because it would make run snapshots mutable and complicate recovery and compatibility. A database trigger with recursive cycle detection is rejected because graph semantics and conditional predicates need typed validation and focused test diagnostics before persistence.

### 5. Evaluate readiness through transactional compare-and-transition operations

The evaluator reads one run's persisted nodes, edges, Logical Jobs, and accepted results, computes required, optional, and conditional inputs at pinned versions, and attempts state transitions with expected entity versions. Each newly ready Logical Job and its canonical outbox record commit in one transaction. Concurrent evaluators may repeat reads, but durable uniqueness and optimistic concurrency allow only one readiness generation and one outbox record.

Required permanent failure blocks descendants; optional absence never blocks; conditional edges use their pinned predicate representation. Match barriers record accepted, failed, missing, skipped, and partial coverage explicitly. Independent successful branches remain accepted, and terminal run state is derived from all durable branches.

Using JetStream consumer state or queue depth as a readiness signal is rejected. Holding one transaction for an entire run lifetime is rejected because it would create long locks and prevent independent branch progress.

### 6. Claim work through PostgreSQL and acknowledge transport afterward

A ready-work message is a transport copy of one Logical Job readiness generation. A Manager-facing claim operation, represented initially by an internal/application boundary and focused tests rather than Analyst execution, validates current eligibility and atomically creates the next numbered Execution Attempt with a new cryptographically random fencing token and lease. The plaintext token is returned once; only a verifier/digest is retained where practical. The consumer acknowledges the JetStream delivery only after a successful or deterministically obsolete claim outcome.

Heartbeats and completion compare Manager, job, attempt, token, expected entity version, and current state. Lease expiry atomically marks the attempt stale, fences the token, consumes the attempt budget, and either records a new ready generation plus outbox row or permanently fails the job. Cancellation uses the same fencing boundary and makes queued copies obsolete.

Creating attempts at message publication time is rejected because broker redelivery would consume attempt numbers and create leases without a claimant. Trusting an acknowledged queue delivery as a claim is rejected because PostgreSQL must remain the fencing authority.

### 7. Accept completion and downstream readiness in one authoritative transaction

Completion first validates the canonical v1 payload, then resolves its scoped idempotency key and compares the complete current attempt and immutable job snapshot. Success inserts one accepted-result reference, transitions attempt and job state, records the idempotency outcome, evaluates affected descendants, and writes resulting outbox records in one explicit transaction. A unique accepted-result constraint per Logical Job and unique scoped request hash guarantee convergence.

Accepted artifact references and manifest digests are stored, not mutable result bytes. Capability-specific fact indexing and object-store verification are deferred, so test fixtures use immutable symbolic artifact references and digests while preserving the same acceptance boundary. A structurally valid but stale completion is an obsolete semantic outcome, not a schema error.

Publishing directly from the completion handler is rejected because commit/publish failure would lose or misorder ready work. Updating an accepted result is rejected because it would destroy fencing and lineage evidence.

### 8. Use one Analysis-owned transactional outbox state machine

Each outbox record contains a stable message ID, message kind, subject-family key, contract name/version, aggregate identity and version, readiness generation when applicable, canonical payload and digest, correlation/causation evidence, occurrence time, state, attempt count, next-attempt time, last sanitized failure category, and published time. Unique constraints bind one canonical publication to its causal transition.

The publisher claims small batches with short PostgreSQL leases so multiple host instances cannot intentionally publish the same row concurrently. It publishes to JetStream with the stable message ID, records broker acknowledgment, and advances pending, published, or failed state using expected versions. A crash after broker acceptance but before the database update intentionally permits duplicate delivery. Retry timing and finite limits are validated configuration with explicit test values; no production defaults are selected here.

A shared cross-module outbox table is rejected because domain modules own outgoing state and payload semantics. Exactly-once claims are rejected because they cannot span PostgreSQL and JetStream; stable identifiers and idempotent consumers provide correctness under at-least-once delivery.

### 9. Persist consumer receipts with the state they cause

Each consumed message is identified by stable message ID, kind, contract version, payload digest, and authoritative scope. Processing validates schema and then revalidates referenced lineage, current job/run state, versions, and readiness against PostgreSQL and typed owners. The notification receipt, canonical processing outcome, any state mutation, and any resulting outbox records commit together.

An equal duplicate returns the stored outcome. Reuse of a message ID with another digest conflicts and is recorded as a sanitized rejection. Fabricated or cross-scope recording notifications create no run. Stale ready-work messages create no attempt. Poison payload handling remains bounded and observable without treating dead-letter placement as workflow truth.

In-memory deduplication is rejected because restart would lose it. Trusting a broker subject or publisher identity without domain validation is rejected because compromised or stale transport state must not create authority.

### 10. Reconcile from PostgreSQL on startup and after broker restoration

Reconciliation first loads authoritative nonterminal runs, active attempts, expired leases, eligible ready generations, and outbox outcomes. It fences expired attempts through the normal transition, recomputes readiness idempotently, and makes missing current publications eligible. It never creates attempts merely because a queue message exists and never republishes completed, blocked, cancelled, superseded, or exhausted jobs.

The API can recover workflow state while NATS is unavailable; only messaging readiness remains degraded. When streams are empty or recreated, current outbox and readiness state reconstruct transport. When stale messages remain, normal claim/consumer validation makes them harmless.

Treating stream replay as event-sourced workflow reconstruction is rejected because the adopted authority is PostgreSQL. Deleting and rebuilding Analysis state from NATS is prohibited.

### 11. Introduce canonical v1 schemas under the repository contract authority

Apply creates a repository-root `contracts/` index and Analysis-owned JSON Schema draft 2020-12 job and completion families under stable v1 paths. Schemas use stable major `$id` values, exact semantic version metadata, lower camel case, offline repository-relative references, opaque identifiers, RFC 3339 UTC timestamps, numeric-second durations, and algorithm-qualified SHA-256 digests. Examples cover segment and match jobs, successful completion, partial coverage, and invalid/stale semantic fixtures.

A deterministic repository contract command validates schema syntax, reference closure, unique IDs, examples, secret-free fixtures, and same-major compatibility. .NET boundary tests independently validate the same fixtures and then test semantic PostgreSQL acceptance. Generated source types are optional implementation outputs and never replace schemas as authority.

Embedding schema strings only in C# is rejected because cross-language Analyst implementations need one canonical artifact. AsyncAPI is deferred because this slice needs payload authority and behavior evidence before a broader messaging description.

This contract root and validation command materially establish the planned contract authority and are an **ADR candidate**. The durable outbox/claim/reconciliation implementation refines established architecture rather than changing its authority boundary; create an additional ADR only if apply introduces a reusable cross-module messaging abstraction or separate executable.

### 12. Separate fast structural tests from real-infrastructure recovery evidence

Add an Analysis integration-test project using PostgreSQL and NATS Testcontainers with explicit per-test configuration. Database tests cover hierarchy round trips, immutable snapshots, graph cycles, dependency kinds, hardware-neutral jobs, entity-version and attempt-number conflicts, lease expiry and exhaustion, completion idempotency/fencing, accepted lineage, and atomic outbox writes. Combined tests cover duplicate delivery, broker outage, bounded retry, acknowledgment-loss duplicate, publisher restart, fabricated notifications, NATS-empty restoration, and recovery while NATS is unavailable.

Host tests verify Aspire composes PostgreSQL, JetStream, and the API; background workers become ready only after migrations and validated configuration; existing health and OpenAPI behavior remains available. Architecture tests enforce Analysis schema/SQL ownership, peer module references, bounded exports, contract location, payload exclusions, and absence of runtime-specific fields.

Tests use deterministic probes, controllable clocks where lease/retry time matters, and eventual assertions bounded by test configuration. They never fall back to developer or production services.

## Risks / Trade-offs

- **[Active foundations may not expose the expected persistence or finalized-lineage contracts]** -> Gate dependent tasks on equivalent accepted behavior and stop rather than bypassing module boundaries or duplicating infrastructure.
- **[Background workers in the API can contend when multiple instances run]** -> Use short database claims, expected versions, stable identities, and idempotent transitions; keep horizontal scale evidence explicit.
- **[Application-level DAG validation can drift from persisted data]** -> Make graph rows immutable, enforce endpoint constraints, persist deterministic ordering, and reload graphs in integration tests.
- **[At-least-once publication duplicates physical work]** -> Validate every claim in PostgreSQL, deduplicate notifications durably, and fence every completion by current attempt.
- **[A finite publisher retry budget can leave failed records requiring intervention]** -> Preserve failed rows, expose age/count/category/lag, and support idempotent reconciliation without silently dropping state.
- **[Token persistence can expose active lease authority]** -> Return plaintext once, persist a verifier where feasible, minimize diagnostics, and fence on expiry, cancellation, or replacement.
- **[Contract v1 may overfit before Analyst execution exists]** -> Limit it to architecture-fixed identities and invariants, validate representative fixtures in .NET, and use additive same-major evolution.
- **[Combined PostgreSQL/NATS tests are slower and timing-sensitive]** -> Keep domain tests separate, use controllable clocks and explicit short test policies, and reserve eventual assertions for actual broker boundaries.
- **[Recordings events still need a publisher outside this scope]** -> Document the dependency honestly; test Analysis consumption with canonical injected messages and do not claim end-to-end automatic finalization triggering.

## Migration Plan

1. Confirm the persistence foundation is implemented or equivalently accepted; add centrally managed NATS, JSON Schema validation, and Testcontainers dependencies plus the Analysis integration-test project.
2. Add additive Analysis migrations and persistence tests for the immutable hierarchy, DAG, idempotency, notification receipts, accepted results, and outbox.
3. Implement run creation, readiness evaluation, claim, heartbeat, expiry, completion, and reconciliation in small slices, running focused PostgreSQL tests after each transition family.
4. Add canonical v1 job/completion schemas, examples, repository contract validation, and .NET fixture validation before publishing payloads.
5. Add JetStream local composition, Analysis publisher/consumers, duplicate handling, and combined outage/restart/recovery tests. Integrate Recordings lineage validation only after its typed contracts are available.
6. Extend host and architecture evidence, synchronize authoritative architecture and development documentation, resolve ADR candidates, then run restore, build, all tests, contract validation, OpenSpec validation, independent verification, and security/governance audit.

Application rollback removes worker and JetStream host wiring while leaving additive Analysis tables, immutable accepted history, and outbox evidence intact. Applied migrations are never edited or automatically reversed. Before rollback, work intake is paused and active attempts are fenced; a prior application version must not interpret newer contract majors or mutate unknown schema state. Disposable local/test infrastructure may be explicitly reset.

## Open Questions

- Which approved finite publication retry values, retention periods, subject names, stream limits, alert thresholds, and service objectives belong to each future production profile? This change uses explicit development/test configuration and does not approve production defaults.
- Which later change will publish the Recordings-owned finalized-event outbox: a Recordings-hosted worker or an approved shared publication orchestration boundary? Analysis consumption remains correct under either choice because it depends on the canonical event and typed lineage validation, not the publisher implementation.
