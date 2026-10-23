# Feature Specification: Match Conversation Archive and Delete

**Feature Branch**: No branch created; specification authored on the existing checkout.

**Created**: 2026-10-23

**Status**: Draft

**Input**: User description: "Let a member end the life of their own match triage conversations: archive a conversation (it leaves the active list, keeps its history, and can be resumed by unarchiving) and delete it (a governed deletion). Today conversations stay Active forever and the architecture's lifecycle operations and tombstones are planned but not built. Scope: the Agent Orchestration functional area gains Archived as a conversation state with archive and unarchive commands, and a delete command that denies query and execution authority first, commits a durable tombstone, moves nonterminal invocations of the conversation to the existing lifecycle-suppressed terminal state (fencing their attempts and queue work without consuming budget), and commits a purge outbox event; a purge worker removes the conversation's questions, advice, evidence references, and lineage text from the Primary and Index copies and records the outcome, keeping only minimized identifiers in the security audit; the tombstone is reapplied by the reconciliation worker so a suppressed conversation never regains visibility or execution authority. New member operations are limited to the conversation's own initiating member; Club Admins and Coaches of the owning team keep their current rules (conversation reads are initiator-only) and Viewers cannot archive or delete. The connected Web UI match conversation views gain an archived filter and confirmed archive, unarchive, and delete actions with explicit states, online only, with no conversation content persisted client-side."

## Clarifications

### Session 2026-10-23

- Q: When the initiating member repeats a delete request for a conversation that is already deleted, what should the platform answer, given that every other read of a deleted conversation must say 'not found'? → A: Report the existing deletion (success, no second tombstone) only to the initiating member who is still in the owning team's scope; every other caller gets 'not found'.
- Q: Who can see that the purge of a deleted conversation has an unresolved (persistently failed) outcome, and how is it recovered after the bounded retries are exhausted? → A: Only operators: the per-copy purge outcome is recorded in the stamp's database and the security audit with identifiers only, and the reconciliation worker keeps retrying it; no member-facing read or screen.

## User Scenarios & Testing *(mandatory)*

The actor is the initiating member of a match triage conversation (a Club Admin
or Coach of the owning team who started it). Other members' read rules do not
change; conversation reads are initiator-only today, so another member cannot open the conversation at all. "Own conversation" means one the member initiated. Archiving is
reversible and keeps history; deletion is governed, irreversible, and removes
the conversation's content from the stamp's active copies.

### User Story 1 - Archive and unarchive my conversation (Priority: P1)

A Coach has finished with a match triage conversation. They archive it. It
leaves the active list but remains readable with its full history under an
archived filter, and they can unarchive it later to resume asking questions.

**Why this priority**: It is the safe, reversible way to tidy the list and the
foundation for the lifecycle states that deletion builds on.

**Independent Test**: Start a conversation with an answered question, archive
it, check the active list and archived filter, attempt a question while it is
archived, then unarchive it and ask again.

**Acceptance Scenarios**:

1. **Given** an Active conversation the member initiated, **When** the member archives it with the current version, **Then** it becomes Archived, leaves the active list, and appears under the archived filter with its history, advice attribution, AI disclosure, and lineage unchanged.
2. **Given** an Archived conversation, **When** the member opens it, **Then** it is readable under the existing read rules and clearly marked Archived.
3. **Given** an Archived conversation, **When** a new question or specialist addition is submitted, **Then** the request is refused with an explicit reason until the conversation is unarchived.
4. **Given** an Archived conversation the member initiated, **When** the member unarchives it, **Then** it becomes Active again, returns to the active list, and accepts new questions.
5. **Given** a conversation with a nonterminal invocation, **When** the member archives it, **Then** the in-flight invocation completes normally and its advice is attached to the archived conversation.
6. **Given** a stale version, a conversation that is already in the requested state, or a repeated identical request, **When** archive or unarchive is submitted, **Then** the stale request is refused with a conflict, and an already-satisfied request is reported without changing state or duplicating audit evidence.

---

### User Story 2 - Delete my conversation (Priority: P1)

A Coach asks that a conversation be deleted. After an explicit confirmation,
the conversation immediately disappears from every read and can no longer
execute anything. Its questions, advice, evidence references, and lineage text
are then purged from the stamp's active copies, and only minimized identifiers
remain in the security audit.

**Why this priority**: Governed deletion of sensitive conversation data is the
core obligation this feature meets.

**Independent Test**: Delete a conversation with a completed and an in-flight
invocation; verify it is unreadable at once, the in-flight work is
suppressed without consuming budget, the purge completes, content is gone from
Primary and Index copies, and the audit holds identifiers only.

**Acceptance Scenarios**:

1. **Given** an Active or Archived conversation the member initiated, **When** the member confirms deletion with the current version, **Then** query and execution authority are denied first, a durable tombstone is committed, and a purge event is committed in the same transaction, before any content is removed.
2. **Given** a committed deletion, **When** anyone (including the initiator, Club Admins, and Coaches) reads, lists, or polls the conversation or its invocations, **Then** it is not found and reveals no content.
3. **Given** nonterminal invocations in the conversation, **When** deletion commits, **Then** each moves to the lifecycle-suppressed terminal state, its attempts and queued work are fenced so late claims, checkpoints, completions, and failures are rejected, and no retry or attempt budget is consumed.
4. **Given** a committed tombstone, **When** the purge runs, **Then** the conversation's questions, advice, evidence references, and lineage text are removed from the Primary copy, the Index copy outcome is recorded as not-present (the stamp holds no separate Index copy yet), and the outcome is recorded per copy.
5. **Given** a purge that fails or is interrupted, **When** it is retried, **Then** it completes idempotently without exposing content in the meantime, and a persistent failure after the bounded retries is recorded as an unresolved purge outcome per copy, visible only to operators (in the stamp's database and, with identifiers only, in the security audit), which the reconciliation worker keeps retrying; no member-facing read or screen shows it.
6. **Given** a tombstoned conversation, **When** the reconciliation worker runs, **Then** it reapplies the tombstone so the conversation never regains visibility or execution authority, even if state is found inconsistent.
7. **Given** the deletion, **When** the security audit is read, **Then** it records the actor, the minimized conversation, invocation and tombstone identifiers, the time, and the outcome, and never question or advice text.
8. **Given** a stale version or an already-deleted conversation, **When** delete is submitted, **Then** a stale request is refused with a conflict, and a repeat of an accepted deletion by the initiating member who is still in the owning team's scope reports the existing deletion as a success without a second tombstone, while every other caller (including a former initiator who lost scope) gets not found.

---

### User Story 3 - Manage conversation lifecycle in the Web UI (Priority: P2)

On the connected match conversation views, the initiating member filters
between active and archived conversations and archives, unarchives, or deletes
a conversation through explicit confirmed actions with clear loading,
success, refusal, conflict, unavailable, and offline states.

**Why this priority**: It exposes the capability to members; the operations
are verifiable without it.

**Independent Test**: In the simulated and platform-triage browser projects,
archive, filter, unarchive, and delete a conversation, and verify the actions
are absent for other members and no content is persisted client-side.

**Acceptance Scenarios**:

1. **Given** the conversation list of a match, **When** the member switches between active and archived, **Then** the list shows only conversations in the chosen state, with an explicit empty state for each.
2. **Given** an own conversation, **When** the member chooses archive, unarchive, or delete, **Then** the action asks for confirmation naming the effect (delete states that it is permanent and removes questions and advice) and acts only after confirmation.
3. **Given** a Viewer, or a member who did not initiate the conversation, **When** they open it, **Then** no archive, unarchive, or delete action is offered (a Viewer sees no action, and a non-initiator gets not found because conversation reads are initiator-only), and a request made anyway is refused by the platform.
4. **Given** an action in progress, **When** it succeeds, is refused, conflicts with a newer version, or fails to reach the platform, **Then** the view shows a distinct explicit state and, after a conflict, offers a reload; no optimistic change hides a refused action.
5. **Given** a deleted conversation, **When** the member returns to its address, **Then** the view shows it is unavailable without any remaining content.
6. **Given** any lifecycle view, **When** the browser is inspected, **Then** no conversation content (questions, advice, evidence, lineage) is stored in client-side persistent storage or caches, and offline use shows an online-only message.

---

### Edge Cases

- Deleting while an invocation is mid-completion: the completion races with the tombstone; whichever commits second loses, and a completion after the tombstone is rejected and cannot create visible advice.
- A purge worker crash after partial removal: retry finishes the removal; content in a not-yet-purged copy stays unreadable because authority is already denied.
- Restoring or replaying stale state inside the stamp (for example a reconciliation pass over an inconsistent row): tombstones are reapplied before the conversation can be read or executed.
- Deleting an Archived conversation is allowed without unarchiving first.
- A member who lost their team role or membership after initiating a conversation can no longer archive, unarchive, or delete it; the existing scope-loss rules apply.
- Two concurrent lifecycle requests on one conversation: exactly one succeeds against the expected version; the other receives a conflict.
- A deleted conversation's identifier is never reused and never reveals whether it once held content to unauthorized callers; only a repeated delete by the initiating member still in scope learns the deletion exists.
- Evidence items referenced by the conversation (recordings, analysis facts) are not deleted; only the conversation's references to them are purged.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The system MUST give conversations the states Active, Archived, and Deleted (tombstoned), with Active as the existing default.
- **FR-002**: The system MUST let only a conversation's initiating member archive an Active conversation, guarded by the expected version.
- **FR-003**: The system MUST let only the initiating member unarchive an Archived conversation, guarded by the expected version.
- **FR-004**: The system MUST keep an Archived conversation's history, advice attribution, AI disclosure, and lineage fully readable under the existing read rules, and MUST refuse new questions and specialist additions to it with an explicit reason.
- **FR-005**: The system MUST let in-flight invocations of a conversation that is being archived finish normally.
- **FR-006**: The system MUST let only the initiating member delete an Active or Archived conversation, guarded by the expected version and, in the Web UI, an explicit confirmation.
- **FR-007**: On deletion the system MUST, in order, deny query and execution authority, then durably commit a tombstone and a purge event in one transaction before any content removal.
- **FR-008**: On deletion the system MUST move every nonterminal invocation of the conversation to the existing lifecycle-suppressed terminal state, fence its attempts and queued work so late operations are rejected, and consume no attempt or retry budget.
- **FR-009**: After the tombstone commits, the system MUST treat the conversation and its invocations as not found for every reader, including the initiator, Club Admins, and Coaches. The only exception is a repeated delete request: the initiating member who is still in the owning team's scope receives the existing deletion reported as a success without a second tombstone, and every other caller receives not found.
- **FR-010**: The system MUST purge the conversation's questions, advice, evidence references, and lineage text from the Primary copy (and from the Index copy where one exists; the stamp holds none yet, so its outcome is not-present), MUST do so idempotently with bounded retry, and MUST record the outcome per copy, including unresolved failures, in the stamp's database and, with identifiers only, in the security audit. The reconciliation worker MUST keep retrying an unresolved purge after the bounded retries are exhausted, and an unresolved purge outcome MUST be visible to operators only, with no member-facing read or screen.
- **FR-011**: The system MUST retain only minimized identifiers, actor, time, and outcome for lifecycle actions in the security audit, and MUST NOT write question or advice text to the audit, logs, or error responses.
- **FR-012**: The system MUST reapply current tombstones through the reconciliation worker so a suppressed conversation never regains visibility or execution authority.
- **FR-013**: The system MUST authorize archive, unarchive, and delete only for the initiating member within the owning team's current scope; Viewers, other members, and unauthenticated callers MUST be refused, and the refusal MUST NOT disclose conversation content.
- **FR-014**: The system MUST NOT change the read rules of Club Admins and Coaches of the owning team for non-deleted conversations.
- **FR-015**: The system MUST expose the lifecycle operations as versioned member operations with anti-forgery protection, expected-version preconditions, and standard problem responses, and MUST make repeated identical requests idempotent.
- **FR-016**: The system MUST make the active list exclude Archived conversations by default and provide an archived filter, and MUST show every conversation's lifecycle state.
- **FR-017**: The Web UI MUST offer archive, unarchive, and delete only to the initiating member, each behind a confirmation, with explicit loading, success, refusal, conflict, unavailable, and offline states.
- **FR-018**: The Web UI MUST remain online only and MUST NOT persist conversation content in client-side storage or caches.
- **FR-019**: The Web UI MUST be covered by browser tests in the simulated and platform-triage projects.
- **FR-020**: The system MUST add the new state and tombstone persistence through a forward-only migration, classify every new persisted table, and keep Agent Orchestration the sole writer of its tables.
- **FR-021**: The system MUST NOT introduce automatic archival, expiration, retention periods, residency values, approval owners, administrator or bulk deletion, sharing, export, or any change to the agent catalog, and MUST NOT claim backup purge or restore-time tombstone reapplication beyond the stamp's database.
- **FR-022**: The feature MUST update the architecture documents so lifecycle operations and tombstones are described as implemented for direct triage conversations and every statement that calls them planned is corrected, while POL-008 values, backup disposition, and Coach Agent and consultation lifecycle remain stated as unresolved or later.

### Key Entities

- **Conversation**: A member's match triage conversation with a lifecycle state (Active, Archived, Deleted) and a version for concurrency control.
- **Conversation Tombstone**: The durable, minimal record that a conversation was deleted, by whom and when, that denies visibility and execution authority and drives reapplication.
- **Invocation**: A logical question to a Specialist Agent within a conversation; nonterminal ones become lifecycle-suppressed on deletion.
- **Purge Record**: The per-copy outcome (including an unresolved failure) of removing conversation content from the Primary and Index copies, held in the stamp's database and visible to operators only.
- **Security Audit Event**: A minimized record of a lifecycle action holding identifiers, actor, time, and outcome only.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% of reads, lists, polls, and workload operations on a deleted conversation or its invocations are refused as not found from the moment the deletion is accepted, including during purge.
- **SC-002**: After a completed purge, 0 questions, advice texts, evidence references, or lineage texts of the deleted conversation remain in the Primary or Index copies, and 0 such texts appear in the security audit or logs.
- **SC-003**: 100% of nonterminal invocations of a deleted conversation end lifecycle-suppressed with no attempt or retry budget consumed and no late attempt operation accepted.
- **SC-004**: A member can archive, find under the archived filter, and unarchive a conversation, and ask a new question afterwards, in under 1 minute in the Web UI.
- **SC-005**: 100% of archive, unarchive, and delete requests from non-initiating members and Viewers are refused, and the Web UI offers the actions to none of them.
- **SC-006**: A reconciliation pass over a deliberately inconsistent tombstoned conversation restores suppression in 100% of cases without the conversation becoming readable or executable.
- **SC-007**: Concurrent lifecycle requests on one conversation yield exactly one success and the rest conflicts or already-satisfied reports in 100% of tests.
- **SC-008**: Browser tests for archive, unarchive, delete, filter, refusal, conflict, and offline states pass in both the simulated and platform-triage projects, and no conversation content is found in client-side persistent storage.

## Assumptions

- The initiating member remains the only owner of a conversation's lifecycle; conversation reads are initiator-only today, so Club Admins and Coaches of the owning team keep exactly their current rules (they read the conversations they initiated and get not found for others') and gain no lifecycle authority over others' conversations.
- Archiving does not cancel in-flight work; deletion does.
- Deletion is immediate and irreversible within the stamp; no deletion delay, grace period, or undo exists, because retention and approval values (POL-008) stay unresolved.
- The stamp holds no separate Index copy of conversation content yet, so the Index purge records the outcome not-present; the purge covers the stamp's Primary copy and any Index copy; JetStream transport copies are bounded and their queued work is fenced, and Hermes caches, telemetry, replicas, and backups are outside this feature.
- Evidence items themselves (recordings, analysis facts) are governed elsewhere and are not deleted.
- Depends on the implemented specs `specs/archive/20261017-204332-agent-orchestration-invocations` (#155) and `specs/archive/20261017-210231-web-ui-triage-conversations` (#156) and on Club and Identity Foundation; it needs no open spec.
- Out of scope: retention periods, residency, approval owners, automatic inactivity archival or expiration, purge of backups and restore-time tombstone reapplication beyond the stamp's database, production telemetry and replica purge evidence, administrator or bulk deletion, sharing, export, Coach Agent and consultation conversations, and any agent catalog change.
