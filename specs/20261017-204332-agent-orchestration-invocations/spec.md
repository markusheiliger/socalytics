# Feature Specification: Agent Orchestration Direct Invocations

**Feature Branch**: `20261017-204332-agent-orchestration-invocations`

**Created**: 2026-10-17

**Status**: Draft

**Input**: User description: "Agent Orchestration Direct Invocations: the architecture makes the platform's Agent Orchestration functional area the authoritative owner of agent conversations, logical invocations, execution attempts, accepted advice, and lineage (docs/architecture/intelligence-and-agents.md#agent-orchestration-authority, #durable-state-and-transaction-boundaries, #client-transport-and-runtime-protocol, #authorization-lineage-and-lifecycle, #post-analysis-match-triage), with Hermes as a separate runtime that claims work through versioned API operations; nothing of it is implemented (docs/architecture/platform-implementation.md#agent-runtime). This feature implements the platform side for direct conversations, so agent work can be submitted, executed by a workload, and read back with full lineage, proven with a simulated agent workload that exists only in Development and Test (like the simulated Analyst). Member side (`/api/v1`, BFF session, anti-forgery): an authorized member (Club Admin, or Coach of the owning team; the spec decides Viewer read access per the architecture) creates a triage conversation bound to one match and one terminal `Completed` or `PartiallyCompleted` analysis run, adds participants chosen from the agent catalog's identifiers (docs/architecture/intelligence-agent-catalog.md; the first wave are direct specialists such as `specialist-tactics` and `specialist-video-analysis`), and submits a direct User→Agent invocation with an idempotency key, receiving `202 Accepted` with the conversation and invocation identifiers and a status location; equivalent repeats return the existing invocation and conflicting key reuse fails. Members read conversation history, invocation status, accepted advice, and lineage through conditional polling (unchanged representations return the protocol's unchanged response), with authorization re-evaluated on every read. Workload side: a stamp-local workload identity (the plan decides the authentication, reusing the platform's existing machine-authentication patterns where possible) claims queued invocations and receives a bounded execution envelope, `attempt_id`, lease, and fencing token; it renews the lease, checkpoints, and completes or fails an attempt; only the current attempt and token can mutate active state, stale/duplicate/late operations get explicit stale dispositions, expired leases fence the attempt and create a new attempt within versioned retry limits, and scope loss (revoked team access, superseded or cancelled run where the architecture says so) invalidates the attempt and fails closed. State follows the architecture's invocation and attempt states; PostgreSQL is authority, a transactional outbox publishes minimal work notifications (identifiers only, no content) to NATS JetStream, and reconciliation republishes missing notifications. Security audit events cover submissions, claims, completions, scope-loss outcomes. Out of scope: CA→SA delegated consultations and Coach Agents (later waves), Hermes itself, real models and providers, prompts, tool policies and safety-policy artifacts, MCP tools, the Web UI (spec 4), lifecycle suppression/tombstones beyond what direct conversations need, production values. Depends on Durable Analysis Workflow, Club and Identity Foundation, Analyst Manager Registration (machine-authentication patterns), Platform Persistence Foundation (merged; archived under specs/archive/), and reads specs/archive/20261017-175648-fact-indexing-queries only for context. Implements the platform part of docs/architecture/intelligence-and-agents.md (sections named above) and docs/architecture/platform-implementation.md#agent-runtime."

## Clarifications

### Session 2026-10-17

- Q: Who may read a conversation, its history, invocations, and lineage? → A: Only the member who created it, while active and holding Club Admin or a Coach or Viewer role on the owning team; no other member, including Club Admins and other team members, has ordinary read access.
- Q: What happens to a conversation when its analysis run is superseded by a newer run? → A: Nothing changes: it stays bound to its run and usable, and the conversation read shows that the run is no longer the match's current run.
- Q: Should the number of unfinished invocations be limited? → A: Yes: each owner may have a configured number of queued or running invocations at a time (development value 5); further submissions are refused with "too many unfinished invocations".
- Q: Which earlier turns does the execution envelope carry? → A: The conversation's earlier succeeded turns of all participants, newest first up to the configured bound, each attributed to its agent.
- Q: Should operators see agent orchestration activity in the platform's telemetry? → A: Yes: counters of submissions, claims, completions, failures, lease expiries, and scope-loss outcomes, and the number of queued invocations, without any conversation content.
- Q: How do members find the agents they can add to a conversation? → A: Through a read of the agents enabled on the stamp (identifier, group, and domain), available to every active member with a team role.

## User Scenarios & Testing *(mandatory)*

The actors are members of a team with an analysed match ([Terminology](../../docs/architecture/terminology-and-principles.md#terminology)) and the agent runtime, a stamp-local workload that executes agent work ([Intelligence and Agents → Client, Transport, And Runtime Protocol](../../docs/architecture/intelligence-and-agents.md#client-transport-and-runtime-protocol)). The platform's Agent Orchestration functional area becomes the durable authority for triage conversations, logical invocations, execution attempts, accepted advice, and lineage, as [Agent Orchestration Authority](../../docs/architecture/intelligence-and-agents.md#agent-orchestration-authority) and [Durable State And Transaction Boundaries](../../docs/architecture/intelligence-and-agents.md#durable-state-and-transaction-boundaries) define. This feature delivers the direct `User -> SA` path of [Post-Analysis Match Triage](../../docs/architecture/intelligence-and-agents.md#post-analysis-match-triage) and the first catalog wave ([Intelligence Agent Catalog → Implementation Waves](../../docs/architecture/intelligence-agent-catalog.md#implementation-waves)). No real agent runtime, model, prompt, or evidence tool exists yet, so a simulated agent workload, available only in Development and Test, executes the work and proves the protocol, as the simulated Analyst does for analysis work ([Analyst Manager Work Execution](../archive/20261012-141233-manager-work-execution/spec.md)).

### User Story 1 - Ask a specialist about an analysed match and read the advice (Priority: P1)

A Coach opens a match whose analysis run has ended as `Completed` or `PartiallyCompleted`, starts a triage conversation bound to that match and run, adds the tactics specialist as a participant, and asks it a question. The platform accepts the question at once with the conversation and invocation identifiers and a status location. The agent workload picks the work up, executes it, and completes it; the Coach polls the status location and reads the accepted advice in the conversation history.

**Why this priority**: It is the feature: without submission, execution, and read-back nothing else has value, and it proves the whole path from member to workload and back.

**Independent Test**: As a Coach of the owning team, create a conversation for a completed run, add `specialist-tactics`, submit a question, let the simulated agent workload execute it, and confirm that polling the status location reports the invocation as succeeded and that the conversation history shows the question followed by exactly one accepted advice attributed to `specialist-tactics`.

**Acceptance Scenarios**:

1. **Given** a match with a `Completed` or `PartiallyCompleted` run, **When** a Club Admin or a Coach of the owning team creates a conversation for that match and run, **Then** an active conversation owned by that member and bound to that match and run is created.
2. **Given** a conversation, **When** its owner adds an available specialist from the agent catalog as a participant, **Then** the participant appears in the conversation and can be addressed.
3. **Given** a conversation with a participant, **When** its owner submits a question to that participant with an idempotency key, **Then** the platform answers at once that the request is accepted, with the conversation and invocation identifiers, a status location, and a suggested polling interval, and the invocation is queued.
4. **Given** a queued invocation, **When** the agent workload claims it, executes it, and completes it with advice, **Then** the invocation has succeeded, the advice is its one accepted advice, and the conversation history shows the question and the advice in order.
5. **Given** a status location, **When** the owner polls it without a change since the last read, **Then** the platform answers that nothing changed, and polling never creates another invocation or attempt.

---

### User Story 2 - Reliable execution through claims, leases, fencing, and retries (Priority: P2)

The agent workload claims queued invocations and receives everything it needs for one execution attempt: a bounded execution envelope, the attempt identifier, a lease, and a fencing token. It renews the lease while it works, records checkpoints, and completes or fails the attempt. When a workload stops or loses its lease, the platform fences the attempt and makes the invocation available again for a new attempt within the invocation's retry limits; late, duplicate, or stale calls never change accepted state.

**Why this priority**: Agent work is long-running and runtimes restart; without durable claims and fencing, advice could be lost, duplicated, or written by a stale runtime. It builds on Story 1.

**Independent Test**: With the simulated agent workload, let one attempt's lease expire and confirm that the attempt is recorded as expired, a second attempt completes the same invocation, and every later call with the first attempt's token gets a stale answer; exhaust the retry limit and confirm that the invocation fails; repeat a claim, checkpoint, and completion with the same operation identifiers and confirm that each returns the original outcome.

**Acceptance Scenarios**:

1. **Given** a queued invocation, **When** the workload claims it, **Then** it receives the execution envelope, a new attempt identifier, the lease expiry, and an opaque fencing token, and the invocation is running.
2. **Given** an active attempt, **When** the workload renews its lease or records a checkpoint with the current fencing token, **Then** the lease is extended or the checkpoint is recorded in order in the invocation's lineage.
3. **Given** an attempt whose lease expired, **When** the platform detects the expiry, **Then** the attempt ends as expired and fenced, its checkpoints stay in the lineage, and the invocation is queued again with a new work notification while attempts remain, or fails with reason "attempts exhausted" otherwise.
4. **Given** an attempt that was expired, failed, or replaced, **When** a renewal, checkpoint, completion, or failure arrives with its token, **Then** it is answered with an explicit stale disposition and nothing changes.
5. **Given** a claim, checkpoint, completion, or failure already processed, **When** it is repeated with the same operation identifier and equivalent content, **Then** the original outcome is returned without another change; repeated with different content, it is refused as a conflict.
6. **Given** an active attempt, **When** the workload reports a retryable failure, **Then** the attempt ends as failed and the invocation is queued again while attempts remain; **When** it reports a non-retryable failure, **Then** the invocation fails.

---

### User Story 3 - Lineage, privacy, and fail-closed authorization (Priority: P3)

Every invocation keeps an ordered, append-only lineage of its submission, attempts, checkpoints, failures, declared execution references, and accepted advice, which its owner can read. Conversations are private to the member who created them. Authorization is re-evaluated at every read and every workload operation; when the owner loses access to the team, active work is fenced and no advice is disclosed or accepted.

**Why this priority**: Advice is only trustworthy with lineage, and agent conversations are sensitive data that must stay private and fail closed. The core flow works without it, but it must not ship without it.

**Independent Test**: Complete an invocation with checkpoints and a retry and read its lineage; read the conversation as another Coach, a Viewer, and another Club Admin of the same team and confirm that it is not found; remove the owner's Coach role while an attempt is active and confirm that the attempt is fenced, the invocation fails with reason "scope lost", the workload's later calls are refused, and the audit trail records the outcome.

**Acceptance Scenarios**:

1. **Given** a succeeded invocation, **When** its owner reads its lineage, **Then** it lists in order the submission, every attempt with its outcome, every checkpoint, every reported failure, the declared execution references, and exactly one accepted advice.
2. **Given** a conversation, **When** any member other than its owner reads it, its history, an invocation, or a lineage, including Club Admins and members of the owning team, **Then** the answer is not found and nothing about the conversation is disclosed.
3. **Given** a conversation owner who no longer holds Club Admin or a Coach or Viewer role on the owning team, or is deactivated, **When** they read the conversation, **Then** the answer is not found.
4. **Given** an active attempt, **When** the owner loses the right to submit for the team (Club Admin or Coach) or is deactivated, **Then** at the next workload operation or monitor check the attempt is fenced, the invocation fails with reason "scope lost", partial output is rejected, and later workload calls for it get a stale disposition.
5. **Given** a queued invocation whose owner lost the right to submit, **When** the workload claims it, **Then** the claim is refused, the invocation fails with reason "scope lost", and no attempt starts.
6. **Given** submissions, claims, completions, failures, expiries, and scope-loss outcomes, **When** the audit trail is inspected, **Then** each has a security audit event with its actor or workload, conversation, invocation, attempt where one exists, and outcome, and no audit event, log, telemetry record, or work notification contains question text, advice text, or checkpoint notes.

---

### Edge Cases

- **Run superseded after the conversation was created**: The conversation stays bound to its run and usable; supersession neither changes nor fails it ([Post-Analysis Match Triage](../../docs/architecture/intelligence-and-agents.md#post-analysis-match-triage)), and the conversation read shows that the run is no longer the match's current run. Analysis on a newer run needs a new conversation.
- **Too many unfinished invocations**: An owner who already has the configured number of queued or running invocations is refused with "too many unfinished invocations" until one ends.
- **Run not terminal-successful**: Conversations can only be created for runs in `Completed` or `PartiallyCompleted`; `Requested`, `Running`, `Failed`, and `Cancelled` runs are refused. Because those two states never change, a conversation's run cannot later become cancelled or failed.
- **Run of another match**: Creating a conversation with a run that does not belong to the match is refused.
- **Agent not available**: Adding a Coach Agent, an unknown identifier, or a specialist not enabled on the stamp is refused with "agent not available".
- **Addressing a non-participant**: Submitting to an agent that is not a participant of the conversation is refused.
- **Concurrent submissions**: An owner may have several invocations in one conversation; each is independent and ordered in the history by submission time.
- **Duplicate work notifications**: Notifications are delivered at least once; a duplicate leads to a claim that is refused as obsolete or returns the existing attempt, and never to a second active attempt.
- **Lost notification**: If a notification was not published or was lost, reconciliation publishes it again for every queued invocation, so no queued invocation waits forever.
- **Workload completes after the lease expired**: The completion gets a stale disposition, and the advice of the replacing attempt or the failure stands.
- **Owner keeps only a Viewer role**: The owner can still read the conversation, but new submissions are refused and active attempts are fenced as scope lost.
- **Workload identity disabled**: Its tokens stop working at once; its active attempts expire through their leases and are retried by another workload identity or fail.

## Requirements *(mandatory)*

### Functional Requirements

#### Conversations and participants

- **FR-001**: Club Admins and Coaches of the team that owns a match MUST be able to create a triage conversation bound to that match and one of its analysis runs in state `Completed` or `PartiallyCompleted`; Viewers, Registrars without a team role, and other members MUST be refused, and the conversation creation MUST require an idempotency key under the platform's existing idempotency rules.
- **FR-002**: Creating a conversation for a run that is not `Completed` or `PartiallyCompleted`, or that does not belong to the match, MUST be refused with a reason, without any change.
- **FR-003**: A conversation MUST be owned by the member who created it, MUST stay bound to its match and run for its whole life, and MUST stay `Active`; supersession of its run MUST NOT change it.
- **FR-004**: The conversation owner MUST be able to add participants chosen from the agent catalog's Specialist Agent identifiers that are enabled on the stamp; a Coach Agent, an unknown identifier, a disabled specialist, or a participant already present MUST be refused, and adding MUST be conditional on the conversation version the owner last saw.
- **FR-005**: The stamp MUST configure which Specialist Agent identifiers are enabled; the Development and Test configuration MUST enable at least `specialist-tactics` and `specialist-video-analysis`. Every active member with Club Admin or a team role MUST be able to read the enabled agents with their identifier, group, and domain.

#### Submission

- **FR-006**: The conversation owner MUST be able to submit a direct question to one participant of the conversation with a required idempotency key scoped to the owner, the conversation, and the submit operation; the question MUST be plain text of bounded length.
- **FR-007**: A valid submission MUST, in one atomic step, create one logical invocation in state `accepted/queued` with its route (`User -> SA`), addressed agent, question, owner, team, match, run, conversation, correlation identifier, and the execution policy version that defines its retry limits, and write one work notification into the transactional outbox.
- **FR-008**: The submission MUST be answered as accepted with the conversation and invocation identifiers, the status location, and a suggested polling interval; a repeated submission with the same key and equivalent content MUST return the existing invocation unchanged, and a repeated key with different content MUST be refused as a conflict without changing it.
- **FR-009**: Submissions MUST be refused when the owner does not currently hold Club Admin or a Coach role on the owning team, when the addressed agent is not a participant or no longer enabled, when the question is empty or too long, or, with "too many unfinished invocations", when the owner already has the configured number of queued or running invocations (development value 5).

#### Member reads

- **FR-010**: The conversation owner MUST be able to read the conversation (match, run, whether the run is still the match's current run, participants, state, version), its history in submission order (each question with its agent, invocation state, and accepted advice once succeeded), each invocation's status (state, attempt count, failure reason once failed, suggested polling interval), and each invocation's lineage.
- **FR-011**: The owner MUST be able to list their own conversations for a match, newest first.
- **FR-012**: Every read MUST support conditional requests so that an unchanged representation is answered as unchanged, and polling MUST never create or change an invocation or attempt.
- **FR-013**: Every read MUST re-evaluate authorization: only the owner, while active and holding Club Admin or a Coach or Viewer role on the owning team, may read; every other member, including Club Admins and other team members, MUST receive the not-found answer that discloses nothing. Purpose-limited governance review stays outside this feature.

#### Workload execution

- **FR-014**: The agent workload MUST authenticate as a stamp-local workload identity configured by the stamp, with short-lived, sender-constrained credentials following the platform's existing machine-authentication pattern for Analyst Managers; member sessions and Analyst Manager credentials MUST NOT be accepted for workload operations, and workload credentials MUST NOT be accepted for member or Analyst Manager operations.
- **FR-015**: The workload MUST receive work notifications that carry only identifiers, route and schema version, and correlation data, and MUST be able to claim a queued invocation with an operation identifier; a successful claim MUST, in one atomic step, create a new attempt with a new attempt identifier, a bounded lease, and an opaque fencing token, move the invocation to `claimed/running` and the attempt to `active`, and return the bounded execution envelope.
- **FR-016**: The execution envelope MUST contain only what the attempt needs: invocation, conversation, attempt, and correlation identifiers, the route, the addressed agent, the question, the conversation's earlier succeeded turns of all participants (question and accepted advice, each attributed to its agent), newest first up to a configured bound, the team, match, and run identifiers, the lease, and the execution limits; it MUST conform to a versioned machine-readable contract.
- **FR-017**: A claim MUST be refused as obsolete when the invocation is not queued, already has an unexpired active attempt, or has no attempts left, so that duplicate notifications never create a second active attempt.
- **FR-018**: The workload MUST be able to renew the lease of its active attempt and record checkpoints with an operation identifier; each checkpoint MUST be appended in order to the invocation's lineage with a bounded kind and note.
- **FR-019**: The workload MUST be able to complete an active attempt with an operation identifier, advice text of bounded length, and the declared execution references (runtime, model provider, model, deployment or configuration, and prompt, tool-policy, and safety-policy references as opaque declared strings); completion MUST, in one atomic step, record exactly one accepted advice, end the attempt as `completed`, end the invocation as `succeeded`, and append the outcome to the lineage.
- **FR-020**: The workload MUST be able to fail an active attempt with an operation identifier, a bounded reason code, and whether the failure is retryable; the attempt MUST end as `failed`, and the invocation MUST be queued again with a new work notification when the failure is retryable and attempts remain, or end as `failed` otherwise.
- **FR-021**: Only the current attempt with its current fencing token and an unexpired lease MUST be able to renew, checkpoint, complete, or fail; every other call MUST receive an explicit stale disposition naming why (attempt not current, lease expired, attempt fenced, invocation ended, scope lost) and change nothing.
- **FR-022**: Repeating a claim, checkpoint, completion, or failure with the same operation identifier and equivalent content MUST return the original outcome without another change; reusing an operation identifier with different content MUST be refused as a conflict.
- **FR-023**: The platform MUST detect expired leases periodically; an expired attempt MUST end as `expired` and its token fenced, its checkpoints MUST stay in the lineage, and the invocation MUST be queued again with a new work notification while attempts remain, or end as `failed` with reason "attempts exhausted".
- **FR-024**: Each attempt MUST be bounded by a maximum duration regardless of renewals; an attempt that reaches it MUST be ended and handled like an expired lease.
- **FR-025**: The maximum attempts per invocation, the lease duration, the maximum attempt duration, the history bound of the envelope, the advice and question length limits, and the per-owner limit of unfinished invocations MUST be explicit configuration recorded as a versioned execution policy on each invocation, with development and test values; production values remain open.

#### Scope loss

- **FR-026**: The platform MUST re-evaluate the owner's authorization to submit (active membership and Club Admin or a Coach role on the owning team) at claim, renewal, checkpoint, completion, and failure, and in a periodic check of active attempts.
- **FR-027**: When a re-evaluation detects scope loss for a queued invocation, the claim MUST be refused and the invocation MUST end as `failed` with reason "scope lost"; for an active attempt, the attempt MUST end as `fenced`, its token MUST stop authorizing anything, any partial or final output MUST be rejected, and the invocation MUST end as `failed` with reason "scope lost". A scope-lost invocation MUST never be retried.

#### Durability and transport

- **FR-028**: Conversations, participants, invocations, attempts, checkpoints, accepted advice, and lineage MUST be stored in the stamp database as the only authority; work notifications MUST be published from the transactional outbox at least once, and the transport MUST never carry question text, advice text, checkpoint notes, or other conversation content.
- **FR-029**: Reconciliation MUST republish a work notification for every queued invocation whose notification is missing or older than a configured bound, idempotently, so that no queued invocation depends on a single delivery.
- **FR-030**: Every state change MUST check the expected record version and allowed prior state, so that concurrent claims, completions, expiries, and scope-loss outcomes leave exactly one consistent result.

#### Simulated agent workload

- **FR-031**: The platform MUST provide a simulated agent workload that runs only in Development and Test, refuses to start in any other environment, authenticates as a configured workload identity, claims notified invocations, records at least one checkpoint, and completes them with deterministic simulated advice that names itself as simulated and declares `simulated` execution references.
- **FR-032**: The simulated agent workload MUST support test-controlled behaviors for failing retryably, failing non-retryably, and stopping without renewing, so that retries, failures, and lease expiry can be proven end to end.
- **FR-033**: The local composition of the platform MUST run the simulated agent workload alongside the API, so that conversations can be exercised locally and by later Web UI browser tests.

#### Audit, privacy, and interfaces

- **FR-034**: The platform MUST record security audit events for conversation creation, participant addition, submission (accepted, replayed, and refused), claim (granted and refused), completion, failure, lease expiry, attempts exhaustion, and scope-loss outcomes, with the acting member or workload identity, conversation, invocation, attempt where one exists, and outcome; question text, advice text, and checkpoint notes MUST NOT appear in audit events, logs, telemetry, or work notifications.
- **FR-034a**: The platform MUST emit telemetry counters of submissions, claims, completions, failures, lease expiries, and scope-loss outcomes, and the number of queued invocations, through its existing telemetry, without conversation content.
- **FR-035**: Member operations MUST be authenticated `/api/v1` operations with the existing session, anti-forgery, conditional-request, and problem-details rules, and workload operations MUST be versioned `/api/v1` operations restricted to the workload identity; all MUST appear in the platform's OpenAPI document with typed request and response schemas, and the committed OpenAPI snapshot MUST be refreshed.
- **FR-036**: The work notification, execution envelope, and completion contracts MUST be versioned machine-readable contracts in the repository's contract catalog with golden fixtures validated by the contract tests.
- **FR-037**: Automated tests MUST cover conversation creation and refusals, participant rules, idempotent and conflicting submission, private and conditional reads, claims, renewals, checkpoints, completion, retryable and non-retryable failures, lease expiry and retry exhaustion, stale and duplicate workload calls, concurrent claims and completions, scope loss at claim and during an attempt, outbox publication and reconciliation, workload authentication boundaries, audit content, and the simulated agent workload end to end.

### Key Entities

- **Triage conversation**: A user-owned conversation bound to one match and one `Completed` or `PartiallyCompleted` analysis run of that match; it has an owner, team, state `Active`, version, and participants. Conversation data is Sensitive agent state under `DAT-008` ([Security and Data Governance](../../docs/architecture/security-and-data-governance.md)).
- **Participant**: A Specialist Agent identifier from the agent catalog added to a conversation, with who added it and when.
- **Logical invocation**: One direct question from the owner to one participant, with its idempotency key, route, question, correlation identifier, execution policy version, state (`accepted/queued`, `claimed/running`, `succeeded`, `failed`), attempt count, current attempt, and failure reason; its identity stays stable across retries.
- **Execution attempt**: One try of an invocation by a workload identity, with attempt number, fencing token, lease expiry, start and end times, and state (`claimed`, `active`, `completed`, `failed`, `expired`, `fenced`); earlier attempts are never erased by a retry.
- **Checkpoint**: An ordered progress entry of an attempt with operation identifier, kind, and bounded note.
- **Accepted advice**: The single accepted response of a succeeded invocation, with its text, the attempt that produced it, and the declared execution references.
- **Lineage entry**: An append-only, ordered record of an invocation's submission, claims, checkpoints, failures, expiries, scope-loss outcomes, and accepted advice.
- **Agent workload identity**: A stamp-configured identity of an agent runtime that may call the workload operations.
- **Work notification**: A transport message with only identifiers, route, schema version, and correlation data, published from the transactional outbox.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: With the simulated agent workload running locally, a member can create a conversation, add a specialist, submit a question, and read the accepted advice within 30 seconds of submission.
- **SC-002**: In automated tests, 100% of submissions with a reused key and equivalent content return the original invocation, and 0 submissions, polls, or duplicate notifications create a second invocation or a second concurrently active attempt.
- **SC-003**: In automated tests, 0 stale, duplicate, or late workload calls change an invocation, attempt, or accepted advice, and every succeeded invocation has exactly one accepted advice.
- **SC-004**: In automated tests, every invocation whose attempt stopped renewing is retried or failed within one lease duration plus one monitor interval, and no invocation stays queued without a published notification longer than the republish bound plus one reconciliation interval.
- **SC-005**: In automated tests, 100% of reads by members other than the owner are answered as not found, and every attempt whose owner lost scope is fenced before any further output is accepted.
- **SC-006**: 0 audit events, logs, telemetry records, or work notifications in the tests contain question text, advice text, or checkpoint notes, and every submission, claim, completion, failure, expiry, and scope-loss outcome has an audit event.

## Assumptions

- **Dependencies**: [Durable Analysis Workflow](../archive/20261005-130703-durable-analysis-workflow/spec.md) (analysis runs and their terminal states, transactional outbox, NATS JetStream publication, reconciliation, the claim, lease, and fencing pattern), [Club and Identity Foundation](../archive/20261005-130701-club-identity-foundation/spec.md) (members, team roles, team-scoped authorization, sessions, anti-forgery, audit), [Analyst Manager Registration](../archive/20261005-130704-analyst-manager-registration/spec.md) (the machine-authentication pattern the workload identity follows), [Analyst Manager Work Execution](../archive/20261012-141233-manager-work-execution/spec.md) (scoped queue credentials and the simulated-runtime precedent), and [Platform Persistence Foundation](../archive/20261005-130700-platform-persistence-foundation/spec.md) (units of work, optimistic concurrency, migrations). [Accepted Fact Indexing and Capability-Scoped Fact Queries](../archive/20261017-175648-fact-indexing-queries/spec.md) is context only: agents read evidence through later MCP tools, not through this feature.
- **Direct specialists only**: The first catalog wave enables direct `User -> SA` conversations ([Implementation Waves](../../docs/architecture/intelligence-agent-catalog.md#implementation-waves)); the invocation states `waiting-for-consultations` and `synthesizing`, consultations, and Coach Agents come with later waves.
- **Execution references are declared**: Prompt, model, tool-policy, and safety-policy artifacts do not exist yet; the workload declares their references as opaque strings, which the lineage records. MCP calls and evidence references enter the lineage with the MCP tools.
- **Configured values**: The enabled specialists, retry limits, lease and attempt durations, envelope history bound, length limits, unfinished-invocation limit, polling interval, monitor and reconciliation intervals, and workload identities are configuration with development and test values; production values remain open.
- **Out of scope**: `CA -> SA` consultations, Coach Agents, Hermes, real models and providers, prompt, tool-policy, safety-policy, and philosophy artifacts, MCP tools and evidence references, the Web UI (the batch's Web UI Match Triage Conversations feature), conversation archival and deletion, lifecycle suppression, tombstones and purge, governance review reads, removing participants, and production values.
