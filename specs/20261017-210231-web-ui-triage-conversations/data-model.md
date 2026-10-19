# Data Model: Web UI Match Triage Conversations

The Web UI keeps no persistent data. These are the application-owned client models in
`src/clients/web/src/platform/models/agents.ts` that `agentConversationAdapter` maps from the generated client, and the
in-memory view state of the triage views. Platform field names follow
[the sibling's API contract](../archive/20261017-204332-agent-orchestration-invocations/contracts/agent-orchestration-api.md).

## EnabledAgent

| Field | Type | Source and rules |
| --- | --- | --- |
| `agentId` | string | `listAvailableAgents.agents[].agentId`; required. |
| `group` | string | `specialist` for the first wave; other values kept raw. |
| `domain` | string | Platform text, rendered as text. |

The readable name is not a field: views resolve `agents.<agentId>` in the `triage` namespace and fall back to `agentId`.

## TriageConversation

| Field | Type | Source and rules |
| --- | --- | --- |
| `conversationId`, `matchId`, `analysisRunId`, `teamId` | string | Required; `matchId` must equal the route's match, else the view shows not found. |
| `analysisRunIsCurrent` | boolean | `false` shows the superseded-run notice (FR-004). |
| `state` | string | `active`; other values shown raw. |
| `participants` | `{ agentId, addedAt }[]` | In platform order. |
| `createdAt` | string (ISO 8601) | Shown with the Web UI's date formatting. |
| `version` | number | Sent as `If-Match` when adding a participant. |
| `etag` | string | From the read's `ETag`; sent as `If-None-Match`. |

`ConversationSummary` (list item) has the same fields without `etag`; the list is shown in platform order (newest
first).

## Turn

| Field | Type | Source and rules |
| --- | --- | --- |
| `invocationId`, `agentId` | string | Required. |
| `question` | string | Plain text; rendered as text with preserved line breaks. |
| `state` | `TurnState` | API values `accepted/queued` and `claimed/running` are pending; `succeeded`, `failed`; unknown values kept raw and shown raw, and polling continues for them until a terminal answer or `404`. |
| `failureReason` | `FailureReason` | `attempts-exhausted`, `execution-failed`, `scope-lost` worded; others raw. |
| `submittedAt`, `endedAt` | string | `endedAt` once terminal. |
| `advice` | `Advice` or absent | Present once `succeeded`. |

`TurnPage` is `{ turns: Turn[], nextCursor?: string, etag: string }`.

## Advice

| Field | Type | Source and rules |
| --- | --- | --- |
| `text` | string | Rendered unaltered as text (FR-012, FR-022). |
| `evidenceReferences` | `EvidenceReference[]` | Empty in the first platform slice; an empty list shows the explicit "none recorded" message (FR-013). |
| `executionReferences` | `ExecutionReferences` or absent | Only from the lineage read: `runtime`, `modelProvider`, `model`, `deployment`, `promptRef`, `toolPolicyRef`, `safetyPolicyRef` (opaque strings). |

## InvocationStatus

| Field | Type | Source and rules |
| --- | --- | --- |
| `invocationId`, `conversationId`, `agentId`, `route` | string | |
| `state`, `failureReason` | as in Turn | |
| `attemptCount` | number | Shown in the turn's status line. |
| `retryAfterSeconds` | number or absent | From the body or `Retry-After`; seeds the polling delay. |
| `etag` | string | For the next conditional read. |

A conditional read returns `{ kind: 'unchanged' }` or `{ kind: 'changed', value, etag, retryAfterSeconds }`.

## InvocationLineage

| Field | Type | Source and rules |
| --- | --- | --- |
| `invocationId`, `policyVersion` | string | |
| `entries` | `{ sequence, kind, attemptId?, attemptNumber?, recordedAt, details }[]` | In `sequence` order; `kind` worded when known, raw otherwise; `details` shown as bounded key and value text. |
| `advice` | `Advice` or absent | With `executionReferences`. |
| `etag` | string | |

## SubmissionAccepted

`{ conversationId, invocationId, statusLocation, retryAfterSeconds }` from the `202` answer; `invocationId` creates the
pending turn and starts polling.

## TriageOutcome (refusals)

`tooManyUnfinished`, `agentNotAvailable`, `agentNotParticipant`, `participantExists`, `runNotEligible`,
`invalidQuestion` (with field errors), `notPermitted`, `staleVersion` (`412`), `notFound`, `unknownOutcome`
(network failure on a state-changing call), and the shared failure kinds otherwise.

## In-memory view state (never persisted)

| State | Owner | Lifecycle |
| --- | --- | --- |
| Question draft: `text`, `agentId`, `idempotencyKey?`, `outcome?` | `useQuestionDraft` | Key created on first send of an intent; kept while the outcome is unknown and text and agent are unchanged; dropped on change or a definite answer; discarded on unmount and sign-out. |
| Conversation creation intent: `analysisRunId`, `idempotencyKey?` | start dialog | Same rule as the draft. |
| Pending turns: `invocationId`, `agentId`, `question`, `submittedAt` | conversation view | Removed when a history page contains the `invocationId`. |
| Polling: `delaySeconds`, `etag`, `timer` | `useInvocationPolling` | Delay = suggested interval, × 1.5 per unchanged answer, max 15 s, reset on change; paused while hidden or offline; stopped on terminal state or `404`. |
| Loaded history pages | TanStack infinite query (memory) | Cleared on sign-out (`queryClient.clear()`). |
