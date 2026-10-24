# Research: Match Conversation Archive and Delete

No `NEEDS CLARIFICATION` remains; the spec's two clarifications are encoded below.

## R1. Conversation state and concurrency

- **Decision**: `agent_conversations.state` allows `active`, `archived`, `deleted`. Archive and unarchive are guarded by
  `If-Match` (the existing `version` column and trigger). A request already satisfied by the current state
  (archive of an archived conversation, unarchive of an active one, delete of a deleted one) returns the current
  state as a success without changing state, version, or audit, **regardless of the supplied version**, so a repeated
  identical request after success is idempotent; a request that would change state with a stale version is `412`.
- **Rationale**: the version increments on success, so a retry necessarily carries a stale version; FR-015 requires
  idempotent repeats and SC-007 requires one winner and conflicts or already-satisfied for the rest.
- **Alternatives**: separate idempotency keys (rejected: the state itself is the natural key and the existing
  participant command uses versions only); ignoring the version entirely (rejected by FR-002 and FR-003).

## R2. Authority denial first

- **Decision**: the `agent-conversation` team-scope source (`AgentScopeSources`) and every conversation, turn, and
  invocation read join on the conversation state and resolve `deleted` as "no scope". The delete transaction sets state `deleted`
  and writes the tombstone together, before the purge worker can run, so no content removal precedes denial. Readers
  and workload operations therefore answer not found from the commit onward (SC-001).
- **Rationale**: one transaction makes "deny, then tombstone, then purge event" atomic from every other session's
  point of view; the purge worker is the only later step.
- **Alternatives**: a separate deny step before the tombstone (rejected: a crash between steps would leave a
  denied conversation without tombstone).

## R3. Tombstone and repeat delete

- **Decision**: `agent_conversation_tombstones` holds only identifiers, actor, team, and time. A repeat delete by the
  tombstone's `deleted_by_member_id` who still holds the owning team's Coach or Club Admin scope (checked by team
  id from the tombstone, not by the conversation) returns `204` with no second tombstone (the repeat path is authorized from the tombstone by team id, before the
  not-found answer, not through the `agent-conversation` resource kind, which no longer resolves); every other caller gets
  the same `404` as a never-existing conversation (clarification 1).
- **Rationale**: the initiator may need to confirm a lost response; no other path may reveal the deletion.
- **Alternatives**: always `404` after deletion (rejected by clarification 1); `410` (rejected: reveals existence).

## R4. Lifecycle-suppressed invocations

- **Decision**: add `lifecycle-suppressed` to `AgentInvocationState` (persisted and wire name identical), terminal,
  reachable from `queued` and `running`. Active or claimed attempts become `fenced` with the new end reason
  `lifecycle-suppressed`; `attempts_used` and retry counters are not changed. The `AgentAttemptGuard` already rejects
  work on terminal invocations and fenced attempts, so late claim, lease, checkpoint, complete, and fail operations
  are rejected; queued work is ignored because the claim only takes `queued` invocations. A lineage entry kind is not
  added (lineage is purged anyway); the transition is recorded in the audit.
- **Rationale**: reuses the architecture's named state and the existing fencing path (as scope loss does), keeping
  budget untouched.
- **Alternatives**: reusing `failed` with a reason (rejected: spec and architecture name a distinct state).

## R5. Archived conversations

- **Decision**: submission and participant addition return `409` with reason `conversation-archived`; in-flight
  invocations complete normally (no change to the claim, completion, or monitor paths). The list gains
  `state=active|archived` (default `active`); every conversation view carries its `state`.
- **Alternatives**: cancelling in-flight work on archive (rejected by FR-005).

## R6. Purge: what is purged and how

- **Decision**: `agent_conversation_purges` has one row per copy (`primary`, `index`) per tombstone with state
  `pending`, `completed`, or `unresolved`, an attempt counter, and a timestamp. Primary purge, in one transaction per
  conversation, deletes the conversation's `agent_accepted_advice`, `agent_lineage_entries`, and
  `agent_workload_operations` rows and blanks `agent_invocations.question` (kept `NOT NULL`, set to the empty string
  and `purged_at` stamped), leaving identifiers, states, pins (digests only), and counts. The immutability triggers
  and `REVOKE`s of those tables are changed in the migration to permit deletion only for conversations that have a
  tombstone (the trigger checks the tombstone). Evidence references live in advice
  `execution_references` and lineage payloads, so they go with those rows. The stamp has no separate Index copy for
  these records yet, so the `index` purge records `completed` with outcome `not-present` and states no more.
- **Rationale**: removes every content-bearing column the spec names while keeping referential integrity and
  minimized identifiers; honest about the Index copy.
- **Alternatives**: hard-deleting invocation rows (rejected: audit and counters reference identifiers; FK cascades
  would widen the change); inventing an index projection to purge (rejected: speculative).

## R7. Purge event, worker, retry, and reconciliation

- **Decision**: the delete transaction enqueues an outbox message `agent-conversation-purge` (contract
  `purge-event/v1`, identifiers only), following `AgentReadyNotification`. The `PublishOutboxBatch` handles the new
  message type; the purge worker (`AgentConversationPurgeWorker`, options `AgentOrchestration:Purge` with
  `PollInterval`, `BatchSize`, `MaxAttempts`, `RetryDelay`; development values) leases pending purge records
  with `FOR UPDATE SKIP LOCKED`, purges, and records the outcome. After `MaxAttempts` a copy becomes `unresolved`
  (audit event `agent.conversation.purge-unresolved`, identifiers only); the reconciliation worker keeps retrying
  `unresolved` records at its interval and flips them to `completed` on success (clarification 2). No member-facing
  read exposes purge records.
- **Rationale**: the purge records, not the message, are the durable work queue, so purge survives missing
  publication; the outbox event satisfies the committed purge event obligation.
- **Subject**: the message is published on the explicit subject `agents.conversations.purge`, added to the events
  stream's subjects (`NatsOptions.EventsSubjects` and `NatsTopology`) so a stream covers it. No consumer is added; the
  `AgentWorkStream` consumer filter and the per-Manager queue permissions are unchanged. A test publishes the message
  successfully.
- **Alternatives**: purge inline in the delete transaction (rejected by FR-007: tombstone commits before removal).

## R8. Reconciliation reapplies tombstones

- **Decision**: `ReconcileAgentWorkHandler` gains a pass that selects tombstones whose conversation row is not
  `deleted`, has nonterminal invocations, active attempts, or unfenced work, and reasserts state `deleted`, the
  suppression of invocations, and the fencing of attempts, each under row locks, then (re)creates missing purge
  records. It runs before the ready-notification republish, so suppressed invocations are never renotified.
- **Rationale**: SC-006; the existing worker already owns bounded recovery.

## R9. Security audit

- **Decision**: new events `agent.conversation.archived`, `.unarchived`, `.deleted`, `.purge-completed`, and
  `.purge-unresolved` with details limited to conversation, invocation count, tombstone, and reason code; no text.
  Refusals of non-owners are not given a distinct disclosing response (always `404`/`403` as today).

## R10. Web UI

- **Decision**: extend the existing triage feature: a state toggle on `TriageSection` (query `state`), a state badge, and
  `ConversationPage` actions shown to the signed-in member for every conversation they can open, only when the role is
  Coach or Club Admin. Conversation reads are initiator-only today (every other caller gets `404`), so a conversation
  the member can open is one they initiated and no owner field or comparison is needed; the non-initiator case is
  verified at API level and by a `404` in the simulated platform. Actions use confirmation dialogs and explicit states reusing the existing failure model (`409`/`412`
  reload offer, unavailable, offline). A `404` on a conversation route shows the existing unavailable view. No
  optimistic update; queries are invalidated after success; no persistence added (the existing storage tests are
  extended to cover the new views). OpenAPI snapshot and Kiota client are regenerated.
- **Alternatives**: optimistic updates (rejected by the spec).
