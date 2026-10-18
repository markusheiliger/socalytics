# Implementation Plan: Self-Service Password Recovery

**Branch**: `20261017-200823-password-recovery` | **Date**: 2026-10-17 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261017-200823-password-recovery/spec.md`

**Note**: This template is filled in by the `/speckit-plan` command; its definition describes the execution workflow.

## Summary

Members with a local password and a verified email address recover a forgotten password from the Web UI sign-in page.
A request by address is answered generically after a minimum duration; for each eligible member it writes a password
recovery record and a `password-recovery` account message into the existing transactional email outbox. The existing
delivery worker re-checks eligibility, issues a fresh single-use token (hash stored, token in the link fragment), and
sends the message through MailKit. The Web UI recovery page checks the token, shows the account name, and completes the
recovery with a new password that meets the policy; completion uses the token, ends every session, and is audited.
Open recoveries become invalid when a newer request supersedes them or when the member's security stamp, address, or
membership changes. Per-member and per-client limits and all lifetimes are configuration with development values.
See [research.md](research.md).

## Technical Context

**Language/Version**: C# on .NET 10 (`src/platform/global.json`); TypeScript with React for the Web UI (Node.js 24).

**Primary Dependencies**: ASP.NET Core minimal APIs, ASP.NET Core Identity (password hashing, policy, security stamps)
with the existing Dapper stores, ASP.NET Core rate limiting, MailKit (existing email transport), DbUp; Web UI: React,
React Router, TanStack Query, MUI, the Kiota client generated from the OpenAPI snapshot, i18next. No new dependency.

**Storage**: PostgreSQL through the existing persistence foundation: one new table `password_recoveries`, one new
nullable column `email_messages.password_recovery_id`, and one new index on `member_email_addresses
(address_normalized)` in one forward-only migration (see [data-model.md](data-model.md)).

**Testing**: xUnit integration tests with Docker-backed PostgreSQL and Mailpit (`SocAlytics.Platform.Integration.Tests`),
Host tests (`OpenApiSnapshotTests`), architecture tests; Vitest unit tests and Playwright `simulated` and `platform`
projects in `src/clients/web`.

**Target Platform**: The API host on Linux containers and developer machines through the Aspire AppHost; the Web UI in
current browsers as an installable PWA (online only).

**Project Type**: Web service (platform monolith) plus web client.

**Performance Goals**: Every request answered no earlier than the configured minimum duration (development 400 ms) and,
under development load, within 800 ms; link checks and completions within normal API latency.

**Constraints**: No account enumeration by status, body, or timing; tokens and full addresses never in logs, audit,
telemetry, or request URLs; production lifetimes and limits stay Open / Blocking.

**Scale/Scope**: One club per stamp; recovery requests are rare per member; three anonymous API operations, two Web UI
pages, one message type.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Gate | Result |
| --- | --- | --- |
| I. Architecture Is the Design Authority | The feature implements [Email Notifications](../../docs/architecture/email-notifications.md) (the account message "self-service password recovery", reserved for its own specification) and the local-account rules of [API And Identity](../../docs/architecture/platform-implementation.md#api-and-identity); design decisions not yet stated there are applied now as decided but not implemented (see Architecture Updates). | Pass |
| II. Respect Source-Area Ownership | Code lives in `src/platform/` (Domain, Application, Infrastructure, Api, Tests) and `src/clients/web/`; no new first-level area or project. | Pass |
| III. API-First Control Plane | The Web UI uses three new `/api/v1` operations through its generated client and adapters; the OpenAPI snapshot is refreshed; the UI holds no recovery logic beyond input checks. | Pass |
| IV. Evidence Over Claims | Integration tests cover answers, eligibility, limits, token lifecycle, completion, sessions, audit, and privacy; Web UI unit and Playwright tests cover the pages; development values are not presented as production values. | Pass |
| V. Focused, Minimal Changes | Reuses the confirmation pattern, outbox, worker, renderer, rate limiting, and credential service; no new dependency, project, or abstraction beyond the recovery store and handlers. | Pass |
| Technology and Tooling | Needs only .NET 10, Docker (PostgreSQL, NATS, Mailpit), Node.js 24, Kiota, and Playwright, which `.github/actions/environment-setup` and `environment-verify` already provide for the platform and Web UI; no environment spec is needed. | Pass |

Post-design re-check: unchanged, all gates pass. No Complexity Tracking entries.

## Architecture Updates

**Narratives** (implemented or changed by this feature):

- [Email Notifications → Purpose And Scope](../../docs/architecture/email-notifications.md#purpose-and-scope),
  [Email Addresses](../../docs/architecture/email-notifications.md#email-addresses),
  [Notification Preferences](../../docs/architecture/email-notifications.md#notification-preferences),
  [Message Content](../../docs/architecture/email-notifications.md#message-content),
  [Delivery](../../docs/architecture/email-notifications.md#delivery),
  [Security And Governance](../../docs/architecture/email-notifications.md#security-and-governance), and
  [Production Values](../../docs/architecture/email-notifications.md#production-values): the password-recovery account
  message, its token, its limits, its audit events, and its production values.
- [Platform Implementation Profile → API And Identity](../../docs/architecture/platform-implementation.md#api-and-identity):
  local-account recovery beside Club Admin-issued reset credentials and break-glass recovery.
- [Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui): the sign-in page's recovery
  entry and the public recovery pages.
- [Security and Data Governance → Threat Model](../../docs/architecture/security-and-data-governance.md#threat-model)
  (`THR-012`), [Audit Events](../../docs/architecture/security-and-data-governance.md#audit-events), and the
  credential-class entry `GOV-CRED-001` (human sessions, recovery).
- [Production Deployment and Operations → Versioned Production Profile](../../docs/architecture/production-operations.md#versioned-production-profile):
  the Email row.

**Design updates** (applied now to the owning narratives, worded as decided but not yet implemented; the implementation
turns each into present tense)

1. `email-notifications.md` gains a section "Password Recovery" (decided, not yet implemented): request by address with
   a generic answer after a minimum duration; eligibility (active member, local password, verified matching address;
   one message per eligible member of a shared address); worker-issued single-use token stored as a hash and carried in
   the fragment; supersession and invalidation through the security stamp, address, and membership; the recovery page
   that shows the account name and completes with a new password, ending every session and creating none; per-member
   message and per-client request and token limits; audit events with masked addresses. The Purpose And Scope list,
   the Notification Preferences bullet, and the Message Content sentence point to it.
2. `email-notifications.md` Production Values adds the recovery link lifetime, the minimum answer duration, and the
   recovery limits to the Open / Blocking list.
3. `platform-implementation.md` API And Identity states that members with a local password and a verified address will
   recover it themselves through a password-recovery account message (decided, not yet implemented), that recovery never
   creates a local password, changes external links, or reactivates an account, and that "no API operation,
   authenticated or not, performs recovery" refers to break-glass recovery.
4. `client-applications.md` Web UI states that the sign-in page will offer a "Forgot your password?" entry leading to
   the public recovery request and recovery pages (decided, not yet implemented).
5. `security-and-data-governance.md`: `THR-012` evidence adds password-recovery request, issue, completion, and refusal
   events with masked addresses, and its residual risk notes that a recovery link lets whoever controls a verified
   mailbox set a new local password; Audit Events lists password recovery with masked addresses; `GOV-CRED-001` notes
   self-service recovery as decided development scope with production values unresolved.
6. `production-operations.md` Versioned Production Profile Email row adds the recovery link lifetime, the minimum
   answer duration, and the recovery limits.

**Status statements at merge** (each must become true for the merged state)

- `email-notifications.md` intro: "Self-service password recovery and invitations are not implemented" → recovery is
  implemented as development evidence against the local mail catcher; invitations are not.
- `email-notifications.md` Purpose And Scope: "each through its own later specification, self-service password recovery
  and invitations" → password recovery is implemented; invitations remain for their own specification.
- `email-notifications.md` Notification Preferences: "(address verification and, once specified, password recovery)" →
  both are account messages that are always sent.
- `email-notifications.md` Message Content: "(verification, and later password recovery) define it in their
  specification" → both define their tokens (verification above, recovery in Password Recovery).
- `email-notifications.md` Password Recovery section (added now) loses "decided, not yet implemented".
- `platform-implementation.md` API And Identity: "Self-service password recovery and invitations may use this channel
  through their own specifications" → recovery uses it (implemented as development evidence); invitations may later;
  the recovery sentence added now loses "decided, not yet implemented".
- `client-applications.md` Web UI recovery sentence (added now) loses "decided, not yet implemented".
- `security-and-data-governance.md` `THR-012`, Audit Events, and `GOV-CRED-001` wording added now loses "decided, not
  yet implemented" and records development evidence.
- `AGENTS.md` "Current State": "No OIDC, MFA enforcement, self-service recovery, Web UI offline mode …" → self-service
  recovery leaves that list and joins the identity foundation evidence (outside `docs/architecture/`, kept current by
  the same documentation task).

**Documentation task**: `/speckit-tasks` MUST generate an outcome-based documentation task that makes the listed
narratives and every status statement this feature affects in `docs/architecture/` true for the merged state,
re-reading the documents at implementation time (including `README.md` summaries and any planned, future-tense, or "not
implemented" wording about recovery elsewhere); the rewrites listed above are examples, not the whole job.

## Project Structure

### Documentation (this feature)

```text
specs/20261017-200823-password-recovery/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── password-recovery-api.md
├── checklists/
│   └── requirements.md
└── tasks.md
```

### Source Code (repository root)

```text
src/platform/
├── SocAlytics.Platform.Application/
│   ├── IdentityAccess/PasswordRecovery/            # new: options, store interface, request, check, complete handlers
│   ├── IdentityAccess/IAccountCredentialService.cs # add the recovery password reset
│   ├── Notifications/NotificationTypes.cs          # add the password-recovery account message
│   └── ApplicationServiceCollectionExtensions.cs   # register handlers and options
├── SocAlytics.Platform.Infrastructure/
│   ├── Persistence/Migrations/NNNN_identityaccess_password_recovery.sql  # new, numbered at implementation time
│   ├── Persistence/IdentityAccess/PasswordRecoveryStore.cs               # new Dapper store
│   ├── Persistence/IdentityAccess/EmailStores.cs                         # message column, per-member count
│   ├── Persistence/PostgresAuditTrail.cs                                 # detail allow-list if needed
│   ├── IdentityAccess/AccountCredentialService.cs                        # recovery password reset
│   ├── Email/EmailDeliveryProcessor.cs                                   # issue recovery tokens, re-check eligibility
│   ├── Email/Messages.en.json                                            # recovery message text
│   └── InfrastructureServiceCollectionExtensions.cs                      # register the store
├── SocAlytics.Platform.Api/
│   ├── Endpoints/IdentityAccess/PasswordRecoveryEndpoints.cs             # new: three anonymous operations
│   ├── Endpoints/IdentityAccess/PasswordRecoveryRateLimiting.cs          # new: per-client policies
│   ├── Program.cs                                                        # map endpoints, add rate limiting
│   └── openapi/v1.json                                                   # refreshed OpenAPI snapshot
└── Tests/
    ├── SocAlytics.Platform.Integration.Tests/IdentityAccess/SelfServicePasswordRecovery*Tests.cs  # new
    └── SocAlytics.Platform.Integration.Tests/Structure/PersistedTableClassifications.cs          # new table

src/clients/web/
├── src/features/sign-in/SignInPage.tsx                    # "Forgot your password?" link
├── src/features/password-recovery/                        # new: request page and recovery page
├── src/platform/adapters/passwordRecoveryAdapter.ts       # new adapter over the Kiota client
├── src/router.tsx                                         # two routes
├── src/i18n/en/passwordRecovery.json, src/i18n/en/email.json  # texts and the new notification type
└── tests/
    ├── unit/features/password-recovery/                   # new Vitest tests
    ├── e2e/simulated/password-recovery.spec.ts            # new
    └── e2e/platform/password-recovery.spec.ts             # new (Mailpit)
```

**Structure Decision**: The feature extends the Identity Access functional area of the existing platform layers and the
existing Web UI; it follows the email confirmation implementation file by file and adds no project. The existing
`PasswordRecoveryEndpointTests.cs` covers Club Admin-issued credentials and stays unchanged; the new tests use the
`SelfServicePasswordRecovery` prefix. The OpenAPI snapshot is refreshed with `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1
dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests` during implementation,
never in the verification environment.
