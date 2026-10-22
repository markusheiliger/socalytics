# Contract: Operations Adapter

`src/clients/web/src/platform/adapters/operationsAdapter.ts` is the only code that calls the five workflow operations.
It uses the generated `PlatformApiClient` request builders through the existing helpers in
`src/clients/web/src/platform/adapters/kiota.ts` (`sendFor`, `errorMappings`) and returns the view models of
[data-model.md](../data-model.md). Failures are thrown as `PlatformFailure` from `src/clients/web/src/platform/failure.ts`;
the adapter adds no new failure kinds.

```ts
export interface OperationsAdapter {
  summary(): Promise<OperationalSummary>;
  listOutboxMessages(filter: { state?: OutboxState; messageType?: OutboxMessageType }, continuationToken?: string): Promise<Page<OutboxMessage>>;
  getOutboxMessage(messageId: string): Promise<OutboxMessage>;
  listNotificationReceipts(filter: { outcome?: ReceiptOutcome }, continuationToken?: string): Promise<Page<NotificationReceipt>>;
  reconcile(): Promise<ReconciliationOutcome>;
}

export function createOperationsAdapter(api: PlatformApi): OperationsAdapter;
```

| Method | Operation | Request | Success | Failures the views handle |
| --- | --- | --- | --- | --- |
| `summary` | `getAnalysisOperationalSummary` (`GET /api/v1/operations/analysis/summary`) | none | `OperationalSummary` | `notPermitted`, `sessionEnded`, `unreachable`, `unexpected` |
| `listOutboxMessages` | `listOutboxMessages` (`GET /api/v1/operations/outbox-messages`) | `state`, `messageType` only when set; `continuationToken` only when given; never `pageSize` | `Page<OutboxMessage>` | as above, plus `programmingError` with field `continuationToken` (restart at the first page) |
| `getOutboxMessage` | `getOutboxMessage` (`GET /api/v1/operations/outbox-messages/{messageId}`) | UUID path parameter | `OutboxMessage` | as above, plus status `404` (not-found view) |
| `listNotificationReceipts` | `listNotificationReceipts` (`GET /api/v1/operations/analysis/notification-receipts`) | `outcome` only when set; `continuationToken` only when given | `Page<NotificationReceipt>` | as for `listOutboxMessages` |
| `reconcile` | `reconcileAnalysisPublication` (`POST /api/v1/operations/analysis/reconciliations`) | no body; antiforgery header from the existing middleware; no `Idempotency-Key`, no `If-Match` | `ReconciliationOutcome` | `notPermitted`, `sessionEnded`, `sessionProblem`, `unreachable` (including `503`), `unexpected` |

Mapping rules:

- Numeric fields typed `integer | string` become `number`; timestamps become `Date`; absent or `null` values become
  `undefined` ([research R2](../research.md#r2-numeric-and-dictionary-values-in-the-generated-models)).
- `runsByState` is read from the generated model's `additionalData` and ordered as in research R2.
- Enumerated strings (states, types, outcomes, categories) pass through unchanged; wording is a view concern.
- The adapter never retries, caches, or stores responses, and never reads a payload field (none exists).

Unit tests (`tests/unit/platform/adapters/operationsAdapter.test.ts`) cover every mapping rule, the omission of unset
filters and `pageSize`, and the failure classification for `403`, `404`, `400` with `continuationToken`, and `503`.
