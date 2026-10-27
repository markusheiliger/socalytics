# Research: Club Admin Security Audit Evidence View

No `NEEDS CLARIFICATION` remains. Each decision below resolves a planning-time choice the spec left open.

## R1. Endpoint shape and authorization

- **Decision**: `GET /api/v1/audit/events`, operation `listSecurityAuditEvents`, tag `Security audit`, registered with `MapMemberApi()` (policy `ActiveMember`, anti-forgery filter irrelevant for GET). The handler authorizes with `IAccessAuthorizer.AuthorizeClubAsync(ClubPermission.Administer)`, the same check as the workflow operations; no team scope is consulted.
- **Rationale**: the existing Club Admin operations use exactly this pattern; unauthenticated and machine callers (DPoP or workload tokens) fail the `ActiveMember` policy before the handler and are not audited, as the spec requires for callers with no member session.
- **Alternatives**: a new `AuditReviewer` role (out of scope per FR-002); team-scoped access (rejected, club-wide only).

## R2. Ordering and cursor

- **Decision**: order by `(occurred_at DESC, id DESC)`; `id` is a UUID, so the order is total. The cursor is opaque base64url text of the last returned `(occurred_at ticks, id)` plus a 16-byte SHA-256 prefix of the canonical filter string. A cursor that fails to decode, has the wrong length, or whose fingerprint differs from the request's filters is refused with reason `invalid-continuation`. The next page selects rows strictly after the cursor with a row-value comparison `(occurred_at, id) < (@at, @id)`.
- **Rationale**: keyset paging keeps pages stable while newer events are recorded (they sort before the cursor), skips nothing, and needs no count. Binding to the filters satisfies FR-006. The cursor holds no secret and is not a credential: it only names a position in data the caller may read, so no signing key is introduced (a re-positioned cursor that still decodes and matches the fingerprint is harmless because it only names a position in data the caller may read; FR-006 therefore requires refusal only of malformed, undecodable, or differently filtered cursors).
- **Residual**: an event whose transaction commits after a page read but carries an earlier `occurred_at` than the cursor can appear on a later page; this is an event that was not yet committed when the first page was read, so no committed event is skipped or repeated. The stability test records events between reads in committed order. Stability is guaranteed only for events committed in occurrence order; an event that commits late with an older timestamp can be missed by later pages until the Club Admin refreshes.
- **Alternatives**: offset paging (skips and repeats under concurrent writes); a sequence column (new column on an append-protected table, rejected).

## R3. Filters and validation

- **Decision**: query parameters `from`, `to` (ISO 8601 UTC instants; `from` inclusive, `to` exclusive), `eventType` (`^[a-z][a-z0-9.\-]{0,63}$`), `outcome` (the five stored values), `actorAccountId`, `teamId` (GUID `D` format), `resourceType` (`^[a-z][a-z0-9\-]{0,63}$`), `resourceId` (1 to 128 printable characters, requires `resourceType`), `pageSize`, `continuationToken` (at most 512 characters). All violations are collected as field violations and returned through the existing validation problem (`400`) with a specific reason per field; a `from` not before `to` yields reason `invalid-time-range` on `to`. `actorAccountId` never matches events without an actor account.
- **Rationale**: matches the `ListOutboxMessagesHandler` and `OperationsPaging` conventions (strings parsed in the handler so malformed values become field violations instead of binding errors).
- **Alternatives**: typed binding (produces unspecific 400s, rejected).

## R4. Page size

- **Decision**: development values `AuditEvidence:DefaultPageSize` 50 and `MaxPageSize` 200; a larger request is refused with `out-of-range` on `pageSize`. The query reads `pageSize + 1` rows to know whether a continuation exists.
- **Rationale**: same values and mechanism as the existing operations paging; kept in their own options so the audit limits can change independently.

## R5. Self-audit and failure behavior

- **Decision**: event type `audit.evidence-read`, action `list`, resource type `audit-events` with no resource id, data class unchanged (`DAT-001` default of the table). Success is recorded with `IAuditTrail.RecordAsync` inside the same unit of work that reads the page, after the page query, and commits before the response, so a read's own event never appears in its own page but appears in later reads; if recording or the commit throws, the existing exception handling answers with the recoverable server problem and no events are returned. Denial (`denied`, reason `audit-read-not-permitted`) and refusal (`refused`, reasons `invalid-filter`, `invalid-continuation`, `page-size-out-of-range`, `invalid-time-range`, `rate-limited`) are recorded with `RecordIndependentAsync` because the unit of work returns without committing a result. A denied read therefore leaves one `audit.evidence-read` event with outcome `denied` plus the independent `authorization.denied` event that the shared `IAccessAuthorizer` records; the handler does not suppress it, and the exactly-one guarantee counts only `audit.evidence-read` events. Details record only the shape: `filtersSet` (sorted comma list of filter names), and the values `from`, `to`, `eventType`, `outcome`, `resourceType` when they are valid. They never record identifiers supplied as filters, the cursor, the page size, or any result data. A refused request records the names of the filters set and only those fixed-vocabulary values that are themselves valid; invalid values are never recorded.
- **Rationale**: FR-016 through FR-019. Recording in the reading transaction makes "never served unaudited" structural. The new detail keys are added to the existing `PostgresAuditTrail` allow-list, which already rejects unknown keys.
- **Alternatives**: record after the read (could serve unaudited data on failure); record in a separate transaction before the read (the audit event would exist for a read that then failed, acceptable but the same-transaction form is stricter).

## R6. Rate limiting

- **Decision**: a fixed-window limiter policy `audit-evidence-read` partitioned by member account (fall back to remote address), `AuditEvidence:ReadsPerMinute` development value 30, modelled on `EvidenceRateLimiting`. Its rejection handler answers `429` with problem code `audit-read-rate-limited`, `Retry-After`, and `Cache-Control: no-store`, and records an independent `refused` audit-read event with reason `rate-limited` for the authenticated actor. Registration order must place the limiter after authentication so the account is known; the integration test proves it.
- **Rationale**: the spec requires a per-member limit with audited limited requests.
- **Alternatives**: limiting inside the handler (the middleware pattern already exists and keeps the handler testable without time).

## R7. Minimization: detail allow-list

- **Decision**: the response includes a `details` object of string values filtered by `AuditDetailProjection`. Returned keys: `role`, `previousRole`, `purpose`, `targetAccountId`, `endReason`, `recoveryId`, `fromState`, `toState`, `previousState`, `matchId`, `partCount`, `grantedPartCount`, `memberCount`, `teamCount`, `memberId`, `managerId`, `stampId`, `os`, `osVersion`, `architecture`, `managerVersion`, `fencedAttempts`, `revokedCredentials`, `selfApproval`, `capabilityId`, `capabilityVersion`, `profileId`, `profileVersion`, `workflowId`, `workflowVersion`, `previousWorkflowId`, `previousWorkflowVersion`, `readyJobsRepublished`, `notificationsRepublished`, `runsSuperseded`, `maskedAddress`, `notificationType`, `enabled`, `logicalJobId`, `runId`, `supersedingRunId`, `provider`, `cause`, `conversationId`, `invocationId`, `attemptId`, `attemptNumber`, `agentId`, `disposition`, `reasonCode`, `kind`, `artifactId`, `artifactVersion`, `previousVersion`, `newVersion`, `differentiationAcknowledged`, `purgedCount`, `inUseCount`, `acceptedResultId`, `ordinal`, `fromMs`, `toMs`, `shortened`, and the audit-read keys `filtersSet`, `from`, `to`, `eventType`, `outcome`, `resourceType`.
- **Omitted** (default to omission when unsure): `keyThumbprint`, `deviceFingerprint`, `pairingSourceAddress`, `registrationCorrelationId`, `claimedKeyProtection`, `claimedApplicationProfile`, `workloadClientId`, `reference`, `imageDigest`, `modelDigest`, `profileDigest`, `definitionDigest`, `digest`, `mediaDigests`, `grantExpiresAt`, `windowExpiresAt`, `violationCodes`, and every key not in the platform's write allow-list.
- **Value rules**: a returned value is at most 256 characters, contains no control characters, no URL scheme (`://`), and, outside `maskedAddress`, no `@`; `maskedAddress` must contain `*` and no unmasked local part pattern (an `@` with an alphanumeric-only local part is dropped). A value failing a rule is omitted, never truncated into something misleading. The projection is Application code so it is unit-testable and independent of storage.
- **Rationale**: FR-013 through FR-015; the omitted keys are device, network, or digest material that identifies endpoints or artifacts and is not needed to answer "who did what".
- **Alternatives**: return all stored details (leaks); a stored-side projection in SQL (spreads policy into Infrastructure).

## R8. Indexes for SC-008

- **Decision**: migration `0033_identityaccess_audit_read_indexes.sql` (renumbered at implementation if 0033 is taken) adds `ix_audit_occurred_id (occurred_at DESC, id DESC)`, `ix_audit_type_occurred (event_type, occurred_at DESC, id DESC)`, `ix_audit_team_occurred (team_id, occurred_at DESC, id DESC) WHERE team_id IS NOT NULL`, and `ix_audit_actor_occurred (actor_account_id, occurred_at DESC, id DESC) WHERE actor_account_id IS NOT NULL`; the existing `ix_audit_resource` serves resource filters. Index creation does not touch rows, so the append-protection triggers are unaffected; no grants change (the application role already has `SELECT`).
- **Rationale**: the unfiltered and single-filter reads must stay ordered index scans at 100,000 rows. A performance test seeds 100,000 events with a bulk insert and asserts the 2-second bound on the first page.
- **Alternatives**: a materialized view (stale, rejected).

## R9. Response headers and logging

- **Decision**: the endpoint sets `Cache-Control: no-store` on success and on problems; the handler logs nothing about filters or results; request-level logging stays as for any request (identifiers and reason codes only).
- **Rationale**: FR-023.

## R10. Web UI shape

- **Decision**: a feature folder `features/audit` with one list page at `/audit` (Club Admin only through the existing `clubAdminOnly` route wrapper). Filters are search parameters (`from`, `to`, `eventType`, `outcome`, `actor`, `resourceType`, `resourceId`, `team`); unknown or malformed values are dropped before any request. Paging keeps a stack of cursors in memory so previous returns to the platform's page boundaries (reusing the pattern of `useContinuationPages`). The detail view is a dialog over the in-memory event; there is no get-by-id operation (the spec defines one operation), so the filtered list is the addressable view and the dialog is part of it. A single refresh button reads the first page again and keeps the previous page visible until it arrives. The navigation entry sits with the other Club Admin entries. No `localStorage`, `sessionStorage`, IndexedDB, or cache use; the service worker already excludes `/api` responses, and the platform test asserts it for audit.
- **Rationale**: matches the workflow operations area (FR-024 through FR-031).
- **Alternatives**: a detail route needs a get-by-id operation and would persist the identifier in the address; deferred.

## R11. Browser test projects

- **Decision**: add `tests/e2e/simulated/audit.spec.ts` and `audit-access.spec.ts` to the existing `simulated` project (fake platform routes in `tests/e2e/support`), and a new `platform-audit` project in `playwright.config.ts` for `tests/e2e/platform/audit.spec.ts`, ordered after `platform-agent-catalog`. The platform spec produces its own evidence through real operations (sign-ins, a denied attempt, a role change) as the scripted Club Admin and asserts the filters against it; planted sensitive values are covered by the integration tests, which can write the table directly.
- **Rationale**: follows the segments and operations precedents; the spec's "new simulated project" is satisfied by the new simulated specs in the existing project, because every simulated spec already shares one fake-platform project.
