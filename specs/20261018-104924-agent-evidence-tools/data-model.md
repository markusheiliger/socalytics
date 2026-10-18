# Data Model: Agent Evidence Tools API

All tables live in the `socalytics` schema and change through one forward-only migration
`NNNN_agentorchestration_tool_calls.sql` (numbered at implementation time as the next free number). All data is
`DAT-008` (Sensitive agent state). No evidence content (fact payloads, links, media) is persisted by this feature.

## agent_tool_calls (immutable, new)

The reservation of one tool call that passed the attempt binding (research R4); protected by
`socalytics.reject_immutable_change()` and classified in `PersistedTableClassifications`.

| Column | Type | Rules |
| --- | --- | --- |
| `attempt_id`, `call_id` | uuid, text | Primary key; `call_id` matches `^[A-Za-z0-9._\-]{1,128}$`; a duplicate is refused as `agent-tool-call-id-reused`. |
| `invocation_id` | uuid | Foreign key to `agent_invocations`. |
| `tool_id` | text | An allowlisted tool identifier (check constraint over the eleven identifiers). |
| `arguments_digest` | text | `sha-256:` of the canonical arguments. |
| `is_clip_resolution` | boolean | True for `get_video_clip`; counted for `MaxClipResolutionsPerAttempt`. |
| `reserved_at` | timestamptz | |

Index: (`attempt_id`) for limit counting (the primary key serves it).

## agent_lineage_entries (existing, changed)

- The `kind` check constraint gains `tool-call`, and a partial unique index on (`attempt_id`, `payload->>'callId'`)
  where `kind = 'tool-call'` makes the tool-call append idempotent (the first entry for a call stands).
- `tool-call` payload (bounded JSON, research R9):

| Field | Rules |
| --- | --- |
| `callId`, `toolId` | As reserved. |
| `outcome` | `answered`, `unavailable`, `not-found`, `not-permitted`, `invalid`, `limit-reached`, `scope-lost`, `failed`. |
| `reason` | Bounded code: for `unavailable` `capability-not-in-run` or `no-accepted-producer`; for `failed` `evidence-storage-unavailable` or `interrupted`; for `invalid` the first validation code. |
| `argumentsDigest` | Always present. |
| `resultDigest` | Present for `answered` and `unavailable`; digest of the canonical result without `media.url` and `media.expiresAt`. |
| `evidence.facts` | `[{ acceptedResultId, ordinal }]`, at most the fact read's maximum page size. |
| `evidence.media` | `[{ mediaDigest, startMs, endMs }]` for available parts of a clip resolution. |
| `producingCapability` | For concept tools: the producing capability or `null`. |

A `not-permitted`, `invalid`, `limit-reached`, or `scope-lost` entry has no reservation when the refusal occurs before
the reservation is written (limits and policy are checked before reserving); every entry is still written in the same
unit of work as the refusal. Refusals by authentication, by the attempt binding (stale attempt), or for a reused call
identifier write no lineage entry and are audited only.

## Tool-call lifecycle

```text
received --binding fails--> refused (audit only)
received --scope lost--> denied: attempt fenced, invocation failed scope-lost, lineage scope-lost + tool-call(scope-lost)
received --not permitted | limit | invalid--> refused: lineage tool-call(outcome), audit for not-permitted and limit
received --authorized--> reserved --read--> lineage tool-call(answered | unavailable | not-found | failed)
reserved --no lineage entry once the attempt ended or after the attempt's maximum duration--> lineage tool-call(failed, interrupted)
```

## Configuration (not persisted)

- `AgentOrchestration:Tools:MaxCallsPerAttempt` (development and test 50) and
  `AgentOrchestration:Tools:MaxClipResolutionsPerAttempt` (development and test 10); validated at start; production
  values open.

## Domain types

- `AgentToolAllowlist` (`Domain/AgentOrchestration/Tools`): version `1.0.0` and the eleven entries of the allowlist
  contract (tool identifier, operation identifier, path segment, answer kind, producing capability).
- `ToolCallOutcome` and `ToolCallEvidence`: the outcome codes and bounded evidence references above.
