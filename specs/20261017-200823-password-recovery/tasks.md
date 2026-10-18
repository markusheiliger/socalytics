---

description: "Task list for Self-Service Password Recovery"
---

# Tasks: Self-Service Password Recovery

**Input**: Design documents from `specs/20261017-200823-password-recovery/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: The spec requires automated evidence (SC-002 to SC-006), so every story has test tasks. Test tasks follow the implementation tasks of their story so that each landed task keeps the build and test suites green.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; they change disjoint files and do not depend on each other
- **[Story]**: Which user story this task belongs to (US1, US2, US3)
- Every task names its exact file paths

---

## Phase 1: Foundational (Blocking Prerequisites)

**Purpose**: Schema, configuration, message type, stores, and the credential operation that every story needs

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

### Schema, configuration, and message type

- [x] T001 [P] Add the forward-only migration `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Migrations/NNNN_identityaccess_password_recovery.sql`, numbered as the next free number on an up-to-date default branch: create `socalytics.password_recoveries` with `id uuid PRIMARY KEY`, `account_id uuid NOT NULL REFERENCES socalytics.member_account (id)`, `address_hash text NOT NULL`, `security_stamp text NOT NULL`, `state text NOT NULL CHECK (state IN ('pending', 'issued', 'used', 'superseded', 'invalidated', 'expired'))`, `token_hash bytea NULL`, `requested_at timestamptz NOT NULL`, `issued_at`, `expires_at`, `used_at timestamptz NULL`, the constraints "`pending` has no token hash; `issued`, `used`, and `expired` carry token hash, `issued_at`, and `expires_at`; `superseded` and `invalidated` carry them only when they were issued before; `used` exactly when `used_at` is set", a unique index on `token_hash` where present, and an index on `account_id` for `state IN ('pending', 'issued')`; add `password_recovery_id uuid NULL REFERENCES socalytics.password_recoveries (id)` to `socalytics.email_messages`; add an index on `socalytics.member_email_addresses (address_normalized)`; never edit an applied migration; and classify `password_recoveries` as unversioned ("single-use recovery state; the state transition is the guard") in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Structure/PersistedTableClassifications.cs` (data-model.md)
- [x] T002 [P] Add `PasswordRecoveryOptions` (section `PasswordRecovery`: `LinkLifetime` 60 minutes, `MaxMessagesPerHour` 3, `RequestsPerMinute` 5, `TokenAttemptsPerMinute` 10, `MinimumResponseDuration` 400 milliseconds, and `IsValid` requiring positive values) in `src/platform/SocAlytics.Platform.Application/IdentityAccess/PasswordRecovery/PasswordRecoveryOptions.cs` (FR-002, FR-015–FR-017)
- [x] T003 [P] Add the account-message type `password-recovery` (`NotificationTypes.PasswordRecoveryCode`, audience `AddressOwner`, `AccountMessage: true`, `DefaultEnabled: true`, link path `/password-recovery/complete`) to `NotificationTypes.All` in `src/platform/SocAlytics.Platform.Application/Notifications/NotificationTypes.cs`, and its English subject and body (kind of message, ignore-if-not-requested note, link lifetime, link; no account name, password, roles, or club data) in `src/platform/SocAlytics.Platform.Infrastructure/Email/Messages.en.json` (FR-019, FR-020)

### Stores and credential operation

- [x] T004 [P] Add `IPasswordRecoveryStore` with the records `StoredPasswordRecovery` and `PasswordRecoveryCandidate` in `src/platform/SocAlytics.Platform.Application/IdentityAccess/PasswordRecovery/IPasswordRecoveryStore.cs` and its Dapper implementation in `src/platform/SocAlytics.Platform.Infrastructure/Persistence/IdentityAccess/PasswordRecoveryStore.cs`: list candidates by normalized address (account id, account name, membership status, whether a password is set, security stamp, address state, and address hash for every member whose current address normalizes to the value, in one query that runs the same way for unknown addresses), create a `pending` recovery, supersede the account's open (`pending`, `issued`) recoveries, find by id, find and lock by token hash, issue (token hash, `issued_at`, `expires_at`), and mark `used`, `invalidated`, or `expired` (data-model.md; FR-003, FR-008, FR-009)
- [x] T005 [P] Add the nullable `PasswordRecoveryId` as an optional trailing parameter (default `null`, so existing callers such as `EmailPersistenceTests` compile) to `NewEmailMessage` and `StoredEmailMessage` in `src/platform/SocAlytics.Platform.Application/IdentityAccess/Email/IEmailStores.cs`, write and read `email_messages.password_recovery_id` in `src/platform/SocAlytics.Platform.Infrastructure/Persistence/IdentityAccess/EmailStores.cs`, and let `IEmailOutbox` in `src/platform/SocAlytics.Platform.Application/Notifications/IEmailOutbox.cs` enqueue a `password-recovery` message for one account, address hash, and recovery id (FR-004)
- [x] T006 [P] Add `ResetPasswordByRecoveryAsync(accountId, newPassword)` to `src/platform/SocAlytics.Platform.Application/IdentityAccess/IAccountCredentialService.cs` and implement it in `src/platform/SocAlytics.Platform.Infrastructure/IdentityAccess/AccountCredentialService.cs` like the reset branch of `RedeemCredentialAsync`: validate the policy, remove and add the password (which changes the security stamp), clear `PasswordChangeRequired`, leave the failed-attempt count and lockout end unchanged, and end every live session with the existing end reason `password-reset` (FR-011, FR-012)

### Registration

- [x] T007 Register `IPasswordRecoveryStore` in `src/platform/SocAlytics.Platform.Infrastructure/InfrastructureServiceCollectionExtensions.cs` and bind and validate `PasswordRecoveryOptions` in `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs`; add any detail key the password-recovery audit events use that the allow-list in `src/platform/SocAlytics.Platform.Infrastructure/Persistence/PostgresAuditTrail.cs` lacks (`maskedAddress` is already allowed) (FR-017, FR-018)

### Foundational tests

- [x] T008 Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/SelfServicePasswordRecoveryStoreTests.cs`: the migration applies and the persistence structure tests accept the new table and column; the state constraints refuse a `pending` row with a token hash and a `used` row without `used_at`; token hashes are unique; candidates are found case-insensitively by normalized address and none for an unknown address; supersession changes only open recoveries of the account; `ResetPasswordByRecoveryAsync` changes the security stamp, clears the password-change requirement, keeps lockout, and ends every session (FR-003, FR-009, FR-011, FR-012)

**Checkpoint**: Schema, options, message type, stores, and the credential operation exist; no behavior is exposed yet

---

## Phase 2: User Story 1 - Recover a forgotten password by email (Priority: P1) 🎯 MVP

**Goal**: A member with a local password and a verified address requests a recovery from the sign-in page, receives one message, opens the link, sees their account name, sets a new password, and signs in with it; every earlier session has ended.

**Independent Test**: With an eligible member, request a recovery, read the message in Mailpit, check the link (account name shown, nothing used), complete with a new password, and confirm that the old password is refused, the new one accepted, and an earlier session ended.

### Handlers and delivery

- [x] T009 [P] [US1] Implement `RequestPasswordRecoveryCommand` and `RequestPasswordRecoveryHandler` in `src/platform/SocAlytics.Platform.Application/IdentityAccess/PasswordRecovery/RequestPasswordRecovery.cs`: validate the address with `EmailAddress.TryCreate` (field violation `address`: `required` or `invalid`), list candidates by the normalized address, and in one `IUnitOfWork` create a `pending` recovery (with the address hash and the member's current security stamp) and enqueue one `password-recovery` message for each candidate who is active, has a local password, and whose address is verified and matches; record `password-recovery.requested` audit events with `maskedAddress` (reason `queued` or `not-eligible` per matched member, or one event without member with reason `no-eligible-account`); define the audit event-type constants in this file; always return the same success result (FR-002–FR-005, FR-006, FR-007, FR-018)
- [x] T010 [P] [US1] Extend `src/platform/SocAlytics.Platform.Infrastructure/Email/EmailDeliveryProcessor.cs` for `password-recovery` messages: re-check that the member is active, still has a local password, and that the stored address is verified and matches the message's address hash, and that the recovery is `pending` or `issued` with the same address hash (otherwise mark it `invalidated` and discard the message); issue a fresh token with `ConfirmationTokens`, store its hash with `expires_at = now + PasswordRecovery:LinkLifetime`, build the link `<Web UI base>/password-recovery/complete#token=<token>`, render with the lifetime, and record a `password-recovery.issued` audit event with `maskedAddress`; never log the token or the address (FR-004, FR-008, FR-018, FR-019)
- [x] T011 [P] [US1] Implement `CheckPasswordRecoveryHandler` and `CompletePasswordRecoveryHandler` in `src/platform/SocAlytics.Platform.Application/IdentityAccess/PasswordRecovery/CheckPasswordRecovery.cs` and `CompletePasswordRecovery.cs`: hash the token, lock the recovery, require state `issued`, `expires_at` in the future, an active member with a local password, and a verified current address with the recovery's address hash (otherwise mark it `expired` or `invalidated` and fail with `OperationFailure.Validation("password-recovery-invalid")` for every case, as `ConfirmEmailHandler` does); the check returns the account name and uses nothing; the completion validates the password policy first (field violation `newPassword`: `required` or `password-policy`, token stays usable), then calls `ResetPasswordByRecoveryAsync`, marks the recovery `used`, records `password-recovery.completed` (succeeded, member), and on failure records an independent `password-recovery.completed` event (failed, reason `recovery-invalid` or `password-policy`); no session is created (FR-007, FR-010–FR-014, FR-018)
- [x] T012 [US1] Register the request, check, and completion handlers in `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs` (FR-021)

### API

- [x] T031 [US1] Update the endpoint classification guard `src/platform/Tests/SocAlytics.Platform.Architecture.Tests/EndpointSecurityTests.cs` for the three self-service operations without weakening it: add `requestPasswordRecovery`, `checkPasswordRecovery`, and `completePasswordRecovery` to `AnonymousOperations` (so each must still be anonymous), and replace the blanket `recovery` entry of `ForbiddenPathSegments` (it fails `POST /api/v1/password-recovery/requests` before T013 can pass) by a check that keeps `bootstrap`, `setup`, and every other recovery path forbidden and allows a `recovery` path only for exactly these three operations at exactly `/api/v1/password-recovery/requests`, `/checks`, and `/completions`; `confirmEmail`-style anonymous operations stay classified as before (research.md R8; FR-021)
- [x] T013 [US1] Add `src/platform/SocAlytics.Platform.Api/Endpoints/IdentityAccess/PasswordRecoveryEndpoints.cs` with the anonymous, JSON-only operations `requestPasswordRecovery` (`POST /api/v1/password-recovery/requests`, `202` empty), `checkPasswordRecovery` (`POST /api/v1/password-recovery/checks`, `200 { accountName }`), and `completePasswordRecovery` (`POST /api/v1/password-recovery/completions`, `204`), tag `Password recovery`, `Cache-Control: no-store` on every answer, `ProblemResults` for 400 and 415, tokens only in bodies; map them in `src/platform/SocAlytics.Platform.Api/Program.cs` (contracts/password-recovery-api.md; FR-021)
- [x] T014 [US1] Refresh the committed OpenAPI snapshot `src/platform/SocAlytics.Platform.Api/openapi/v1.json` with `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests` and confirm the Host tests pass with the three operations (FR-021)

### Web UI

- [x] T015 [US1] Add `src/clients/web/src/platform/adapters/passwordRecoveryAdapter.ts` over the generated Kiota client with `requestRecovery(address)`, `checkRecovery(token)` returning the account name, and `completeRecovery(token, newPassword)`, mapping `password-recovery-invalid`, field violations, and `429` with its wait time to application results (FR-021)
- [x] T016 [US1] Add the request page `src/clients/web/src/features/password-recovery/PasswordRecoveryRequestPage.tsx` (one address field; the generic confirmation after success; field problems; the wait time after `429`) and the namespace `src/clients/web/src/i18n/en/passwordRecovery.json`, registered in `src/clients/web/src/i18n/index.ts` (FR-001, FR-002, FR-022)
- [x] T017 [US1] Add the recovery page `src/clients/web/src/features/password-recovery/PasswordRecoveryCompletePage.tsx`: read `token` from the fragment and remove it from the address bar, call the check, show the account name and the new-password and confirmation fields, report mismatches and policy problems, complete, and lead to `/sign-in` with a success notice; for `password-recovery-invalid` show one neutral message with a link to `/password-recovery`; add its texts to `src/clients/web/src/i18n/en/passwordRecovery.json` (FR-010, FR-011, FR-013, FR-014, FR-022)
- [x] T018 [US1] Add the routes `/password-recovery` (under `RequireSignedOut`) and `/password-recovery/complete` (public) in `src/clients/web/src/router.tsx`, the "Forgot your password?" link in `src/clients/web/src/features/sign-in/SignInPage.tsx`, and the `password-recovery` notification type label in `src/clients/web/src/i18n/en/email.json` (FR-001, FR-020, FR-022)

### Tests for User Story 1

- [x] T019 [P] [US1] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/SelfServicePasswordRecoveryTests.cs` with the Mailpit-backed email harness: an eligible request answers `202` and produces exactly one message whose content matches FR-019 and whose link carries the token only in the fragment; the check returns the account name and leaves the recovery usable; a policy violation leaves the token usable; completion replaces the password, uses the token (a second completion fails with `password-recovery-invalid`), ends every session, clears a pending password-change requirement, creates no session, and leaves an external identity link unchanged; `getOwnNotificationPreferences` lists `password-recovery` as an account message and `setOwnNotificationPreference` refuses it (FR-001–FR-014, FR-019–FR-021; SC-001, SC-004)
- [x] T020 [P] [US1] Add `src/clients/web/tests/unit/features/password-recovery/passwordRecovery.test.tsx` for the adapter mapping, the request page (confirmation, field problem, wait time), the recovery page (account name, mismatch, policy problem, success redirect, neutral invalid message, token removed from the address bar), and the sign-in link (FR-001, FR-010, FR-013, FR-014, FR-022)
- [x] T021 [US1] Add `src/clients/web/tests/e2e/simulated/password-recovery.spec.ts` and the fake operations it needs in `src/clients/web/tests/e2e/support/simulatedApi.ts`: request from the sign-in page, open the recovery page with a token, complete, and land on sign-in; an invalid token shows the neutral message; pages pass the accessibility check (FR-001, FR-013, FR-022)
- [ ] T022 [US1] Add `src/clients/web/tests/e2e/platform/password-recovery.spec.ts`: verify an address for a member through Mailpit (`src/clients/web/tests/e2e/support/mailpit.ts`), request a recovery from the sign-in page, open the link from Mailpit, complete with a new password, and sign in with it (SC-001)

**Checkpoint**: User Story 1 is fully functional and testable through the API and the Web UI

---

## Phase 3: User Story 2 - Recovery reveals nothing and helps only eligible accounts (Priority: P2)

**Goal**: Answers are indistinguishable in status, body, and timing for every address, and only eligible members can ever recover.

**Independent Test**: Request recoveries for unknown, unverified, deactivated, provider-only, and eligible addresses; all answers are identical and take at least the minimum duration, and only the eligible member receives a message.

### Implementation for User Story 2

- [ ] T023 [US2] Return every `requestPasswordRecovery` answer (success and validation failure alike) no earlier than `PasswordRecovery:MinimumResponseDuration` after receipt, measured with the injected `TimeProvider`, in `src/platform/SocAlytics.Platform.Api/Endpoints/IdentityAccess/PasswordRecoveryEndpoints.cs` (FR-002; SC-002)

### Tests for User Story 2

- [ ] T024 [US2] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/SelfServicePasswordRecoveryEligibilityTests.cs`: answers for unknown, unverified, deactivated, no-local-password (provider-only and unredeemed set-password), and eligible addresses are identical in status and body and each takes at least the minimum duration, with no case consistently slower over repeated requests; only the eligible member receives a message; an empty or malformed address fails field validation; a shared address sends one message per eligible member whose link applies only to that member; a member deactivated after the message was sent gets the neutral failure and stays deactivated; unknown, expired (with an advanced `TimeProvider`), and used tokens all fail with the same `password-recovery-invalid` answer; a locked-out member completes a recovery and remains locked until the lockout ends; recovery never creates a password for a member without one (FR-002–FR-007, FR-012, FR-013; SC-002, SC-003)

**Checkpoint**: User Stories 1 and 2 work and are tested independently

---

## Phase 4: User Story 3 - Abuse limits, token lifecycle, and audit (Priority: P3)

**Goal**: Newer requests and other account changes invalidate open links, per-member and per-client limits hold, and every step is audited without secrets.

**Independent Test**: Exceed the per-member and per-client limits, request twice and use the first link, change the password before using a link, and inspect the audit trail and logs.

### Implementation for User Story 3

- [ ] T025 [US3] Supersede the member's open recoveries when a new one is created in `src/platform/SocAlytics.Platform.Application/IdentityAccess/PasswordRecovery/RequestPasswordRecovery.cs`, and require the member's current security stamp to equal the recovery's recorded stamp (otherwise mark it `invalidated`) in `CheckPasswordRecovery.cs` and `CompletePasswordRecovery.cs` in the same folder, so every password change, address change, and deactivation ends open links (FR-009; research R2)
- [ ] T026 [US3] Enforce the per-member limit in `src/platform/SocAlytics.Platform.Application/IdentityAccess/PasswordRecovery/RequestPasswordRecovery.cs`: count `password-recovery` messages of the member in the last hour with `IEmailMessageStore.CountCreatedSinceAsync` after locking the recipient with `LockRecipientAsync`, create no recovery or message at or above `MaxMessagesPerHour`, record the request event with reason `rate-limited`, and still return the generic result (FR-015; SC-006)
- [ ] T027 [US3] Add the per-client policies `password-recovery-request` (`RequestsPerMinute`) and `password-recovery-token` (`TokenAttemptsPerMinute`, shared by check and completion) partitioned by remote address, refusing with `429`, `Retry-After`, `Cache-Control: no-store`, and problem code `password-recovery-rate-limited`, in the new `src/platform/SocAlytics.Platform.Api/Endpoints/IdentityAccess/PasswordRecoveryRateLimiting.cs`; register them in `src/platform/SocAlytics.Platform.Api/Program.cs`, apply them and declare `429` in `src/platform/SocAlytics.Platform.Api/Endpoints/IdentityAccess/PasswordRecoveryEndpoints.cs`, and refresh `src/platform/SocAlytics.Platform.Api/openapi/v1.json` as in T014 (FR-016; contracts/password-recovery-api.md)

### Tests for User Story 3

- [ ] T028 [US3] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/SelfServicePasswordRecoveryLimitsTests.cs`: a fourth request within an hour for one member answers `202` and sends nothing; a sixth request per minute from one client and an eleventh check or completion per minute are refused with `429` and `Retry-After` for known and unknown addresses alike; a second accepted request makes the first link fail and the link of the second message work; an own password change, a redeemed Club Admin reset credential, a break-glass directive, an address change, an address removal, and a deactivation each make an open link fail with the neutral answer; the audit trail holds one event per request (per matched member or one without member), sent message, completion, and refused completion with masked addresses, and no audit event, captured log, or telemetry contains a full address, token, password, or message body (FR-009, FR-015–FR-018; SC-005, SC-006)

**Checkpoint**: All user stories are independently functional and tested

---

## Phase 5: Polish & Cross-Cutting Concerns

**Purpose**: Architecture truth for the merged state and end-to-end validation

- [ ] T029 Make `docs/architecture/` describe the implemented state: re-read `docs/architecture/email-notifications.md`, `platform-implementation.md` (API And Identity), `client-applications.md` (Web UI), `security-and-data-governance.md` (`THR-012`, Audit Events, `GOV-CRED-001`), `production-operations.md` (Versioned Production Profile), and `docs/architecture/README.md`, and correct every statement this feature makes false, including the planned, future-tense, "decided, not yet implemented", and "not implemented" wording about self-service password recovery (the plan's Architecture Updates list examples, not the whole job); keep production values Open / Blocking and invitations unimplemented; update `AGENTS.md` "Current State" so self-service recovery leaves the list of what does not exist and joins the identity evidence (constitution Principle I)
- [ ] T030 Run the validation in `specs/20261017-200823-password-recovery/quickstart.md`: `dotnet build src/platform/SocAlytics.Platform.slnx --no-restore`, `dotnet test src/platform/SocAlytics.Platform.slnx --no-build`, and in `src/clients/web` `npm run build`, `npm test`, and `npm run test:e2e`, and `node .github/scripts/check-markdown.mjs`; fix any failure in the files this feature changed (SC-001–SC-006)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Foundational (Phase 1)**: No dependencies; blocks all user stories.
- **User Story 1 (Phase 2)**: Depends on Phase 1.
- **User Story 2 (Phase 3)**: Depends on Phase 1 and on the request endpoint and handlers of User Story 1 (T009–T013), which it hardens.
- **User Story 3 (Phase 4)**: Depends on Phase 1 and on the handlers and endpoints of User Story 1 (T009–T014).
- **Polish (Phase 5)**: Depends on all user stories.

### Within Each Phase

- T001–T003 run in parallel; T004–T006 run in parallel after them; T007 registers what they created; T008 tests it.
- T009–T011 run in parallel; T012 registers their handlers; T031 first teaches the endpoint classification guard about the three operations; T013 exposes them; T014 refreshes the snapshot the Web UI client is generated from; T015–T018 build the Web UI in order; T019 and T020 run in parallel after it; T021 and T022 run one after the other because both are end-to-end suites with shared support files.
- T023 then T024; T025, T026, and T027 run one after the other because T025 and T026 change the same handler and T027 changes the endpoints and snapshot; T028 tests them.

### Parallel Opportunities

```text
Phase 1, group 1: T001 migration | T002 options | T003 message type and text
Phase 1, group 2: T004 recovery store | T005 email message column and outbox | T006 credential operation
Phase 2, group 1: T009 request handler | T010 delivery processor | T011 check and completion handlers
Phase 2, group 2: T019 platform integration tests | T020 Web UI unit tests
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Foundational.
2. Complete Phase 2: User Story 1.
3. Validate the happy path with the US1 tests and the quickstart scenario 1.

### Incremental Delivery

1. Foundational → User Story 1 (MVP: recovery works end to end).
2. User Story 2 → no enumeration and eligibility hardening.
3. User Story 3 → limits, invalidation, and audit evidence.
4. Polish → architecture and `AGENTS.md` truth for the merged state, full validation.
