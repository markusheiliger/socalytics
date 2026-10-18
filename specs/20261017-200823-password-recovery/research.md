# Research: Self-Service Password Recovery

All Technical Context items are resolved from the architecture, the existing code, and the clarifications in
[spec.md](spec.md). No new framework, SDK, or tool is needed.

## R1 - Recovery record and token issue

- **Decision**: A new table `password_recoveries` modelled on `email_confirmations` (migration `0019`): one row per
  eligible member and request, state `pending` → `issued` → `used` (or `superseded`, `invalidated`, `expired`), the
  token hash written by the delivery worker when it sends the message, `issued_at`, `expires_at`, `used_at`, the hashed
  address the message was created for, and the member's security stamp at request time. `email_messages` gains a
  nullable `password_recovery_id` column referencing it, beside `confirmation_id`. The token is generated and hashed with
  the existing `ConfirmationTokens` helper and carried in the link fragment (`#token=`), exactly as confirmations.
- **Rationale**: Reuses the proven confirmation pattern that
  [Email Notifications → Email Addresses](../../docs/architecture/email-notifications.md#email-addresses) prescribes
  (worker-issued token, hash only, fragment), keeps the raw token out of the database and logs, and lets each worker
  attempt issue a fresh token.
- **Alternatives considered**: Reusing `one_time_credential` with a new purpose (rejected: those credentials are issued
  synchronously by a Club Admin, carry an issuer, and are revoked on issuer role loss, none of which fits a worker-issued
  token); reusing `email_confirmations` with a type column (rejected: confirmations change address state and their
  lifecycle and audit differ).

## R2 - Invalidation by other changes

- **Decision**: A recovery is usable only while (a) its state is `issued` and it has not expired, (b) the member is
  active and still has a local password, (c) the member's current address is verified and its hash equals the
  recovery's address hash, and (d) the member's security stamp equals the stamp recorded at request time. A new
  request for the member marks the member's open recoveries `superseded` in the same unit of work. Completion and the
  link check evaluate these conditions under a row lock; a failing recovery is marked `invalidated` (or `expired`) and
  answered with the neutral failure.
- **Rationale**: ASP.NET Core Identity changes the security stamp whenever a password is set or removed, so every
  password path (own change, Club Admin reset credential, break-glass directive, recovery) invalidates open recoveries
  without touching those handlers; address changes and deactivation are covered by (b) and (c). This satisfies FR-007
  and FR-009 with one check.
- **Alternatives considered**: Revoking recoveries explicitly in every password, address, and membership handler
  (rejected: many touch points, easy to miss one); not checking the stamp (rejected: a password changed after the
  request would leave a valid link).

## R3 - Request answer without enumeration

- **Decision**: The request handler normalizes the address with the existing `EmailAddress` value, looks up every
  member whose current address normalizes to it (one indexed query, also for unknown addresses), evaluates eligibility
  and the per-member limit, writes recoveries, outbox entries, and audit events in one unit of work, and the endpoint
  returns `202 Accepted` with an empty body no earlier than `PasswordRecovery:MinimumResponseDuration` (development
  400 ms) after receipt. A migration adds an index on `member_email_addresses (address_normalized)`.
- **Rationale**: Same work for every address plus a response floor that exceeds the work time makes answers
  indistinguishable in status, body, and timing (FR-002, SC-002), matching the existing rule that refused sign-ins
  perform the same hash work.
- **Alternatives considered**: Queueing the raw request for background matching (rejected: would persist addresses of
  non-members); constant-time padding without the floor (rejected: database timing still leaks).

## R4 - Rate limits

- **Decision**: Per-client limits use ASP.NET Core rate limiting partitioned by remote address, as
  `EmailRateLimiting` does: `password-recovery-request` (development 5 per minute) and `password-recovery-token`
  (development 10 per minute, shared by link check and completion), refused with `429` and `Retry-After`. The per-member
  message limit (development 3 per hour) counts `email_messages` of type `password-recovery` for the member in the last
  hour (existing index `ix_email_messages_type_account_created`); a request beyond it creates no recovery and still gets
  the generic answer.
- **Rationale**: Mirrors the confirmation limits, survives restarts and multiple API instances for the per-member limit,
  and never reveals an account through the per-member limit (FR-015, FR-016).
- **Alternatives considered**: A per-address in-memory limiter (rejected: per member was chosen in the clarification and
  in-memory state is lost across instances).

## R5 - Completion

- **Decision**: `CompletePasswordRecoveryHandler` validates the password policy first (a violation leaves the token
  usable), then in one unit of work locks the recovery by token hash, re-evaluates R2, replaces the password through
  a new `IAccountCredentialService.ResetPasswordByRecoveryAsync` (remove and add password, clear
  `PasswordChangeRequired`, keep lockout fields unchanged), marks the recovery `used`, ends every session with the
  existing end reason `password-reset`, and records the completion audit event. Failures record an independent audit
  event. No session is created.
- **Rationale**: Reuses the redemption flow of Club Admin reset credentials (`AccountCredentialService.RedeemCredentialAsync`)
  and its session ending; `password-reset` already exists in the `member_session.end_reason` check, so no constraint
  change is needed. FR-011 to FR-014.
- **Alternatives considered**: A new end reason `password-recovered` (rejected: needs a constraint change and adds no
  behavior; the audit event distinguishes recovery); signing the member in after completion (rejected by FR-014).

## R6 - Message type and content

- **Decision**: A new account-message notification type `password-recovery` in `NotificationTypes` (audience
  `AddressOwner`, `AccountMessage: true`, link path `/password-recovery/complete`), with English text in
  `Infrastructure/Email/Messages.en.json` (subject, ignore-if-not-requested note, lifetime, link). The delivery processor
  treats it like the confirmation type: re-check eligibility (active, local password, verified matching address,
  recovery still `pending` or `issued`), issue a fresh token, and record the `password-recovery.issued` audit event.
- **Rationale**: Being in `NotificationTypes.All` lists it as an always-sent account message in the existing
  preferences read without new endpoints (FR-020); content stays minimal (FR-019).
- **Alternatives considered**: A separate delivery path (rejected: duplicates the outbox rules).

## R7 - Audit events

- **Decision**: Event types `password-recovery.requested` (one per matched member with reason `queued`,
  `not-eligible`, or `rate-limited`, or one without member with reason `no-eligible-account`; details `maskedAddress`),
  `password-recovery.issued` (member, `maskedAddress`), and
  `password-recovery.completed` (succeeded with member; failed with reason `recovery-invalid` or `password-policy`).
  Detail keys are added to the `PostgresAuditTrail` allow-list where missing. No token, password, or full address is
  ever recorded.
- **Rationale**: FR-018 and the masked-address audit rule of
  [Email Notifications → Security And Governance](../../docs/architecture/email-notifications.md#security-and-governance).
  Link checks change nothing and are rate limited, so they are not audited.
- **Alternatives considered**: One event per request listing all members (rejected: the audit resource is a single
  member).

## R8 - API shape

- **Decision**: Three anonymous, JSON-only `/api/v1` operations: `POST /api/v1/password-recovery/requests`
  (`requestPasswordRecovery`, body `{ address }`, `202`), `POST /api/v1/password-recovery/checks`
  (`checkPasswordRecovery`, body `{ token }`, `200 { accountName }`), and `POST /api/v1/password-recovery/completions`
  (`completePasswordRecovery`, body `{ token, newPassword }`, `204`). Tokens travel only in bodies; every answer is
  `Cache-Control: no-store`. Neutral failures use the problem code `password-recovery-invalid`. See
  [contracts/password-recovery-api.md](contracts/password-recovery-api.md).
- **Rationale**: Follows `confirmEmail` (anonymous, `JsonOnlyFilter`, rate limited) and keeps tokens out of URLs.
- **Alternatives considered**: A `GET` link check with the token in the query (rejected: tokens would reach logs).

## R9 - Web UI

- **Decision**: A "Forgot your password?" link on `SignInPage`; a request page `/password-recovery` under
  `RequireSignedOut`; a public recovery page `/password-recovery/complete` that reads the token from the fragment,
  calls the check, shows the account name and the password form, and leads to `/sign-in` on success. A
  `passwordRecoveryAdapter` over the Kiota client, an `en/passwordRecovery.json` namespace, and a translation for the
  new notification type in the preferences list. Vitest unit tests and Playwright `simulated` and `platform` tests (the
  latter reads Mailpit through `tests/e2e/support/mailpit.ts`).
- **Rationale**: Mirrors `EmailConfirmPage` and `SetPasswordPage` and the adapter rule of the Web UI.
- **Alternatives considered**: Putting the request form inline on the sign-in page (rejected: separate pages keep the
  sign-in page simple and testable).
