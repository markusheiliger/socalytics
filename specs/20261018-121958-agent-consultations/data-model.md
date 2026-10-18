# Data Model: Delegated Coach-to-Specialist Consultations

All changes are made by one forward-only migration `NNNN_agentorchestration_consultations.sql` (numbered at
implementation time) in the `socalytics` schema, on top of the tables of
[Agent Orchestration Direct Invocations](../20261017-204332-agent-orchestration-invocations/data-model.md), the
[Agent Catalog Registry](../20261018-101517-agent-catalog-registry/data-model.md), the
[Agent Evidence Tools API](../20261018-104924-agent-evidence-tools/data-model.md), and
[Coach Agent Direct Conversations](../20261018-115720-coach-agent-conversations/data-model.md). All data is `DAT-008`
(Sensitive) except catalog records (`DAT-007`).

## agent_consultations (versioned, new)

One row per consultation; a row in state `pending` is the parent's waiting edge. Calls
`socalytics.attach_version_trigger`.

| Column | Type | Rules |
| --- | --- | --- |
| `id` | uuid | Primary key; the `consultation_id` (UUIDv7). |
| `parent_invocation_id` | uuid | Foreign key to `agent_invocations`; the parent has route `user-to-coach` and no parent itself. |
| `sequence` | int | 1..`maxConsultationsPerInvocation` of the pinned policy, unique per parent. |
| `requesting_attempt_id` | uuid | Foreign key to `agent_attempts`; the parent attempt that requested it. |
| `child_invocation_id` | uuid | Foreign key to `agent_invocations`; unique. |
| `agent_id` | text | Requested Specialist Agent identifier (`specialist-*`). |
| `question` | text | Plain text, 1 to the policy's `maxQuestionCharacters`. |
| `decision_support` | text | Plain text, 1 to the policy's `maxDecisionSupportCharacters`. |
| `known_evidence` | jsonb | 0–10 `{ callId, toolId, outcome, evidenceReferences }` copied from the requesting attempt's `tool-call` lineage entries with outcome `answered`. |
| `state` | text | `pending`, `answered`, `unavailable`, `ended` (check constraint). |
| `outcome_reason` | text null | For `unavailable`: the child's failure reason (`attempts-exhausted`, `execution-failed`, `agent-not-available`); for `ended`: `parent-ended` or `scope-lost`. |
| `created_at`, `resolved_at` | timestamptz | `resolved_at` set with the first non-`pending` state. |
| `version` | bigint | Version trigger. |

Index: (`parent_invocation_id`, `state`) for waiting checks and reads.

State transitions: `pending → answered | unavailable | ended`; terminal states never change.

## agent_invocations (existing, changed)

| Column | Change |
| --- | --- |
| `parent_invocation_id` | New, uuid null, foreign key to `agent_invocations`; set only for children. |
| `consultation_id` | New, uuid null, unique; set only for children. |
| `inherited_tool_policy_digest` | New, text null; the parent's pinned tool-policy digest, set only for children. |
| `route` | Check constraint adds `coach-to-specialist`; children always have it, nothing else may. |
| `state` | Check constraint adds `waiting-for-consultations` and `synthesizing`. |
| `failure_reason` | Check constraint adds `consultation-unavailable` (parents) and `parent-ended` (children). |
| `policy_version` | Adds `consulting-invocation/1` (Coach Agent versions with a consultation policy) and `consultation-invocation/1` (children). |

A check constraint makes `parent_invocation_id`, `consultation_id`, `inherited_tool_policy_digest`, and route
`coach-to-specialist` all set or all null. The unfinished-invocation limit counts only rows with
`parent_invocation_id IS NULL`. Index: (`parent_invocation_id`).

### Invocation states (parents with consultations)

```text
running | synthesizing --consultation created--> waiting-for-consultations
waiting-for-consultations (attempt active) --last consultation resolved--> synthesizing
waiting-for-consultations (no attempt) --last consultation resolved, attempts left--> queued
queued --claim, all consultations resolved--> synthesizing
running | waiting-for-consultations | synthesizing --attempt ends retryably, consultation pending, attempts left--> waiting-for-consultations (no attempt)
running | synthesizing --attempt ends retryably, none pending, attempts left--> queued
synthesizing --complete--> succeeded
any non-terminal --non-retryable failure | exhaustion | consultation-unavailable (policy fail) | scope lost--> failed
```

Children follow the direct invocation states (`queued`, `running`, `succeeded`, `failed`) and additionally fail with
`parent-ended` or `scope-lost` when their parent ends or loses scope.

## agent_attempts (existing, changed)

`end_reason` check adds `parent-ended` (child attempt fenced because its parent ended) and `consultation-unavailable`
(parent attempt fenced because the pinned policy fails the parent).

## agent_invocation_pins (existing, changed)

`kind` adds `consultation-policy` (Coach Agent versions that reference one). A child's pins are the Specialist version
enabled when the consultation was created and its artifacts.

## agent_lineage_entries (existing, changed)

`kind` check adds, on parents, `consultation-requested`, `consultation-answered`, `consultation-unavailable`, and
`consultation-ended`, and, on children, `consultation-created`; payloads hold identifiers, sequence, agent
identifier, state, and reason codes only (question, decision support, and responses live in `agent_consultations` and
`agent_accepted_advice`). Hermes' checkpoint kinds `consultation-requested`, `consultation-refused`, and
`consultations-resolved` are ordinary `checkpoint` entries.

## agent_accepted_advice (existing, changed)

| Column | Change |
| --- | --- |
| `consultation_influence` | New, jsonb null: for parents with consultations, the validated `consultationInfluence` array (`consultationId`, `use`, `agreement`, `note`). |

A child's accepted advice is an ordinary row; it is returned only inside its parent's reads (and the child's own reads
once the parent has ended).

## agent_workload_operations (existing, changed)

`kind` check adds `consult`; `outcome` stores `{ consultationId, childInvocationId, sequence }` for replays.

## Catalog artifacts (existing tables, new kind)

`agent_catalog_artifacts.kind` check adds `consultation-policy`. Document (`consultation-policy` 1.0.0):

| Field | Rules |
| --- | --- |
| `allowedSpecialists` | 1–6 unique Specialist Agent identifiers of the catalog baseline. |
| `maxConsultationsPerInvocation` | Integer 1–6. |
| `maxQuestionCharacters` | Integer 1–2,000. |
| `maxDecisionSupportCharacters` | Integer 1–1,000. |
| `onUnavailable` | `continue` or `fail`. |

`agent-version` 1.1.0 adds the optional `artifacts.consultationPolicy { id, version, digest }` (Coach Agent versions
only; their safety policy must list `coach-to-specialist`).

## Persisted table classifications

`agent_consultations` is versioned and needs no entry in `PersistedTableClassifications`; the classifications of the
changed tables stay as their features defined them.

## Configuration (not persisted)

| Setting | Development value |
| --- | --- |
| `AgentOrchestration:Execution:ConsultingParent` (`consulting-invocation/1`) | As `direct-invocation/1` with a 900-second attempt maximum. |
| `AgentOrchestration:Execution:Consultation` (`consultation-invocation/1`) | 3 attempts, 30-second lease, 300-second attempt maximum, 10 history turns, 16,000 advice and 1,000 note characters. |
| `SOCALYTICS_HERMES_MAX_WAITING_ATTEMPTS` | 8 (1–64). |
| `SOCALYTICS_HERMES_CONSULTATION_POLL_SECONDS` | 2 (1–30). |

Production values remain open.
