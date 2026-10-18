# Implementation Plan: Member Invitations by Email

**Branch**: `20261017-202624-member-invitations` | **Date**: 2026-10-17 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261017-202624-member-invitations/spec.md`

**Note**: This template is filled in by the `/speckit-plan` command; its definition describes the execution workflow.

## Summary

Club Admins invite a person by email address with an account name and optional team roles. An invitation is a
separate, versioned pending record that reserves its account name and address; creating it writes a
`member-invitation` account message into the existing transactional email outbox. The existing delivery worker
re-checks that the invitation is pending and the message is current, issues a fresh single-use token (hash stored on
the invitation's current link, token in the link fragment), and sends the message through MailKit. The public Web UI invitation page
checks the token, shows the club and account name, and accepts with a password that meets the policy; acceptance
creates the active member with the verified address and the invited team roles in one unit of work and creates no
session. Club Admins list invitations (pending, expired, accepted, revoked) and resend or revoke them conditionally on
the version. Link lifetime, resend limit, and per-client token limit are configuration with development values. The
worker-issued token, check-then-complete pattern, and per-client token limit reuse the mechanics of
[Self-Service Password Recovery](../20261017-200823-password-recovery/plan.md). See [research.md](research.md).

## Technical Context

**Language/Version**: C# on .NET 10 (`src/platform/global.json`); TypeScript with React for the Web UI (Node.js 24).

**Primary Dependencies**: ASP.NET Core minimal APIs, ASP.NET Core Identity (password hashing and policy) with the
existing Dapper stores, ASP.NET Core rate limiting, MailKit (existing email transport), DbUp; Web UI: React, React
Router, TanStack Query, MUI, the Kiota client generated from the OpenAPI snapshot, i18next. No new dependency.

**Storage**: PostgreSQL through the existing persistence foundation: new tables `member_invitations` (versioned),
`member_invitation_team_roles` (aggregate child), and `member_invitation_links` (unversioned), and on `email_messages`
a nullable `account_id`, a new nullable `invitation_link_id`, and a type check, in one forward-only migration (see [data-model.md](data-model.md)).

**Testing**: xUnit integration tests with Docker-backed PostgreSQL and Mailpit (`SocAlytics.Platform.Integration.Tests`),
Host tests (`OpenApiSnapshotTests`), architecture tests; Vitest unit tests and Playwright `simulated` and `platform`
projects in `src/clients/web`.

**Target Platform**: The API host on Linux containers and developer machines through the Aspire AppHost; the Web UI in
current browsers as an installable PWA (online only).

**Project Type**: Web service (platform monolith) plus web client.

**Performance Goals**: Invitation operations and link checks within normal API latency; invitation messages follow the
existing delivery polling.

**Constraints**: Tokens and full addresses never in logs, audit, telemetry, or request URLs; no member account before
acceptance; production lifetime and limits stay Open / Blocking.

**Scale/Scope**: One club per stamp; tens to hundreds of invitations per season; four authenticated and two anonymous
API operations, one members-area section with a dialog, one public page, one message type.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Gate | Result |
| --- | --- | --- |
| I. Architecture Is the Design Authority | The feature implements the invitation account message of [Email Notifications](../../docs/architecture/email-notifications.md), the club-user management of [Terminology](../../docs/architecture/terminology-and-principles.md#terminology) and [Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui), and the member rules of [API And Identity](../../docs/architecture/platform-implementation.md#api-and-identity); design decisions not yet stated there are applied now as decided but not implemented (see Architecture Updates). | Pass |
| II. Respect Source-Area Ownership | Code lives in `src/platform/` (Domain, Application, Infrastructure, Api, Tests) and `src/clients/web/`; no new first-level area or project. | Pass |
| III. API-First Control Plane | The Web UI uses six new `/api/v1` operations through its generated client and adapters; the OpenAPI snapshot is refreshed; the UI holds no invitation rules beyond input checks. | Pass |
| IV. Evidence Over Claims | Integration tests cover creation, conflicts, delivery, token lifecycle, acceptance, resend, revoke, expiry, limits, authorization, and audit privacy; Web UI unit and Playwright tests cover the members area and the invitation page; development values are not presented as production values. | Pass |
| V. Focused, Minimal Changes | Reuses the outbox, worker, renderer, token helper, rate limiting, account creation, and role assignment; one new aggregate and its handlers; no new dependency or project. | Pass |
| Technology and Tooling | Needs only .NET 10, Docker (PostgreSQL, NATS, Mailpit), Node.js 24, Kiota, and Playwright, which `.github/actions/environment-setup` and `environment-verify` already provide for the platform and Web UI; no environment spec is needed. | Pass |

Post-design re-check: unchanged, all gates pass. No Complexity Tracking entries.

## Architecture Updates

**Narratives** (implemented or changed by this feature):

- [Email Notifications → Purpose And Scope](../../docs/architecture/email-notifications.md#purpose-and-scope),
  [Notification Preferences](../../docs/architecture/email-notifications.md#notification-preferences),
  [Message Content](../../docs/architecture/email-notifications.md#message-content),
  [Delivery](../../docs/architecture/email-notifications.md#delivery),
  [Member Invitations](../../docs/architecture/email-notifications.md#member-invitations) (added now),
  [Security And Governance](../../docs/architecture/email-notifications.md#security-and-governance), and
  [Production Values](../../docs/architecture/email-notifications.md#production-values): the invitation account
  message, its pending record, token, acceptance, administration, limits, audit events, and production values.
- [Platform Implementation Profile → API And Identity](../../docs/architecture/platform-implementation.md#api-and-identity):
  invitations beside direct member creation with handed-over credentials.
- [Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui): club-user management with
  invitations in the members area and the public invitation page.
- [Architectural Principles → Terminology](../../docs/architecture/terminology-and-principles.md#terminology): Club
  administrators add club users directly or by invitation.
- [Security and Data Governance](../../docs/architecture/security-and-data-governance.md): `DAT-001` (pending
  invitations with invited addresses), `THR-012` in the
  [Threat Model](../../docs/architecture/security-and-data-governance.md#threat-model),
  [Audit Events](../../docs/architecture/security-and-data-governance.md#audit-events), and `GOV-CRED-001`.
- [Production Deployment and Operations → Versioned Production Profile](../../docs/architecture/production-operations.md#versioned-production-profile):
  the Email row.

**Design updates** (applied now to the owning narratives, worded as decided but not yet implemented; the
implementation turns each into present tense)

1. `email-notifications.md` gains the section "Member Invitations" (decided, not yet implemented): Club Admin-only
   invitations with address, chosen account name, and team roles; a pending record instead of a member, reserving
   name and address; conflicts refused with the conflict only; outbox write and worker-issued, hashed, fragment-carried
   token; public page that checks without using and accepts with a local password, creating the active member with
   verified address and team roles and no session; list with pending, expired, accepted, revoked and delivery state;
   conditional resend and revoke; resend and per-client limits; audit events with masked addresses. Purpose And Scope,
   Notification Preferences (invitations always sent, never among member preferences), Message Content, Delivery
   (delivery record references the invitation), and Security And Governance point to it.
2. `email-notifications.md` Production Values adds the invitation link lifetime, the resend limit, and the per-client
   link-check and acceptance limit to the Open / Blocking list.
3. `platform-implementation.md` API And Identity states that Club Admins will also invite by email (decided, not yet
   implemented) and that the member, its verified address, and team roles are created only at acceptance with a local
   password.
4. `client-applications.md` Web UI: the club-user management bullet names invitations in the members area and the
   public invitation page (decided, not yet implemented).
5. `terminology-and-principles.md` Terminology (Club): Club administrators add club users directly or, once
   implemented, by email invitation.
6. `security-and-data-governance.md`: `DAT-001` includes pending invitations with invited addresses; `THR-012`
   evidence adds invitation events with masked addresses and its residual risk notes that an invitation link lets
   whoever controls the invited mailbox create the invited account with its team roles; Audit Events lists member
   invitations; `GOV-CRED-001` notes invitation links as decided development scope with production values unresolved.
7. `production-operations.md` Versioned Production Profile Email row adds the invitation link lifetime, resend limit,
   and link-check limit.

**Status statements at merge** (each must become true for the merged state)

- `email-notifications.md` intro: "Self-service password recovery and invitations are not implemented" (or, if
  [Self-Service Password Recovery](../20261017-200823-password-recovery/plan.md) merged first, its rewrite) →
  invitations are implemented as development evidence against the local mail catcher.
- `email-notifications.md` Purpose And Scope: "each through its own later specification, self-service password
  recovery and invitations" and "invitations are decided as described in Member Invitations and not yet implemented"
  → invitations are implemented.
- `email-notifications.md` Notification Preferences, Message Content, Delivery ("for invitations, once implemented,
  the invitation"), Security And Governance, and Production Values: the invitation wording loses "decided, not yet
  implemented" and "once implemented".
- `email-notifications.md` Member Invitations section (added now) loses "decided as follows and not yet implemented".
- `platform-implementation.md` API And Identity: "Self-service password recovery and invitations may use this channel
  through their own specifications" → invitations use it (implemented as development evidence); the invitation
  sentence added now loses "will" and "decided, not yet implemented".
- `client-applications.md` Web UI club-user management bullet loses "decided, not yet implemented".
- `terminology-and-principles.md` Club entry loses "once implemented".
- `security-and-data-governance.md` `DAT-001`, `THR-012`, Audit Events, and `GOV-CRED-001` invitation wording loses
  "decided, not yet implemented" and records development evidence.
- `production-operations.md` Email row invitation wording loses "decided, not yet implemented".
- `AGENTS.md` "Current State" (outside `docs/architecture/`, kept current by the same documentation task): the
  identity foundation evidence names member invitations by email.

**Documentation task**: `/speckit-tasks` MUST generate an outcome-based documentation task that makes the listed
narratives and every status statement this feature affects in `docs/architecture/` true for the merged state,
re-reading the documents at implementation time (including `README.md` summaries and any planned, future-tense, or
"not implemented" wording about invitations elsewhere); the rewrites listed above are examples, not the whole job.

## Project Structure

### Documentation (this feature)

```text
specs/20261017-202624-member-invitations/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── member-invitations-api.md
├── checklists/
│   └── requirements.md
└── tasks.md
```

### Source Code (repository root)

```text
src/platform/
├── SocAlytics.Platform.Domain/IdentityAccess/MemberInvitation.cs            # new aggregate, states, transitions
├── SocAlytics.Platform.Application/
│   ├── IdentityAccess/Invitations/                    # new: options, store interface, create, list, resend, revoke, check, accept handlers
│   ├── IdentityAccess/CreateMemberHandler.cs          # refuse names reserved by pending invitations
│   ├── IdentityAccess/Email/NotificationPreferenceHandlers.cs  # omit Invitee-audience types
│   ├── Notifications/NotificationTypes.cs             # Invitee audience, member-invitation type
│   └── ApplicationServiceCollectionExtensions.cs      # register handlers and options
├── SocAlytics.Platform.Infrastructure/
│   ├── Persistence/Migrations/NNNN_identityaccess_member_invitations.sql  # new, numbered at implementation time
│   ├── Persistence/IdentityAccess/MemberInvitationStore.cs                # new Dapper store
│   ├── Persistence/IdentityAccess/EmailStores.cs                          # invitation messages, nullable account
│   ├── Persistence/PostgresAuditTrail.cs                                  # detail allow-list
│   ├── Email/EmailDeliveryProcessor.cs                                    # invitation re-check and token issue
│   ├── Email/Messages.en.json                                             # invitation message text
│   └── InfrastructureServiceCollectionExtensions.cs                       # register the store
├── SocAlytics.Platform.Api/
│   ├── Endpoints/IdentityAccess/InvitationEndpoints.cs                    # new: six operations
│   ├── Endpoints/IdentityAccess/InvitationRateLimiting.cs                 # new: invitation-token policy
│   ├── Program.cs                                                         # map endpoints, add rate limiting
│   ├── appsettings.Development.json                                       # Invitations section
│   └── openapi/v1.json                                                    # refreshed OpenAPI snapshot
└── Tests/
    ├── SocAlytics.Platform.Integration.Tests/IdentityAccess/MemberInvitation*Tests.cs  # new
    └── SocAlytics.Platform.Integration.Tests/Structure/PersistedTableClassifications.cs  # team-role child and links table

src/clients/web/
├── src/features/members/MembersPage.tsx                   # "Invite member" and invitations section
├── src/features/members/InviteMemberDialog.tsx            # new
├── src/features/members/InvitationsSection.tsx            # new
├── src/features/invitations/InvitationAcceptPage.tsx      # new public page
├── src/platform/adapters/invitationAdapter.ts             # new adapter over the Kiota client
├── src/router.tsx                                         # /invitations/accept route
├── src/i18n/en/invitations.json                           # texts
└── tests/
    ├── unit/features/invitations/ and unit/features/members/  # new Vitest tests
    ├── e2e/simulated/invitations.spec.ts                  # new
    └── e2e/platform/invitations.spec.ts                   # new (Mailpit)
```

**Structure Decision**: The feature extends the Identity Access functional area of the existing platform layers and
the existing Web UI members area; it follows the email confirmation and password recovery implementations and adds
no project. `PersistedTableClassifications` classifies the team-role table as a child of `member_invitations` and the
links table as unversioned. The OpenAPI snapshot is refreshed with
`SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests`
during implementation, never in the verification environment. Where
[Self-Service Password Recovery](../20261017-200823-password-recovery/plan.md) merges first, its shared pieces
(fragment-token page pattern, anonymous JSON-only token endpoints) are reused; where this feature merges first, it
builds the same pieces on the email confirmation implementation.
