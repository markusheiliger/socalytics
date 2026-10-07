# Feature Specification: Durable Analysis Workflow

**Feature Branch**: `20261005-130703-durable-analysis-workflow`

**Created**: 2026-10-05

**Status**: Draft

**Input**: User description: "Durable Analysis Workflow: durable, restart-safe Analysis Runs and workflow graphs that are the single source of truth, readiness evaluation with required, optional, and conditional dependencies, cycle rejection, at-least-once publication of ready work with observable publication state and lag, attempt-fenced and idempotent completion handling, immutable accepted-result references, and versioned hardware-neutral Analyst job and completion contracts. Consumes finalized recording-set lineage from 20261005-130702-recording-lineage-upload and the durable storage foundation from 20261005-130700-platform-persistence-foundation; Analyst execution is not part of this feature."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Finalized recording set produces one durable Analysis Run (Priority: P1)

When a Coach finalizes a match's recording set, the platform receives the finalized-recording-set notification, validates it against the authoritative recording-set lineage owned by the Recordings capability, and creates exactly one Analysis Run for that finalized recording-set version. The run captures, at creation time, an immutable snapshot of the workflow definition and every resolved execution choice (capability declarations, Analyst profiles, image and model digests, policies, schemas, resource, retry, lease, and timeout values) together with the Workflow Nodes, dependency edges, and hardware-neutral Logical Jobs it needs. Invalid workflow graphs are rejected before any work exists. The run and its whole hierarchy survive platform restarts and are the single source of truth for analysis progress.

**Why this priority**: Without a durable, immutable, correctly scoped run there is nothing to schedule, recover, or audit. Every later capability (readiness, attempts, publication) builds on this record, and Coaches need confidence that a finalized match is never silently lost or analyzed twice.

**Independent Test**: Deliver a finalized-recording-set notification (including duplicates, fabricated variants, and a cyclic workflow definition), restart the platform, and reload the run. The test passes when exactly one run exists per valid notification, its hierarchy round-trips unchanged, invalid inputs created no schedulable work, and later definition changes do not alter the stored snapshot.

**Acceptance Scenarios**:

1. **Given** a Coach has finalized recording-set version V of a match, **When** the platform receives the finalized notification for V, **Then** it validates V's lineage through the Recordings capability's boundary and durably creates one Analysis Run containing the immutable run snapshot, Workflow Nodes, dependency edges, and initial Logical Jobs for the resolved workflow; the run is `Requested` until its first Logical Job becomes ready and `Running` thereafter.
2. **Given** the finalized notification for V has already produced a run, **When** the same notification is delivered again (sequentially or concurrently), **Then** no second run, node, Logical Job, or publication is created and the original processing outcome is reported.
3. **Given** a syntactically valid notification that references a nonexistent, non-finalized, mismatched, or other-team recording-set version, **When** it is processed, **Then** no Analysis Run is created and an observable, sanitized rejection outcome is recorded.
4. **Given** the resolved workflow graph contains a direct, indirect, or self-referencing cycle, or an edge to a node outside the graph, **When** run creation is attempted, **Then** the run is recorded as `Failed` with an invalid-graph reason and no Workflow Node becomes schedulable, no Logical Job is created, and no ready work is published.
5. **Given** a run exists, **When** a capability declaration, Analyst profile, image, model, policy, schema, resource, retry, lease, or timeout definition changes afterwards, **Then** the existing run snapshot is unchanged and only a new finalized recording-set version or new request produces a new run that uses the changed definition.
6. **Given** a run with its full hierarchy has been committed, **When** the platform restarts and the run is reloaded, **Then** its nodes, edges, Logical Jobs, Execution Attempts, and accepted-result references reproduce the same identities, values, ordering, and relationships that were committed.
7. **Given** a workflow with one segment-scoped and one match-scoped capability and a finalized recording set covering N validated analysis segments, **When** the run is created, **Then** it contains N segment-level Logical Jobs, each recording the finalized recording-set version and complete analysis-segment identity, plus one match-level Logical Job, and every Logical Job of a capability carries the same pinned Analyst profile, image digest, and model digest and no host, Manager, or runtime selection.

---

### User Story 2 - Readiness is evaluated from durable state with explicit dependency semantics (Priority: P2)

The Scheduler evaluates each run's graph only from durable run, node, Logical Job, and accepted-result state. A Logical Job becomes ready only when every required dependency has an accepted result at the pinned versions; optional dependencies never delay or block; conditional dependencies are required only when their pinned predicate applies. Segment-level work fans out across segments, match-level work waits for the foundational barrier, permanently failed required upstream work blocks only its descendants, and the run ends as `Completed`, `PartiallyCompleted`, or `Failed` according to the architecture's rules.

**Why this priority**: Correct readiness is what turns a stored run into useful, ordered work. Wrong readiness either wastes Analyst capacity on work whose inputs are missing or silently stalls analysis.

**Independent Test**: Using representative workflow fixtures and directly recorded accepted or failed upstream outcomes (no Analyst execution required), evaluate readiness and compare the resulting ready, waiting, and blocked sets and terminal run states to expected tables, including after a restart between evaluations.

**Acceptance Scenarios**:

1. **Given** a node with a required dependency that has no accepted result at every declared input version, **When** readiness is evaluated, **Then** its Logical Jobs are not ready and no ready work is published for them.
2. **Given** an optional dependency that has no accepted result or has failed, **When** the node's remaining requirements are satisfied, **Then** the node's Logical Jobs become ready and their input snapshot records the optional input as absent or failed.
3. **Given** a conditional dependency whose pinned predicate does not apply, **When** readiness is evaluated, **Then** the dependency does not delay or block the node and its non-applicability is recorded in the input snapshot; **and Given** the predicate applies, **Then** the dependency behaves as required.
4. **Given** a match-level node that depends on a segment-scoped capability, **When** some but not all required segment Logical Jobs have accepted results, **Then** the match-level Logical Job is not ready; it becomes ready only once accepted results exist for every required segment.
5. **Given** a required upstream Logical Job fails permanently, **When** readiness is evaluated, **Then** every dependent node becomes `Blocked`, no blocked Logical Job is ever published, and independent branches continue and keep their accepted results.
6. **Given** at least one required useful branch has an accepted result and another independent branch has permanently failed, **When** no further work can become ready, **Then** the run terminates as `PartiallyCompleted`; **and Given** no required useful result can be accepted, **Then** the run terminates as `Failed`; **and Given** all nodes have accepted results, **Then** the run terminates as `Completed`.
7. **Given** the platform restarts between two readiness evaluations, **When** evaluation resumes, **Then** it reaches the same ready, waiting, and blocked sets as an uninterrupted evaluation without creating duplicate ready work.

---

### User Story 3 - Execution Attempts are fenced and completion is idempotent (Priority: P3)

An Analyst Manager that pulls a ready Logical Job claims it from the platform. Each successful claim creates exactly one numbered Execution Attempt with a new lease and opaque fencing token, consuming one unit of the Logical Job's immutable attempt budget. Heartbeats renew only the current attempt's lease. Expired leases are detected by the platform from durable state, fenced, and retried while budget remains. Only one attempt can ever become the accepted completion for a Logical Job; stale, duplicate, fabricated, or replayed completions cannot change accepted history.

**Why this priority**: At-least-once delivery and host failures guarantee duplicate physical work. Fencing and idempotent completion are what keep accepted results unique and trustworthy despite that.

**Independent Test**: Drive claim, heartbeat, lease-expiry, failure, and completion operations directly against ready Logical Jobs on behalf of test Analyst Manager identities (no container execution), including concurrent claims, replays, late completions, and restarts, and verify attempt numbering, budget consumption, and that exactly one accepted-result reference exists per completed Logical Job.

**Acceptance Scenarios**:

1. **Given** a ready Logical Job, **When** two claims race for it, **Then** exactly one creates the next consecutively numbered Execution Attempt with a new lease and fencing token, and the other is rejected without consuming an attempt number or budget.
2. **Given** an active attempt, **When** a heartbeat arrives from the claiming Manager with the current attempt identity and token, **Then** the lease is renewed; **and When** the heartbeat carries a stale, superseded, fabricated, or other-Manager token, **Then** it is rejected as obsolete and nothing changes.
3. **Given** the current attempt's lease expires and attempt budget remains, **When** the platform detects expiry, **Then** it marks the attempt stale, invalidates its token, and makes the same Logical Job eligible for a new, consecutively numbered attempt.
4. **Given** the current attempt's lease expires after the final permitted attempt, **When** the platform detects expiry, **Then** the Logical Job fails permanently, is never republished, and descendant-blocking and run-outcome rules apply.
5. **Given** a completion matching the current attempt, token, pinned Logical Job snapshot, and declared contract version, **When** it is submitted, **Then** exactly one immutable accepted-result reference with complete lineage is recorded and readiness for dependents is re-evaluated in the same durable step.
6. **Given** a completion was accepted, **When** the identical completion with the same idempotency key is replayed, **Then** the original outcome and accepted-result identity are returned and nothing new is created; **and When** the same idempotency key is reused with different completion content, **Then** the request is rejected as a conflict and the recorded outcome is unchanged.
7. **Given** an attempt was fenced by lease expiry and a later attempt was accepted, **When** the old attempt submits a well-formed completion, **Then** it is rejected as obsolete and cannot replace, alter, or add to the accepted result.
8. **Given** the platform is down while a lease expires, **When** it restarts, **Then** the expired lease is detected from durable state and handled exactly as if the platform had stayed up.

---

### User Story 4 - Ready work is published reliably and the operator can see publication health (Priority: P4)

Every state change that makes work ready, or that requires a lifecycle notification, durably records the corresponding outgoing message in the same atomic step as the state change. A background publisher delivers these messages to the message transport at least once with bounded retries, and the platform operator can observe each message's publication outcome, attempt count, age, and lag. The transport is never workflow truth: if it is unavailable, emptied, or holds stale copies, the platform recovers from durable state alone and republishes only currently eligible work.

**Why this priority**: Reliable publication connects durable workflow state to Analyst Managers. It depends on Stories 1–3 for what is published and on correct claim validation to make duplicates harmless.

**Independent Test**: With a controllable message transport, inject transport outages, lost publication acknowledgments, publisher restarts, an emptied transport, and stale transport copies; verify that every eligible message is eventually published, no ineligible work is published, duplicates are handled idempotently, and publication state is observable throughout.

**Acceptance Scenarios**:

1. **Given** a state change makes a Logical Job ready, **When** the change commits, **Then** exactly one outgoing ready-work message for that readiness occurrence is durably recorded in the same atomic step; **and Given** the state change fails and rolls back, **Then** neither the state change nor the message exists.
2. **Given** the message transport is unavailable, **When** the publisher attempts delivery, **Then** the message stays pending, its attempt evidence and next eligible time advance according to the configured finite retry policy, and workflow state is unaffected.
3. **Given** a message's final permitted publication attempt fails, **When** the retry budget is exhausted, **Then** the message becomes observably failed, is retained for operator reconciliation, and the associated workflow state remains authoritative and unchanged.
4. **Given** the transport accepted a message but the publisher stopped before recording success, **When** the publisher restarts, **Then** it may publish the same stably identified message again and the duplicate is processed idempotently.
5. **Given** the transport is restored empty after loss, **When** reconciliation runs, **Then** every currently eligible ready Logical Job and pending notification is republished with its stable identity, and completed, blocked, exhausted, already-claimed, or otherwise ineligible work is not.
6. **Given** the transport still holds a ready-work message that no longer matches durable eligibility (already claimed, completed, blocked, or a superseded readiness occurrence), **When** a Manager claims from it, **Then** the claim is rejected without creating an Execution Attempt and the delivery is acknowledged as obsolete.
7. **Given** any outgoing message, **When** the operator inspects publication state, **Then** its pending, published, or failed outcome, attempt count, last attempt time, next eligible attempt, sanitized failure category, age, and publication lag are visible without inspecting transport contents, and the related workflow signals are correlated by run, Logical Job, attempt, Manager, and capability while containing no secrets, fencing tokens, media, or access URLs.
8. **Given** outgoing messages of mixed outcomes, **When** retention runs, **Then** only messages with a known publication outcome are removed and every pending or failed message remains.

---

### User Story 5 - Versioned, hardware-neutral job and completion contracts (Priority: P5)

Analyst capability owners and Analyst Manager implementers rely on versioned machine-readable contracts for the ready-work job payload and the fenced completion payload. Contracts carry stable identities, pinned versions and digests, and full lineage; they never select hardware or runtime products and never carry secrets. Contract validation runs offline and deterministically and rejects breaking changes within a major version.

**Why this priority**: Contracts must exist before any Analyst Manager or Analyst implementation can produce compatible payloads, but they are only useful once the workflow they describe exists.

**Independent Test**: Run the repository's contract validation offline against valid and invalid example payloads and against a proposed breaking revision; verify deterministic pass/fail outcomes and that the platform's own job and completion handling accepts the valid examples and rejects the invalid ones.

**Acceptance Scenarios**:

1. **Given** a segment-scoped job payload with the finalized recording-set version, complete analysis-segment identity, and all pinned versions and digests, **When** it is validated, **Then** it passes as one hardware-neutral Logical Job snapshot; **and Given** a match-scoped job payload, **Then** it identifies the run, match, capability, and accepted dependency results without a synthetic segment.
2. **Given** a job payload that names a specific Manager, host, container runtime product, accelerator model, or preprocessing tile, **When** it is validated, **Then** it is rejected.
3. **Given** a completion payload missing its attempt identity, attempt number, fencing token, idempotency key, or required lineage, **When** it is validated, **Then** it is rejected before any acceptance logic runs; **and Given** a structurally valid but stale completion, **Then** structural validation passes but durable acceptance rejects it.
4. **Given** a job or completion example containing a credential, presigned or otherwise expiring access URL, broker connection detail, or media bytes, **When** it is validated, **Then** it is rejected.
5. **Given** a proposed revision that removes or renames a field, adds a required input, narrows an accepted value, or changes established completion semantics within the same major version, **When** compatibility validation runs, **Then** it fails and requires a new side-by-side major version.
6. **Given** two semantically identical completions serialized with different field ordering, **When** they are compared for idempotent replay, **Then** they are treated as the same request.

---

### Edge Cases

- A Coach finalizes a new recording-set version for the same match while a run for the previous version is still `Running`: the new version produces its own new run; the earlier run continues unaffected because supersession and cancellation are deferred.
- The same finalized notification is delivered concurrently to two consumers: durable uniqueness on the finalized recording-set version and resolved workflow version yields exactly one run.
- A finalized notification arrives for a version whose lineage the Recordings boundary cannot currently confirm because that boundary is unavailable: the notification is not acknowledged as processed and is retried; no run is created from unverified evidence.
- A workflow graph has a node with no dependencies and no dependents, or an empty requested capability set: an isolated node is valid; an empty capability set is an invalid graph and the run is recorded as `Failed` with no work.
- A conditional dependency's predicate depends on an upstream that has itself failed: the predicate is evaluated from durable accepted state only; an indeterminate predicate is treated as applicable, so the dependency behaves as required.
- An optional upstream result is accepted after its dependent Logical Job already became ready: the dependent's recorded input snapshot is not changed retroactively; the late optional result remains accepted history for later runs only.
- A completion and a lease expiry for the same attempt are processed at the same moment: exactly one transition wins; if expiry commits first, the completion is rejected as obsolete; if completion commits first, the expiry is a no-op.
- An attempt reports an explicit failure outcome instead of a result: the attempt is closed, and the Logical Job is retried while budget remains or fails permanently when exhausted.
- A Manager's claim succeeds but its transport acknowledgment is lost, so the work is redelivered: the redelivered claim is rejected as already claimed without consuming another attempt, and the delivery is acknowledged as obsolete.
- Some segment Logical Jobs for a required segment-scoped capability fail permanently: the match-level dependent is `Blocked`; partial input is never treated as a satisfied barrier.
- Publication retries are exhausted for a ready-work message while the transport is down for a long period: the message is `failed` but the Logical Job stays ready in durable state, and operator-initiated or startup reconciliation can republish it once the transport returns.
- The transport contains a ready-work message for a run whose terminal state is already recorded: the claim is rejected and no state changes.
- A completion references accepted upstream results that are not the ones recorded in the Logical Job's input snapshot: it is rejected as a lineage mismatch.

## Requirements *(mandatory)*

### Functional Requirements

#### Run creation and immutable snapshots

- **FR-001**: The system MUST create an Analysis Run only from a finalized-recording-set notification whose recording-set version, match, and team the Recordings capability's boundary confirms as finalized; it MUST NOT read Recordings-owned data directly.
- **FR-002**: The system MUST create at most one Analysis Run per finalized recording-set version and resolved workflow definition version, regardless of how many times or how concurrently the triggering notification is delivered.
- **FR-003**: The system MUST record an observable, sanitized rejection outcome, and create no Analysis Run, for a notification that references nonexistent, non-finalized, mismatched, or other-team lineage.
- **FR-004**: Each Analysis Run MUST durably record its team, match, finalized recording-set version, workflow identity and version, requested capability set, completion policy, state, and creation and transition timestamps.
- **FR-005**: At creation, the system MUST copy into an immutable run and node snapshot every resolved capability declaration, Analyst profile, image digest, model digest, policy, schema, resource, retry (attempt budget), lease, stale-timeout, and execution-timeout identity and value; later changes to those definitions MUST NOT alter any existing snapshot.
- **FR-006**: Any changed source input, Analyst profile, image, model, policy, capability, or contract version MUST result in an entirely new Analysis Run that recomputes every node; prior runs and accepted results MUST remain unchanged and MUST NOT be imported as completed nodes.
- **FR-007**: A persisted Analysis Run hierarchy (run, Workflow Nodes, dependency edges, Logical Jobs, Execution Attempts, accepted-result references) MUST reload with the same identities, values, ordering, and relationships that were committed.

#### Workflow graph validation

- **FR-008**: Before any Workflow Node becomes schedulable, the system MUST validate the resolved workflow graph and reject graphs that contain a cycle (including self-dependencies), an edge whose endpoint is not in the graph, an undeclared dependency kind, or an empty requested capability set.
- **FR-009**: A rejected graph MUST leave the Analysis Run in state `Failed` with an invalid-graph reason and MUST NOT create any schedulable Logical Job or outgoing ready-work message; no partially created graph may ever become visible.
- **FR-010**: Each dependency edge MUST be recorded as exactly one of `required`, `optional`, or `conditional`; a conditional edge MUST record its pinned predicate.

#### Logical Jobs

- **FR-011**: The system MUST create one Logical Job per segment for each segment-scoped capability and one Logical Job per match for each match-scoped capability, and every Logical Job of a capability MUST carry the same run-pinned Analyst profile, image digest, and model digest.
- **FR-012**: A segment-scoped Logical Job MUST record the finalized recording-set version and the complete analysis-segment identity (recording version, timeline-mapping digest, segmentation-policy digest, and segment number), accepted only after membership and match/team scope validation.
- **FR-013**: Logical Jobs MUST be hardware-neutral: they MUST NOT identify a Manager, host, container runtime product, accelerator model, or container-private preprocessing choice, and no in-container preprocessing step (such as spatial tiling) may become a Workflow Node or Logical Job.
- **FR-014**: A Logical Job's identity, pinned inputs, digests, resource requirements, and attempt budget MUST remain unchanged across all retries, including retries claimed by a different Manager.

#### Readiness and run outcome

- **FR-015**: The system MUST derive readiness solely from durable run, node, Logical Job, and accepted-result state at the pinned versions; transport contents, consumer positions, or queue depth MUST NOT influence readiness.
- **FR-016**: A Logical Job MUST become ready only when every applicable required dependency has an accepted result at every declared version; optional dependencies MUST never delay or block readiness; a conditional dependency MUST be treated as required when its predicate applies or cannot be determined and as optional otherwise.
- **FR-017**: When a Logical Job becomes ready, the system MUST record its input snapshot, including accepted upstream-result references and the absent, failed, or non-applicable status of every optional or conditional input.
- **FR-018**: A match-level Logical Job that depends on a segment-scoped capability MUST NOT become ready until accepted results exist for every required segment of that capability; partial segment coverage MUST NOT satisfy the barrier.
- **FR-019**: When a required upstream Logical Job fails permanently, the system MUST mark every dependent node `Blocked`, MUST NOT publish any blocked Logical Job, and MUST let independent branches continue.
- **FR-020**: The system MUST move a run from `Requested` to `Running` when its first Logical Job becomes ready and MUST terminate it as `Completed` when every node has accepted results, `PartiallyCompleted` when at least one required useful branch is accepted and another has permanently failed or is blocked, and `Failed` when no required useful result can be accepted or creation failed. The `Cancelled` state MUST be representable, but no transition into it is provided by this feature.

#### Execution Attempts and fencing

- **FR-021**: Each successful claim of a ready Logical Job MUST create exactly one Execution Attempt with the next consecutive positive attempt number, the claiming Manager's identity, a new lease, and a new opaque fencing token, and MUST consume one unit of the job's attempt budget; concurrent or duplicate claims MUST NOT create additional attempts or consume budget.
- **FR-022**: A claim for a Logical Job that is not currently ready (already claimed, completed, blocked, permanently failed, or referencing a superseded readiness occurrence) MUST be rejected as obsolete without creating an attempt or changing state.
- **FR-023**: A heartbeat MUST renew a lease only when it matches the current attempt, its fencing token, and the claiming Manager; any other heartbeat MUST be rejected as obsolete without changing state.
- **FR-024**: The platform, not the Manager, MUST detect lease expiry from durable state, including expiries that occurred while the platform was stopped; on expiry it MUST mark the attempt stale and invalidate its token in one durable step.
- **FR-025**: After a stale or explicitly failed attempt, the system MUST make the same Logical Job ready again only while attempt budget remains; on exhaustion, the Logical Job MUST fail permanently, MUST NOT be republished, and FR-019 and FR-020 MUST apply.

#### Completion and accepted results

- **FR-026**: The system MUST accept a completion only when it matches the current attempt, fencing token, and claiming Manager, the pinned Logical Job snapshot (scope, capability, schema, Analyst-profile, image, and model digests), the recorded input snapshot, and a supported completion contract version.
- **FR-027**: At most one completion MUST ever be accepted per Logical Job; acceptance MUST, in one durable step, close the attempt successfully, record one immutable accepted-result reference, and re-evaluate readiness for dependents.
- **FR-028**: An accepted-result reference MUST be immutable and MUST record the run, node, Logical Job, accepted attempt, result manifest reference and checksum, capability, schema, Analyst-profile, executed image and model digests, policy versions, and the accepted upstream-result lineage it consumed.
- **FR-029**: Replaying a completion with the same idempotency key and semantically identical content MUST return the original outcome and accepted-result identity without creating or advancing anything; reusing an idempotency key with different content MUST be rejected as a conflict.
- **FR-030**: A completion from a stale, superseded, fabricated, or wrong-attempt token, or one arriving after another attempt was accepted, MUST be rejected or reported obsolete and MUST NOT replace, alter, or supplement any accepted result.

#### Reliable publication and transport recovery

- **FR-031**: Every outgoing ready-work message or lifecycle notification MUST be recorded durably in the same atomic step as the state change that requires it; a rolled-back state change MUST leave no outgoing message, and repeated processing of the same change MUST converge on one message with one stable identity.
- **FR-032**: The system MUST publish recorded messages to the message transport at least once with stable message identities, record each acknowledged publication durably, and resume all unpublished eligible messages after publisher or platform restart without re-running the originating change.
- **FR-033**: Publication MUST follow an explicitly configured finite retry policy; on exhaustion the message MUST become observably `failed` and be retained, and workflow state MUST remain unchanged and authoritative.
- **FR-034**: Outgoing messages MUST be removable by retention only after their publication outcome is known and no reconciliation is pending; pending and failed messages MUST be retained.
- **FR-035**: Reconciliation at startup, after transport restoration, or on operator request MUST republish every currently eligible ready Logical Job and pending notification with its stable identity from durable state, and MUST NOT publish completed, blocked, exhausted, already-claimed, or otherwise ineligible work.
- **FR-036**: Every consumer of analysis-related messages MUST deduplicate by stable message identity and MUST validate referenced identities, scope, versions, and current state against durable state before changing anything; possession of a transport message MUST confer no authority.
- **FR-037**: The job contract MUST state that a Manager acknowledges a ready-work delivery only after the platform has returned a successful or deterministically obsolete claim outcome.

#### Contracts

- **FR-038**: The system MUST define versioned machine-readable contracts for the hardware-neutral job payload and the fenced completion payload in the repository's canonical contract location, each with a stable major-version identity, an exact version, offline-resolvable references, and valid and invalid examples.
- **FR-039**: The job contract MUST carry the contract version, message identity, run, node, Logical Job, and readiness-occurrence identities, team, match, capability and schema versions, execution scope, source and accepted-upstream-result lineage, pinned Analyst-profile, image, and model digests, resource requirements, attempt budget, lease and timeout values, and correlation context, and MUST NOT carry a stamp or club discriminator, Manager, host, runtime product, accelerator model, or preprocessing choice.
- **FR-040**: The completion contract MUST carry the contract version, idempotency key, team, match, run, node, Logical Job, attempt identity and number, fencing token, outcome, result manifest reference and checksum, capability and schema versions, the executed runtime description, executed image and model digests, accepted upstream-result lineage, and correlation context.
- **FR-041**: Contract payloads MUST follow the repository's representation conventions (opaque identifiers, UTC timestamps, non-negative second durations, positive fencing tokens, algorithm-qualified digests) and MUST exclude credentials, expiring access URLs, transport connection details, media bytes, and unrestricted logs.
- **FR-042**: Contract validation MUST run offline and deterministically and MUST reject, within the same major version, removed or renamed fields, newly required inputs, narrowed accepted values, or changed completion semantics; the platform MUST validate inbound completions and outbound jobs against the same contracts.

#### Observability

- **FR-043**: The system MUST expose, per outgoing message, its pending, published, or failed outcome, attempt count, last attempt time, next eligible attempt, sanitized failure category, age, and publication lag, and MUST expose aggregate counts of blocked runs, lease expiries, rejected stale completions, and rejected notifications.
- **FR-044**: Workflow signals MUST be correlatable by analysis run, Logical Job, Execution Attempt, Manager, and capability where applicable, and MUST omit secrets, fencing tokens, media, access URLs, and unnecessary personal or team data.

### Key Entities *(include if feature involves data)*

- **Analysis Run**: One durable execution of a versioned full-match capability graph for one finalized recording-set version. Holds team, match, recording-set version, workflow identity and version, requested capability set, completion policy, state (`Requested`, `Running`, `PartiallyCompleted`, `Completed`, `Failed`, `Cancelled`), timestamps, and its immutable run snapshot.
- **Run snapshot / node snapshot**: Immutable copies, taken at run creation, of the resolved workflow definition and of each node's capability declaration, Analyst profile, image and model digests, policies, schemas, resources, attempt budget, lease, stale-timeout, and execution-timeout values.
- **Workflow Node**: One capability in a run's graph, with its tier, execution scope (segment or match), required inputs, node snapshot, and node state including `Blocked`.
- **Dependency edge**: A run-local directed link from an upstream node to a dependent node, typed `required`, `optional`, or `conditional` (with its pinned predicate).
- **Logical Job**: One schedulable, hardware-neutral realization of a Workflow Node for one segment or for the whole match, with a stable identity across retries, pinned inputs, attempt budget, recorded input snapshot, and state (waiting, ready, claimed, completed, failed, blocked).
- **Execution Attempt**: One numbered, leased execution of a Logical Job by one Analyst Manager, with its lease, opaque fencing token, state (active, stale, failed, accepted, rejected), and timestamps.
- **Accepted result reference**: The single immutable record of the accepted completion for a Logical Job, linking run, node, job, attempt, manifest reference and checksum, executed digests, versions, and consumed upstream lineage.
- **Published work message**: A durably recorded outgoing ready-work message or lifecycle notification with a stable identity, the state change that caused it, and its publication outcome, attempts, age, and lag.
- **Notification receipt**: The durable record that an inbound message (such as a finalized-recording-set notification) was processed, with its processing outcome, used for deduplication and rejection visibility.
- **Idempotency outcome**: The durable record of a completion's idempotency key, its canonical request identity, and the outcome returned for replays.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Across at least 20 forced platform restarts injected at distinct lifecycle points (run creation, readiness evaluation, claim, lease expiry, completion, publication), 100% of runs reach the same terminal state and the same set of accepted results as an uninterrupted reference execution, with zero lost and zero duplicated accepted results.
- **SC-002**: When every finalized notification and every ready-work message is delivered at least twice, zero duplicate Analysis Runs, Execution Attempts beyond successful claims, accepted results, or downstream messages are created.
- **SC-003**: 100% of completions and heartbeats carrying stale, superseded, fabricated, wrong-attempt, or wrong-Manager fencing context are rejected, and none changes workflow state or accepted history.
- **SC-004**: 100% of invalid workflow graphs in the validation fixture set (direct cycle, indirect cycle, self-dependency, dangling edge, empty capability set) are rejected with zero schedulable Logical Jobs and zero published ready work.
- **SC-005**: Across the readiness fixture set covering required, optional, conditional, barrier, and failure-propagation cases, 100% of evaluated ready, waiting, and blocked sets and terminal run states match the expected outcomes, and zero blocked Logical Jobs are ever published.
- **SC-006**: Under concurrent claim tests with at least 10 simultaneous claimants per Logical Job, every Logical Job's attempt numbers are consecutive from 1 with no gaps or duplicates, and no Logical Job ever exceeds its attempt budget.
- **SC-007**: With the message transport unavailable or emptied, workflow recovery completes from durable state alone; after the transport becomes available, 100% of currently eligible ready work is republished without manual data repair and 0% of ineligible work is published.
- **SC-008**: For 100% of outgoing messages, an operator can determine outcome, attempt count, age, and publication lag, and can list every unpublished or failed message, without inspecting transport contents.
- **SC-009**: Offline contract validation produces identical outcomes on repeated runs; 100% of valid examples pass and 100% of invalid examples (hardware-specific fields, secrets, missing fencing or lineage, in-major breaking changes) fail.
- **SC-010**: After changing any definition captured in a run snapshot, 0 existing run or node snapshots differ from their committed values.

## Assumptions

- **Trigger and actors**: The primary trigger is a Coach finalizing a recording set (specified by `specs/20261005-130702-recording-lineage-upload`); every validated finalized recording-set version triggers one run of the stamp's default full-match workflow. A user-facing explicit analysis-request operation for Coaches or Club Admins, and upload-policy configuration that suppresses automatic runs, are deferred.
- **Dependency on recording lineage and upload**: This feature consumes the finalized recording-set lineage boundary and the canonical finalized-recording-set notification from `specs/20261005-130702-recording-lineage-upload`, never Recordings-owned data. Publishing that notification is owned by that feature; until it is available end to end, this feature is exercised with canonical injected notifications validated against its boundary.
- **Dependency on the persistence foundation**: Durable storage, data organization, migrations, explicit transactions, and optimistic concurrency come from `specs/20261005-130700-platform-persistence-foundation` and are not duplicated here.
- **Relationship to Analyst Manager registration**: Analyst Manager registration and authentication (`specs/20261005-130704-analyst-manager-registration`) are a sibling foundation. Claim, heartbeat, and completion are specified here as platform behaviors that record and check the Manager identity; exposing them as authenticated Manager-facing operations, and fencing attempts on Manager revocation, follow from that feature and later slices.
- **Workflow definitions and segments as inputs**: Workflow definitions, capability declarations, and Analyst profiles are resolved through the owning Registry boundary; importing Analyst manifests into that registry is out of scope, and representative workflow definitions may be supplied as fixtures. Analysis-segment identities are supplied through the owning segment boundary; segment encoding and materialization are out of scope.
- **Acceptance depth**: Completion acceptance validates fencing, identity, pinned digests, contract shape, and declared lineage. Reading and validating result-manifest contents from object storage, fact-window checks, indexing accepted facts, and the capability-scoped result query surface for high-level Analysts are deferred.
- **Optional input timing**: A Logical Job's optional and conditional inputs are fixed when it becomes ready; a later-accepted optional upstream result does not change that snapshot.
- **Indeterminate conditional predicates** are treated as applicable (required), the conservative default.
- **Out of scope / deferred**: Analyst Manager and Analyst container execution; segment encoding or materialization; model inference; any Analyst capability implementation; capability-permitted partial-coverage barriers (all segment barriers are treated as complete-coverage); run cancellation, supersession, and the queued-work revocation they require (the `Cancelled` state is representable only); descendant-only recomputation and cross-run result reuse; production transport stream, subject, and consumer values; production retry counts and intervals, lease and heartbeat values, backpressure and admission thresholds; production retention periods, objectives, and alert values; production credentials and identity for the transport; client UI; deployment configuration.
- **Development defaults**: Publication retry limits, lease, heartbeat, and stale-timeout values used in development and tests are explicit configuration, not adopted production values.
- **Contract timing**: The architecture places machine-readable job and completion contracts in "the first Scheduler and Analyst implementation slice"; this feature introduces them ahead of Analyst execution, limited to architecture-fixed identities and invariants, with additive same-major evolution expected. Contract completion still requires the multi-owner review the architecture defines.
- **Evidence**: Behavior is complete only with executable evidence, including restart recovery with the transport empty or unavailable; local success is not production readiness.
- **Architecture References**: `docs/architecture/job-processing.md`, `docs/architecture/analyst-runtime-and-recovery.md`, `docs/architecture/contracts-and-compatibility.md`, `docs/architecture/analysts-models-and-hardware.md`, `docs/architecture/terminology-and-principles.md`, `docs/architecture/platform-implementation.md`, `docs/architecture/production-operations.md`.
