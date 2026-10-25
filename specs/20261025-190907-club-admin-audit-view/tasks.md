---

description: "Task list for Club Admin Security Audit Evidence View"
---

# Tasks: Club Admin Security Audit Evidence View

**Input**: Design documents from `/specs/20261025-190907-club-admin-audit-view/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/audit-events-api.md, contracts/audit-web-ui.md, quickstart.md

**Tests**: Required by FR-032 (integration and OpenAPI tests) and FR-033 (simulated and platform browser tests), plus Vitest unit tests for the Web UI.

**Organization**: Tasks are grouped by user story. The platform operation (US1 to US3) is delivered before the Web UI (US4), which needs the refreshed OpenAPI snapshot and generated client. All platform paths are under `src/platform/` and all Web UI paths under `src/clients/web/`.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies); consecutive `[P]` tasks under one heading run in parallel, each from the same commit
- **[Story]**: Which user story this task belongs to (US1, US2, US3, US4)

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: The only schema change and the development options.

### Migration and options

- [ ] T001 [P] Add the forward-only migration `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Migrations/0033_identityaccess_audit_read_indexes.sql` (renumber to the next free number on an up-to-date default branch) creating `ix_audit_occurred_id (occurred_at DESC, id DESC)`, `ix_audit_type_occurred (event_type, occurred_at DESC, id DESC)`, partial `ix_audit_team_occurred (team_id, occurred_at DESC, id DESC) WHERE team_id IS NOT NULL`, and partial `ix_audit_actor_occurred (actor_account_id, occurred_at DESC, id DESC) WHERE actor_account_id IS NOT NULL` on `socalytics.security_audit_event` per data-model.md "Indexes"; no row, trigger, or grant change, and no `PersistedTableClassifications` entry because no table is added.
- [ ] T002 [P] Create `src/platform/SocAlytics.Platform.Application/IdentityAccess/Audit/AuditEvidenceOptions.cs` (section `AuditEvidence`: `DefaultPageSize` 50, `MaxPageSize` 200, `ReadsPerMinute` 30; development values, validated as positive with default not above maximum) and add the `AuditEvidence` section with those values to `src/platform/SocAlytics.Platform.Api/appsettings.Development.json`.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Application-level building blocks and test support that every platform story uses.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

### Application building blocks and test support

- [ ] T003 [P] Create `src/platform/SocAlytics.Platform.Application/IdentityAccess/Audit/ISecurityAuditQueries.cs` with the query port `ISecurityAuditQueries.ListAsync(AuditQuery, cancellationToken)` and the records `AuditQuery` (from, to, eventType, outcome, actorAccountId, resourceType, resourceId, teamId, page size, cursor position), `AuditEventRecord` (the stored fields of data-model.md "Security Audit Event" including raw details), and `AuditEventRecordPage` (items plus whether more exist), per data-model.md "Audit Query".
- [ ] T004 [P] Create `src/platform/SocAlytics.Platform.Application/IdentityAccess/Audit/AuditPageCursor.cs`: opaque base64url cursor of `occurred_at` ticks, `id`, and a 16-byte SHA-256 prefix of the canonical filter string (sorted `name=value` pairs of the filters, excluding `pageSize` and `continuationToken`); `Encode` and `TryDecode(token, fingerprint)` returning failure for undecodable text, wrong length, or fingerprint mismatch (reason `invalid-continuation`), per research.md R2 and data-model.md "Cursor".
- [ ] T005 [P] Create `src/platform/SocAlytics.Platform.Application/IdentityAccess/Audit/AuditDetailProjection.cs`: the allow-list of returned detail keys and the omitted keys exactly as in research.md R7, with the value rules (at most 256 characters, no control characters, no `://`, no `@` outside `maskedAddress`, `maskedAddress` must contain `*` and drop an `@` with an alphanumeric-only local part); a value failing a rule is omitted, never truncated.
- [ ] T006 [P] In `src/platform/SocAlytics.Platform.Infrastructure/Persistence/PostgresAuditTrail.cs` add the keys `filtersSet`, `from`, `to`, `eventType`, `outcome`, and `resourceType` to the existing detail allow-list, and in `src/platform/SocAlytics.Platform.Application/Abstractions/AuditEvent.cs` add `audit-events` to the resource-type comment.
- [ ] T007 [P] Create `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/Support/AuditEvidenceFixtureSupport.cs` with helpers shared by the audit integration tests: sign in as a Club Admin, a Coach, and a member of another role on the disposable stamp (reuse the existing `IdentityAccess.Support` helpers), seed `socalytics.security_audit_event` rows directly (type, outcome, actor, resource, team, time, details), bulk-seed 100,000 rows, and count rows by event type, and replace `IAuditTrail` with a failing implementation for the forced audit write failure of T021.

---

## Phase 3: User Story 1 - Review recent security events (Priority: P1) 🎯 MVP

**Goal**: A Club Admin lists recorded events newest first, one page at a time, with a stable continuation position.

**Independent Test**: With events of several types, a Club Admin lists them newest first with the minimized stored fields and pages through all of them seeing each exactly once, including events recorded between page reads and events with identical times; an empty store gives an empty page without a continuation.

### Implementation

- [ ] T008 [US1] Create `src/platform/SocAlytics.Platform.Application/IdentityAccess/Audit/ListSecurityAuditEvents.cs`: the query, result, and handler; authorize with `IAccessAuthorizer.AuthorizeClubAsync(ClubPermission.Administer)` (no team scope), apply `AuditEvidenceOptions` default page size, read `pageSize + 1` rows through `ISecurityAuditQueries`, project each row through `AuditDetailProjection`, issue the cursor of the last returned row only when more exist (no count), per research.md R1, R2, R4; filters, validation, and the self-audit are added by later tasks, so keep the handler structured for them.
- [ ] T009 [US1] Create `src/platform/SocAlytics.Platform.Infrastructure/Persistence/IdentityAccess/SecurityAuditQueries.cs` implementing `ISecurityAuditQueries` with Npgsql: order `(occurred_at DESC, id DESC)`, keyset condition `(occurred_at, id) < (@at, @id)` for the next page, limit `pageSize + 1`, reading only the stored columns of data-model.md; register it (and the handler and options) in the Application and Infrastructure composition methods.
- [ ] T010 [US1] Create `src/platform/SocAlytics.Platform.Api/Endpoints/IdentityAccess/AuditEvidenceEndpoints.cs`: `GET /api/v1/audit/events` through `MapMemberApi()` with operation id `listSecurityAuditEvents`, tag `Security audit`, the typed `AuditEventPageDto` (`items`, nullable `continuationToken`) and `AuditEventDto` (including `details` as a string map, `outcome` as an enum, and `actorKind` as an enum of `member`, `system`, `anonymous`, `analyst-manager`, `agent-workload`) schemas per contracts/audit-events-api.md, `ProblemResults` for failures, `Cache-Control: no-store` on success and problems (research.md R9), and the `pageSize` and `continuationToken` query parameters; call its mapping from `src/platform/SocAlytics.Platform.Api/Program.cs`.

### Tests for User Story 1

- [ ] T011 [P] [US1] Create `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/AuditEvidencePagingTests.cs`: newest-first order with the minimized fields, paging each seeded event exactly once, identical occurrence times (tie order by id), events recorded between page reads (committed in order; none skipped or repeated, newer ones only after a restart), empty store (excluding `audit.evidence-read` events of earlier reads) gives an empty page without a continuation, each seeded event exactly once (excluding `audit.evidence-read` events), no total count anywhere in the body, page size default and maximum (above the maximum refused), and malformed or undecodable cursors refused with `invalid-continuation`; `Cache-Control: no-store` on the `200` and `400` responses.
- [ ] T012 [P] [US1] Create `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/AuditEvidencePerformanceTests.cs`: bulk-seed 100,000 events and assert the first unfiltered page is returned to a Club Admin within 2 seconds, and that a filtered first page by event type is also served (SC-008), using the migration's indexes.

**Checkpoint**: a Club Admin can list and page events; US1 is testable alone.

---

## Phase 4: User Story 2 - Narrow the evidence to a question (Priority: P1)

**Goal**: Seven filters, alone or combined, with specific refusals for invalid values.

**Independent Test**: Against events varying in time, type, outcome, actor, resource, and team, each filter alone and in combination returns exactly the matching events, invalid values are refused with a specific problem naming the filter, and paging with a filter returns each match once.

### Filter implementation

- [ ] T013 [US2] In `src/platform/SocAlytics.Platform.Application/IdentityAccess/Audit/ListSecurityAuditEvents.cs` parse and validate the filters in the handler and collect field violations (reasons `invalid`, `out-of-range`, `invalid-time-range` on `to`, `required-with-resource-type`, `invalid-continuation`) with these exact rules: `eventType` `^[a-z][a-z0-9.\-]{0,63}$`; `outcome` one of `succeeded`, `unchanged`, `denied`, `failed`, `refused`; `actorAccountId` and `teamId` GUID `D` format; `resourceType` `^[a-z][a-z0-9\-]{0,63}$`; `resourceId` 1 to 128 printable characters, requires `resourceType`; `from` inclusive before `to` exclusive when both are set; `pageSize` integer 1 to `MaxPageSize`; `continuationToken` at most 512 characters; compute the filter fingerprint for the cursor (research.md R3).
- [ ] T014 [US2] In `src/platform/SocAlytics.Platform.Infrastructure/Persistence/IdentityAccess/SecurityAuditQueries.cs` add the AND-combined parameterized predicates for time range (`>= from`, `< to`), event type, outcome, actor account (never matching rows without an actor), resource type, resource type with identifier, and team, keeping the order and keyset paging unchanged.
- [ ] T015 [US2] In `src/platform/SocAlytics.Platform.Api/Endpoints/IdentityAccess/AuditEvidenceEndpoints.cs` bind the string query parameters `from`, `to`, `eventType`, `outcome`, `actorAccountId`, `resourceType`, `resourceId`, and `teamId` (strings, so malformed values become field violations rather than binding errors) and publish them with their rules in the OpenAPI description; map violations to the existing `400` validation problem.

### Tests for User Story 2

- [ ] T016 [P] [US2] Create `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/AuditEvidenceFilterTests.cs`: each filter alone (type, each outcome, from and to bounds inclusive and exclusive, actor, resource type, resource type with identifier, team), combinations, anonymous and system events never matching an actor filter, well-formed unknown account, team, and resource returning an empty page identical in shape to an empty store, a future time range returning an empty page, empty-page assertions made on seeded event types or excluding `audit.evidence-read`, paging with a filter returning each match once, a cursor used with different filters refused with `invalid-continuation`, and every invalid value (unknown outcome, malformed identifiers, `resourceId` without `resourceType`, `from` not before `to`) refused with a problem naming the filter and the specific reason.

**Checkpoint**: US1 and US2 work independently of the self-audit.

---

## Phase 5: User Story 3 - Trust that the evidence view is itself accountable and safe (Priority: P1)

**Goal**: Every authenticated read is audited without result content, non-admins are refused without disclosure, details are minimized, and reads are rate limited.

**Independent Test**: A Club Admin read leaves exactly one `audit.evidence-read` event with the filter shape only; Coach and other roles are refused and a `denied` event is recorded; unauthenticated and machine callers are refused without audit; planted sensitive values never appear; a failing audit write returns no events.

### Self-audit and protection implementation

- [ ] T017 [US3] In `src/platform/SocAlytics.Platform.Application/IdentityAccess/Audit/ListSecurityAuditEvents.cs` implement the self-audit per research.md R5 and data-model.md "Audit Read Event": record `audit.evidence-read` / `list` on resource type `audit-events` through `IAuditTrail.RecordAsync` inside the unit of work that reads the page (outcome `succeeded`, so a failed write or commit returns no events and the existing exception handling answers the recoverable server problem); record `denied` with reason `audit-read-not-permitted` (the independent `authorization.denied` event that the shared authorizer records stays, so a denied read leaves both events) and `refused` with reasons `invalid-filter`, `invalid-continuation`, `page-size-out-of-range`, and `invalid-time-range` through `IAuditTrail.RecordIndependentAsync`; details are only `filtersSet` (sorted comma list of filter names) plus the valid values of `from`, `to`, `eventType`, `outcome`, and `resourceType`, never account, team, or resource identifiers, the cursor, the page size, or any result data; the denial must be identical whatever the evidence holds (FR-003).
- [ ] T018 [US3] Create `src/platform/SocAlytics.Platform.Api/Endpoints/IdentityAccess/AuditEvidenceRateLimiting.cs`: a fixed-window limiter policy `audit-evidence-read` partitioned by member account (falling back to the remote address) with `AuditEvidence:ReadsPerMinute`, modelled on `src/platform/SocAlytics.Platform.Api/Analysis/EvidenceRateLimiting.cs`; its rejection answers `429` with problem code `audit-read-rate-limited`, `Retry-After`, and `Cache-Control: no-store`, and records an independent `refused` audit-read event with reason `rate-limited` for the authenticated actor; register it in `src/platform/SocAlytics.Platform.Api/Program.cs` after authentication (research.md R6).
- [ ] T019 [US3] In `src/platform/SocAlytics.Platform.Api/Endpoints/IdentityAccess/AuditEvidenceEndpoints.cs` attach the `audit-evidence-read` policy to the operation and publish the `400`, `401`, `403`, `429` (with `Retry-After`), and `500` problem responses in its OpenAPI description (contracts/audit-events-api.md "Responses").

### Tests for User Story 3

- [ ] T020 [P] [US3] Create `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/AuditEvidenceAuthorizationTests.cs`: Club Admin allowed; Coach, a member of another role, and a Club Admin whose role was removed between two page reads refused with the same `403` as other Club Admin operations; unauthenticated callers and machine callers (DPoP Manager and workload tokens) refused by authentication before the handler; the refusal body identical for an empty and a full store; team scope grants nothing; a Club Admin sees events of all teams and events without a team; `Cache-Control: no-store` on the `200`, `400`, `403`, and `429` responses.
- [ ] T021 [P] [US3] Create `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/AuditEvidenceSelfAuditTests.cs`: exactly one `audit.evidence-read` event per authenticated read (counting only `audit.evidence-read` events; a denied read additionally leaves the shared authorizer's `authorization.denied` event, which the test asserts) with outcome `succeeded`, `denied` (`audit-read-not-permitted`), or `refused` (each reason of data-model.md), attributed to the actor with session and correlation identifier; details hold only the filter shape and never returned events, counts, cursors, page size, or account, team, and resource identifiers; no event for unauthenticated callers; a read's own event never appears in its own page but appears in later reads, and assertions on other events exclude `audit.evidence-read`; reads leave the table unchanged apart from the self-audit events (SC-006); a forced audit write failure returns a recoverable server problem and no events (using the failing `IAuditTrail` fixture helper of T007); a captured logger shows that no filter value or event content is logged by the operation, and `Cache-Control: no-store` is on the `500` response.
- [ ] T022 [P] [US3] Create `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/AuditEvidenceMinimizationTests.cs`: seed events whose stored details hold planted credentials, tokens, presigned links, free text, unmasked addresses, omitted keys (R7), over-long values, and control characters, and assert none appears in any response (SC-005), only allow-listed keys with valid values are returned, a masked address is returned only in masked form, and no other stored field is returned.
- [ ] T023 [P] [US3] Create `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/AuditEvidenceRateLimitTests.cs`: reads above `ReadsPerMinute` get `429` `audit-read-rate-limited` with `Retry-After` and `no-store`, the limit is per member, the limited request leaves one `refused` event with reason `rate-limited`, and the limiter runs after authentication so the account is known.
- [ ] T024 [P] [US3] Create `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/AuditDetailProjectionTests.cs`: unit-level checks of the allow-list, omitted keys, and every value rule of `AuditDetailProjection` (including the `@` and `masked` rules and the 256-character bound).

- [ ] T025 [P] [US3] Create `src/platform/Tests/SocAlytics.Platform.Host.Tests/AuditEvidenceOpenApiTests.cs` (the typed-schema test, using the shared stamp pattern of `WorkflowOperationsOpenApiTests.cs`): operation id `listSecurityAuditEvents`, tag `Security audit`, all query parameters and their rules, the enum for `outcome`, `AuditEventPageDto` without any count property, `AuditEventDto` fields, and the `400`, `401`, `403`, `429`, and `500` responses.

### OpenAPI snapshot

- [ ] T026 [US3] Build the platform and refresh the committed snapshot `src/platform/SocAlytics.Platform.Api/openapi/v1.json` with `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests` (never in the verification environment), then run the filters `AuditEvidence` and `OpenApiSnapshot` of the Host tests and the `AuditEvidence` filter of the Integration tests without the update variable.

**Checkpoint**: the platform operation is complete and verified (FR-001 to FR-023, FR-032).

---

## Phase 6: User Story 4 - Use the evidence from the Web UI (Priority: P2)

**Goal**: An online-only Audit area for Club Admins with filters, paging, manual refresh, and an event detail dialog.

**Independent Test**: Simulated and platform browser tests open `/audit` as a Club Admin, filter, page, refresh, and open an event; a Coach sees neither the entry nor the area and no audit request is sent; no audit data is in browser storage or service-worker caches; nothing refreshes without the Club Admin's action; accessibility checks report no violations.

### Generated client

- [ ] T027 [US4] Regenerate the Kiota client from the refreshed snapshot with `npm run generate` in `src/clients/web` (output `src/clients/web/src/generated/platform-api/`, committed) and confirm that `npm run build` still compiles.

### Models, adapter, filters, and translations

#### View models

- [ ] T028 [US4] Create `src/clients/web/src/platform/models/audit.ts` with the `AuditEvent` view model (all fields of `AuditEventDto`, optional values as `undefined`, `details` as a string record), `AuditEventPage`, `AuditFilters`, the five outcomes, and the five actor kinds `member`, `system`, `anonymous`, `analyst-manager`, `agent-workload`.

#### Adapter, filters, and formatting

- [ ] T029 [P] [US4] Create `src/clients/web/src/platform/adapters/auditAdapter.ts` with `listAuditEvents(filters, continuationToken?, signal?)` returning `{ items, continuationToken? }` mapped from the generated `AuditEventPageDto`; failures become `PlatformFailure` with reasons `not-permitted`, `invalid-filter` (with the field), `invalid-continuation`, `rate-limited` (with retry-after), and `unavailable`; components never import the generated client (contracts/audit-web-ui.md "Adapter").
- [ ] T030 [P] [US4] Create `src/clients/web/src/features/audit/auditFilters.ts`: parse and serialize the address filters `from`, `to`, `eventType`, `outcome`, `actor`, `resourceType`, `resourceId`, `team`, dropping unknown parameters and invalid values (not a valid instant, pattern mismatch, not one of the five outcomes, not a GUID, `resourceId` without `resourceType`) before any request; the cursor and page number are never part of the address.
- [ ] T031 [P] [US4] Create `src/clients/web/src/features/audit/formatAudit.ts` with plain-text formatting helpers (time, actor kind and account, resource, team, absent marker) and `src/clients/web/src/i18n/en/audit.json` with every Audit string (title, columns, filter labels, the read-only development evidence and "reading it is audited" notice, states, refusals, detail dialog, absent marker).

### Page, dialog, route, and navigation

- [ ] T032 [US4] Create `src/clients/web/src/features/audit/AuditPage.tsx` at `/audit`: filter form with problems shown next to the filter, list newest first (time, type, action, outcome, actor, resource, team), next and previous through an in-memory cursor stack (reusing `useContinuationPages` from `src/clients/web/src/features/operations/useContinuationPages.ts`), a manual refresh that re-reads the first page and keeps the old page visible, the persistent read-only and audited notice, and explicit loading, empty, error with retry, invalid-continuation (restart at the first page with an explanation), rate-limited (retry after the stated time), not-permitted, and connection-required states; no automatic refresh, no storage, no export, redaction, deletion, retention, integrity, hold, or alerting control.
- [ ] T033 [US4] Create `src/clients/web/src/features/audit/AuditEventDialog.tsx`: an accessible dialog over the in-memory event that shows every returned field, an explicit "absent" marker for absent values, copyable identifiers (reuse `src/clients/web/src/features/operations/CopyableValue.tsx`), platform text as plain text with bounded wrapping, and keyboard operation; open it from `AuditPage.tsx`.
- [ ] T034 [US4] Add the `/audit` route wrapped with `clubAdminOnly` in `src/clients/web/src/router.tsx`, the Club Admin–only "Audit" entry next to the other Club Admin entries in `src/clients/web/src/shell/navItems.ts`, add `'audit'` to the `NavItem` `labelKey` union in the same file and the `audit` navigation label to `src/clients/web/src/i18n/en/shell.json`, and register the `audit` namespace in `src/clients/web/src/i18n/index.ts`.

### Unit tests

- [ ] T035 [P] [US4] Create `src/clients/web/tests/unit/features/audit/filters.test.ts` and `src/clients/web/tests/unit/features/audit/format.test.ts` covering address parsing and dropping of unknown or invalid values, `resourceId` without `resourceType`, and the formatting helpers.
- [ ] T036 [P] [US4] Create `src/clients/web/tests/unit/platform/adapters/auditAdapter.test.ts` covering the mapping of the page and every failure reason (`not-permitted`, `invalid-filter` with the field, `invalid-continuation`, `rate-limited` with retry-after, `unavailable`).
- [ ] T037 [P] [US4] Create `src/clients/web/tests/unit/features/audit/auditPage.test.tsx` covering loading, empty, error with retry, filter problems, paging with the cursor stack, refresh keeping the old page, the detail dialog with absent values, the notice, and that no `localStorage`, `sessionStorage`, or timer-based refresh is used.
- [ ] T038 [P] [US4] In `src/clients/web/tests/unit/shell/navigation.test.tsx` add cases that the Audit entry is shown only to Club Admins.

### Browser tests

- [ ] T039 [US4] In `src/clients/web/tests/e2e/support/simulatedApi.ts` add the fake platform routes for `GET /api/v1/audit/events` (filters, keyset-style cursors, refusals `403`, `400` with field violations, `429`, `500`, and a request log so tests can assert that no audit request was sent).
- [ ] T040 [P] [US4] Create `src/clients/web/tests/e2e/simulated/audit.spec.ts` (project `simulated`): newest page with the notice, filters applied and kept in the address, unknown address values ignored, next and previous page boundaries, manual refresh keeping the old page until the new one arrives with no automatic refresh, detail dialog with absent values and copyable identifiers, empty, error, invalid-filter, invalid-continuation, rate-limited, offline connection-required states, no audit data in `localStorage`, `sessionStorage`, IndexedDB, or service-worker caches, and axe accessibility checks with keyboard operation for every view, dialog, and state; Audit is reachable from the home view in at most 3 navigation steps and a filtered question can be answered (SC-001).
- [ ] T041 [P] [US4] Create `src/clients/web/tests/e2e/simulated/audit-access.spec.ts` (project `simulated`): the Audit entry is shown to a Club Admin and not to a Coach or other members, opening `/audit` as another member shows the not-permitted view and the fake platform logs no audit request, and a session that ends while the area is open follows the existing signed-out handling.
- [ ] T042 [US4] In `src/clients/web/playwright.config.ts` add the project `platform-audit` (`testDir` `./tests/e2e/platform`, `testMatch` `audit.spec.ts`, `fullyParallel: false`, `dependencies: ['platform-agent-catalog']`, Desktop Chrome with `baseURL: platformUrl`) after `platform-agent-catalog`.
- [ ] T043 [US4] Create `src/clients/web/tests/e2e/platform/audit.spec.ts` (project `platform-audit`, real platform with the scripted Club Admin): produce evidence through real operations (sign-ins, a denied attempt by a Coach, a role change), open Audit, filter by event type, outcome, actor, and time range, page, refresh, open the detail dialog, find the `audit.evidence-read` event of the first read with its filter shape and no result content, show that a Coach has no entry and `/audit` shows the not-permitted view followed by one `denied` event in a later Club Admin read, and assert no audit data in browser storage or service-worker caches and accessibility with axe.

**Checkpoint**: all four stories are independently functional.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Architecture documentation true for the merged state, repository documentation, and final validation.

### Architecture documents

- [ ] T044 [P] Update `docs/architecture/security-and-data-governance.md`: in Audit Events state in present tense that Club Admins read audit evidence through `GET /api/v1/audit/events` with minimization, a filter-bound cursor, and one `audit.evidence-read` event per read (development evidence; the independent audit authority `GOV-AUD-001` and `POL-009` stay unresolved and Blocking), name audit evidence reads in the list of events that require audit evidence, and correct `CTL-009` so evidence reading by Club Admins is development evidence while the production audit store, export, redaction, integrity verification, retention, and holds remain deferred.
- [ ] T045 [P] Update `docs/architecture/platform-implementation.md`: in Data Organization add the read path of the audit table, its four indexes (migration `0033`, or the number actually used), and that the runtime role may still only insert and read; in API And Identity add the audit evidence read to the Club Admin operations until a stamp-operator or audit-reviewer role exists; update the current-state and evidence summaries (including the browser project list that names `platform-agent-catalog`) to include the audit evidence view and `platform-audit`.
- [ ] T046 [P] Update `docs/architecture/client-applications.md`: in Web UI add the Audit area (online only, memory-only data, manual refresh, read-only, Club Admin only) to the connected administration workflows and the workflow operations narrative, and make the sentence listing the implemented platform configuration and administration areas include it.
- [ ] T047 Re-read every document in `docs/architecture/` at implementation time and correct each remaining status statement, planned, future-tense, or unimplemented wording that the merged feature makes false (for example anything saying audit evidence cannot be read without database access), changing only statements the feature affects and keeping every unresolved decision explicit.

### Repository documentation

- [ ] T048 [P] Update the lists of current executable evidence in `README.md` and `src/platform/README.md` to include the Club Admin audit evidence view (`GET /api/v1/audit/events`, the Web UI Audit area, and the `platform-audit` browser project) as development evidence without implying production readiness.
- [ ] T049 [P] Update `AGENTS.md` ("Current State") with the same development-evidence summary, naming the `simulated` and `platform-audit` browser projects, the `audit-evidence-read` rate-limiter policy, and the `AuditEvidence` options as development values, keeping it consistent with the architecture documents.

### Final validation

- [ ] T050 Run `node .github/scripts/check-markdown.mjs`, then run the validation steps of `specs/20261025-190907-club-admin-audit-view/quickstart.md` (platform integration and Host filters `AuditEvidence`, `npm run build`, `npm test`, `npx playwright test --project simulated`, and `--project platform-audit` in `src/clients/web`) and fix any failure caused by this feature.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: no dependencies; T001 and T002 touch different files and run in parallel.
- **Foundational (Phase 2)**: after Phase 1; blocks all user stories.
- **US1 (Phase 3)**: after Phase 2; the MVP (list and page).
- **US2 (Phase 4)**: after US1 (extends the same handler, query, and endpoint files).
- **US3 (Phase 5)**: after US2 (extends the same handler and endpoint files); T026 refreshes the snapshot after the endpoint shape is final.
- **US4 (Phase 6)**: after T026 (needs the refreshed snapshot and generated client).
- **Polish (Phase 7)**: after all stories; T047 after T044 to T046; T050 last.

### Within Each Story

- Handler before query before endpoint where they share a story; tests after the implementation they verify, in the same phase.
- The `[P]` groups under one heading touch different files and may run together.

## Parallel Example

```text
Phase 1 group:  T001 migration | T002 AuditEvidenceOptions.cs + appsettings.Development.json
Phase 2 group:  T003 ISecurityAuditQueries.cs | T004 AuditPageCursor.cs | T005 AuditDetailProjection.cs | T006 PostgresAuditTrail.cs + AuditEvent.cs | T007 AuditEvidenceFixtureSupport.cs
US3 tests:      T020 Authorization | T021 SelfAudit | T022 Minimization | T023 RateLimit | T024 DetailProjection | T025 AuditEvidenceOpenApiTests.cs
US4 adapters:   (after T028 audit.ts) T029 auditAdapter.ts | T030 auditFilters.ts | T031 formatAudit.ts + audit.json
```

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1 and Phase 2.
2. Complete Phase 3 (US1) and validate paging with the integration tests.
3. Stop and validate, then continue with filters (US2) and the self-audit (US3); the operation must not be exposed to Club Admins without US3 in a real stamp, because reads must never be served unaudited.

### Incremental Delivery

1. Platform operation: US1, then US2, then US3 (snapshot refreshed at T026).
2. Web UI: US4 on the refreshed snapshot.
3. Documentation and final validation.

## Notes

- Every limit (page sizes, reads per minute) is a development value; `POL-009` and `GOV-AUD-001` stay unresolved.
- Do not edit applied migrations; renumber T001 if the number is taken.
- Never refresh the OpenAPI snapshot in the verification environment.
