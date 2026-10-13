# Implementation Plan: Member Email Addresses and Pending-Request Notifications

**Branch**: `20261013-140140-email-notifications` | **Date**: 2026-10-13 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261013-140140-email-notifications/spec.md`

## Summary

Implement the first use of the adopted transactional email channel.

- **Platform (`src/platform`)**:
  - **Addresses**: optional, one per member, set by the member or a Club
    Admin, visible only to them, and verified through a single-use token. The
    delivery worker generates the token at send time and stores only its hash;
    the Web UI confirms it only on an explicit click.
  - **Preferences**: per notification type, with account messages always sent.
  - **Pending-request notification**: queued for eligible Club Admins in the
    same unit of work as the pairing-code submission, coalesced per Club Admin
    over 15 minutes.
  - **Delivery**: a dedicated transactional email outbox table
    (`email_messages`) and an `EmailDeliveryWorker` in the API host. The
    worker re-checks eligibility, renders English messages, and submits them
    through MailKit SMTP with TLS and authentication. It applies bounded
    retries and keeps minimal records, never bodies or tokens.
  - **Audit**: events with masked addresses.
- **AppHost**: a pinned Mailpit container as the development relay, with
  `Email:*` and `Notifications:*` development values.
- **Web UI (`src/clients/web`)**: an email and preferences section in the
  account view, the member email row for Club Admins, and the `/email/confirm`
  page (token from the URL fragment, removed at once).

Design rationale: [research.md](research.md). Tables and configuration:
[data-model.md](data-model.md). Contracts: [contracts/](contracts/).
Validation: [quickstart.md](quickstart.md).

## Technical Context

**Language/Version**: C# on .NET 10 for the platform; TypeScript 5.x on Node.js
24 for the Web UI.

**Primary Dependencies**:

- **Platform**: existing ASP.NET Core minimal APIs, Dapper, DbUp, the unit of
  work, `IAuditTrail`, and rate limiting. New: **MailKit 4.18.1** (MIT), pinned
  in `src/platform/Directory.Packages.props` and referenced only by
  Infrastructure.
- **Web UI**: the foundation's pinned dependencies; no new ones.
- **Container**: **Mailpit `axllent/mailpit:v1.31.5`** for the AppHost and the
  tests.

**Storage**: PostgreSQL through the persistence foundation. One forward-only
migration `NNNN_identityaccess_email_notifications.sql`, numbered at
implementation time. It adds `member_email_addresses` (versioned) and
`email_confirmations`, `notification_preferences`, and `email_messages`, which
have no version column and are classified in `PersistedTableClassifications.cs`.

**Testing**:

- **Integration tests** with PostgreSQL and Mailpit containers, including a
  new `MailpitContainerFixture`.
- **Host tests** for the AppHost resource and settings.
- **Web UI**: Vitest unit tests, and the Playwright `simulated` and `platform`
  projects (the latter reads mail from Mailpit's REST API).

**Target Platform**: Platform in Linux containers (Aspire locally); Web UI in
Chromium-based browsers.

**Project Type**: Web service plus Web UI.

**Performance Goals**:

- SC-001: an address is verified in under 2 minutes.
- Member actions never wait for SMTP (SC-005).

**Constraints**:

- Messages only for committed changes (FR-013).
- No address, body, or token in storage beyond the address table and token
  hashes (FR-016).
- Tokens never in logs, audit, or browser storage.
- Addresses visible only to the owner and Club Admins (FR-022).
- All values are explicit non-production configuration.

**Scale/Scope**: one club stamp; tens of members; a handful of messages per
day.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | Implements [email-notifications.md](../../docs/architecture/email-notifications.md) and the email notification of `analyst-manager.md` § Fleet Visibility In The Web UI; follows `security-and-data-governance.md` (`DAT-001`, `BND-010`, `THR-012`, audit events). The dedicated email outbox table and the confirmation step on the landing page refine the narrative; they are applied through Architecture Updates. | Pass |
| II. Source-area ownership | Platform code in `src/platform`, Web UI code in `src/clients/web`. | Pass |
| III. API-first control plane | All behavior is exposed as API operations; the Web UI only consumes them; the relay is reached by SMTP, a standard protocol, behind an infrastructure abstraction. | Pass |
| IV. Evidence over claims | Integration, host, unit, and browser tests, including a relay outage and an end-to-end mail check; no production readiness claimed. | Pass |
| V. Focused, minimal changes | One new package (MailKit), justified by FR-014 and the adopted SMTP channel; no other notification types, recovery, or invitations. | Pass |
| Tooling constraints | MailKit restores through the existing `dotnet restore`; Mailpit is a container image pulled by Docker, which the environment provides; the Web UI uses the merged Web UI environment. No environment feature is needed. | Pass |

Post-design re-check: passes; no Complexity Tracking entries.

## Architecture Updates

**Narratives**:

- [email-notifications.md](../../docs/architecture/email-notifications.md): the whole document (Purpose And Scope, Email Addresses, Notification Preferences, Message Content, Delivery, Security And Governance).
- [analyst-manager.md](../../docs/architecture/analyst-manager.md) § Fleet Visibility In The Web UI: the email notification of pending requests.
- [security-and-data-governance.md](../../docs/architecture/security-and-data-governance.md): § Data Classes (`DAT-001`), § Trust Boundaries (`BND-010`), § Threat Model (`THR-012`), § Audit Events.
- [platform-implementation.md](../../docs/architecture/platform-implementation.md) § API And Identity, § Acceptance Evidence, § Architecture Reassessment.
- [tenancy-and-technology.md](../../docs/architecture/tenancy-and-technology.md) § Control Plane.
- [overview.md](../../docs/architecture/overview.md) § Decision Status.
- [production-operations.md](../../docs/architecture/production-operations.md) § Versioned Production Profile (the Email row stays Open / Blocking).

**Design updates** (applied with this plan as decided, not implemented; the implementation turns them into present tense):

- **`email-notifications.md` § Email Addresses**:
  - the delivery worker generates the confirmation token at send time, so the raw token is never stored;
  - the token travels in the URL fragment;
  - the landing page verifies only on one explicit click;
  - only the member and Club Admins can read an address.
- **`email-notifications.md` § Delivery**:
  - a dedicated transactional email outbox table, not the NATS-published outbox;
  - the worker re-checks eligibility before sending;
  - MailKit as the SMTP client;
  - the pending-request coalescing rule (first message at once, then suppressed within the window), a per-hour confirmation limit, and Mailpit as the pinned mail catcher.
- **`platform-implementation.md` § API And Identity** and **`tenancy-and-technology.md` § Control Plane**: the same outbox, MailKit, and Mailpit wording.
- **To turn into present tense at merge**: every "(decided, not yet implemented)" added above, and "The channel is decided, not yet implemented".

**Status statements at merge**:

- **`email-notifications.md` introduction**: "The channel is decided as the implementation target; no part of it is implemented yet." It must say that addresses, verification, preferences, delivery, and the pending-request notification are implemented, and that password recovery, invitations, and production values remain open.
- **`platform-implementation.md` § API And Identity**: "The channel is decided, not yet implemented" must become implemented.
- **`tenancy-and-technology.md` § Control Plane**: "(decided, not yet implemented …)" for SMTP must become implemented.
- **`analyst-manager.md` § Fleet Visibility In The Web UI**: "Club Admins with a verified email address will also be notified of pending requests by email … in a later specification." It must say this is implemented.
- **`overview.md` § Decision Status**: the email row stays "Accepted; production delivery values Open / Blocking", and the narrative it links to describes what is implemented.
- **`platform-implementation.md` § Acceptance Evidence and § Architecture Reassessment**: they must list member email addresses, verification, and transactional email delivery with the mail catcher as implemented development evidence. The production relay, sender authentication, and bounce handling stay unsupported.

The documentation task must make these narratives and every status statement this feature affects true for the merged state. It re-reads `docs/architecture/` at implementation time; the updates listed here are examples, not the whole job.

## Project Structure

### Documentation (this feature)

```text
specs/20261013-140140-email-notifications/
├── plan.md
├── research.md          # Decisions R1–R10
├── data-model.md        # Tables, notification types, configuration
├── quickstart.md
├── contracts/
│   ├── email-api.md          # Own address, preferences, confirmation, members' addresses
│   ├── email-messages.md     # Message types, content, links, headers
│   └── web-ui-routes.md      # Account, confirmation page, member view
├── checklists/requirements.md
└── tasks.md
```

### Source Code (repository root)

```text
src/platform/
├── Directory.Packages.props                                   # + MailKit 4.18.1
├── SocAlytics.Platform.Domain/IdentityAccess/EmailAddress.cs  # validation, normalization, masking
├── SocAlytics.Platform.Application/
│   ├── IdentityAccess/Email/                                  # set/change/remove, confirm, resend, preferences, members' addresses, audit events
│   └── Notifications/                                         # notification types, recipient resolution, coalescing, IEmailOutbox, options
├── SocAlytics.Platform.Infrastructure/
│   ├── Email/                                                 # MailKitEmailTransport, renderer, Messages.en.json, EmailDeliveryWorker
│   ├── Persistence/IdentityAccess/EmailStores.cs
│   └── Persistence/Migrations/NNNN_identityaccess_email_notifications.sql
├── SocAlytics.Platform.Api/Endpoints/IdentityAccess/EmailEndpoints.cs   # + member DTO email for Club Admins
├── SocAlytics.Platform.Application/Registry/Decisions/SubmitPairingCode.cs   # + queue registration-pending
├── SocAlytics.Platform.Api/openapi/v1.json                    # regenerated
├── SocAlytics.Platform.AppHost/Program.cs                     # Mailpit container, Email__*, Notifications__*
└── Tests/
    ├── SocAlytics.Platform.Integration.Tests/Notifications/   # email tests, MailpitContainerFixture
    └── SocAlytics.Platform.Host.Tests/PlatformHostTests.cs

src/clients/web/
├── src/platform/adapters/emailAdapter.ts, src/platform/models/email.ts
├── src/features/account/ (email and preferences sections), src/features/email-confirm/, src/features/members/ (email row)
├── src/i18n/en/email.json
└── tests/unit/features/email/, tests/e2e/simulated/email.spec.ts, tests/e2e/platform/email.spec.ts, tests/e2e/support/mailpit.ts

docs/architecture/ (see Architecture Updates), src/platform/README.md, src/clients/README.md, README.md, AGENTS.md
```

**Structure Decision**: Email addresses and confirmations belong to the
Identity Access area; notification types, recipients, and the email outbox form
a new `Notifications` folder in Application; SMTP and rendering live in
Infrastructure `Email/`. The Web UI extends the foundation's account and member
features.

## Complexity Tracking

No constitution violations.
