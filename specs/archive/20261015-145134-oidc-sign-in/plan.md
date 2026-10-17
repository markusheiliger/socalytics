# Implementation Plan: External OpenID Connect Sign-In

**Branch**: `20261015-145134-oidc-sign-in` | **Date**: 2026-10-15 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/archive/20261015-145134-oidc-sign-in/spec.md`

**Note**: This template is filled in by the `/speckit-plan` command; its definition describes the execution workflow.

## Summary

Members sign in through OpenID Connect providers that the stamp configures.
The platform runs the authorization code flow with PKCE, state, nonce, and a
browser-binding cookie itself, stores pending sign-ins in PostgreSQL so the API
still needs no shared key ring, validates ID tokens with the Microsoft identity
model libraries, and, for a linked active member, creates the same
server-validated session as a local sign-in; no provider token reaches the
browser or is kept. Membership stays platform data: a Club Admin issues a
single-use link credential (a new purpose of the existing one-time
credentials), the member redeems it through the provider, and the provider's
issuer and subject are linked uniquely to the member; Club Admins can unlink.
Member creation can issue a link credential instead of a set-password
credential. The Web UI shows provider buttons, a link-account page, and the
member page's link section. SocAlytics offers no MFA for local accounts;
providers own MFA. A containerized test provider makes the flow testable end
to end.

## Technical Context

**Language/Version**: C# on .NET 10 (`src/platform/global.json`); TypeScript
with React 19 in `src/clients/web`.

**Primary Dependencies**: Existing ASP.NET Core minimal APIs, ASP.NET Core
Identity stores on Dapper and PostgreSQL, `Microsoft.IdentityModel.JsonWebTokens`;
new `Microsoft.IdentityModel.Protocols.OpenIdConnect` (same 8.x line as the
existing IdentityModel package) for discovery and signing keys. Web UI: React
Router, MUI, TanStack Query, Kiota-generated client.

**Storage**: PostgreSQL; one migration adding `external_identity_link`,
`pending_external_sign_in`, and `one_time_credential.provider_id`
([data-model.md](data-model.md)).

**Testing**: Platform Integration tests with a new
`OidcProviderContainerFixture` (Testcontainers, `mock-oauth2-server` pinned by
digest), validator unit tests with locally signed tokens, Host tests for the
OpenAPI snapshot and configuration validation, Web UI Vitest tests, Playwright
`simulated` and `platform` projects (the AppHost `e2e` mode adds the test
provider container).

**Target Platform**: The platform API behind the Web UI on one origin.

**Project Type**: Web service plus web application (existing).

**Performance Goals**: No new targets beyond SC-001; discovery documents and
signing keys are cached and refreshed by the configuration manager.

**Constraints**: No provider token in the browser or in storage after sign-in;
no shared key ring; generic failures; secrets only in protected configuration;
`GOV-CRED-006` values configurable.

**Scale/Scope**: A handful of providers per stamp; one link per member and
provider.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Evidence |
| --- | --- | --- |
| I. Architecture is the design authority | Pass | Implements the external sign-in of `platform-implementation.md` (API And Identity) and `client-applications.md` (Web UI); the linking model, the database-held pending sign-in, and the MFA decision reach the architecture through `## Architecture Updates` and the documentation task. |
| II. Source-area ownership | Pass | Platform code in `src/platform` (Domain, Application, Infrastructure, Api folders under IdentityAccess); Web UI code in `src/clients/web`. |
| III. API-first control plane | Pass | Every operation is in the versioned API and the OpenAPI snapshot; the Web UI uses the generated client; the browser-navigated callback is part of the API. |
| IV. Evidence over claims | Pass | Integration, Host, Web UI unit, and Playwright tests against a test provider (quickstart.md); no production-readiness claim. |
| V. Focused, minimal changes | Pass | One new production package with a clear need (research.md § R1); no MFA code. |
| Tooling | Pass | NuGet and container images only; Docker, Kiota, and Playwright Chromium are already provided by the environment actions; no environment feature is needed (research.md § R10). |

Re-check after Phase 1 design: still passes; no Complexity Tracking entries.

## Architecture Updates

### Narratives

- [Platform Implementation](../../../docs/architecture/platform-implementation.md):
  API And Identity (external sign-in, linking, MFA decision), the opening
  status paragraph, Acceptance Evidence, and Architecture Reassessment —
  implemented by this feature.
- [Client Applications](../../../docs/architecture/client-applications.md): Web UI
  (login options, linking, MFA from the provider).
- [Security and Data Governance](../../../docs/architecture/security-and-data-governance.md):
  `ACT-006` (optional OIDC provider), `BND-007` (explicit trust configuration,
  minimum scopes, failure closed), `CTL-002` (BFF sessions), and
  `GOV-CRED-006` (external identity credentials) — evidence provided, values
  still unresolved.
- [Analyst Manager](../../../docs/architecture/analyst-manager.md) (pairing: "normal
  local or external OpenID Connect authentication in the system browser") and
  [Tenancy and Technology](../../../docs/architecture/tenancy-and-technology.md)
  ("optional external OpenID Connect providers") — become implemented, no
  wording change needed beyond status.

### Design updates

Applied now:

1. **MFA decision** (`platform-implementation.md` API And Identity,
   `client-applications.md` Web UI, `security-and-data-governance.md`
   `ACT-006`): SocAlytics offers no MFA for local accounts; clubs that need MFA
   use an external provider that enforces it; the platform does not evaluate
   MFA claims. Removed "lockout, and MFA" from the ASP.NET Core Identity
   sentence and every "MFA enforcement" mention from the status and evidence
   lists, and replaced the evidence target "including MFA enforcement tests"
   with linking through Club Admin link credentials. This decision is already
   true and needs no change at merge.
2. **External sign-in design** (`platform-implementation.md` API And
   Identity, marked "decided, not yet implemented"): platform-run code flow
   with PKCE, state, nonce, database-held pending sign-ins bound to the
   browser and completable once (no shared key ring), ID-token validation, no
   provider token kept, linking only through Club Admin link credentials, no
   claim-based matching, unlink ends sessions, no refresh tokens.
3. **Web UI linking** (`client-applications.md` Web UI, marked "decided, not
   yet implemented").

The implementation turns updates 2 and 3 into present tense.

### Status statements at merge

- `platform-implementation.md`, opening paragraph: "Analyst execution,
  external OpenID Connect sign-in, the Web UI's offline mode … do not exist
  yet." → external OpenID Connect sign-in exists.
- `platform-implementation.md`, API And Identity: "It implements no external
  OpenID Connect sign-in." → it implements external sign-in with linking.
- `platform-implementation.md`, Acceptance Evidence: the evidenced tests gain
  external sign-in, linking, and unlinking against a test provider.
- `platform-implementation.md`, Architecture Reassessment: "External OpenID
  Connect sign-in, … remain unsupported by implementation evidence" and the
  target "optional OIDC login shares the local-account BFF session, with
  external identities linked only through Club Admin link credentials" →
  evidenced.
- `security-and-data-governance.md`, `CTL-002` and `GOV-CRED-006`: record the
  evidence; production values stay unresolved.
- Outside `docs/architecture/` (kept current by the documentation task):
  `README.md` and `src/platform/README.md` ("OIDC, MFA … do not exist"),
  `AGENTS.md` Current State ("No OIDC, MFA enforcement, …"), and
  `src/clients/web/README.md` if it lists sign-in options.

The documentation task in `tasks.md` must make every narrative above and every
status statement the feature affects anywhere in `docs/architecture/` true for
the merged state, re-reading the documents at implementation time; the
rewrites listed here are examples, not the whole job.

## Project Structure

### Documentation (this feature)

```text
specs/archive/20261015-145134-oidc-sign-in/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── external-sign-in-api.md
├── checklists/
│   └── requirements.md
└── tasks.md             # /speckit-tasks
```

### Source Code (repository root)

```text
src/platform/
├── SocAlytics.Platform.Domain/IdentityAccess/
│   └── ExternalIdentityLink.cs                          # link rules (uniqueness, provider id format)
├── SocAlytics.Platform.Application/IdentityAccess/
│   ├── IdentityAccessOptions.cs                         # + ExternalProviders, ExternalSignIn
│   ├── ExternalSignIn/StartExternalSignIn.cs            # handler: pending row, authorization URL
│   ├── ExternalSignIn/CompleteExternalSignIn.cs         # handler: validate, map or link, session
│   ├── ExternalSignIn/IExternalIdentityStore.cs, IPendingExternalSignInStore.cs, IOidcProviderClient.cs
│   ├── ExternalSignIn/UnlinkExternalIdentity.cs
│   └── CreateMemberHandler.cs, IssueCredential (purpose identity-link)
├── SocAlytics.Platform.Infrastructure/
│   ├── IdentityAccess/ExternalIdentity/OidcProviderClient.cs   # discovery, token exchange, ID-token validation
│   ├── IdentityAccess/ExternalIdentity/DapperExternalIdentityStore.cs, DapperPendingExternalSignInStore.cs
│   ├── IdentityAccess/OneTimeCredentialTokenProvider.cs        # provider-bound purpose
│   ├── Persistence/PostgresAuditTrail.cs                       # + provider, cause details
│   ├── Persistence/Migrations/NNNN_identityaccess_external_identities.sql
│   └── InfrastructureServiceCollectionExtensions.cs
├── SocAlytics.Platform.Api/
│   ├── Endpoints/IdentityAccess/ExternalSignInEndpoints.cs     # providers, start, callback
│   ├── Endpoints/IdentityAccess/MemberEndpoints.cs             # creation choice, link credential, unlink, read
│   ├── Security/ExternalBindingCookie.cs
│   ├── Program.cs                                              # options binding and validation
│   └── openapi/v1.json
├── SocAlytics.Platform.AppHost/Program.cs                      # e2e mode: test provider container
└── Tests/
    ├── SocAlytics.Platform.Integration.Tests/IdentityAccess/ExternalSignIn*Tests.cs, Support/OidcProviderContainerFixture.cs
    ├── SocAlytics.Platform.Integration.Tests/Structure/PersistedTableClassifications.cs
    └── SocAlytics.Platform.Host.Tests/                          # snapshot, options validation

src/clients/web/
├── src/features/sign-in/SignInPage.tsx                          # provider buttons, ?external=failed
├── src/features/link-account/LinkAccountPage.tsx                # new public route
├── src/features/members/MembersPage.tsx, MemberPage.tsx, ExternalIdentitySection.tsx
├── src/platform/adapters/sessionAdapter.ts, memberAdapter.ts
├── src/router.tsx
└── tests/unit/…, tests/e2e/simulated/…, tests/e2e/platform/external-sign-in.spec.ts, tests/e2e/support/simulatedApi.ts
```

**Structure Decision**: External sign-in is a folder in the IdentityAccess area
of each platform layer, following the existing sign-in and credential
handlers; the Web UI adds one route and extends the sign-in and member
features through its adapters.

## Complexity Tracking

No constitution violations.
