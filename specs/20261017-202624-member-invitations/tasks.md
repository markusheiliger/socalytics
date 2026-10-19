---

description: "Task list for Member Invitations by Email"
---

# Tasks: Member Invitations by Email

**Input**: Design documents from `specs/20261017-202624-member-invitations/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: The spec requires automated evidence (SC-002 to SC-006), so every story has test tasks. Test tasks follow the implementation tasks of their story so that each landed task keeps the build and test suites green.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; they change disjoint files and do not depend on each other
- **[Story]**: Which user story this task belongs to (US1, US2, US3)
- Every task names its exact file paths

---

## Phase 1: Foundational (Blocking Prerequisites)

**Purpose**: Schema, configuration, domain rules, message type, and stores that every story needs

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

### Schema, configuration, domain, and message type

- [x] T001 [P] Add the forward-only migration `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Migrations/NNNN_identityaccess_member_invitations.sql`, numbered as the next free number on an up-to-date default branch: create the versioned `socalytics.member_invitations` (`id`, `address`, `address_normalized`, `address_hash`, `account_name` with the `AccountName` rules "3 to 64 characters", `account_name_normalized`, `state text NOT NULL CHECK (state IN ('pending', 'accepted', 'revoked'))`, `invited_by`, `created_at`, `resend_count integer NOT NULL DEFAULT 0`, `revoked_by`/`revoked_at` "set exactly when `state = 'revoked'`", `accepted_member_id`/`accepted_at` "set exactly when `state = 'accepted'`", `version`) with `socalytics.attach_version_trigger`, unique partial indexes on `account_name_normalized` and `address_normalized` "where `state = 'pending'`", and an index on `created_at DESC`; the aggregate child `socalytics.member_invitation_team_roles` (`invitation_id`, `team_id REFERENCES socalytics.team (id)`, `role text CHECK (role IN ('coach', 'viewer'))`, primary key `(invitation_id, team_id)`) with `socalytics.attach_aggregate_child_triggers`; the unversioned `socalytics.member_invitation_links` (`id`, `invitation_id`, `state text CHECK (state IN ('pending', 'issued', 'used', 'superseded'))`, `token_hash bytea NULL`, `created_at`, `issued_at`, `expires_at`, `used_at`) with the constraints "`pending` has no token hash; `issued` and `used` carry token hash, `issued_at`, and `expires_at`; `superseded` carries them only when issued before; `used` exactly when `used_at` is set", a unique index on `token_hash` where present, and an index on `invitation_id` for `state IN ('pending', 'issued')`; on `socalytics.email_messages` drop `NOT NULL` from `account_id`, add `invitation_ref uuid NULL REFERENCES socalytics.member_invitation_links (id)`, and add the check "`(notification_type = 'member-invitation') = (invitation_ref IS NOT NULL AND account_id IS NULL)`"; never edit an applied migration; classify `member_invitation_team_roles` as `ChildOf("member_invitations", "invitation_id")` and `member_invitation_links` as unversioned ("single-use invitation link state; the state transition is the guard") in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Structure/PersistedTableClassifications.cs` (data-model.md) — redo: the first attempt named the column `invitation_link_id`, which `EmailPersistenceTests.EmailMessagesStoreNoAddressBodyOrTokenColumns` rejects (no `email_messages` column may contain `link`); the column is `invitation_ref`, and the unapplied migration `0023_identityaccess_member_invitations.sql` is edited in place
- [x] T002 [P] Add `InvitationOptions` (section `Invitations`: `LinkLifetime` 7 days, `MaxResendsPerHour` 3, `TokenAttemptsPerMinute` 10, and `IsValid` requiring positive values) in `src/platform/SocAlytics.Platform.Application/IdentityAccess/Invitations/InvitationOptions.cs`, and the audit event-type and reason constants of data-model.md (`invitation.created`, `invitation.issued`, `invitation.resent`, `invitation.revoked`, `invitation.accepted`, `invitation.acceptance-refused`) in `src/platform/SocAlytics.Platform.Application/IdentityAccess/Invitations/InvitationAuditEvents.cs` (FR-014, FR-017–FR-019)
- [x] T003 [P] Add the domain aggregate `MemberInvitation` with its states (`pending`, `accepted`, `revoked`), wire values, the derived display state (`expired` when the open link is issued and past `expires_at`), and the transition rules (resend from pending or expired, revoke from pending or expired, accept from pending with an open unexpired link; `accepted` and `revoked` final) in `src/platform/SocAlytics.Platform.Domain/IdentityAccess/MemberInvitation.cs` (data-model.md; FR-008, FR-012, FR-016)
- [x] T004 [P] Add the audience `Invitee` and the account-message type `member-invitation` (`NotificationTypes.MemberInvitationCode`, `AccountMessage: true`, `DefaultEnabled: true`, link path `/invitations/accept`) to `NotificationTypes.All` in `src/platform/SocAlytics.Platform.Application/Notifications/NotificationTypes.cs`; omit `Invitee`-audience types from the own preference list and refuse them like unknown types in `src/platform/SocAlytics.Platform.Application/IdentityAccess/Email/NotificationPreferenceHandlers.cs`; add its English subject and body (invitation to join the club's SocAlytics with `{clubName}`, ignore-if-unexpected note, `{lifetime}`, `{link}`; no account name, password, inviting member, roles, or other club data) in `src/platform/SocAlytics.Platform.Infrastructure/Email/Messages.en.json` (FR-020, FR-021)

### Stores and outbox

- [x] T005 [P] Add `IMemberInvitationStore` with its records in `src/platform/SocAlytics.Platform.Application/IdentityAccess/Invitations/IMemberInvitationStore.cs` and its Dapper implementation in `src/platform/SocAlytics.Platform.Infrastructure/Persistence/IdentityAccess/MemberInvitationStore.cs`: whether a normalized account name is taken by a member or reserved by a pending or expired invitation; whether a normalized address is the current address of a member (any state) or of a pending or expired invitation; insert an invitation with its team roles; find and lock by id; list newest first with an optional display-state filter, team names, inviting member, the newest link's `issued_at` and `expires_at`, and the delivery state of its message (`queued`/`sending` as queued, `sent`, `failed`/`discarded` as failed); create a `pending` link and supersede the invitation's open links; count links created since a time; find a link by id; find and lock a link by token hash with its invitation; issue a link (token hash, `issued_at`, `expires_at`); mark a link `used`; and mark an invitation `revoked` or `accepted` conditional on its version (data-model.md; FR-003–FR-005, FR-008, FR-013–FR-016)
- [x] T006 [P] Add the nullable `InvitationRef` as an optional trailing parameter (default `null`) and make `AccountId` nullable on `NewEmailMessage` and `StoredEmailMessage` in `src/platform/SocAlytics.Platform.Application/IdentityAccess/Email/IEmailStores.cs`, write and read `email_messages.invitation_ref` in `src/platform/SocAlytics.Platform.Infrastructure/Persistence/IdentityAccess/EmailStores.cs`, let `IEmailOutbox` in `src/platform/SocAlytics.Platform.Application/Notifications/IEmailOutbox.cs` enqueue a `member-invitation` message for an address hash and an invitation link id without an account, and keep `src/platform/SocAlytics.Platform.Infrastructure/Email/EmailDeliveryProcessor.cs` and the existing callers in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Notifications/EmailPersistenceTests.cs` and `EmailDeliveryWorkerTests.cs` compiling, the processor by discarding messages without an account until US1 handles them (FR-005)

### Registration

- [x] T007 Register `IMemberInvitationStore` in `src/platform/SocAlytics.Platform.Infrastructure/InfrastructureServiceCollectionExtensions.cs`; bind and validate `InvitationOptions` in `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs`; set the development values explicitly in the `Invitations` section of `src/platform/SocAlytics.Platform.Api/appsettings.Development.json`; and add the detail keys the invitation audit events use that the allow-list in `src/platform/SocAlytics.Platform.Infrastructure/Persistence/PostgresAuditTrail.cs` lacks (`teamCount`, `memberId`; `maskedAddress` is already allowed) (FR-018, FR-019)

### Foundational tests

- [x] T008 Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/MemberInvitationStoreTests.cs`: the migration applies and the persistence structure tests accept the new tables and columns; the version trigger bumps the invitation on its own and its team-role changes but not on link changes; the pending-only unique indexes refuse a second pending invitation for the same normalized name or address and accept one after revocation; the link constraints refuse a `pending` link with a token hash and a `used` link without `used_at`; the `email_messages` check refuses a `member-invitation` message with an account and any other type without one; creating a link supersedes the open ones; the list derives `expired` from the newest link and reports the delivery state (FR-003–FR-005, FR-008, FR-013)

**Checkpoint**: Schema, options, domain rules, message type, and stores exist; no behavior is exposed yet

---

## Phase 2: User Story 1 - Invite a person and have them join (Priority: P1) 🎯 MVP

**Goal**: A Club Admin invites an address with an account name and team roles; the invitee receives one message, opens the link, sees club and account name, sets a password, and signs in as an active member with the verified address and the team roles.

**Independent Test**: As a Club Admin, invite an address with a Coach role on one team, read the message in Mailpit, open its link, set a password, and confirm that the new member signs in, holds the Coach role, and has the invited address verified.

### Handlers and delivery

- [x] T009 [P] [US1] Implement `CreateInvitationCommand` and `CreateInvitationHandler` in `src/platform/SocAlytics.Platform.Application/IdentityAccess/Invitations/CreateInvitation.cs`: authorize `ClubPermission.Administer` (otherwise `Forbidden`); validate the address with `EmailAddress.TryCreate`, the account name with `AccountName.TryCreate`, and each team role (team exists, role `coach` or `viewer`, no duplicate team) with field violations `address`, `accountName`, `teamRoles[i].teamId`, `teamRoles[i].role`; refuse `email-address-in-use` and `account-name-taken` (also on unique violations) and record `invitation.created` refused with `maskedAddress` and the reason; otherwise, in one `IUnitOfWork`, insert the invitation and its team roles, create its first link, enqueue one `member-invitation` message through `IEmailOutbox`, and record `invitation.created` (succeeded, `maskedAddress`, `teamCount`) (FR-001–FR-005, FR-019)
- [x] T010 [P] [US1] Extend `src/platform/SocAlytics.Platform.Infrastructure/Email/EmailDeliveryProcessor.cs` for `member-invitation` messages: re-check that the message's link is `pending` or `issued`, that its invitation is `pending`, and that the invitation's address hash equals the message's (otherwise discard the message); issue a fresh token with `ConfirmationTokens`, store its hash with `expires_at = now + Invitations:LinkLifetime`, build the link `<Web UI base>/invitations/accept#token=<token>`, render with the club name and lifetime, send to the invitation's address, and record an `invitation.issued` audit event with `maskedAddress`; never log the token or the address (FR-005, FR-006, FR-019, FR-020)
- [x] T011 [P] [US1] Implement `CheckInvitationHandler` and `AcceptInvitationHandler` in `src/platform/SocAlytics.Platform.Application/IdentityAccess/Invitations/CheckInvitation.cs` and `AcceptInvitation.cs`: hash the token, lock the link and its invitation, require the link `issued` with `expires_at` in the future and the invitation `pending` (otherwise fail with `OperationFailure.Validation("invitation-invalid")` for every case); the check returns the club name and the account name and changes nothing; the acceptance validates the password policy first (field violation `password`: `required` or `password-policy`, link stays usable), then in one unit of work creates the account with `IAccountCredentialService.CreateAccountAsync` (reserved name, chosen password, no password-change requirement, created by the inviting Club Admin), inserts the invited address with `IMemberEmailAddressStore.InsertAsync` and marks it verified with `MarkVerifiedAsync`, upserts every invited team role with `IMemberAccountStore.UpsertTeamRoleAsync`, marks the link `used` and the invitation `accepted` with the member, and records `member.created`, `team-role.assigned`, and `invitation.accepted` (`memberId`, `maskedAddress`); on failure it records an independent `invitation.acceptance-refused` event (reason `invitation-invalid` or `password-policy`); no session is created (FR-007–FR-012, FR-019)
- [x] T012 [US1] Register the create, check, and accept handlers in `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs` (FR-022)

### API

- [ ] T013 [US1] Add `src/platform/SocAlytics.Platform.Api/Endpoints/IdentityAccess/InvitationEndpoints.cs` with `createInvitation` (`POST /api/v1/invitations` under `MapMemberApi()`, `201` with `Invitation`, `ETag`, and `Location`) and the anonymous, JSON-only `checkInvitation` (`POST /api/v1/invitations/checks`, `200 { clubName, accountName }`) and `acceptInvitation` (`POST /api/v1/invitations/acceptances`, `204`), tag `Invitations`, `Cache-Control: no-store` on the anonymous answers, `ProblemResults` for every failure, tokens only in bodies; map them in `src/platform/SocAlytics.Platform.Api/Program.cs`; add `checkInvitation` and `acceptInvitation` to the `AnonymousOperations` set in `src/platform/Tests/SocAlytics.Platform.Architecture.Tests/EndpointSecurityTests.cs` (the architecture test that classifies every `/api/v1` endpoint and fails on an unlisted anonymous one; `createInvitation` stays authenticated and needs no entry), changing nothing else in that test (contracts/member-invitations-api.md; FR-022)
- [ ] T014 [US1] Refresh the committed OpenAPI snapshot `src/platform/SocAlytics.Platform.Api/openapi/v1.json` with `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests` and confirm the Host tests pass with the three operations (FR-022)

### Web UI

- [ ] T015 [US1] Add `src/clients/web/src/platform/adapters/invitationAdapter.ts` over the generated Kiota client with `createInvitation(address, accountName, teamRoles)`, `checkInvitation(token)` returning club and account name, and `acceptInvitation(token, password)`, mapping `invitation-invalid`, `email-address-in-use`, `account-name-taken`, field violations, and `429` with its wait time to application results (FR-022)
- [ ] T016 [US1] Add `src/clients/web/src/features/members/InviteMemberDialog.tsx` (address, account name, and team-role rows of team and Coach or Viewer; field problems and the two conflicts) and the "Invite member" action in `src/clients/web/src/features/members/MembersPage.tsx`, with the namespace `src/clients/web/src/i18n/en/invitations.json` registered in `src/clients/web/src/i18n/index.ts` (FR-001–FR-003, FR-023)
- [ ] T017 [US1] Add the public page `src/clients/web/src/features/invitations/InvitationAcceptPage.tsx`: read `token` from the fragment and remove it from the address bar, call the check, show club and account name with the password and confirmation fields, report mismatches and policy problems, accept, and lead to `/sign-in` with a success notice; for `invitation-invalid` show one neutral message that asks the invitee to contact their club; add the public route `/invitations/accept` in `src/clients/web/src/router.tsx` and the page texts to `src/clients/web/src/i18n/en/invitations.json` (FR-007–FR-011, FR-023)

### Tests for User Story 1

- [ ] T018 [P] [US1] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/MemberInvitationTests.cs` with the Mailpit-backed email harness: a Club Admin's invitation answers `201` and produces exactly one message whose content matches FR-020 and whose link carries the token only in the fragment; no member account, role, or member address exists before acceptance; the check returns club and account name and leaves the link usable; a policy violation leaves the link usable; acceptance creates exactly one active member with the reserved name, the chosen password, the invited address verified, and the invited team roles, requires no password change, creates no session, and uses the link (a second acceptance fails with `invitation-invalid`); the new member signs in; `getOwnNotificationPreferences` does not list `member-invitation` and `setOwnNotificationPreference` refuses it (FR-001–FR-012, FR-020–FR-022; SC-001, SC-002)
- [ ] T019 [P] [US1] Add `src/clients/web/tests/unit/features/invitations/invitations.test.tsx` for the adapter mapping, the invite dialog (team-role rows, field problems, conflicts), and the invitation page (club and account name, mismatch, policy problem, success redirect, neutral invalid message, token removed from the address bar) (FR-001–FR-003, FR-007–FR-011, FR-023)
- [ ] T020 [US1] Add `src/clients/web/tests/e2e/simulated/invitations.spec.ts` and the fake operations it needs in `src/clients/web/tests/e2e/support/simulatedApi.ts`: invite from the members area, open the invitation page with a token, accept, and land on sign-in; an invalid token shows the neutral message; pages pass the accessibility check (FR-001, FR-008, FR-023)
- [ ] T021 [US1] Add `src/clients/web/tests/e2e/platform/invitations.spec.ts`: invite an address with a team role as the bootstrap Club Admin, open the link from Mailpit (`src/clients/web/tests/e2e/support/mailpit.ts`), accept with a password, and sign in as the new member who sees the team (SC-001)

**Checkpoint**: User Story 1 is fully functional and testable through the API and the Web UI

---

## Phase 3: User Story 2 - Administer invitations (Priority: P2)

**Goal**: Club Admins list invitations with their states and delivery state, resend pending or expired ones, and revoke pending ones, conditionally on the version they saw.

**Independent Test**: Invite, resend (only the second link works), let an invitation expire and resend it, revoke one (its link fails), and confirm the list shows each state.

### Handlers

- [ ] T022 [P] [US2] Implement `ListInvitationsQuery` and `ListInvitationsHandler` in `src/platform/SocAlytics.Platform.Application/IdentityAccess/Invitations/ListInvitations.cs`: authorize `ClubPermission.Administer`, validate the optional state filter (`pending`, `expired`, `accepted`, `revoked`), and return every invitation newest first with the fields of the contract's `Invitation` (FR-013)
- [ ] T023 [P] [US2] Implement `ResendInvitationHandler` and `RevokeInvitationHandler` in `src/platform/SocAlytics.Platform.Application/IdentityAccess/Invitations/ResendInvitation.cs` and `RevokeInvitation.cs`: authorize `ClubPermission.Administer`; lock the invitation; refuse a version other than the expected one (`VersionMismatch`) and accepted or revoked invitations (`invitation-not-open`); resend counts the links the invitation's resends created in the last hour (the link created with the invitation does not count) and refuses at or above `Invitations:MaxResendsPerHour` with `invitation-resend-rate-limited` and the time to wait, otherwise creates a new link (superseding the open one), enqueues a new message, and increments `resend_count`; revoke supersedes the open link and marks the invitation `revoked` with actor and time, releasing its reservations; both record `invitation.resent` or `invitation.revoked` as succeeded or refused with the reason and `maskedAddress` (FR-014–FR-016, FR-019)
- [ ] T024 [US2] Register the list, resend, and revoke handlers in `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs` (FR-022)

### API and Web UI

- [ ] T025 [US2] Add `listInvitations` (`GET /api/v1/invitations?state=`), `resendInvitation` (`POST /api/v1/invitations/{invitationId}/resend`), and `revokeInvitation` (`POST /api/v1/invitations/{invitationId}/revoke`) under `MapMemberApi()` with `IfMatchHeader`, `ETag`, `404`, `409 invitation-not-open`, `412`, `428`, and `429` with `Retry-After`, in `src/platform/SocAlytics.Platform.Api/Endpoints/IdentityAccess/InvitationEndpoints.cs`, and refresh `src/platform/SocAlytics.Platform.Api/openapi/v1.json` as in T014 (contracts/member-invitations-api.md; FR-013–FR-016, FR-022)
- [ ] T026 [US2] Add `src/clients/web/src/features/members/InvitationsSection.tsx` (newest-first list with state filter, address, account name, team roles, inviting Club Admin, times, delivery state, link to the accepted member; resend and confirmed revoke with conflict and wait-time handling) shown in `src/clients/web/src/features/members/MembersPage.tsx`, with `listInvitations`, `resendInvitation`, and `revokeInvitation` in `src/clients/web/src/platform/adapters/invitationAdapter.ts` and the texts in `src/clients/web/src/i18n/en/invitations.json` (FR-013–FR-016, FR-023)

### Tests for User Story 2

- [ ] T027 [P] [US2] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/MemberInvitationAdministrationTests.cs`: the list returns every invitation newest first, filters by state, and shows `expired` once the newest link passed its lifetime (advanced `TimeProvider`) and the delivery state; resend of a pending invitation sends a new message, the first link fails with `invitation-invalid`, and the second works; resend of an expired invitation makes it pending; revoke of a pending and of an expired invitation makes the link fail and releases name and address for a new invitation; resend and revoke of accepted or revoked invitations answer `409 invitation-not-open`; a stale `If-Match` answers `412` and a missing one `428`; a fourth resend within an hour answers `429` with `Retry-After`; message delivery does not change the invitation's version (FR-013–FR-016; SC-006)
- [ ] T028 [P] [US2] Add `src/clients/web/tests/unit/features/members/invitationsSection.test.tsx` for the list, the state filter, the delivery state, resend, the revoke confirmation, the conflict refresh, and the wait time (FR-013–FR-016, FR-023)
- [ ] T029 [US2] Add `src/clients/web/tests/e2e/simulated/invitation-administration.spec.ts` and the fake list, resend, and revoke operations in `src/clients/web/tests/e2e/support/simulatedApi.ts`: the members area lists invitations by state, resends, and revokes after confirming; the section passes the accessibility check (FR-013–FR-016, FR-023)

**Checkpoint**: User Stories 1 and 2 work and are tested independently

---

## Phase 4: User Story 3 - Safe invitations: conflicts, invalid links, limits, and audit (Priority: P3)

**Goal**: Invitations never create duplicates, never work after they end, never flood a mailbox or allow token guessing, are restricted to Club Admins, and are audited without secrets.

**Independent Test**: Invite a used address and a taken name, open expired, revoked, superseded, used, and unknown links, exceed the per-client link limit, try the operations as other roles, and inspect the audit trail and logs.

### Implementation for User Story 3

- [ ] T030 [US3] Refuse an account name reserved by a pending or expired invitation with `account-name-taken` in `src/platform/SocAlytics.Platform.Application/IdentityAccess/CreateMemberHandler.cs`, using `IMemberInvitationStore` (FR-003; research R2)
- [ ] T031 [US3] Add the per-client policy `invitation-token` (`Invitations:TokenAttemptsPerMinute`, shared by check and acceptance) partitioned by remote address, refusing with `429`, `Retry-After`, `Cache-Control: no-store`, and problem code `invitation-rate-limited`, in the new `src/platform/SocAlytics.Platform.Api/Endpoints/IdentityAccess/InvitationRateLimiting.cs`; register it in `src/platform/SocAlytics.Platform.Api/Program.cs`, apply it and declare `429` in `src/platform/SocAlytics.Platform.Api/Endpoints/IdentityAccess/InvitationEndpoints.cs`, and refresh `src/platform/SocAlytics.Platform.Api/openapi/v1.json` as in T014 (FR-017; contracts/member-invitations-api.md)

### Tests for User Story 3

- [ ] T032 [US3] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/MemberInvitationSafetyTests.cs`: invitations for the current address of a member (verified, unverified, or deactivated member) or of a pending or expired invitation answer `409 email-address-in-use`, and for a member's or pending or expired invitation's account name `409 account-name-taken`, before any message; `createMember` refuses a name reserved by a pending or expired invitation; expired, revoked, superseded, used, and unknown tokens all answer the same `invitation-invalid` problem on check and acceptance and create no member; an eleventh check or acceptance per minute from one client answers `429` with `Retry-After`; Coaches, Viewers, Registrars, and signed-out callers are refused for create, list, resend, and revoke and learn nothing; the audit trail holds one event per creation, sent message, resend, revocation, acceptance, refused creation, refused resend or revocation, and refused acceptance with masked addresses, and no audit event, captured log, or telemetry contains a full address, token, password, or message body (FR-003, FR-008, FR-012, FR-017, FR-019; SC-003–SC-006)

**Checkpoint**: All user stories are independently functional and tested

---

## Phase 5: Polish & Cross-Cutting Concerns

**Purpose**: Architecture truth for the merged state and end-to-end validation

- [ ] T033 Make `docs/architecture/` describe the implemented state: re-read `docs/architecture/email-notifications.md`, `platform-implementation.md` (API And Identity), `client-applications.md` (Web UI), `terminology-and-principles.md` (Terminology), `security-and-data-governance.md` (`DAT-001`, `THR-012`, Audit Events, `GOV-CRED-001`), `production-operations.md` (Versioned Production Profile), and `docs/architecture/README.md`, and correct every statement this feature makes false, including the planned, future-tense, "decided, not yet implemented", "once implemented", and "not implemented" wording about invitations (the plan's Architecture Updates list examples, not the whole job); keep production values Open / Blocking; update `AGENTS.md` "Current State" so the identity foundation evidence names member invitations by email (constitution Principle I)
- [ ] T034 Run the validation in `specs/20261017-202624-member-invitations/quickstart.md`: `dotnet build src/platform/SocAlytics.Platform.slnx --no-restore`, `dotnet test src/platform/SocAlytics.Platform.slnx --no-build`, and in `src/clients/web` `npm run build`, `npm test`, and `npm run test:e2e`, and `node .github/scripts/check-markdown.mjs`; fix any failure in the files this feature changed (SC-001–SC-006)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Foundational (Phase 1)**: No dependencies; blocks all user stories.
- **User Story 1 (Phase 2)**: Depends on Phase 1.
- **User Story 2 (Phase 3)**: Depends on Phase 1 and on the create handler, delivery, endpoints, adapter, and members-area action of User Story 1 (T009–T017).
- **User Story 3 (Phase 4)**: Depends on Phase 1 and on the handlers and endpoints of User Stories 1 and 2 (T009–T014, T022–T025).
- **Polish (Phase 5)**: Depends on all user stories.

### Within Each Phase

- T001–T004 run in parallel; T005 and T006 run in parallel after them; T007 registers what they created; T008 tests it.
- T009–T011 run in parallel; T012 registers their handlers; T013 exposes them; T014 refreshes the snapshot the Web UI client is generated from; T015–T017 build the Web UI in order (T016 and T017 share the translation namespace); T018 and T019 run in parallel after it; T020 and T021 run one after the other because both are end-to-end suites with shared support files.
- T022 and T023 run in parallel; T024 registers them; T025 exposes them and refreshes the snapshot; T026 builds the section; T027 and T028 run in parallel; T029 follows because it changes the shared simulated API.
- T030, T031, and T032 run one after the other.

### Parallel Opportunities

```text
Phase 1, group 1: T001 migration | T002 options and audit constants | T003 domain aggregate | T004 message type and text
Phase 1, group 2: T005 invitation store | T006 email message columns and outbox
Phase 2, group 1: T009 create handler | T010 delivery processor | T011 check and accept handlers
Phase 2, group 2: T018 platform integration tests | T019 Web UI unit tests
Phase 3, group 1: T022 list handler | T023 resend and revoke handlers
Phase 3, group 2: T027 platform integration tests | T028 Web UI unit tests
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Foundational.
2. Complete Phase 2: User Story 1.
3. Validate the happy path with the US1 tests and quickstart steps 1 to 3 and 5.

### Incremental Delivery

1. Foundational → User Story 1 (MVP: invite and accept end to end).
2. User Story 2 → list, resend, revoke, and expiry.
3. User Story 3 → conflicts with direct member creation, per-client limits, authorization, and audit evidence.
4. Polish → architecture and `AGENTS.md` truth for the merged state, full validation.
