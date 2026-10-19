# Data Model: Agent Orchestration Direct Invocations

All tables live in the `socalytics` schema and are created by one forward-only migration
`NNNN_agentorchestration_conversations_and_invocations.sql` (numbered at implementation time). Versioned tables call
`socalytics.attach_version_trigger`; the participant child table calls `socalytics.attach_aggregate_child_triggers`;
immutable tables use `socalytics.reject_immutable_change()`. Every table without `version` is classified in
`PersistedTableClassifications`. All data is `DAT-008` (Sensitive).

## agent_conversations (versioned aggregate)

| Column | Type | Rules |
| --- | --- | --- |
| `id` | uuid | Primary key (UUIDv7). |
| `team_id`, `match_id`, `analysis_run_id` | uuid | Foreign keys; the run belongs to the match and was `Completed` or `PartiallyCompleted` at creation. Never change. |
| `owner_member_id` | uuid | Creating member; never changes. |
| `state` | text | `active` only (check constraint); archival and deletion are out of scope. |
| `idempotency_key`, `request_hash` | text | Unique per (`owner_member_id`, `idempotency_key`); equal hash replays, different hash conflicts. |
| `created_at` | timestamptz | |
| `version` | bigint | Version trigger; incremented by participant changes. |

## agent_conversation_participants (aggregate child of agent_conversations)

| Column | Type | Rules |
| --- | --- | --- |
| `conversation_id` | uuid | Foreign key; part of the primary key. |
| `agent_id` | text | Enabled Specialist Agent identifier (`specialist-*`); part of the primary key, so duplicates are refused. |
| `added_by_member_id`, `added_at` | uuid, timestamptz | |

## agent_invocations (versioned)

| Column | Type | Rules |
| --- | --- | --- |
| `id` | uuid | Primary key; stable across retries. |
| `conversation_id`, `team_id`, `match_id`, `analysis_run_id`, `owner_member_id` | uuid | Copied from the conversation at submission. |
| `agent_id` | text | A participant at submission. |
| `route` | text | `user-to-specialist` (check constraint). |
| `question` | text | Plain text, 1 to `maxQuestionCharacters`. |
| `idempotency_key`, `request_hash` | text | Unique per (`owner_member_id`, `conversation_id`, `idempotency_key`). |
| `correlation_id` | uuid | Propagated to notifications, attempts, and audit. |
| `policy_version`, `execution_policy` | text, jsonb | `direct-invocation/1` and its resolved values (R5). |
| `state` | text | `queued`, `running`, `succeeded`, `failed` (API names `accepted/queued`, `claimed/running`, `succeeded`, `failed`). |
| `attempts_used` | int | Number of attempts created. |
| `current_attempt_id` | uuid null | The attempt that may act; null while queued or ended. |
| `ready_occurrence` | int | Incremented each time the invocation becomes queued; part of the notification's message id. |
| `last_notified_at` | timestamptz null | Set when the ready notification is written; read by reconciliation. |
| `failure_reason` | text null | `attempts-exhausted`, `execution-failed`, `scope-lost`. |
| `submitted_at`, `ended_at` | timestamptz | |
| `progress_version` | bigint | Incremented on every change visible to the owner (state, attempts, lineage); source of the read `ETag`. |
| `version` | bigint | Version trigger. |

Index: (`owner_member_id`) where `state IN ('queued','running')` for the unfinished-invocation limit;
(`state`, `last_notified_at`) for reconciliation; (`conversation_id`, `submitted_at`) for the history.

### Invocation states

```text
queued --claim--> running --complete--> succeeded
running --retryable failure | lease expiry | deadline (attempts left)--> queued
running --non-retryable failure | exhaustion--> failed
queued | running --scope lost--> failed (scope-lost, never retried)
```

## agent_attempts (versioned)

| Column | Type | Rules |
| --- | --- | --- |
| `id` | uuid | `attempt_id`. |
| `invocation_id` | uuid | Foreign key. |
| `attempt_number` | int | 1..`maxAttempts`, unique per invocation. |
| `workload_client_id` | text | Configured workload client that claimed it. |
| `fencing_token` | bigint | Strictly increasing per invocation; returned as an opaque string. |
| `state` | text | `claimed`, `active`, `completed`, `failed`, `expired`, `fenced`. |
| `claimed_at`, `lease_expires_at`, `deadline_at`, `ended_at` | timestamptz | `deadline_at = claimed_at + maxAttemptSeconds`. |
| `end_reason` | text null | `completed`, `failed-retryable`, `failed-permanent`, `lease-expired`, `deadline-reached`, `scope-lost`. |
| `version` | bigint | Version trigger. |

A claim creates the attempt in `claimed` and moves it to `active` in the same transaction (the architecture's
`claimed` state is transient here). `expired` and `scope-lost` endings also fence the token.

## agent_lineage_entries (immutable)

| Column | Type | Rules |
| --- | --- | --- |
| `invocation_id`, `sequence` | uuid, int | Primary key; gap-free order per invocation. |
| `kind` | text | `submitted`, `claimed`, `checkpoint`, `lease-expired`, `deadline-reached`, `failed`, `scope-lost`, `exhausted`, `completed`. |
| `attempt_id` | uuid null | |
| `payload` | jsonb | Bounded: checkpoint `kind` and `note`; failure `reasonCode` and `retryable`; completion declared references. |
| `recorded_at` | timestamptz | |

## agent_accepted_advice (immutable)

| Column | Type | Rules |
| --- | --- | --- |
| `invocation_id` | uuid | Primary key: exactly one per succeeded invocation. |
| `attempt_id` | uuid | The completing attempt. |
| `advice` | text | 1 to `maxAdviceCharacters`. |
| `execution_references` | jsonb | `runtime`, `modelProvider`, `model`, `deployment`, `promptRef`, `toolPolicyRef`, `safetyPolicyRef` (opaque strings, each 1 to 200 characters). |
| `accepted_at` | timestamptz | |

## agent_workload_operations (immutable)

| Column | Type | Rules |
| --- | --- | --- |
| `workload_client_id`, `operation_id` | text | Primary key; `operation_id` matches `^[A-Za-z0-9._\-]{1,128}$`. |
| `kind` | text | `claim`, `checkpoint`, `complete`, `fail`. |
| `request_hash` | text | SHA-256 of the canonical request. |
| `invocation_id`, `attempt_id` | uuid | |
| `outcome` | jsonb | The recorded response (claim grants store the envelope reference, not the content). |
| `recorded_at` | timestamptz | |

## agent_workload_access_tokens (unversioned)

| Column | Type | Rules |
| --- | --- | --- |
| `token_hash` | text | Primary key; SHA-256 of the access token. |
| `workload_client_id` | text | |
| `key_thumbprint` | text | DPoP binding. |
| `issued_at`, `expires_at` | timestamptz | Expired rows are deleted by the monitor. |

## Other changes

- `security_audit_event.ck_security_audit_event_actor_kind` gains `agent-workload`.
- `outbox_messages` is reused unchanged; message type `agent-invocation-ready`, payload per
  `contracts/agent-orchestration/work-notification/v1`.

## Configuration (not persisted)

- `AgentOrchestration:Agents:Enabled`: enabled Specialist Agent identifiers (Development and Test:
  `specialist-tactics`, `specialist-video-analysis`).
- `AgentOrchestration:Execution`: the policy values of research R5.
- `AgentOrchestration:Workloads`: `TokenLifetime` (development 5 minutes) and `Clients[]` with `ClientId`,
  `PublicJwk`, `Enabled`.
- `AgentOrchestration:Monitor`: monitor and reconciliation intervals and the republish bound (research R7).
