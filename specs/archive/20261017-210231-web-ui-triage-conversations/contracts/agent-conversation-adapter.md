# Contract: Agent Conversation Adapter

`src/clients/web/src/platform/adapters/agentConversationAdapter.ts`, over the generated Kiota client (regenerated from
the OpenAPI snapshot that [Agent Orchestration Direct Invocations](../../20261017-204332-agent-orchestration-invocations/contracts/agent-orchestration-api.md)
refreshes). Models: [data-model.md](../data-model.md). Callers own idempotency keys; the adapter never creates one.

| Method | Operation | Request | Result |
| --- | --- | --- | --- |
| `listAgents()` | `listAvailableAgents` | — | `EnabledAgent[]` |
| `listConversations(matchId)` | `listAgentConversations` | — | `ConversationSummary[]` (platform order) |
| `createConversation(matchId, analysisRunId, key)` | `createAgentConversation` | `Idempotency-Key`; `{ analysisRunId }` | `TriageConversation` (`201` or replay `200`) |
| `getConversation(conversationId, etag?)` | `getAgentConversation` | `If-None-Match` | `Conditional<TriageConversation>` |
| `addParticipant(conversationId, agentId, version)` | `addAgentConversationParticipant` | `If-Match`; `{ agentId }` | `TriageConversation` |
| `submitQuestion(conversationId, agentId, question, key)` | `submitAgentInvocation` | `Idempotency-Key`; `{ agentId, question }` | `SubmissionAccepted` (`202`, replay `202`) |
| `listTurns(conversationId, cursor?, etag?)` | `listAgentConversationTurns` | `cursor`; `If-None-Match` | `Conditional<TurnPage>` |
| `getInvocationStatus(invocationId, etag?)` | `getAgentInvocation` | `If-None-Match` | `Conditional<InvocationStatus>` with `Retry-After` |
| `getLineage(invocationId, etag?)` | `getAgentInvocationLineage` | `If-None-Match` | `Conditional<InvocationLineage>` |

`Conditional<T>` is `{ kind: 'unchanged' } | { kind: 'changed'; value: T; etag: string; retryAfterSeconds?: number }`.
Conditional reads use a response handler that turns `304` into `unchanged` and reads `ETag` and `Retry-After`; other
statuses go through the shared problem mapping (research R3).

## Refusal translation

The adapter throws a `TriageFailure` carrying a `TriageOutcome` (data-model.md) derived from the shared
`PlatformFailure`, leaving `mapProblem` unchanged.

| Status and code | Outcome |
| --- | --- |
| `429 too-many-unfinished-invocations` | `tooManyUnfinished` |
| `409 agent-not-available` | `agentNotAvailable` |
| `409 agent-not-participant` | `agentNotParticipant` |
| `409 participant-exists` | `participantExists` |
| `409 analysis-run-not-eligible` | `runNotEligible` |
| `400` with field errors | `invalidQuestion` (field errors kept) |
| `403` | `notPermitted` |
| `404` | `notFound` |
| `412` | `staleVersion` |
| network failure (`unreachable`) on create or submit | `unknownOutcome` |
| `409 idempotency-key-reused`, `400 idempotency-key-missing`, `428` | `programmingError` (shared) |
| anything else | the shared failure kind |

## Mapping rules

- Required identifiers missing from a response raise `incompatible`.
- Unknown `state`, `failureReason`, lineage `kind`, and `group` values are kept as raw strings with `known: false`.
- `advice` in turns and lineage maps to `Advice` with an empty `evidenceReferences` list; lineage advice adds
  `executionReferences`.
- No adapter method writes to any browser storage.
