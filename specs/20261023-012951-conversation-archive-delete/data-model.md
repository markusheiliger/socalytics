# Data Model: Match Conversation Archive and Delete

All tables belong to the Agent Orchestration functional area and are written only by its handlers and stores.
Migration: `0032_agentorchestration_conversation_lifecycle.sql`.

## Conversation (`agent_conversations`, changed)

| Field | Change |
| --- | --- |
| `state` | CHECK widened to `active`, `archived`, `deleted` |
| `archived_at` | new, `timestamptz NULL`; set on archive, cleared on unarchive |

Transitions (guarded by `version`):

```text
active   --archive-->   archived
archived --unarchive--> active
active | archived --delete--> deleted   (terminal, irreversible)
```

`deleted` is never visible through a read; the row keeps only identifiers and counters needed for referential
integrity. The `ITeamScopeSource` for `agent-conversation` resolves `deleted` as no scope.

## Conversation Tombstone (`agent_conversation_tombstones`, new)

| Field | Notes |
| --- | --- |
| `conversation_id` | PK, FK `agent_conversations(id)` |
| `team_id` | owning team at deletion, for the in-scope repeat-delete check |
| `deleted_by_member_id` | FK `member_account(id)`; the initiating member |
| `deleted_at` | `timestamptz` |
| `invocations_suppressed` | integer count, identifiers-only evidence |

Immutable (reject update and delete). Classified `Immutable` in `PersistedTableClassifications`. Holds no text.

## Purge Record (`agent_conversation_purges`, new)

| Field | Notes |
| --- | --- |
| `conversation_id`, `copy` | PK; `copy` in `primary`, `index` |
| `state` | `pending`, `completed`, `unresolved` |
| `outcome` | `purged`, `not-present`, or null while pending |
| `attempts` | integer, bounded by `Purge:MaxAttempts` per cycle |
| `next_attempt_at`, `completed_at`, `updated_at` | scheduling and evidence |

Versioned (`socalytics.attach_version_trigger`) because workers update it concurrently with leased claims; visible to
operators in the database and, as identifiers only, in the security audit; no member read.

## Invocation (`agent_invocations`, changed)

| Field | Change |
| --- | --- |
| `state` | CHECK adds `lifecycle-suppressed` (terminal; from `queued` or `running`) |
| `question` | blanked (`''`) by the purge |
| `purged_at` | new, `timestamptz NULL` |

`attempts_used` and `ready_occurrence` are not modified by suppression. Active or claimed attempts end `fenced` with
`end_reason = 'lifecycle-suppressed'` (added to the CHECK).

## Purged content

Rows deleted per conversation by the Primary purge: `agent_accepted_advice`, `agent_lineage_entries`,
`agent_workload_operations`. Their immutability triggers permit deletion only when the conversation has a tombstone.
Participants, pins, and attempts remain as identifier-only structure.

## Outbox

`outbox_messages.message_type` CHECK adds `agent-conversation-purge`. Payload: `schemaVersion`, `conversationId`,
`requestedAt` (the tombstone time); validated by [contracts/purge-event.md](contracts/purge-event.md).

## Security audit events

`agent.conversation.archived`, `agent.conversation.unarchived`, `agent.conversation.deleted`,
`agent.conversation.purge-completed`, `agent.conversation.purge-unresolved`. Details: conversation id, team id,
invocation count, copy, reason code; never question, advice, or evidence text.

## Validation rules

- Only the conversation's `owner_member_id`, still holding Coach or Club Admin scope on the owning team, may archive,
  unarchive, or delete; others get `404` (as today), Viewers `403`.
- Submission and participant addition require `active` (otherwise `409 conversation-archived`).
- A missing `If-Match` is `428`; a stale version on a state-changing request is `412`.
