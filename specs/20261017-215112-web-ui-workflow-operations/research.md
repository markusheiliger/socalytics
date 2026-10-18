# Research: Web UI Workflow Operations for Club Admins

## R1. OpenAPI schemas for the operations

- **Decision**: Use the existing typed schemas of the committed snapshot
  (`src/platform/SocAlytics.Platform.Api/openapi/v1.json`): `AnalysisOperationalSummaryDto` with
  `WorkflowDefinitionDto`, `OutboxMessagePageDto`/`OutboxMessageDto`, `NotificationReceiptPageDto`/
  `NotificationReceiptDto`, and `ReconciliationResultDto`. Query parameters (`state`, `messageType`, `outcome`,
  `pageSize`, and `continuationToken`) are plain strings. Add a Host test (`WorkflowOperationsOpenApiTests`) that asserts
  each of the five operations references its typed response schema and declares its query parameters, so a later
  endpoint change cannot silently drop the generated models (FR-018). No snapshot refresh and no endpoint change.
- **Rationale**: The endpoints already declare `Produces<…>` and the snapshot contains the component schemas, unlike
  the Registry operations before [Web UI Analyst Registry Administration](../20261017-185438-web-ui-registry-admin/plan.md).
  The existing `PlatformHostTests` only assert that the operation ids exist.
- **Alternatives considered**: Refreshing the snapshot (nothing changed); untyped `fetch` calls (violate the adapter
  rule and Principle III).

## R2. Numeric and dictionary values in the generated models

- **Decision**: The adapter normalizes every numeric field the schema types as `integer | string` (counts, attempts,
  rounds, ages, lags) to `number` and treats a value that is not a finite integer as absent with the raw value kept
  for display. `runsByState` is an `additionalProperties` object, which Kiota exposes through `additionalData`; the
  adapter maps it to an ordered list of `{ state, count }` entries in the platform's order, known run states first in
  lifecycle order (`Requested`, `Running`, `Completed`, `PartiallyCompleted`, `Failed`, `Cancelled`) and unknown
  states afterwards with their raw names. A state the platform does not report is not shown.
- **Rationale**: Keeps views free of generated-model quirks (FR-022) and follows how existing adapters read
  `additionalData` (`recordingUploadAdapter.ts`).
- **Alternatives considered**: Showing zero for unreported states (contradicts the spec's edge case).

## R3. Navigation entry and routes

- **Decision**: `navItems` gains `{ to: '/operations', labelKey: 'operations', allow: isClubAdmin }`, placed after the
  Registry entry of [Web UI Analyst Registry Administration](../20261017-185438-web-ui-registry-admin/contracts/routes-and-roles.md)
  when that entry exists at implementation time and otherwise directly after `analystManagers`. Routes: `/operations`,
  `/operations/outbox-messages`, `/operations/outbox-messages/:messageId`, `/operations/notification-receipts`, each
  wrapped in the existing `clubAdminOnly` guard ([contracts/routes-and-roles.md](contracts/routes-and-roles.md)).
- **Rationale**: Keeps the Club Admin administration entries together (FR-003) without depending on the Registry
  spec's merge order; the `/operations` prefix mirrors the API's operation group.
- **Alternatives considered**: Nesting under `/club` (mixes club structure with stamp operations); a sub-menu (the
  navigation has none today).

## R4. Paging with continuation tokens

- **Decision**: `useContinuationPages` keeps, per filter combination, an in-memory stack of the continuation tokens
  that led to each visited page; "Next" requests with the current page's `continuationToken`, "Previous" re-requests
  the page with the token below it on the stack, and refresh or a filter change clears the stack and reads the first
  page. The address holds only the filters (`state`, `messageType`, `outcome`), never tokens. The adapter omits
  `pageSize`, so the platform default (50) applies. A `400` naming the field `continuationToken` (mapped to
  `programmingError` with that field error by `failure.ts`) resets to the first page and shows an explanation.
- **Rationale**: The platform pages forward only with opaque tokens; re-requesting a remembered token returns the same
  boundaries (FR-008), and tokens are not kept in browser storage or history (FR-019).
- **Alternatives considered**: "Load more" accumulating rows (no way back to a page boundary; unbounded memory);
  tokens in the address (stale tokens in shared links).

## R5. Filters from the address

- **Decision**: `operationsFilters.ts` defines the known values: states `pending`, `published`, `failed`,
  `discarded`; message types `matches.recordings-finalized`, `analysis.ready-work`, `analysis.run-state-changed`;
  outcomes `run-created`, `run-exists`, `rejected`. Address values outside these sets are ignored (not sent) and
  dropped from the address on the next filter change (FR-009, FR-013). Filter controls are MUI selects with an "all"
  choice; labels come from `operations.json`, with the raw value as fallback for any value the platform returns.
- **Rationale**: The platform refuses unknown values with `400`; ignoring them avoids a refused request from a stale
  link. Other features add their message types to the platform's list and to this file.
- **Alternatives considered**: Free-text filters (invite refusals); deriving choices from returned data (incomplete).

## R6. Reading and refreshing

- **Decision**: TanStack Query with `retry: false`, `refetchOnWindowFocus: false`, no `refetchInterval`, and
  `placeholderData: keepPreviousData` so values stay visible during a refresh; the summary shows the platform's
  `observedAt`, and a refresh failure keeps the last values marked with that time (FR-005, FR-021). A completed
  reconciliation invalidates the summary query.
- **Rationale**: The spec asks for manual refresh only; the fleet view's polling is not wanted here.
- **Alternatives considered**: Polling (out of scope: no dashboards or automatic refresh).

## R7. Reconciliation request

- **Decision**: `ReconcileDialog` uses a `useMutation` with `retry: false`; the confirm button is disabled from the
  first click until the answer, and the dialog cannot send twice. The operation needs no `Idempotency-Key` or
  `If-Match`; the antiforgery middleware adds the token. `200` shows the outcome (FR-016); `403` leads to the
  not-permitted view; `503`/`502`/`504` (`unreachable`) and other `5xx` (`unexpected`) show that the reconciliation did
  not complete and may be tried later (FR-017); an offline browser shows the connection-required state through the
  existing connectivity middleware. Leaving the view does not cancel the platform's work.
- **Rationale**: Matches the platform contract (`RunAnalysisReconciliationHandler`) and the Web UI's confirmation
  pattern (`RevokeDialog`).
- **Alternatives considered**: Optimistic display of an outcome (would invent data).

## R8. Detail view and not-found handling

- **Decision**: `OutboxMessagePage` validates `:messageId` as a UUID before any request and shows `NotFoundPage` for a
  malformed value; a `404` from `getOutboxMessage` also shows `NotFoundPage`, a `403` the `NotPermittedPage`.
  Identifiers are rendered as text with a copy control; run, logical job, and capability identifiers are not links
  (spec clarification).
- **Rationale**: The API route constrains `{messageId:guid}`; validating first avoids a pointless request.

## R9. Formatting

- **Decision**: `formatOperations.ts` formats timestamps in the member's local time (the existing date formatting of
  the Web UI) with the exact ISO value available as a tooltip and accessible description, durations from seconds into
  the largest sensible units (seconds, minutes, hours, days) with the exact seconds available, absent values with the
  `operations:absent` text, and unknown enumeration values as their raw text. Known failure and rejection categories
  get worded explanations where `operations.json` has a key; others show the raw category.
- **Rationale**: FR-022 and the spec's edge cases.

## R10. Online-only and storage

- **Decision**: No change to the service worker: it precaches only the integrity-verified shell and never caches API
  responses. The `simulated` tests assert that no `/api/v1/operations` response is in Cache Storage or in local,
  session, or IndexedDB storage after using the area, and that an offline browser shows the connection-required
  state (FR-019).
- **Rationale**: Same evidence pattern as the fleet and Registry areas.

## R11. Test fixtures and the platform test

- **Decision**: `tests/e2e/support/simulatedApi.ts` gains in-memory operational fixtures (a summary, outgoing messages
  in every state and type across more than one page, receipts with each outcome, and a reconciliation outcome) with
  handlers that honor the filters and continuation tokens and can be switched to `403`, `503`, and invalid-token
  answers. The `platform` test signs in as the seeded Club Admin of the e2e AppHost mode, finalizes nothing new, reads
  the summary, opens the outgoing messages and receipts lists (empty or not), opens a message when one exists, and
  runs a reconciliation, asserting that an outcome with counts and times is shown.
- **Rationale**: Deterministic coverage of every state in `simulated`; the `platform` test proves the generated client
  and adapter against the real API without forcing transport failures.
- **Alternatives considered**: Stopping NATS in the platform test to create failed messages (slow and flaky).
