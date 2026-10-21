---

description: "Task list for Web UI Workflow Operations for Club Admins"
---

# Tasks: Web UI Workflow Operations for Club Admins

**Input**: Design documents from `specs/20261017-215112-web-ui-workflow-operations/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: Required. FR-023 asks for automated tests of the adapter mapping, filters and paging, the reconciliation confirmation, its cancellation, duplicate prevention, and refusals, role-based access, unknown values, accessibility rule checks, the online-only rule, and a browser test against the running platform; test work is part of the tasks below.

**Organization**: Tasks are grouped by user story so each story can be implemented and tested independently. All paths are relative to the repository root; Web UI paths start with `src/clients/web/`.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; their files never overlap. Parallel tasks never change `src/clients/web/src/i18n/en/operations.json`, `src/clients/web/src/router.tsx`, or `src/clients/web/tests/e2e/simulated/operations.spec.ts`.
- **[Story]**: Which user story this task belongs to (US1–US5).
- Every task names the files it creates or changes.

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: The translation texts and client models every view and the adapter use.

- [x] T001 [P] Create the `operations` translation namespace `src/clients/web/src/i18n/en/operations.json` with English texts for every heading, label, action, state, and message of `specs/20261017-215112-web-ui-workflow-operations/contracts/routes-and-roles.md`: the summary (workflow definition resolvable or not, "New analysis runs cannot start until the default workflow resolves", reason category, since, each count, "Nothing is pending", "Observed at {{time}}", refresh), display names for the run states `Requested`, `Running`, `Completed`, `PartiallyCompleted`, `Failed`, `Cancelled`, the outgoing message states `pending`, `published`, `failed`, `discarded`, the message types `matches.recordings-finalized`, `analysis.ready-work`, `analysis.run-state-changed`, and the receipt outcomes `run-created`, `run-exists`, `rejected`, filter labels with an "All" choice, every field of `specs/20261017-215112-web-ui-workflow-operations/data-model.md`, "absent", copy, copied, copy failed, next page, previous page, empty states ("No outgoing message matches", "No outgoing messages yet", "No notification receipts yet", clear filters), "The list position expired; showing the first page again", the reconciliation confirmation text (queues every currently eligible ready job and unpublished notification for publication again from durable state; never authorizes work, drops jobs, publishes blocked work, or changes accepted results; may take a while; recorded in the audit trail), progress, outcome ("{{jobs}} ready jobs and {{notifications}} notifications were queued again", "Nothing needed to be queued again", started and completed), the refusal "The reconciliation did not complete; nothing was changed by the Web UI; try again later", and duration units; add `nav.operations` ("Operations") to `src/clients/web/src/i18n/en/shell.json`; register the namespace in `ns` and `resources` of `src/clients/web/src/i18n/index.ts` (FR-004 to FR-017, FR-020, FR-022)
- [ ] T002 [P] Create the application-owned models of `specs/20261017-215112-web-ui-workflow-operations/data-model.md` in `src/clients/web/src/platform/models/operations.ts`: `OperationalSummary` (with `runsByState: { state: string; count: number }[]` and `oldestPendingAgeSeconds: number | undefined`), `OutboxMessage`, `NotificationReceipt`, `Page<T>` (`items`, `continuationToken: string | undefined`), `ReconciliationOutcome`, and the filter unions `OutboxState` (`pending`, `published`, `failed`, `discarded`), `OutboxMessageType` (`matches.recordings-finalized`, `analysis.ready-work`, `analysis.run-state-changed`), and `ReceiptOutcome` (`run-created`, `run-exists`, `rejected`); enumerated fields of returned records are their known values or `string` ("unknown values are shown raw") (FR-022)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Schema evidence, the adapter, shared view helpers, and the simulated API fixtures.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

- [ ] T003 [P] Create `src/platform/Tests/SocAlytics.Platform.Host.Tests/WorkflowOperationsOpenApiTests.cs`: for `getAnalysisOperationalSummary`, `listOutboxMessages`, `getOutboxMessage`, `listNotificationReceipts`, and `reconcileAnalysisPublication`, the running API's OpenAPI document references the typed `200` `application/json` schemas `AnalysisOperationalSummaryDto`, `OutboxMessagePageDto`, `OutboxMessageDto`, `NotificationReceiptPageDto`, and `ReconciliationResultDto`, and the list operations declare the query parameters `state`, `messageType`, `outcome` (as applicable), `pageSize`, and `continuationToken`; no endpoint or snapshot change (FR-018; research.md § R1)
- [ ] T004 [P] Create `src/clients/web/src/platform/adapters/operationsAdapter.ts` with `createOperationsAdapter(api)` as specified in `specs/20261017-215112-web-ui-workflow-operations/contracts/operations-adapter.md` (using `sendFor` and the generated request builders for the five operations; filters and `continuationToken` sent only when set, `pageSize` never sent; numbers normalized from `integer | string`, timestamps to `Date`, `null` to `undefined`, `runsByState` read from `additionalData` and ordered known states first in lifecycle order then unknown ones; no retries, caching, or storage), and `src/clients/web/tests/unit/platform/adapters/operationsAdapter.test.ts` covering every mapping rule, the omitted parameters, and the failure kinds for `403`, `404`, `400` with field `continuationToken`, and `503` (FR-018, FR-022; research.md § R2)
- [ ] T005 [P] Create `src/clients/web/src/features/operations/formatOperations.ts` (local timestamps with the exact ISO value available, durations from seconds in seconds/minutes/hours/days with the exact seconds available, absent values, raw fallback for unknown enumerations and categories) and `src/clients/web/src/features/operations/operationsFilters.ts` (the known values of `data-model.md` § Filters, parsing of the `state`, `messageType`, and `outcome` search parameters that ignores unknown values, and writing them back), with `src/clients/web/tests/unit/features/operations/format.test.ts` and `src/clients/web/tests/unit/features/operations/filters.test.ts` (FR-009, FR-013, FR-022; research.md § R5, R9)
- [ ] T006 [P] Create `src/clients/web/src/features/operations/useContinuationPages.ts`, a hook over TanStack Query that keeps an in-memory stack of continuation tokens per filter key, offers `next`, `previous`, and `refresh` (refresh and filter changes return to the first page), keeps previous data while loading (`keepPreviousData`, `retry: false`, `refetchOnWindowFocus: false`), and resets to the first page with an `expired` flag when a request fails with a `programmingError` naming the field `continuationToken`; with `src/clients/web/tests/unit/features/operations/paging.test.ts` covering forward and back without skips or repeats, refresh, filter change, and the expired-token reset (FR-008, FR-012; research.md § R4)
- [ ] T007 [P] Extend `src/clients/web/tests/e2e/support/simulatedApi.ts` with in-memory operational fixtures and handlers for the five operations: a summary (including an unknown run state and an unresolvable workflow definition variant), outgoing messages in every state and type across more than one page (one with a markup subject, one with an unknown failure category), receipts with each outcome across more than one page, filtering and continuation tokens like the platform, and switches that answer `403`, `503`, or `400` for `continuationToken`, and record how many reconciliation requests were received (FR-023; research.md § R11)

**Checkpoint**: Adapter, helpers, and fixtures are ready; the Host test pins the schemas.

---

## Phase 3: User Story 1 - See the analysis workflow's health at a glance (Priority: P1) 🎯 MVP

**Goal**: Club Admins open the Operations area and read the analysis summary with its observation time and a manual refresh.

**Independent Test**: In the `simulated` project a Club Admin opens **Operations** and sees every summary value and the observation time, refreshes it, and follows a count's link; a Coach sees no entry and the not-permitted view.

- [ ] T008 [US1] Create `src/clients/web/src/features/operations/OperationsSummaryPage.tsx` (route `/operations`): the workflow definition state with category and since-time, the "New analysis runs cannot start until the default workflow resolves" statement when not resolvable, run counts per reported state, runs with blocked nodes, stale attempts, rejected notifications, pending and failed outgoing messages, the oldest pending age or "Nothing is pending", and "Observed at"; a **Refresh** action that keeps the values visible and announces the read; links from the pending and failed counts to `/operations/outbox-messages?state=pending|failed` and from the rejected count to `/operations/notification-receipts?outcome=rejected`; loading, error with "Try again" that keeps the last values (for every failure listed as recoverable in `contracts/routes-and-roles.md` § States per view, including `429` and `5xx`), and not-permitted states; with `src/clients/web/tests/unit/features/operations/summary.test.tsx` (FR-004 to FR-007, FR-021, FR-022)
- [ ] T009 [US1] Add the `/operations` route behind `clubAdminOnly` in `src/clients/web/src/router.tsx` and the `operations` navigation entry (`allow: isClubAdmin`, after the `registry` entry when it exists, otherwise directly after `analystManagers`, extending the `labelKey` union) in `src/clients/web/src/shell/navItems.ts`; extend `src/clients/web/tests/unit/shell/navigation.test.tsx` for the entry's presence for Club Admins, absence for Registrars, Coaches, and Viewers, and its position, with every existing entry unchanged (FR-001, FR-003; contracts/routes-and-roles.md § Navigation)
- [ ] T010 [US1] Create `src/clients/web/tests/e2e/simulated/operations.spec.ts` with the `Summary` block: a Club Admin opens **Operations** from the navigation, sees every summary value and the observation time, refreshes and sees the new observation time, sees the unresolvable-definition statement in that variant, and follows the failed-count link; a Coach has no **Operations** entry and `/operations` shows the not-permitted view without an operations request (FR-001, FR-004 to FR-007; SC-003)

**Checkpoint**: The summary is usable on its own (MVP).

---

## Phase 4: User Story 2 - Find stuck publications (Priority: P1)

**Goal**: Club Admins filter and page the outgoing messages and open one message.

**Independent Test**: In the `simulated` project each filter combination shows the expected fixture messages, paging forward and back keeps page boundaries, the detail shows every field, and an unknown or malformed identifier shows the not-found view.

### Outgoing message views

- [ ] T011 [P] [US2] Create `src/clients/web/src/features/operations/OutboxMessagesPage.tsx` (route `/operations/outbox-messages`): state and message-type filters from the address via `operationsFilters.ts`, rows with type, state, attempt count, creation time, and failure category linking to the detail, next, previous, and refresh through `useContinuationPages.ts`, the expired-position explanation, the empty states with "clear filters", and loading, recoverable error (as in `contracts/routes-and-roles.md` § States per view), and not-permitted states; with `src/clients/web/tests/unit/features/operations/outbox.test.tsx` (FR-008, FR-009, FR-011, FR-021, FR-022)
- [ ] T012 [P] [US2] Create `src/clients/web/src/features/operations/OutboxMessagePage.tsx` (route `/operations/outbox-messages/:messageId`): UUID validation before any request (not-found view otherwise), every field of `data-model.md` § OutboxMessage with absent values shown as absent, readable durations, platform text as plain text, full identifiers with copy controls and no links for run, logical job, and capability identifiers, no payload and no retry, edit, discard, or delete action, `404` → not-found view, `403` → not-permitted view; with `src/clients/web/tests/unit/features/operations/outboxMessage.test.tsx` (FR-002, FR-010, FR-011, FR-022)

### Outgoing message wiring

- [ ] T013 [US2] Add the routes `/operations/outbox-messages` and `/operations/outbox-messages/:messageId` behind `clubAdminOnly` to `src/clients/web/src/router.tsx` (FR-001; contracts/routes-and-roles.md § Routes)
- [ ] T014 [US2] Add the `Outgoing messages` block to `src/clients/web/tests/e2e/simulated/operations.spec.ts`: filter by state, by type, and both, with the filters in the address and an unknown address value ignored; page forward and back over two fixture pages without skips or repeats; refresh returns to the first page; the invalid-token switch shows the expired-position explanation and the first page; open a message and see every field, the markup subject as text, and the unknown failure category raw; an unknown and a malformed identifier show the not-found view (FR-002, FR-008 to FR-011, FR-022; SC-001, SC-002)

**Checkpoint**: Stuck publications are visible without raw API calls.

---

## Phase 5: User Story 3 - Check what happened to finalized-recording notifications (Priority: P2)

**Goal**: Club Admins filter and page the notification receipts.

**Independent Test**: In the `simulated` project each outcome filter shows exactly the matching fixture receipts with their fields, paging works, and an empty result states that no receipt matches.

- [ ] T015 [US3] Create `src/clients/web/src/features/operations/NotificationReceiptsPage.tsx` (route `/operations/notification-receipts`): the outcome filter from the address, rows with message identifier, type, recording-set version identifier, outcome, rejection category (worded when known, raw otherwise), run identifier, and receipt time, identifiers as plain copyable text, paging and refresh through `useContinuationPages.ts`, empty, loading, error, and not-permitted states; with `src/clients/web/tests/unit/features/operations/receipts.test.tsx` (FR-012, FR-013, FR-021, FR-022)
- [ ] T016 [US3] Add the route `/operations/notification-receipts` behind `clubAdminOnly` to `src/clients/web/src/router.tsx` and the `Notification receipts` block to `src/clients/web/tests/e2e/simulated/operations.spec.ts`: each outcome filter, paging over two fixture pages, the rejected-count link from the summary, and the empty state (FR-012, FR-013; SC-002)

**Checkpoint**: Receipts are visible and filterable.

---

## Phase 6: User Story 4 - Run a reconciliation deliberately (Priority: P2)

**Goal**: Club Admins start a confirmed reconciliation from the summary and see its outcome.

**Independent Test**: In the `simulated` project the confirmation states its text, cancelling sends nothing, confirming sends exactly one request even with repeated clicks, the outcome shows counts and times, the summary is read again, `503` is explained, and `403` leads to the not-permitted view.

- [ ] T017 [US4] Create `src/clients/web/src/features/operations/ReconcileDialog.tsx`: the confirmation text of `contracts/routes-and-roles.md` § Confirmation, cancel without request, confirm through a `useMutation` with `retry: false` that disables the confirm control from the first click with an announced progress state, the outcome (counts, start and completion times, or "Nothing needed to be queued again"), the refusal explanation for `unreachable` and `unexpected` failures, and the not-permitted view for `notPermitted`; with `src/clients/web/tests/unit/features/operations/reconcile.test.tsx` covering cancellation, one request under repeated clicks, both outcomes, and each refusal (FR-014 to FR-017; research.md § R7)
- [ ] T018 [US4] Add the always-available **Run reconciliation** action to `src/clients/web/src/features/operations/OperationsSummaryPage.tsx` that opens `ReconcileDialog` and invalidates the summary query after a successful outcome, and extend `src/clients/web/tests/unit/features/operations/summary.test.tsx` for the action's presence with zero and non-zero counts and the summary read after the outcome (FR-005, FR-014, FR-016)
- [ ] T019 [US4] Add the `Reconciliation` block to `src/clients/web/tests/e2e/simulated/operations.spec.ts`: cancel sends nothing; confirm with a double click sends exactly one request (fixture counter) and shows the outcome and a newer observation time; the `503` switch shows the refusal explanation without a second request; the `403` switch shows the not-permitted view (FR-014 to FR-017; SC-004)

**Checkpoint**: The only state-changing action is confirmed, single, and explained.

---

## Phase 7: User Story 5 - Use the area safely in every supported setting (Priority: P3)

**Goal**: Keyboard, screen reader, accessibility, and online-only evidence for every view, dialog, and state.

**Independent Test**: Automated checks reach every view and action by keyboard with no axe violations, find no operations response in caches or storage, and show the connection-required state offline.

### Quality evidence

- [ ] T020 [P] [US5] Add the `Quality` block to `src/clients/web/tests/e2e/simulated/operations.spec.ts`: keyboard-only use of every view, filter, paging control, copy control, and the dialog with visible focus; axe checks (`tests/e2e/support/axe.ts`) on each view in its loading, data, empty, refused, and error states and on the dialog's confirmation, progress, outcome, and refusal; no `/api/v1/operations` response in Cache Storage, `localStorage`, `sessionStorage`, or IndexedDB after the session; offline navigation to `/operations` and offline confirmation show the connection-required state and send nothing (FR-019, FR-020, FR-021; SC-005, SC-006)
- [ ] T021 [P] [US5] Extend `src/clients/web/tests/unit/serviceWorker/shellWorker.test.ts` so requests to `/api/v1/operations/analysis/summary`, `/api/v1/operations/outbox-messages…`, `/api/v1/operations/analysis/notification-receipts…`, and `/api/v1/operations/analysis/reconciliations` are passed to the network and never cached (FR-019)

**Checkpoint**: Every user story is complete and verified in the simulated project.

---

## Phase 8: Polish & Cross-Cutting Concerns

**Purpose**: Platform browser evidence, documentation, and final validation.

- [ ] T022 [P] Create `src/clients/web/tests/e2e/platform/operations.spec.ts` against the AppHost `e2e` mode: sign in as the seeded Club Admin, open **Operations**, read the summary with its observation time, open the outgoing messages list and, when it has entries from the earlier upload flow, one message's detail, open the receipts list, and run a reconciliation and see its counts and times; add the chained project `platform-operations` (`testMatch: /operations\.spec\.ts$/` in `./tests/e2e/platform`, depending on the last platform project at implementation time) to `src/clients/web/playwright.config.ts` so it runs last (FR-023; research.md § R11)
- [ ] T023 [P] Update the architecture and repository documentation so it is true for the merged state: make the narratives and every status statement listed in `## Architecture Updates` of `specs/20261017-215112-web-ui-workflow-operations/plan.md` true, re-reading `docs/architecture/` (at least `docs/architecture/client-applications.md` and `docs/architecture/platform-implementation.md`), `AGENTS.md` "Current State", `src/clients/README.md`, and the root `README.md` at implementation time and correcting every statement this feature makes false, including planned, future-tense, or "decided, not yet implemented" wording about the workflow operations area, with the plan's rewrites as examples rather than the whole job; run `node .github/scripts/check-markdown.mjs` (constitution Principle I)
- [ ] T024 Run the validation of `specs/20261017-215112-web-ui-workflow-operations/quickstart.md`: `dotnet build src/platform/SocAlytics.Platform.slnx`, the `WorkflowOperationsOpenApiTests` Host tests, and in `src/clients/web` `npm run build`, `npm test`, and `npm run test:e2e` (all projects), and fix any failure in the files of this feature

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: no dependencies.
- **Foundational (Phase 2)**: depends on Setup (T004 uses T002's models; T005 and T006 use T002's filter unions). Blocks every user story.
- **US1 (Phase 3)**: depends on Foundational; MVP. T008 before T009 (route needs the page) before T010.
- **US2 (Phase 4)**: depends on Foundational and on T009 (navigation and the `/operations` route) and T010 (the simulated spec file it extends).
- **US3 (Phase 5)**: depends on Foundational and T010; independent of US2 except that both extend `router.tsx` and `operations.spec.ts` sequentially.
- **US4 (Phase 6)**: depends on US1 (it extends `OperationsSummaryPage.tsx` and `summary.test.tsx`).
- **US5 (Phase 7)**: depends on US1–US4 (it checks every view and the dialog).
- **Polish (Phase 8)**: depends on all stories.

### Within Each Phase

- Each `###` heading starts a group; consecutive `[P]` tasks under it run in parallel.
- Tasks that change `src/clients/web/src/router.tsx` (T009, T013, T016) or add blocks to `src/clients/web/tests/e2e/simulated/operations.spec.ts` (T010, T014, T016, T019, T020) never run in parallel with each other.

### Parallel Opportunities

- Phase 1: T001, T002 (translations, models).
- Phase 2: T003, T004, T005, T006, T007 (Host test, adapter, formatting and filters, paging hook, simulated API).
- Phase 4: T011, T012 (list and detail views).
- Phase 7: T020, T021 (browser quality evidence, service-worker test).
- Phase 8: T022, T023 (platform browser test, documentation), then T024.

---

## Parallel Example: Foundational Phase

```text
Task: "T003 [P] WorkflowOperationsOpenApiTests.cs"
Task: "T004 [P] operationsAdapter.ts with operationsAdapter.test.ts"
Task: "T005 [P] formatOperations.ts and operationsFilters.ts with their tests"
Task: "T006 [P] useContinuationPages.ts with paging.test.ts"
Task: "T007 [P] simulatedApi.ts operational fixtures"
```

## Parallel Example: User Story 2

```text
Task: "T011 [P] [US2] OutboxMessagesPage.tsx with outbox.test.tsx"
Task: "T012 [P] [US2] OutboxMessagePage.tsx with outboxMessage.test.tsx"
Then: "T013 [US2] routes", "T014 [US2] simulated block"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1 and Phase 2 (texts, models, adapter, helpers, fixtures).
2. Complete Phase 3: Club Admins read the analysis summary without raw API calls.
3. Stop and validate with the `Summary` block and the unit tests.

### Incremental Delivery

1. US1 → summary (MVP).
2. US2 → outgoing messages and detail.
3. US3 → notification receipts.
4. US4 → confirmed reconciliation.
5. US5 → accessibility and online-only evidence; then platform browser evidence and documentation.
