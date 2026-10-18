# Contract: Agent Orchestration API (`/api/v1`)

Every operation is in the OpenAPI document with typed request and response schemas and uses the existing problem
details (`urn:socalytics:problem:<code>`). Field names are camelCase JSON. Identifiers are UUIDs unless noted.

## Member operations

Session-authenticated (`MapMemberApi()`, policy `ActiveMember`); state-changing operations require `X-CSRF-Token`.
Non-owners and members without current team access receive `404 not-found` for every conversation-scoped operation.

| Operation id | Method and path | Request | Success | Refusals |
| --- | --- | --- | --- | --- |
| `listAvailableAgents` | `GET /api/v1/agent-catalog/agents` | — | `200` `{ agents: [{ agentId, group: "specialist", domain }] }` | `403` without Club Admin or team role |
| `createAgentConversation` | `POST /api/v1/matches/{matchId}/agent-conversations` | `Idempotency-Key`; `{ analysisRunId }` | `201` conversation, `ETag`, `Location` (replay: `200`) | `400 idempotency-key-missing`, `403` (Viewer), `404`, `409 idempotency-key-reused`, `409 analysis-run-not-eligible` (state or other match) |
| `listAgentConversations` | `GET /api/v1/matches/{matchId}/agent-conversations` | — | `200` own conversations, newest first | `404` |
| `getAgentConversation` | `GET /api/v1/agent-conversations/{conversationId}` | `If-None-Match` | `200` conversation, `ETag`; `304` | `404` |
| `addAgentConversationParticipant` | `POST /api/v1/agent-conversations/{conversationId}/participants` | `If-Match`; `{ agentId }` | `200` conversation, `ETag` | `400` field violation, `404`, `409 agent-not-available`, `409 participant-exists`, `412`/`428` (existing `IfMatchHeader` rules) |
| `submitAgentInvocation` | `POST /api/v1/agent-conversations/{conversationId}/invocations` | `Idempotency-Key`; `{ agentId, question }` | `202` `{ conversationId, invocationId, statusLocation, retryAfterSeconds }`, `Location`, `Retry-After` (replay: same `202`) | `400` field violation, `403` (no submit right), `404`, `409 idempotency-key-reused`, `409 agent-not-participant`, `409 agent-not-available`, `429 too-many-unfinished-invocations` |
| `listAgentConversationTurns` | `GET /api/v1/agent-conversations/{conversationId}/turns?cursor=&limit=` | `If-None-Match` | `200` `{ turns: [{ invocationId, agentId, question, state, submittedAt, endedAt, failureReason, advice }], nextCursor }`, `ETag`; `304` | `404` |
| `getAgentInvocation` | `GET /api/v1/agent-invocations/{invocationId}` | `If-None-Match` | `200` `{ invocationId, conversationId, agentId, route, state, attemptCount, failureReason, advice, submittedAt, endedAt, retryAfterSeconds }`, `ETag`, `Retry-After` while unfinished; `304` | `404` |
| `getAgentInvocationLineage` | `GET /api/v1/agent-invocations/{invocationId}/lineage` | `If-None-Match` | `200` `{ invocationId, policyVersion, entries: [{ sequence, kind, attemptId, attemptNumber, recordedAt, details }], advice }`, `ETag`; `304` | `404` |

The conversation representation is
`{ conversationId, matchId, analysisRunId, analysisRunIsCurrent, teamId, state, participants: [{ agentId, addedAt }], createdAt, version }`;
`analysisRunIsCurrent` is false once the run is superseded.

## Workload operations

Token-authenticated with `Authorization: DPoP <token>` and a `DPoP` proof (policy `AgentWorkload`); member sessions
and Analyst Manager tokens get `401`. HTTPS is required, as for Analyst Manager operations.

| Operation id | Method and path | Request | Success | Refusals |
| --- | --- | --- | --- | --- |
| `issueAgentWorkloadToken` | `POST /api/v1/agent-orchestration/workload/token` | form: `grant_type=client_credentials`, `client_id`, `client_assertion_type`, `client_assertion`, `scope=agent-workload`; `DPoP` header | `200` `{ access_token, token_type: "DPoP", expires_in }` | `400` OAuth errors (`invalid_client`, `invalid_dpop_proof`, `invalid_scope`, `unsupported_grant_type`) |
| `issueAgentWorkloadQueueCredentials` | `POST /api/v1/agent-orchestration/workload/queue-credentials` | `{ userPublicKey }` | `200` `{ userJwt, expiresAt, queueUrl, stream, consumer, inboxPrefix }` | `400 invalid-user-key`, `409 queue-credentials-unavailable` (mode `None`) |
| `claimAgentInvocation` | `POST /api/v1/agent-orchestration/invocations/{invocationId}/claims` | `{ operationId }` | `201` execution envelope (`execution-envelope/v1`); replay `200` | `404`, `409 claim-obsolete`, `409 operation-id-reused`, `409 agent-scope-lost` |
| `renewAgentAttemptLease` | `POST /api/v1/agent-orchestration/attempts/{attemptId}/lease` | `{ fencingToken }` | `200` `{ attemptId, leaseExpiresAt }` | `404`, `409 agent-attempt-stale` |
| `recordAgentCheckpoint` | `POST /api/v1/agent-orchestration/attempts/{attemptId}/checkpoints` | `attempt-report/v1` checkpoint | `200` `{ attemptId, sequence, replayed }` | `400`, `404`, `409 agent-attempt-stale`, `409 operation-id-reused` |
| `completeAgentAttempt` | `POST /api/v1/agent-orchestration/attempts/{attemptId}/completion` | `attempt-report/v1` completion | `200` `{ attemptId, invocationId, invocationState: "succeeded", replayed }` | `400`, `404`, `409 agent-attempt-stale`, `409 operation-id-reused` |
| `failAgentAttempt` | `POST /api/v1/agent-orchestration/attempts/{attemptId}/failure` | `attempt-report/v1` failure | `200` `{ attemptId, invocationId, invocationState, replayed }` | `400`, `404`, `409 agent-attempt-stale`, `409 operation-id-reused` |

`409 agent-attempt-stale` carries `disposition`: `attempt-not-current`, `lease-expired`, `attempt-fenced`,
`invocation-ended`, or `scope-lost`.

## Machine-readable contracts (`contracts/agent-orchestration/`)

- `work-notification/v1`: `{ schemaVersion: "1.0.0", invocationId, route, agentId, readyOccurrence, correlationId }`;
  additional properties are refused, so no content can be added.
- `execution-envelope/v1`: `{ schemaVersion, invocationId, conversationId, attemptId, attemptNumber, fencingToken,
  leaseExpiresAt, leaseSeconds, deadlineAt, correlationId, route, agentId, question, history: [{ invocationId, agentId,
  question, advice }], scope: { teamId, matchId, analysisRunId }, limits: { maxAdviceCharacters,
  maxCheckpointNoteCharacters } }`.
- `attempt-report/v1`: one of `checkpoint { operationId, fencingToken, kind, note }`,
  `completion { operationId, fencingToken, advice, executionReferences { runtime, modelProvider, model, deployment,
  promptRef, toolPolicyRef, safetyPolicyRef } }`, or `failure { operationId, fencingToken, reasonCode, retryable }`.

Each has valid and invalid golden fixtures (for example a notification carrying `question`, an envelope without
`fencingToken`, a completion without `executionReferences`).
