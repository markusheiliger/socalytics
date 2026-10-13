# Research: Member Email Addresses and Pending-Request Notifications

Builds on the adopted channel in [email-notifications.md](../../docs/architecture/email-notifications.md),
Club and Identity Foundation (`member_account`, sessions, `IAuditTrail`,
`MapMemberApi`, `IUnitOfWork`), Analyst Manager Registration
(`SubmitPairingCode`, `RegistryAuditEvents.PendingRequestCreated`), and Web UI
Foundation (account and member views, generated client, `simulated` and
`platform` browser projects, AppHost Web UI resource).

## R1. SMTP client

- **Decision**: MailKit `4.18.1` (MIT), pinned in
  `src/platform/Directory.Packages.props`, used only in Infrastructure behind
  `IEmailTransport`.
  - **Connection**: it connects with `SecureSocketOptions.StartTlsWhenAvailable`
    only in Development, and with `SslOnConnect` or `StartTls` otherwise
    (`Email:Smtp:Security`).
  - **Authentication**: it authenticates when credentials are configured.
  - **Errors**: transient SMTP errors (4xx, connection failures) are retried;
    permanent errors (5xx) are recorded as failed.
- **Rationale**: `System.Net.Mail.SmtpClient` is marked as not recommended for
  new development. MailKit is the de facto .NET SMTP client and supports the
  modern TLS options.
- **Alternatives considered**: provider HTTP APIs, which the architecture
  rejects in favor of provider-neutral SMTP.

## R2. Development relay and tests

- **Decision**: Mailpit `axllent/mailpit:v1.31.5` as a plain AppHost container
  resource (SMTP port 1025, web UI and REST API on 8025).
  - **Configuration**: the API's `Email:Smtp:*` points to it, with no
    authentication and TLS "when available".
  - **Tests**: integration tests start the same pinned image with
    Testcontainers. A `MailpitContainerFixture` reads messages through
    Mailpit's REST API (`/api/v1/messages`). Browser tests in the `platform`
    project read the confirmation link from Mailpit.
- **Rationale**: a single pinned image is used everywhere, no message leaves
  the machine (FR-017), and Docker is already available in the environment.
- **Alternatives considered**: `CommunityToolkit.Aspire.Hosting.MailPit`, an
  extra package for one container line; and smtp4dev, which has a weaker REST
  API.

## R3. Queue: a dedicated transactional email outbox

- **Decision**: a new table `email_messages` in the platform database, written
  in the same unit of work as the triggering change (FR-013).
  - **Worker**: `EmailDeliveryWorker` in the API host polls queued rows with
    `FOR UPDATE SKIP LOCKED`, renders, re-checks eligibility (FR-015), sends,
    and records the outcome.
  - **Backoff**: 1 min, 2 min, 4 min … capped at 1 hour, with at most
    `Email:Delivery:MaxAttempts` (development value 8); then `failed`.
  - **Duplicates**: a row is marked `sending` with a lease before the SMTP
    call. If the host crashes after the relay accepted the message but before
    the row is marked `sent`, a resend can happen; the platform minimizes it
    with the lease and accepts at most one such duplicate (FR-014). A row
    marked `sent` is never resent.
- **Rationale**: the existing outbox publishes to NATS, and email needs no
  broker. A table-backed outbox keeps the transactional guarantee the
  architecture names, with fewer moving parts.
- **Architecture**: `email-notifications.md` § Delivery says the "platform's
  transactional outbox". This plan records that email uses its own
  transactional outbox table polled by the delivery worker, not the NATS
  outbox. The change is applied through Architecture Updates.

## R4. Confirmation tokens without storing them

- **Decision**: setting an address creates an `email_confirmations` row in the
  `pending` state with no token, plus a queued confirmation message.
  - **Token creation**: when the delivery worker sends that message, it
    generates a 256-bit random token, stores its SHA-256 hash, the issue time,
    and the expiry (24 h) on the confirmation row in the same transaction that
    marks the message `sending`, and puts the raw token only into the rendered
    link. A retry after a failure generates a new token and overwrites the
    hash, so an earlier link stops working.
  - **Supersession**: a newer confirmation for the same account marks older
    ones `superseded`.
  - **Single use**: confirming marks the row `used` and the address
    `verified`.
- **Rationale**: the raw token never touches the database (FR-003), and no
  encrypted body storage is needed.

## R5. Link format and confirmation step

- **Decision**: links take the form
  `{Notifications:WebUiBaseUri}/email/confirm#token=<base64url>`.
  - **Fragment**: the token is in the fragment, so it never reaches server
    logs or a `Referer` header. The Web UI page reads it, removes it from the
    address with `history.replaceState`, and shows one "Confirm this address"
    button. Only the click calls `POST /api/v1/email-confirmations` with the
    token (FR-004, clarification).
  - **Notification links**: they point to `{WebUiBaseUri}/analyst-managers`
    and carry nothing else (FR-012).
  - **AppHost**: sets `Notifications__WebUiBaseUri` to the Web UI endpoint.
- **Confirmation endpoint**: it is anonymous, because the member may be signed
  out (US1 scenario 7), and rate-limited per client address through the
  limiter policy `email-confirmation` (`Email:Confirmation:AttemptsPerMinute`,
  development value 10). It returns `204`
  on success and one generic `400 email-confirmation-invalid` for every
  refusal (FR-004).

## R6. Coalescing and resend limits

- **Decision**: both are derived from `email_messages`.
  - **Coalescing (FR-011)**: when a request becomes pending, the platform
    skips a Club Admin who already has a `registration-pending` message
    created within the last `Notifications:CoalescingWindow` (15 min) that is
    not `discarded`. The check and the insert run in the same unit of work
    under the registration transaction, serialized per recipient with a
    transaction-scoped PostgreSQL advisory lock keyed by the account, so
    concurrent submissions cannot both pass the check.
  - **Resend limit (FR-005)**: confirmation messages per account in the last
    hour (3). A request beyond it gets `429 email-confirmation-rate-limited`
    with `Retry-After`.
- **Rationale**: no extra state, and it is correct under the unit of work.

## R7. Eligibility and preferences

- **Decision**:
  - The notification type registry is in code: `registration-pending`
    (audience: active Club Admins; account message: no) and
    `email-confirmation` (account message: yes).
  - Preferences are stored only when a member turned a type off
    (`notification_preferences`: `account_id`, `type`, `enabled`); a missing
    row means on (FR-008).
  - Recipients are resolved when the message is created (FR-010). The worker
    re-checks at send time that the account is active, that the address hash
    equals the current address, that the address is verified (except for
    confirmations), and, for types other than account messages, that the
    account still belongs to the type's audience (an active Club Admin for
    `registration-pending`) and that the preference is still on (FR-015).
    Otherwise
    the message is `discarded`.

## R8. Minimal records and privacy

- **Decision**: `email_messages` keeps the id, type, `account_id`,
  `address_hash` (SHA-256 of the normalized address), state, attempt count,
  next attempt, timestamps, and the last error category. It keeps no address,
  body, or token (FR-016).
  - **Audit**: `identity.email-address-set`, `-changed`, `-removed`,
    `-verified`, and `notification-preference-changed`, with masked addresses
    (`j***@e***.com`) (FR-021).
  - **Telemetry**: logs and metrics use message id, type, and state only. A
    test scans captured logs for full addresses and tokens.
  - **Visibility**: `GET /api/v1/members` and `GET /api/v1/members/{id}`
    include `email` only for Club Admins; all other responses never include it
    (FR-022).

## R9. Web UI

- **Decision**:
  - **Account view** (`/account`): an email section (address, state, set,
    change, remove, resend) and a notification-preferences section listing the
    types that apply to the member's roles.
  - **Confirmation page**: a new route `/email/confirm`, available signed in
    or out.
  - **Member detail** (`/members/:memberId`): an email row for Club Admins.
  - **Text and tests**: keys in `src/i18n/en/email.json`, and the foundation's
    browser-test fixtures for axe, CSP, and storage.
- **Message rendering**: the platform keeps its own English resource file of
  message texts (subject, plain text, and minimal HTML) in Infrastructure,
  keyed by type.

## R10. Test strategy

- **Platform integration tests** (PostgreSQL and Mailpit):
  - address commands, validation, and authorization;
  - token issue, confirmation, expiry, and supersession;
  - resend limit, recipient resolution, coalescing, and preferences;
  - worker re-checks, retries with a relay outage (stop and start Mailpit),
    permanent failure, and restart recovery;
  - audit masking, visibility, and a log scan.
- **Host tests**: the AppHost Mailpit resource and the `Email` and
  `Notifications` settings.
- **Web UI**:
  - **Unit tests**: the email adapter, the fragment handling, and the views.
  - **`simulated` project**: every Web UI scenario.
  - **`platform` project**: set an address, read the link from Mailpit,
    confirm, pair a Manager with the scripted Manager of the fleet spec if it
    is merged (otherwise through the registration API as a Registrar), and see
    the Club Admin's message.
