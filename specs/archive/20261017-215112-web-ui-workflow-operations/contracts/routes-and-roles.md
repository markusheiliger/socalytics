# Contract: Operations Routes and Roles

Extends the Web UI's route table (`src/clients/web/src/router.tsx`) and navigation
(`src/clients/web/src/shell/navItems.ts`). Every route below is wrapped in the existing `clubAdminOnly` guard
(`RequireRole allow={isClubAdmin}`): members without the Club Admin role get the not-permitted view and no operations
request is sent (FR-001). The platform stays authoritative: a `403` from any operation shows the not-permitted view,
and a `404` from `getOutboxMessage` shows the not-found view (FR-002).

## Navigation

| Key | Target | Shown when | Position |
| --- | --- | --- | --- |
| `operations` | `/operations` | `isClubAdmin(member)` | after `registry` when that entry exists, otherwise after `analystManagers` |

The label comes from `shell.json` (`nav.operations`, English "Operations"). No existing entry changes address or
position (FR-003).

## Routes

| Path | View | Purpose | Spec |
| --- | --- | --- | --- |
| `/operations` | `OperationsSummaryPage` | Analysis summary with observation time, refresh, links to filtered lists, "Run reconciliation" | US1, US4, FR-004 to FR-007, FR-014 |
| `/operations/outbox-messages` | `OutboxMessagesPage` | Outgoing messages; search parameters `state`, `messageType`; next, previous, refresh | US2, FR-008, FR-009 |
| `/operations/outbox-messages/:messageId` | `OutboxMessagePage` | Every field of one outgoing message | US2, FR-010, FR-011 |
| `/operations/notification-receipts` | `NotificationReceiptsPage` | Receipts; search parameter `outcome`; next, previous, refresh | US3, FR-012, FR-013 |

`:messageId` must be a UUID; otherwise the not-found view is shown without a request. Search parameter values outside
the known sets ([data-model.md](../data-model.md#filters)) are ignored. Continuation tokens never appear in addresses.

## Confirmation

| Action | Dialog | Must state |
| --- | --- | --- |
| Run reconciliation | `ReconcileDialog` | that the platform queues every currently eligible ready job and unpublished notification for publication again from its durable state; that it never authorizes work, drops jobs, publishes blocked work, or changes accepted results; that it may take a while; that it is recorded in the audit trail |

Cancelling sends nothing. Confirming sends exactly one `reconcileAnalysisPublication` request, disables the confirm
control with an announced progress state until the answer, then shows the outcome (ready jobs and notifications queued
again, start and completion times, or that nothing needed to be queued again) or the refusal, and never repeats the
request. A successful outcome invalidates the summary so it is read again (FR-015, FR-016, FR-017).

## States per view

Every view has loading, data, empty, refused (not-permitted view), error with "Try again", and offline
(connection-required) states (FR-021). Every failure other than not-permitted, signed-out, not-found, and the
expired list position (including `429`, `5xx`, and unexpected answers) is shown as a recoverable error with "Try
again". The summary and lists keep previously shown values during a refresh and after a failed refresh.
