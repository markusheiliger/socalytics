# Data Model: Web UI Workflow Operations for Club Admins

Client-side view models in `src/clients/web/src/platform/models/operations.ts`. They are produced only by
`operationsAdapter` from the generated client's models and live only in memory (TanStack Query cache). The platform
owns every value; the Web UI never derives state from them. Field sources are the DTOs in
`src/platform/SocAlytics.Platform.Api/Operations/WorkflowOperationsEndpoints.cs`.

## OperationalSummary (from `AnalysisOperationalSummaryDto`)

| Field | Type | Notes |
| --- | --- | --- |
| `workflowDefinition.state` | `string` | `resolvable`, `unresolvable`, or a raw unknown value |
| `workflowDefinition.category` | `string \| undefined` | reason category when not resolvable |
| `workflowDefinition.since` | `Date \| undefined` | |
| `runsByState` | `{ state: string; count: number }[]` | known states first in lifecycle order, then unknown ones; unreported states absent ([research R2](research.md#r2-numeric-and-dictionary-values-in-the-generated-models)) |
| `runsWithBlockedNodes` | `number` | |
| `staleAttempts` | `number` | |
| `rejectedNotifications` | `number` | links to receipts filtered by `rejected` |
| `outboxPending` | `number` | links to messages filtered by `pending` |
| `outboxFailed` | `number` | links to messages filtered by `failed` |
| `oldestPendingAgeSeconds` | `number \| undefined` | absent means nothing pending |
| `observedAt` | `Date` | shown with the summary |

## OutboxMessage (from `OutboxMessageDto`)

| Field | Type | Notes |
| --- | --- | --- |
| `messageId` | `string` (UUID) | route parameter of the detail view |
| `messageType` | `string` | known types worded, others raw |
| `contractVersion` | `string` | |
| `subject` | `string` | plain text |
| `state` | `string` | `pending`, `published`, `failed`, `discarded`, or raw |
| `attemptCount` | `number` | |
| `publicationRound` | `number` | |
| `lastAttemptAt`, `nextAttemptAt` | `Date \| undefined` | |
| `failureCategory` | `string \| undefined` | worded when known, raw otherwise |
| `createdAt` | `Date` | list order is the platform's |
| `publishedAt` | `Date \| undefined` | |
| `ageSeconds` | `number` | readable duration |
| `publicationLagSeconds` | `number \| undefined` | readable duration |
| `runId`, `logicalJobId` | `string \| undefined` | plain copyable text |
| `capabilityId` | `string \| undefined` | plain copyable text |

No payload field exists and none is requested (FR-011).

## NotificationReceipt (from `NotificationReceiptDto`)

| Field | Type | Notes |
| --- | --- | --- |
| `messageId` | `string` | |
| `messageType` | `string` | |
| `recordingSetVersionId` | `string \| undefined` | plain copyable text |
| `outcome` | `string` | `run-created`, `run-exists`, `rejected`, or raw |
| `rejectionCategory` | `string \| undefined` | worded when known, raw otherwise |
| `runId` | `string \| undefined` | plain copyable text |
| `receivedAt` | `Date` | |

## Page&lt;T&gt; (from `OutboxMessagePageDto`, `NotificationReceiptPageDto`)

| Field | Type | Notes |
| --- | --- | --- |
| `items` | `T[]` | platform order |
| `continuationToken` | `string \| undefined` | opaque; kept only in the in-memory page stack |

## ReconciliationOutcome (from `ReconciliationResultDto`)

| Field | Type | Notes |
| --- | --- | --- |
| `readyJobsRepublished` | `number` | |
| `notificationsRepublished` | `number` | both zero → "nothing needed to be queued again" |
| `startedAt`, `completedAt` | `Date` | |

## Filters

| Filter | Values sent | Address parameter |
| --- | --- | --- |
| Outgoing message state | `pending`, `published`, `failed`, `discarded` | `state` |
| Outgoing message type | `matches.recordings-finalized`, `analysis.ready-work`, `analysis.run-state-changed` | `messageType` |
| Receipt outcome | `run-created`, `run-exists`, `rejected` | `outcome` |

Unknown address values are ignored and never sent ([research R5](research.md#r5-filters-from-the-address)).
