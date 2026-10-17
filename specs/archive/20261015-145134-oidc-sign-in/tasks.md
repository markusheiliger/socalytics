---

description: "Task list for External OpenID Connect Sign-In"
---

# Tasks: External OpenID Connect Sign-In

**Input**: Design documents from `specs/archive/20261015-145134-oidc-sign-in/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/external-sign-in-api.md](contracts/external-sign-in-api.md), [quickstart.md](quickstart.md)

**Tests**: Required by the constitution. Every task adds the tests for the code it creates in the same task, so the platform solution and the Web UI build and their test suites stay green after each task.

**Organization**: Tasks are grouped by user story. Merged before this feature starts (do not re-create): Club and Identity Foundation (`SignInHandler`, `SessionStore`, `SessionCookie`, `SessionAuthenticationHandler`, `SessionAntiforgeryFilter`, `AccountCredentialService`, `CreateMemberHandler`, `IssueCredentialHandler`, `RedeemCredentialHandler`, `GetMemberHandler`, `OneTimeCredentialTokenProvider`, `IdentityAccessOptions`, `PostgresAuditTrail`, `PlatformApiFactory`, `ApiSession`, the break-glass recovery) and Web UI Foundation (`SignInPage`, `SetPasswordPage`, `MembersPage`, `MemberPage`, `CredentialDialog`, `sessionAdapter`, `memberAdapter`, `router.tsx`, the `simulated` and `platform` Playwright projects, the AppHost `e2e` mode). The migration takes the next free number on an up-to-date default branch (`0019` at planning time).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies on incomplete tasks); consecutive `[P]` tasks under one heading run in parallel, each from the same commit
- **[Story]**: Which user story this task belongs to (US1–US3)
- Every task names its exact file paths

---

## Phase 1: Foundational (Blocking Prerequisites)

**Purpose**: Storage for links and pending sign-ins, and the provider client.

- [x] T001 [P] Persistence and audit (data-model.md; FR-007, FR-011, FR-018): add `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Migrations/NNNN_identityaccess_external_identities.sql` (`socalytics.external_identity_link` with `socalytics.attach_version_trigger`, unique `(issuer, subject)` and `(account_id, provider_id)`; `socalytics.pending_external_sign_in` with unique `state_hash`; nullable `provider_id` on `socalytics.one_time_credential` with a check that it is set exactly for purpose `identity-link`); classify `pending_external_sign_in` in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Structure/PersistedTableClassifications.cs`; add `src/platform/SocAlytics.Platform.Domain/IdentityAccess/ExternalIdentityLink.cs` (provider id `[a-z0-9-]{1,32}`); add `IExternalIdentityStore` and `IPendingExternalSignInStore` in `src/platform/SocAlytics.Platform.Application/IdentityAccess/ExternalSignIn/` and their internal Dapper implementations `DapperExternalIdentityStore.cs` and `DapperPendingExternalSignInStore.cs` in `src/platform/SocAlytics.Platform.Infrastructure/IdentityAccess/ExternalIdentity/` (find by issuer and subject, insert with uniqueness violations as distinct results, delete with the member version, list by account; create pending, consume once with `UPDATE … WHERE consumed_at IS NULL AND expires_at > now()`, delete at most 100 expired or consumed rows); add the detail keys `provider` and `cause` to the allow-list in `src/platform/SocAlytics.Platform.Infrastructure/Persistence/PostgresAuditTrail.cs`. Tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/ExternalIdentityStoreTests.cs`: both uniqueness rules, single consumption under concurrency, expiry, batch deletion, the check constraint, and the audit detail keys.
- [x] T002 [P] Provider client and configuration (research.md § R1, § R7; FR-001, FR-003, FR-005): add `Microsoft.IdentityModel.Protocols.OpenIdConnect` (the 8.x version of the existing `Microsoft.IdentityModel.JsonWebTokens`) to `src/platform/Directory.Packages.props` and `src/platform/SocAlytics.Platform.Infrastructure/SocAlytics.Platform.Infrastructure.csproj`; add `ExternalProviders` (`DisplayName`, `Authority`, `ClientId`, `ClientSecret`, `Scopes` default `openid`, `TokenEndpointAuthMethod` default `client_secret_basic`) and `ExternalSignIn` (`PendingLifetime` default 5 minutes, `WebUiBaseUrl`) to `src/platform/SocAlytics.Platform.Application/IdentityAccess/IdentityAccessOptions.cs` with startup validation in `src/platform/SocAlytics.Platform.Api/Program.cs` (URL-safe identifiers, HTTPS authorities outside Development and Test, secrets present); add `IOidcProviderClient` in `src/platform/SocAlytics.Platform.Application/IdentityAccess/ExternalSignIn/IOidcProviderClient.cs` and `src/platform/SocAlytics.Platform.Infrastructure/IdentityAccess/ExternalIdentity/OidcProviderClient.cs` (discovery and signing keys through `ConfigurationManager<OpenIdConnectConfiguration>` per provider with refresh on unknown keys; authorization URL with `S256` PKCE, state, and nonce; token exchange; ID-token validation of issuer, audience, signature, lifetime, and nonce; returns issuer and subject only; never logs tokens, codes, or secrets); add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/Support/OidcProviderContainerFixture.cs` (`ghcr.io/navikt/mock-oauth2-server` pinned by digest, Testcontainers like the existing fixtures). Tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/OidcProviderClientTests.cs` (locally signed tokens: wrong issuer, audience, signature, expired, nonce mismatch, key rotation; and a round trip against the container) and in `src/platform/Tests/SocAlytics.Platform.Host.Tests/ExternalProviderOptionsTests.cs` (configuration validation).

**Checkpoint**: Links and pending sign-ins can be stored, and providers can be talked to.

---

## Phase 2: User Story 1 - A linked member signs in with the club's identity provider (Priority: P1) 🎯 MVP

**Goal**: A linked, active member signs in through a configured provider and gets the normal session.

**Independent Test**: Seed a link for a member, choose the provider, authenticate at the test provider, and confirm the session; an unlinked provider user is refused.

- [x] T003 [US1] Platform sign-in (contracts/external-sign-in-api.md; research.md § R2, § R4, § R5; FR-002–FR-007, FR-012, FR-018): add `StartExternalSignInHandler` in `src/platform/SocAlytics.Platform.Application/IdentityAccess/ExternalSignIn/StartExternalSignIn.cs` (provider check, pending row with state, nonce, verifier, and browser-binding hashes, batch deletion of expired rows, authorization URL) and `CompleteExternalSignInHandler` in `ExternalSignIn/CompleteExternalSignIn.cs` (binding cookie and state check, single consumption, token exchange and validation through `IOidcProviderClient`, lookup by issuer and subject, active membership check, session creation through the existing `SessionStore` path with the `replaced` ending of a presented session, audit `session.external-sign-in` or `session.external-sign-in-failed` with `cause`, claims such as email and name ignored); register the stores, the provider client, and the handlers in `src/platform/SocAlytics.Platform.Infrastructure/InfrastructureServiceCollectionExtensions.cs` and the Application composition; add `src/platform/SocAlytics.Platform.Api/Security/ExternalBindingCookie.cs` (`__Host-socalytics-external`, `Secure`, `HttpOnly`, `SameSite=Lax`) and `src/platform/SocAlytics.Platform.Api/Endpoints/IdentityAccess/ExternalSignInEndpoints.cs` (`GET /api/v1/identity-providers`, `POST /api/v1/session/external` without link credentials, `GET /api/v1/session/external/callback` redirecting to `WebUiBaseUrl` with `?external=failed` on any failure) mapped next to the session endpoints; update `src/platform/SocAlytics.Platform.Api/openapi/v1.json`; give the three operations the operation IDs `listIdentityProviders`, `startExternalSignIn`, and `completeExternalSignIn` and add all three to the `AnonymousOperations` set in `src/platform/Tests/SocAlytics.Platform.Architecture.Tests/EndpointSecurityTests.cs` (the architecture test otherwise fails with "must not allow anonymous access"; run that test project too). Tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/ExternalSignInEndpointTests.cs` against `OidcProviderContainerFixture` (links seeded through the store): provider listing (empty and configured), linked sign-in with roles, unlinked, deactivated, provider `error`, tampered state, missing or foreign binding cookie, expired, and replayed callbacks all failing generically; no provider token, code, or secret in any response body, header, cookie other than the session cookie, captured log, or audit record (SC-002, SC-003); with no provider configured, the existing session tests pass unchanged (SC-005).
- [x] T004 [US1] Web UI sign-in (research.md § R9; FR-002, FR-019): regenerate the Kiota client; add `listProviders` and `startExternalSignIn` to `src/clients/web/src/platform/adapters/sessionAdapter.ts`; show one button per provider below the local form in `src/clients/web/src/features/sign-in/SignInPage.tsx`, navigate the browser to the returned `authorizationUrl`, show the existing generic refusal for `?external=failed`, and show a distinct "provider currently unavailable" message for `provider-unavailable` while the local form stays usable; extend `src/clients/web/tests/e2e/support/simulatedApi.ts` with providers, the start call, and a simulated callback redirect. Tests in `src/clients/web/tests/unit/features/sign-in/sign-in.test.tsx` and `src/clients/web/tests/unit/session/sessionAdapter.test.ts` (including the unavailable-provider message), and a `simulated` Playwright test in `src/clients/web/tests/e2e/simulated/external-sign-in.spec.ts` with the axe check; no buttons when no provider is configured.

**Checkpoint**: User Story 1 works and is the MVP.

---

## Phase 3: User Story 2 - A Club Admin links and unlinks a member's external identity (Priority: P2)

**Goal**: Link credentials connect a provider identity to a member; Club Admins see and remove links; member creation can issue a link credential.

**Independent Test**: Issue a link credential, redeem it through the test provider, sign in through the provider, unlink, and be refused.

- [x] T005 [US2] Platform linking (contracts/external-sign-in-api.md § Member operations; research.md § R2, § R5, § R6; FR-008–FR-014, FR-018, FR-020): support purpose `identity-link` with `provider_id` in `src/platform/SocAlytics.Platform.Infrastructure/IdentityAccess/AccountCredentialService.cs` (the credential insert stores `provider_id`; the existing revocation by issuer and member applies unchanged), `src/platform/SocAlytics.Platform.Infrastructure/IdentityAccess/OneTimeCredentialTokenProvider.cs` (validation checks the provider), and `src/platform/SocAlytics.Platform.Application/IdentityAccess/IssueCredentialHandler.cs` (configured provider required, existing lifetime, revocation, and audit rules); add the optional `credential` choice (`set-password` default, or `identity-link` with `provider`) to `src/platform/SocAlytics.Platform.Application/IdentityAccess/CreateMemberCommand.cs` and `CreateMemberHandler.cs`; accept `accountName` and `linkCredential` in `StartExternalSignInHandler` (validate without consuming; store the credential id on the pending row) and, in `CompleteExternalSignInHandler`, consume the credential and insert the link in the same unit of work, refusing an identity or member-provider pair that is already linked (audit `identity.linked` or `identity.link-refused`); add `UnlinkExternalIdentityHandler` in `src/platform/SocAlytics.Platform.Application/IdentityAccess/ExternalSignIn/UnlinkExternalIdentity.cs` (Club Admin only, `If-Match`, ends the member's sessions, audit `identity.unlinked`); add `externalIdentities` (provider, display name, link time, provider still configured; no issuer or subject) to `src/platform/SocAlytics.Platform.Application/IdentityAccess/GetMemberHandler.cs`; map the changes in `src/platform/SocAlytics.Platform.Api/Endpoints/IdentityAccess/MemberEndpoints.cs` and `ExternalSignInEndpoints.cs`; update `src/platform/SocAlytics.Platform.Api/openapi/v1.json`. Tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/ExternalIdentityLinkingTests.cs` covering US2 scenarios 1–7 and the creation choice, with credential values absent from audit records and logs.
- [x] T006 [US2] Web UI linking (research.md § R9; FR-014, FR-019, FR-020): regenerate the Kiota client; add `src/clients/web/src/features/link-account/LinkAccountPage.tsx` (account name, link code, provider; public route `/link-account` in `src/clients/web/src/router.tsx`; the code is never placed in the address); add the credential choice to member creation in `src/clients/web/src/features/members/MembersPage.tsx` (only when providers exist) and `src/clients/web/src/features/members/ExternalIdentitySection.tsx` on `MemberPage.tsx` (linked providers with link time and "no longer configured", issue link credential through the existing `CredentialDialog`, unlink with confirmation); extend `src/clients/web/src/platform/adapters/memberAdapter.ts` and `sessionAdapter.ts`, and `src/clients/web/tests/e2e/support/simulatedApi.ts`. Tests in `src/clients/web/tests/unit/features/members/members.test.tsx`, `memberAdapter.test.ts`, `src/clients/web/tests/unit/features/link-account/link-account.test.tsx`, and the `simulated` Playwright test `src/clients/web/tests/e2e/simulated/external-linking.spec.ts` with the axe check.
- [x] T007 [US2] End to end against the platform (quickstart.md § Automated verification; SC-001, SC-004): add the test provider container (`ghcr.io/navikt/mock-oauth2-server` pinned by digest) and `IdentityAccess:ExternalProviders:test` with `IdentityAccess:ExternalSignIn:WebUiBaseUrl` to the `e2e` mode in `src/platform/SocAlytics.Platform.AppHost/Program.cs`; add `src/clients/web/tests/e2e/platform/external-sign-in.spec.ts`: a Club Admin creates a member with a link credential, the member redeems it on `/link-account` through the test provider, signs out, signs in through the provider, the Club Admin unlinks, and the next provider sign-in is refused.

**Checkpoint**: User Stories 1 and 2 work end to end.

---

## Phase 4: User Story 3 - Members choose how they sign in (Priority: P3)

**Goal**: Local and external sign-in coexist without weakening recovery.

**Independent Test**: An external-only member is refused locally; a member with both paths uses either; password change, reset, and break-glass leave links unchanged.

- [x] T008 [US3] Coexistence (FR-015, FR-016): ensure a member without a local password is refused by `POST /api/v1/session` with the generic failure and equal timing in `src/platform/SocAlytics.Platform.Infrastructure/IdentityAccess/AccountCredentialService.cs` (change only if the existing behavior differs). Tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/ExternalIdentityCoexistenceTests.cs`: US3 scenarios 1–4, including a break-glass directive applied to a linked Club Admin, and the timing rule of `SignInTimingTests` for external-only members.

**Checkpoint**: All user stories work.

---

## Phase 5: Polish & Cross-Cutting Concerns

- [x] T009 Documentation (plan.md § Architecture Updates): make every narrative listed in plan.md § Architecture Updates and every status statement this feature affects anywhere in `docs/architecture/` true for the merged state, re-reading the documents now rather than only applying the listed rewrites (they are examples, not the whole job): turn the decided external sign-in and linking design in `docs/architecture/platform-implementation.md` and `docs/architecture/client-applications.md` into present tense, correct the "OpenID Connect sign-in does not exist" and evidence statements, and record the evidence for `CTL-002` and `GOV-CRED-006` in `docs/architecture/security-and-data-governance.md` with production values still unresolved; update `README.md` and `src/platform/README.md` (external providers, configuration keys, no MFA for local accounts), `src/clients/web/README.md` (provider sign-in and the link-account page), and the "Current State" in `AGENTS.md` (replace "No OIDC, MFA enforcement" accordingly).
- [x] T010 Run the validation in [quickstart.md](quickstart.md) § Automated verification and `node .github/scripts/check-markdown.mjs`; fix what fails.

---

## Dependencies & Execution Order

- **Phase 1**: T001 and T002 in parallel (persistence and audit vs. provider client and options; registration follows in T003).
- **US1 (Phase 2)**: after Phase 1; T003, then T004 (needs the OpenAPI operations of T003).
- **US2 (Phase 3)**: after US1; T005, then T006 (needs the operations of T005), then T007 (needs the pages of T006).
- **US3 (Phase 4)**: after US2.
- **Polish**: after all stories.

### Parallel opportunities

- Phase 1: T001 (migration, stores, audit allow-list, store tests) with T002 (package, options, provider client, fixture, client tests).

## Implementation Strategy

1. Phase 1, then User Story 1 as the MVP: linked members sign in through a provider.
2. User Story 2 (linking, unlinking, creation choice, end-to-end), then User Story 3 (coexistence).
3. Documentation and validation.
