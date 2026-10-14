---

description: "Task list for Member Email Addresses and Pending-Request Notifications"
---

# Tasks: Member Email Addresses and Pending-Request Notifications

**Input**: Design documents from `specs/20261013-140140-email-notifications/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/email-api.md](contracts/email-api.md), [contracts/email-messages.md](contracts/email-messages.md), [contracts/web-ui-routes.md](contracts/web-ui-routes.md), [quickstart.md](quickstart.md)

**Tests**: Required. The spec requires automated coverage of every acceptance scenario and an end-to-end set against the real platform and mail catcher (SC-006); the constitution requires focused tests. Every task adds the tests for the code it creates in the same task, so the platform suites, `npm run build`, `npm test`, and `npm run test:e2e` stay green after each task. Web UI browser tests use the foundation's fixtures (axe WCAG 2.2 AA, CSP violations, no secrets in browser storage).

**Organization**: Tasks are grouped by user story. Merged before this feature starts (do not re-create): Club and Identity Foundation `specs/archive/20261005-130701-club-identity-foundation` (members, roles, sessions, `IAuditTrail`, `MapMemberApi`, `IUnitOfWork`, `PlatformApiFactory`, `TestMembers`), Analyst Manager Registration `specs/archive/20261005-130704-analyst-manager-registration` (`SubmitPairingCode`, `TestAnalystManager`), and Web UI Foundation `specs/archive/20261012-150657-web-ui-foundation` (account and member views, the generated client, `simulatedApi.ts`, fixtures, the `platform` browser project, the AppHost Web UI resource, the OpenAPI snapshot). One new package (MailKit 4.18.1) and one pinned container image (`axllent/mailpit:v1.31.5`).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies on incomplete tasks); consecutive `[P]` tasks under one heading run in parallel, each from the same commit
- **[Story]**: Which user story this task belongs to (US1–US4)
- Every task names its exact file paths

## Path Conventions

- Platform: `src/platform/SocAlytics.Platform.{Domain,Application,Infrastructure,Api,AppHost}/`, tests under `src/platform/Tests/`.
- Web UI: `src/clients/web/` with `src/`, `tests/unit/`, `tests/e2e/simulated/`, `tests/e2e/platform/`, `tests/e2e/support/`.

---

## Phase 1: Setup (Package, Relay, and Test Fixture)

**Purpose**: The SMTP package, the development relay, and the mail-catcher test fixture.

- [x] T001 SMTP package, Mailpit relay, and test fixture (research.md § R1, § R2; data-model.md § Configuration; FR-017): pin `MailKit` `4.18.1` in `src/platform/Directory.Packages.props` and reference it from `src/platform/SocAlytics.Platform.Infrastructure/SocAlytics.Platform.Infrastructure.csproj`; in `src/platform/SocAlytics.Platform.AppHost/Program.cs` add a `mailpit` container resource (`axllent/mailpit`, tag `v1.31.5`, SMTP endpoint on 1025 and HTTP endpoint on 8025) and pass to the API the development values of data-model.md § Configuration (`Email__Smtp__Host`/`Port` from the `mailpit` SMTP endpoint, `Email__Smtp__Security` = `StartTlsWhenAvailable`, `Email__SenderAddress` = `socalytics@localhost`, `Email__SenderName` = `SocAlytics`, `Email__Confirmation__Lifetime` = `1.00:00:00`, `Email__Confirmation__MaxPerHour` = `3`, `Email__Confirmation__AttemptsPerMinute` = `10`, `Email__Delivery__MaxAttempts` = `8`, `Email__Delivery__PollInterval` = `00:00:05`, `Email__Delivery__LeaseDuration` = `00:02:00`, `Notifications__CoalescingWindow` = `00:15:00`, `Notifications__WebUiBaseUri` = the Web UI endpoint), in the default and the `e2e` mode; add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Notifications/Support/MailpitContainerFixture.cs` (starts the same pinned image with Testcontainers; reads, searches, and clears messages through Mailpit's REST API `/api/v1/messages`; can stop and start the container for outage tests). Tests: extend `src/platform/Tests/SocAlytics.Platform.Host.Tests/PlatformHostTests.cs` to assert the `mailpit` resource and the settings; a smoke test of the fixture in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Notifications/MailpitFixtureTests.cs`.

**Checkpoint**: The platform builds with MailKit; the AppHost and tests have a mail catcher.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Persistence, the address value object, transport and rendering, then the outbox, notification types, and the delivery worker. No user story starts before this phase is complete.

### Persistence, address value object, and transport

- [x] T002 [P] Email persistence (data-model.md; research.md § R3, § R4, § R8): add the next free forward-only migration `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Migrations/NNNN_identityaccess_email_notifications.sql` (numbered at implementation time on an up-to-date default branch) creating `member_email_addresses` (columns and rules of data-model.md, `address` "Trimmed; ≤ 254 characters", `state` "`unverified` or `verified`", versioned through `socalytics.attach_version_trigger`), `email_confirmations` (`state` "`pending` → `issued` → `used`; or `superseded`, `expired`", `token_hash` bytea), `notification_preferences` (primary key `(account_id, notification_type)`), and `email_messages` (`state` "`queued` → `sending` → `sent`; or `failed`, `discarded`", indexes on `(notification_type, account_id, created_at)` and `(state, next_attempt_at)`), with foreign keys to `member_account`; add the store interfaces in `src/platform/SocAlytics.Platform.Application/IdentityAccess/Email/IEmailStores.cs` and their Dapper implementations in `src/platform/SocAlytics.Platform.Infrastructure/Persistence/IdentityAccess/EmailStores.cs` (no DI registration here; T005 registers them); classify the three tables without `version` in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Structure/PersistedTableClassifications.cs`. Tests: `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Notifications/EmailPersistenceTests.cs` (round trips, version conflicts on addresses, no address, body, or token columns in `email_messages`).
- [x] T003 [P] Email address value object (research.md § R8; FR-002, FR-021): add `src/platform/SocAlytics.Platform.Domain/IdentityAccess/EmailAddress.cs` (syntactic validation, "≤ 254 characters", trimming, normalization to lower case, SHA-256 hash of the normalized form, masking as `j***@e***.com`). Tests: `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Notifications/EmailAddressValueTests.cs`.
- [x] T004 [P] SMTP transport and message rendering (research.md § R1, § R9; contracts/email-messages.md; FR-012, FR-014): add `src/platform/SocAlytics.Platform.Application/Notifications/IEmailTransport.cs` and `IEmailRenderer.cs`, `src/platform/SocAlytics.Platform.Infrastructure/Email/MailKitEmailTransport.cs` (TLS mode from `Email:Smtp:Security`, refusing anything other than `SslOnConnect` or `StartTls` outside Development; authentication when credentials are configured; mapping 4xx and connection errors to transient and 5xx to permanent), `src/platform/SocAlytics.Platform.Infrastructure/Email/EmailRenderer.cs`, and `src/platform/SocAlytics.Platform.Infrastructure/Email/Messages.en.json` (the two message types of contracts/email-messages.md, plain text and minimal HTML, the headers of the contract), plus `src/platform/SocAlytics.Platform.Infrastructure/Email/EmailOptions.cs` validated at start (no DI registration here; T005 registers them). Tests: `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Notifications/EmailTransportTests.cs` against Mailpit (delivery, headers, no host label, fingerprint, or other data in rendered text, transient versus permanent mapping).

### Outbox, notification types, and delivery worker

- [ ] T005 Email outbox, notification types, and delivery worker (research.md § R3, § R4, § R7; data-model.md § Notification types; FR-006, FR-009, FR-013–FR-016): add `src/platform/SocAlytics.Platform.Application/Notifications/NotificationTypes.cs` (code registry: `email-confirmation` account message for the address owner; `registration-pending` for active Club Admins, on by default), `src/platform/SocAlytics.Platform.Application/Notifications/IEmailOutbox.cs` (queues an `email_messages` row in the active unit of work with the recipient's current address hash), `src/platform/SocAlytics.Platform.Application/Notifications/NotificationOptions.cs` (`CoalescingWindow`, `WebUiBaseUri`, validated at start), and `src/platform/SocAlytics.Platform.Infrastructure/Email/EmailDeliveryWorker.cs` (polls queued rows with `FOR UPDATE SKIP LOCKED` and a sending lease; re-checks before sending that the account is active, the address hash equals the current address, the address is verified except for `email-confirmation`, and, for types other than account messages, that the account still belongs to the type's audience (an active Club Admin for `registration-pending`) and the preference is on, else marks `discarded`; for `email-confirmation` generates a 256-bit token, stores only its SHA-256 hash, issue time, and expiry on the confirmation row in the same transaction that marks the message `sending`, and puts the raw token only into the link `{WebUiBaseUri}/email/confirm#token=…`; sends through `IEmailTransport`; backoff 1, 2, 4 … minutes capped at 1 hour up to `MaxAttempts`, then `failed`; never resends a `sent` row, so only a crash between relay acceptance and recording may cause one duplicate (FR-014); logs message id, type, and state only); register the stores of T002, the transport, renderer, and options of T004, the outbox, and the worker in `src/platform/SocAlytics.Platform.Infrastructure/InfrastructureServiceCollectionExtensions.cs` and `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs`. Tests: `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Notifications/EmailDeliveryWorkerTests.cs` with Mailpit (delivery, re-check discards for deactivated, changed, removed, unverified, opted-out, and no-longer-Club-Admin recipients, token issued only at send and a retry issuing a new token, relay outage with every queued message delivered exactly once afterwards (SC-005), permanent failure, restart recovery, no full address, token, or body in captured logs (SC-004)).

**Checkpoint**: Messages can be queued in a unit of work and are delivered to the mail catcher.

---

## Phase 3: User Story 1 - Add and verify my email address (Priority: P1) 🎯 MVP

**Goal**: Members set, change, remove, and verify their own address.

**Independent Test**: Set an address, open the confirmation link from Mailpit, click confirm, and see it verified; change it and observe that the old address receives nothing; an old or expired link is refused.

- [ ] T006 [US1] Own address and confirmation API (contracts/email-api.md § Own address, § Confirmation; research.md § R4, § R5, § R6, § R8; FR-001–FR-007, FR-021; US1 scenarios 1–7): add `src/platform/SocAlytics.Platform.Application/IdentityAccess/Email/` handlers for set, change, remove, resend (limit "`Email:Confirmation:MaxPerHour`" per account counted from `email_messages`, `429 email-confirmation-rate-limited` with `Retry-After`), and confirm (hash lookup; only `issued`, unexpired rows; single use; one generic `400 email-confirmation-invalid` for every refusal), each queueing `email-confirmation` messages through `IEmailOutbox` and superseding earlier confirmations in the same unit of work, with audit events `identity.email-address-set`, `-changed`, `-removed`, `-verified` with masked addresses; map `GET`/`PUT`/`DELETE /api/v1/me/email` and `POST /api/v1/me/email/confirmation-requests` with `MapMemberApi` and `IfMatchHeader`, and the anonymous `POST /api/v1/email-confirmations` in `src/platform/SocAlytics.Platform.Api/Endpoints/IdentityAccess/EmailEndpoints.cs`, the latter with a new per-client-address limiter policy `email-confirmation` (`Email:Confirmation:AttemptsPerMinute`, bound and validated at start) defined in `src/platform/SocAlytics.Platform.Api/Endpoints/IdentityAccess/EmailRateLimiting.cs` and registered with the API's rate limiter; regenerate `src/platform/SocAlytics.Platform.Api/openapi/v1.json`. Tests: `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Notifications/EmailAddressTests.cs` and `EmailConfirmationTests.cs` with Mailpit for US1 scenarios 1–7, the invalid-address refusal, the resend limit, the confirmation endpoint's `429` beyond the per-client limit, the scanner case (reading the link without the confirm call verifies nothing), signing in with a verified address as the sign-in name failing (FR-007), and SC-003.
- [ ] T007 [US1] Web UI email section and confirmation page (contracts/web-ui-routes.md; research.md § R5, § R9; FR-018, FR-020; US1 scenarios 1–7; clarification on the confirmation click): add `src/clients/web/src/platform/adapters/emailAdapter.ts` and `src/clients/web/src/platform/models/email.ts`; add the email section to the account view in `src/clients/web/src/features/account/` (address, state, set, change, remove, resend with the wait time); add `src/clients/web/src/features/email-confirm/EmailConfirmPage.tsx` at `/email/confirm` (reads `#token=` from the fragment, removes it with `history.replaceState` before rendering the button, calls `confirmEmail` only on the click, success, invalid, and rate-limited states, signed in or out) and register it in `src/clients/web/src/router.tsx`; keys in `src/clients/web/src/i18n/en/email.json`. Tests: unit tests in `src/clients/web/tests/unit/features/email/`; browser tests in `src/clients/web/tests/e2e/simulated/email.spec.ts` for US1 scenarios 1–7, the token absent from the address, history, and storage after load, keyboard operation (`tests/e2e/support/keyboard.ts`), and axe and CSP (SC-007).
- [ ] T008 [US1] Platform end-to-end verification (SC-001, SC-006): add `src/clients/web/tests/e2e/support/mailpit.ts` (reads messages and links from the AppHost's Mailpit REST API) and `src/clients/web/tests/e2e/platform/email.spec.ts`: as the Club Admin, set an address in the account view, read the link from Mailpit, open it, click confirm, and see the address verified, asserting that the steps from setting the address to seeing it verified complete within 2 minutes (SC-001).

**Checkpoint**: Members can verify addresses end to end.

---

## Phase 4: User Story 2 - Club Admins hear about pending Manager requests by email (Priority: P2)

**Goal**: Eligible Club Admins receive one minimal message per coalescing window when a request becomes pending.

**Independent Test**: With one verified and one unverified Club Admin and a verified Coach, submit a pairing code as Registrar and see exactly one message for the verified Club Admin; submit two more within 15 minutes and see none.

- [ ] T009 [US2] Pending-request notification (research.md § R6, § R7; contracts/email-api.md § Delivery side effects; FR-010–FR-012; US2 scenarios 1–6; clarification on coalescing): add `src/platform/SocAlytics.Platform.Application/Notifications/RegistrationPendingNotifier.cs` (resolves active Club Admins with a verified address and the type enabled at that moment, takes a transaction-scoped PostgreSQL advisory lock per recipient account, skips those with a `registration-pending` message that is not `discarded` created within `Notifications:CoalescingWindow`, and queues one message each through `IEmailOutbox`) and call it from `src/platform/SocAlytics.Platform.Application/Registry/Decisions/SubmitPairingCode.cs` in the same unit of work that creates the pending request. Tests: `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Notifications/PendingRequestNotificationTests.cs` with Mailpit and `TestAnalystManager` for US2 scenarios 1–6, the coalescing window, two concurrent pairing-code submissions producing one message per Club Admin, a `discarded` message not suppressing the next one, a message saying that "one or more" requests wait, and SC-002 and SC-004 (no host label, fingerprint, submitting member, or token in the message).
- [ ] T010 [US2] Platform end-to-end notification (SC-006): add `src/clients/web/tests/e2e/support/analystManager.ts` (a scripted Analyst Manager for tests: generates a P-256 key pair, starts a pairing with the platform's pairing API, and returns the pairing code, following `TestAnalystManager` and the Analyst Manager registration contracts) and extend `src/clients/web/tests/e2e/platform/email.spec.ts`: with the Club Admin's verified address, a Registrar (created in test setup) submits the scripted Manager's pairing code through `submitAnalystManagerPairingCode`, and the Club Admin's notification appears in Mailpit with only the event and the fleet link.

**Checkpoint**: User Stories 1 and 2 work independently.

---

## Phase 5: User Story 3 - Choose which notifications I receive (Priority: P3)

**Goal**: Members turn notification types off and on; account messages are always sent.

**Independent Test**: Turn the pending-request notification off, submit a pairing code, see no message; turn it on and see the next one.

- [ ] T011 [US3] Notification preferences API (contracts/email-api.md § Own address and preferences; research.md § R7; FR-008, FR-009, FR-021; US3 scenarios 1–4): add preference handlers in `src/platform/SocAlytics.Platform.Application/IdentityAccess/Email/` (list the types that apply to the member's roles with `accountMessage`, set `enabled` storing a row only when it differs from the default, refuse account messages and types outside the member's roles with `400 notification-type-not-configurable`, audit `identity.notification-preference-changed`) and map `GET /api/v1/me/notification-preferences` and `PUT /api/v1/me/notification-preferences/{type}` in `src/platform/SocAlytics.Platform.Api/Endpoints/IdentityAccess/EmailEndpoints.cs`; regenerate `src/platform/SocAlytics.Platform.Api/openapi/v1.json`. Tests: `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Notifications/NotificationPreferenceTests.cs` for US3 scenarios 1–4 including an opted-out Club Admin receiving no pending-request message.
- [ ] T012 [US3] Web UI notification preferences (contracts/web-ui-routes.md; FR-018, FR-020): add the preferences section to the account view in `src/clients/web/src/features/account/` (types for the member's roles, toggles, account messages shown as always sent) using `src/clients/web/src/platform/adapters/emailAdapter.ts`; extend `src/clients/web/src/i18n/en/email.json`. Tests: unit tests in `src/clients/web/tests/unit/features/email/preferences/`; browser tests in `src/clients/web/tests/e2e/simulated/email-preferences.spec.ts` with keyboard operation (`tests/e2e/support/keyboard.ts`), axe, and CSP.

**Checkpoint**: User Stories 1–3 work independently.

---

## Phase 6: User Story 4 - Club Admins manage members' addresses (Priority: P4)

**Goal**: Club Admins see and set members' addresses; nobody else sees them.

**Independent Test**: As Club Admin, set a member's address, confirm it as the member, and see it verified in both views; as a Coach, see no address anywhere.

- [ ] T013 [US4] Members' addresses API and visibility (contracts/email-api.md § Members' addresses; research.md § R8; FR-001, FR-022; US4 scenarios 1–4): add Club Admin handlers in `src/platform/SocAlytics.Platform.Application/IdentityAccess/Email/` (set, change, remove another member's address with the same verification and audit as own addresses, `set_by` = the Club Admin) and map `GET`/`PUT`/`DELETE /api/v1/members/{memberId}/email` in `src/platform/SocAlytics.Platform.Api/Endpoints/IdentityAccess/EmailEndpoints.cs`; include `email` (`{ address, state }`) in `GET /api/v1/members` and `GET /api/v1/members/{memberId}` only for Club Admins in `src/platform/SocAlytics.Platform.Api/Endpoints/IdentityAccess/MemberEndpoints.cs`; regenerate `src/platform/SocAlytics.Platform.Api/openapi/v1.json`. Tests: `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Notifications/MemberEmailTests.cs` for US4 scenarios 1–4 and `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Notifications/EmailPrivacyTests.cs` (0 responses to non-owners other than Club Admins contain an address across member, team, fleet, and registration endpoints (SC-008); masked audit events).
- [ ] T014 [US4] Web UI member email row (contracts/web-ui-routes.md; FR-019, FR-020): add the email row with state and set, change, remove actions to the member detail view in `src/clients/web/src/features/members/` using `src/clients/web/src/platform/adapters/emailAdapter.ts`; extend `src/clients/web/src/i18n/en/email.json`. Tests: unit tests in `src/clients/web/tests/unit/features/email/members/`; browser tests in `src/clients/web/tests/e2e/simulated/member-email.spec.ts` for US4 scenarios 1–3 with keyboard operation (`tests/e2e/support/keyboard.ts`), axe, and CSP.

**Checkpoint**: All user stories work independently.

---

## Phase 7: Polish & Cross-Cutting Concerns

- [ ] T015 Documentation (plan.md § Architecture Updates; Constitution Principle I): make the narratives listed in plan.md § Architecture Updates and every status statement this feature affects true for the merged state, re-reading `docs/architecture/` at implementation time; the listed rewrites are examples, not the whole job. In particular, turn every "(decided, not yet implemented)" about the email channel in `docs/architecture/email-notifications.md`, `docs/architecture/platform-implementation.md`, and `docs/architecture/tenancy-and-technology.md` into present tense, rewrite the introduction of `docs/architecture/email-notifications.md` and the "in a later specification" sentence of `docs/architecture/analyst-manager.md` § Fleet Visibility In The Web UI, and update `docs/architecture/platform-implementation.md` § Acceptance Evidence and § Architecture Reassessment, keeping production relay values Open / Blocking. Update `src/platform/README.md`, `src/clients/README.md`, the root `README.md` (Mailpit in the AppHost), and the "Current State" in `AGENTS.md`.
- [ ] T016 Run the validation in [quickstart.md](quickstart.md): platform restore, build, and tests, then in `src/clients/web/` `npm ci`, `npm run build`, `npm test`, and `npm run test:e2e` (both projects), and `node .github/scripts/check-markdown.mjs`; fix what fails.

---

## Dependencies & Execution Order

- **Setup (Phase 1)**: T001 first.
- **Foundational (Phase 2)**: after Setup; T002, T003, and T004 in parallel, then T005 (uses all three and registers them). Blocks all stories.
- **US1 (Phase 3)**: T006, then T007 (uses T006's operations through the regenerated client), then T008. **US2 (Phase 4)**: after US1 (verified addresses). **US3 (Phase 5)**: after US2 (a configurable notification type). **US4 (Phase 6)**: after US1.
- **Polish (Phase 7)**: after all stories.

### Parallel opportunities

- Phase 2: T002 (the migration, `Application/IdentityAccess/Email/IEmailStores.cs`, `Infrastructure/Persistence/IdentityAccess/EmailStores.cs`, `PersistedTableClassifications.cs`, `Notifications/EmailPersistenceTests.cs`), T003 (`Domain/IdentityAccess/EmailAddress.cs`, `Notifications/EmailAddressValueTests.cs`), and T004 (`Application/Notifications/IEmailTransport.cs`, `IEmailRenderer.cs`, `Infrastructure/Email/MailKitEmailTransport.cs`, `EmailRenderer.cs`, `Messages.en.json`, `EmailOptions.cs`, `Notifications/EmailTransportTests.cs`) change disjoint files; all DI registration is deferred to T005.
- No story phase has tasks with disjoint footprints: platform tasks share `EmailEndpoints.cs` and `openapi/v1.json`, and Web UI tasks share the account view, `emailAdapter.ts`, and `email.json`.

## Implementation Strategy

1. Package, relay, and foundations, then User Story 1 as the MVP (verified addresses end to end).
2. Add User Story 2 (pending-request notification), 3 (preferences), and 4 (Club Admin management).
3. Documentation and full validation.
