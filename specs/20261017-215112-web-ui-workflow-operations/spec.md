# Feature Specification: Web UI Workflow Operations for Club Admins

**Feature Branch**: `20261017-215112-web-ui-workflow-operations`

**Created**: 2026-10-17

**Status**: Draft

**Input**: User description: "Web UI Workflow Operations for Club Admins: the platform exposes operational workflow inspection and reconciliation under `/api/v1/operations`, restricted to Club Admins until a dedicated stamp-operator role exists and audited when they change state (docs/architecture/platform-implementation.md#api-and-identity; `src/platform/SocAlytics.Platform.Api/Operations/WorkflowOperationsEndpoints.cs`: list and read outbox messages by state and message type, the analysis operational summary, the list of analysis notification receipts by outcome, and starting an analysis reconciliation), but the Web UI offers no screen for them, so a Club Admin has to call the API by hand to see stuck publications or failed notifications. This feature adds a connected Operations area to the Web UI for Club Admins: an analysis summary (the counts the summary operation returns, with when it was read and a manual refresh), outbox messages filterable by state and message type with paging and a detail view (identifiers, type, state, attempts, timestamps, last error category; no payload secrets, exactly what the API returns), notification receipts filterable by outcome with paging, and a run reconciliation action with confirmation that shows the reconciliation's outcome and states plainly what it does (it never authorizes work or overrides accepted state, per docs/architecture/job-processing.md#production-queue-recovery-and-backpressure). Non-admin members never see the area or its navigation entry; the API remains the authority for every check; explicit empty, loading, error, and refusal states. Everything goes through application-owned adapters over the generated Kiota client, with the existing navigation, i18n, accessibility, problem handling, and online-only PWA rules; if the committed OpenAPI snapshot lacks typed schemas for these operations, the plan adds them without changing operation behavior (as the registry admin spec did). Out of scope: new operational capabilities, retrying or editing individual messages, a stamp-operator role, dashboards, alerts, metrics charts, production operations tooling. Depends on Durable Analysis Workflow and Web UI Foundation (merged; archived under specs/archive/), Installable Web UI with Service Worker Updates (merged); aligns with `specs/archive/20261017-185438-web-ui-registry-admin` navigation. Implements docs/architecture/client-applications.md#web-ui (platform configuration and administration) for workflow operations and the inspection part of docs/architecture/platform-implementation.md#api-and-identity."

## Clarifications

### Session 2026-10-17

- Q: Where in the Operations area is the run-reconciliation action offered? → A: Only on the area's summary view, next to the counts it affects.
- Q: Is the reconciliation action available when the summary shows no pending or failed messages? → A: Yes, it is always available to Club Admins; the confirmation still applies, and the outcome states when nothing needed to be queued again.
- Q: Should the run, logical job, recording-set version, and capability identifiers in the operational views link to other Web UI views? → A: No; they are shown as plain, copyable text, because the operational records name no match and the run views are reached from a match.
- Q: Do the outgoing message and notification receipt lists offer a manual refresh? → A: Yes; refreshing re-reads the first page of the current filters.

## User Scenarios & Testing *(mandatory)*

The actor is the Club Admin, who today is the only role allowed to inspect and reconcile the stamp's analysis workflow ([API And Identity](../../docs/architecture/platform-implementation.md#api-and-identity)). The operations already exist on the platform ([Durable Analysis Workflow](../archive/20261005-130703-durable-analysis-workflow/spec.md), FR-034 and FR-035); this feature adds a connected Web UI area over them. It implements "platform configuration and administration" of [Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui) for workflow operations. The platform decides and checks everything; the Web UI shows what the platform returns and explains its refusals.

### User Story 1 - See the analysis workflow's health at a glance (Priority: P1)

A Club Admin opens the Operations area from the navigation and sees the analysis summary the platform reports: whether the stamp's default workflow definition currently resolves (and if not, the reason category and since when), how many analysis runs are in each state, how many runs have blocked capability nodes, how many execution attempts are stale, how many finalized-recording notifications were rejected, how many outgoing messages are pending or failed, and the age of the oldest pending message, together with the time the platform observed these values. The Club Admin refreshes the summary on demand.

**Why this priority**: It replaces a raw API call for the most frequent operational question, "is anything stuck?", and every other story starts from it. It changes nothing and is valuable on its own.

**Independent Test**: With a running platform that has one finalized recording set, a Club Admin opens the area and sees every summary value with the observation time; after a further finalization, the refresh shows the changed counts and a newer observation time; a Coach sees neither the navigation entry nor the area, and opening its address shows the not-permitted view.

**Acceptance Scenarios**:

1. **Given** a signed-in Club Admin, **When** the navigation is shown, **Then** it contains an entry for the Operations area; **Given** any member without the Club Admin role, **Then** it does not, and opening any Operations address shows the not-permitted view without requesting operational data.
2. **Given** the Operations area, **When** it loads, **Then** it shows the workflow definition state (resolvable or not, with category and since when when not resolvable), the run count per run state, the runs with blocked nodes, the stale attempts, the rejected notifications, the pending and failed outgoing messages, the oldest pending message age (or that none is pending), and the time the platform observed them.
3. **Given** the summary is shown, **When** the Club Admin refreshes it, **Then** the values and the observation time are read again from the platform, and the previous values stay visible until the new ones arrive.
4. **Given** the workflow definition does not resolve, **When** the summary is shown, **Then** the area states plainly that new analysis runs cannot start until the default workflow resolves, and names the reason category the platform reports.
5. **Given** non-zero pending or failed messages or rejected notifications, **When** the summary is shown, **Then** each such count links to the matching filtered list.

---

### User Story 2 - Find stuck publications (Priority: P1)

A Club Admin opens the outgoing messages list, filters it by state (pending, published, failed, discarded) and by message type, pages through it, and opens one message to read everything the platform reports about it: its identifier, type, contract version, subject, state, attempt count, publication round, last and next attempt times, failure category, creation and publication times, age, publication lag, and the run, logical job, and capability it concerns where present.

**Why this priority**: Failed or long-pending publications are the reason a Club Admin has to look at operations at all; seeing them is the prerequisite for deciding to reconcile.

**Independent Test**: Against seeded outgoing messages in several states and types, the list shows the expected messages for each filter combination, paging forward and back returns the same pages, and a message's detail view shows every listed field; an unknown message identifier shows the not-found view.

**Acceptance Scenarios**:

1. **Given** outgoing messages exist, **When** the Club Admin opens the list, **Then** it shows the newest messages first with type, state, attempt count, creation time, and failure category where present, one page at a time.
2. **Given** the list, **When** the Club Admin chooses a state, a message type, or both, **Then** only matching messages are shown, the filters are part of the address so the view can be reopened or shared with another Club Admin, and clearing a filter shows all messages again.
3. **Given** more messages than fit one page, **When** the Club Admin moves to the next page and back, **Then** the platform's page boundaries are kept and no message is skipped or repeated.
4. **Given** a message, **When** the Club Admin opens it, **Then** the detail view shows every field the platform returns for it, shows absent values as absent rather than as zero or empty text, and offers the full identifiers to read and copy.
5. **Given** a message identifier that does not exist or is malformed, **When** its address is opened, **Then** the not-found view is shown.
6. **Given** no message matches the filters, **When** the list loads, **Then** it states that no message matches and offers to clear the filters.
7. **Given** the list on any page, **When** the Club Admin refreshes it, **Then** the first page of the current filters is read again.

---

### User Story 3 - Check what happened to finalized-recording notifications (Priority: P2)

A Club Admin opens the notification receipts list, filters it by outcome (run created, run already existed, rejected), and pages through it: each receipt shows the notification's identifier and type, the recording-set version it concerned, its outcome, the rejection category for rejected notifications, the run it created or found, and when it was received.

**Why this priority**: Rejected notifications explain why a finalized recording set has no analysis run; they are less frequent than publication problems but otherwise invisible.

**Independent Test**: Against seeded receipts with each outcome, each outcome filter shows exactly the matching receipts with their fields, paging works as for outgoing messages, and an empty result states that no receipt matches.

**Acceptance Scenarios**:

1. **Given** receipts exist, **When** the Club Admin opens the list, **Then** it shows the newest receipts first with every field listed in this story, one page at a time.
2. **Given** the list, **When** the Club Admin filters by an outcome, **Then** only receipts with that outcome are shown and the filter is part of the address.
3. **Given** a rejected receipt, **When** it is shown, **Then** its rejection category is shown with a worded explanation for the categories the Web UI knows and as the raw value otherwise.

---

### User Story 4 - Run a reconciliation deliberately (Priority: P2)

A Club Admin who sees pending or failed publications starts an analysis reconciliation from the Operations area's summary view, where the action is always available. A confirmation states plainly what it does: the platform queues every currently eligible ready job and every unpublished notification for publication again from its durable workflow state; it never authorizes work, drops jobs, publishes blocked work, or changes accepted results; it may take a while; and it is recorded in the audit trail under the Club Admin's name. After confirming, the Club Admin sees the outcome: how many ready jobs and notifications were queued again and when the reconciliation started and completed. The summary is then read again.

**Why this priority**: It is the only state-changing action of the area. It is useful after the inspection stories exist, and the platform already reconciles on start-up and after transport recovery, so a manual run is the exception.

**Independent Test**: With a failed outgoing message for an eligible ready job, a Club Admin starts a reconciliation, confirms, sees the counts and times the platform returned, and the refreshed summary and list show the message pending again; cancelling the confirmation sends nothing; a second click while the first reconciliation runs sends nothing.

**Acceptance Scenarios**:

1. **Given** the Operations area's summary view, whatever its counts, **When** the Club Admin chooses to run a reconciliation, **Then** a confirmation with the statements of this story is shown, and cancelling it sends nothing.
2. **Given** the confirmation, **When** the Club Admin confirms, **Then** exactly one reconciliation is requested, the action stays disabled with a visible and announced progress state until the platform answers, and the outcome shows the queued ready jobs, the queued notifications, and the start and completion times.
3. **Given** a reconciliation that queued nothing, **When** its outcome is shown, **Then** it states that nothing needed to be queued again.
4. **Given** the platform answers that it is unavailable, **When** the outcome is shown, **Then** the Web UI explains that the reconciliation could not run, that nothing was changed by the Web UI, and that the Club Admin may try again later; it never retries automatically.
5. **Given** the Club Admin role was removed in the meantime, **When** the Club Admin confirms, **Then** the platform's refusal leads to the not-permitted view.

---

### User Story 5 - Use the area safely in every supported setting (Priority: P3)

The Operations area follows the Web UI's existing rules: it uses the same navigation, translation keys, problem handling, and accessibility as the rest of the Web UI, works with keyboard and screen readers, and is online only, so an offline browser or an installed app without network shows the connection-required state and no operational data is cached or stored in the browser.

**Why this priority**: These are the Web UI's existing quality rules applied to the new views; they protect access control, accessibility, and the PWA's no-local-data rule.

**Independent Test**: Automated browser tests reach every Operations view and action by keyboard, run accessibility rule checks on each view, dialog, and state, check that no operational response is served from service-worker caches or found in browser storage, and check that an offline browser shows the connection-required state.

**Acceptance Scenarios**:

1. **Given** any Operations view, dialog, or state (loading, data, empty, refused, error), **When** automated accessibility rule checks run, **Then** they report no violations, and every control is operable by keyboard with visible focus and announced results.
2. **Given** the browser is offline, **When** the Club Admin opens a view or starts a reconciliation, **Then** the connection-required state is shown and nothing is queued for later.
3. **Given** the Club Admin used the area, **When** browser storage and the service-worker caches are inspected, **Then** they contain no operational responses.

### Edge Cases

- No outgoing messages or receipts at all: each list states that there is nothing yet and why (no recording set has been finalized or no work has been published).
- A run state, message state, message type, outcome, workflow definition state, failure category, or rejection category the Web UI does not know is shown with its raw value instead of being hidden or failing; a run state the platform does not report is not shown as zero.
- A filter value in the address that the Web UI does not know is ignored and the list shows all messages or receipts, rather than sending a request the platform refuses.
- A continuation position the platform refuses (for example because it expired or was altered) restarts the list at the first page with an explanation.
- Platform-provided text (subjects, categories, identifiers) that contains markup or very long text is shown as plain text within bounded space and never interpreted.
- The summary or a list is read while the platform is under load and answers with "too many requests", "unavailable", or a server error: the Web UI shows a recoverable error with a way to try again, keeps the last values it showed marked with their observation time, and changes nothing.
- The Club Admin leaves the area while a reconciliation runs: the reconciliation continues on the platform, the Web UI does not resend it, and the next summary shows its effect.
- Two Club Admins start reconciliations at the same time: each confirmation sends one request; the platform decides how they run, and each Club Admin sees the outcome of their own request.
- A session that ends while the area is open follows the Web UI's existing signed-out handling.
- Ages and durations reported in seconds are shown in readable units (for example minutes, hours, days), with the exact value available.

## Requirements *(mandatory)*

### Functional Requirements

#### Navigation and access

- **FR-001**: The Web UI MUST offer an Operations area with its own navigation entry, shown only to members with the Club Admin role; every Operations view MUST be addressable, and opening any Operations address without the Club Admin role MUST show the not-permitted view without requesting operational data.
- **FR-002**: The platform's authorization MUST stay authoritative: a refusal for lack of permission from any operation MUST show the not-permitted view, and a missing outgoing message MUST show the not-found view.
- **FR-003**: The Operations navigation entry MUST sit with the other Club Admin administration entries, after the Analyst Manager fleet entry and after the Registry entry of [Web UI Analyst Registry Administration](../archive/20261017-185438-web-ui-registry-admin/spec.md) when that entry exists, and MUST NOT change the address or position of any existing entry.

#### Summary

- **FR-004**: The area's entry view MUST show the analysis summary exactly as the platform returns it: workflow definition state, its category and since-time where present, run counts per run state, runs with blocked nodes, stale attempts, rejected notifications, pending outgoing messages, failed outgoing messages, the age of the oldest pending message or that none is pending, and the platform's observation time.
- **FR-005**: The summary MUST be read when the view opens and again only when the Club Admin asks for it or after a reconciliation completes; while a new read is in progress the previous values MUST stay visible and the read MUST be announced.
- **FR-006**: When the workflow definition is not resolvable, the summary MUST state that new analysis runs cannot start until the default workflow resolves and MUST name the platform's reason category.
- **FR-007**: The pending and failed outgoing message counts and the rejected notification count MUST link to the outgoing messages list filtered by that state and to the receipts list filtered by the rejected outcome, respectively.

#### Outgoing messages

- **FR-008**: The outgoing messages list MUST show the platform's pages in the platform's order with message type, state, attempt count, creation time, and failure category where present, and MUST let the Club Admin move to the next page and back to previous pages of the same filter without skipping or repeating messages; a manual refresh MUST re-read the first page of the current filters.
- **FR-009**: The list MUST support filtering by state (pending, published, failed, discarded) and by message type (finalized recordings, ready work, and run state changed), alone or combined; filters MUST be part of the view's address, and an unknown filter value in the address MUST be ignored.
- **FR-010**: An outgoing message's view MUST show every field the platform returns for it: message identifier, message type, contract version, subject, state, attempt count, publication round, last attempt time, next attempt time, failure category, creation time, publication time, age, publication lag, run identifier, logical job identifier, and capability identifier; absent values MUST be shown as absent, and identifiers MUST be readable and copyable in full; run, logical job, and capability identifiers MUST be plain text without links to other Web UI views, because the operational records name no match.
- **FR-011**: The Web UI MUST NOT show, request, or infer any message payload or other content the platform does not return, and MUST offer no retry, edit, discard, or delete action for individual messages.

#### Notification receipts

- **FR-012**: The receipts list MUST show the platform's pages in the platform's order with message identifier, message type, recording-set version identifier, outcome, rejection category, run identifier, and receipt time, with the same paging and refresh behavior as FR-008; its identifiers MUST be plain, copyable text without links to other Web UI views.
- **FR-013**: The list MUST support filtering by outcome (run created, run already existed, rejected); the filter MUST be part of the view's address, and an unknown filter value in the address MUST be ignored.

#### Reconciliation

- **FR-014**: The Club Admin MUST be able to start an analysis reconciliation from the summary view, and only there, whatever the summary's counts, and only after a confirmation that states that the platform queues every currently eligible ready job and unpublished notification for publication again from its durable state; that it never authorizes work, drops jobs, publishes blocked work, or changes accepted results; that it may take a while; and that it is recorded in the audit trail.
- **FR-015**: Confirming MUST send exactly one reconciliation request; the action MUST be disabled with an announced progress state until the platform answers, and the Web UI MUST never repeat the request automatically.
- **FR-016**: The outcome MUST show the number of ready jobs and notifications queued again and the start and completion times the platform returned, MUST say plainly when nothing needed to be queued again, and MUST be followed by a new summary read.
- **FR-017**: When the platform refuses or cannot run the reconciliation (for example because it is unavailable), the Web UI MUST explain that the reconciliation did not complete and that the Club Admin may try again later.

#### Platform access, client rules, and quality

- **FR-018**: The Web UI MUST reach the platform only through application-owned adapters over the generated client and only through the existing operations `getAnalysisOperationalSummary`, `listOutboxMessages`, `getOutboxMessage`, `listNotificationReceipts`, and `reconcileAnalysisPublication`; it MUST NOT add or change platform operations. The platform's OpenAPI document MUST describe these operations' parameters and response bodies so the generated client has models for them, without changing their behavior.
- **FR-019**: The Operations area MUST be online only: operational responses MUST NOT be cached by the service worker or stored in browser storage, the reconciliation MUST NOT be queued, and an offline browser or an installed app without network MUST show the connection-required state.
- **FR-020**: All new user-facing text MUST come from translation keys with English as the only shipped language; all new views and dialogs MUST be operable by keyboard with visible focus, announce loading, results, and errors to assistive technology, and pass automated WCAG 2.2 AA rule checks; all platform-provided text MUST be rendered as text, never as markup.
- **FR-021**: Every view MUST have explicit loading, empty, error, and refused states; recoverable errors (too many requests, unavailable, server error, lost connection) MUST offer a way to try again and MUST NOT discard values already shown.
- **FR-022**: Values the Web UI does not know (states, types, outcomes, categories) MUST be shown with their raw value rather than hidden or failing; timestamps MUST be shown in the member's local time with the exact time available, and ages and lags reported in seconds MUST be shown in readable units with the exact value available.
- **FR-023**: Automated tests MUST cover the adapters' mapping of the summary, messages, receipts, and reconciliation outcome; filters and paging; the reconciliation confirmation, its cancellation, duplicate prevention, and refusals; role-based access; unknown values; accessibility rule checks; the online-only rule; and a browser test against the running platform that reads the summary, lists and opens an outgoing message, lists receipts, and runs a reconciliation.

### Key Entities *(include if feature involves data)*

- **Analysis Operational Summary**: The platform's point-in-time counts for the analysis workflow, with the workflow definition state and the time the platform observed them.
- **Outgoing Message**: A record of the platform's transactional outbox as the platform reports it: identity, type, contract version, subject, publication state and attempts, timing, failure category, and the run, logical job, and capability it concerns; never its payload.
- **Notification Receipt**: The platform's record of how it handled one finalized-recordings notification: outcome, rejection category, recording-set version, run, and receipt time.
- **Reconciliation Outcome**: The counts of ready jobs and notifications the platform queued again and when the reconciliation started and completed.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A Club Admin finds out whether any outgoing message has failed, and opens one failed message, in at most 3 navigation steps from the Web UI's home view and without any raw API call.
- **SC-002**: In automated tests, every summary value, outgoing message field, and receipt field the platform returns is shown in 100% of the covered scenarios, including absent values.
- **SC-003**: In automated tests, members without the Club Admin role never see the navigation entry and are refused every Operations view in 100% of cases.
- **SC-004**: In automated tests, confirming a reconciliation sends exactly one request in 100% of cases, including repeated clicks, and cancelling sends none.
- **SC-005**: Automated accessibility rule checks report zero violations for every Operations view, dialog, and state, and every Operations action is reachable by keyboard.
- **SC-006**: In automated tests, no operational response is found in service-worker caches or browser storage after a session that used the area.

## Assumptions

- The Club Admin role stays the only role allowed to use these operations until a dedicated stamp-operator role exists ([API And Identity](../../docs/architecture/platform-implementation.md#api-and-identity)); a stamp-operator role, its navigation, and a move of the area to it are out of scope.
- The platform operations exist with their current behavior: page sizes and continuation positions, the known state, type, and outcome values, the audit event of a reconciliation, and the reconciliation's effect are decided by the platform ([Durable Analysis Workflow](../archive/20261005-130703-durable-analysis-workflow/spec.md), FR-034 and FR-035; [Production Queue Recovery and Backpressure](../../docs/architecture/job-processing.md#production-queue-recovery-and-backpressure)). The Web UI uses the platform's default page size.
- Message types added later by other features (for example agent orchestration notifications) appear in the list with their raw type; adding them as filter choices belongs to those features.
- Operational inspection of the dedicated email outbox, Agent Orchestration state, Analyst Manager fleet, and Registry is not part of this area; the fleet and Registry have their own areas.
- The summary is a point-in-time read; automatic refresh, charts, trends, alerts, dashboards, metrics, and production operations tooling are out of scope.
- Dependencies: [Durable Analysis Workflow](../archive/20261005-130703-durable-analysis-workflow/spec.md) and [Web UI Foundation](../archive/20261012-150657-web-ui-foundation/spec.md) (merged), [Installable Web UI with Service Worker Updates](../archive/20261015-194113-web-ui-pwa-updates/spec.md) (merged); navigation aligns with [Web UI Analyst Registry Administration](../archive/20261017-185438-web-ui-registry-admin/spec.md) without depending on it.
