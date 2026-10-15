# Research: External OpenID Connect Sign-In

All Technical Context unknowns are resolved below. Each entry records the
decision, its rationale, and the alternatives considered.

## R1 — Protocol implementation

- **Decision**: The platform runs the authorization code flow itself in
  `SocAlytics.Platform.Infrastructure/IdentityAccess/ExternalIdentity/`, using
  `Microsoft.IdentityModel.Protocols.OpenIdConnect` (discovery document and
  signing-key retrieval through its `ConfigurationManager`, which refreshes keys
  on rotation) and the already referenced
  `Microsoft.IdentityModel.JsonWebTokens` (ID-token validation: issuer,
  audience, signature, lifetime, `nonce`). The token request uses
  `client_secret_post` or `client_secret_basic` per provider and PKCE (`S256`).
  Pending sign-ins are stored in PostgreSQL (R3), not in cookies protected by
  ASP.NET Core Data Protection.
- **Rationale**: The architecture keeps the API free of a shared key ring
  across instances ("the API needs no shared key ring across instances"). The
  ASP.NET Core OpenID Connect handler keeps state and nonce in Data-Protection
  cookies and is built around the cookie authentication scheme, while the
  platform uses its own server-validated session scheme
  (`SessionAuthenticationHandler`). Running the flow directly keeps one
  session model and makes "completed at most once" (FR-007) a database rule.
- **Alternatives considered**: `Microsoft.AspNetCore.Authentication.OpenIdConnect`
  with a temporary external cookie (needs a shared Data Protection key ring and
  a second authentication scheme); a third-party OpenID Connect client library
  (an extra dependency for what two Microsoft packages already cover).

## R2 — Endpoints and browser flow

- **Decision**:
  - `GET /api/v1/identity-providers` (anonymous): identifiers and display
    names of configured providers.
  - `POST /api/v1/session/external` (anonymous, JSON `{ provider }` or
    `{ provider, accountName, linkCredential }`): validates the provider (and
    a link credential without consuming it), creates a pending external
    sign-in, sets the browser-binding cookie (R4), and returns
    `{ authorizationUrl }`. The Web UI then navigates the browser to that URL.
    The link credential travels only in this POST body, never in an address.
  - `GET /api/v1/session/external/callback?code&state` (anonymous, the
    redirect URI registered at the provider): completes the pending sign-in,
    sets the session cookie on success, and redirects to the Web UI (`/` on
    success, `/sign-in?external=failed` or `/link-account?external=failed` on
    failure, without any cause).
  - `POST /api/v1/members/{memberId}/external-identities/link-credentials`,
    `DELETE /api/v1/members/{memberId}/external-identities/{provider}`, and the
    member read gaining `externalIdentities` (Club Admin only).
  - `POST /api/v1/members` gains an optional `credential` choice:
    `{ "kind": "set-password" }` (default) or
    `{ "kind": "identity-link", "provider": "<id>" }`.
- **Rationale**: Keeps the redirect a full-page navigation (required by the
  protocol) while every input from the Web UI goes through the generated
  client; the callback is the only browser-navigated endpoint.

## R3 — Persistence

- **Decision**: One forward-only migration (`NNNN_identityaccess_external_identities.sql`,
  the next free number at implementation, `0019` at planning time) adds:
  - `socalytics.external_identity_link` (versioned): `id`, `account_id`,
    `provider_id`, `issuer`, `subject`, `linked_at`, `linked_by`, `version`;
    unique `(issuer, subject)` and unique `(account_id, provider_id)`.
  - `socalytics.pending_external_sign_in` (insert, then consume; no `version`,
    classified in `PersistedTableClassifications`): `id`, `state_hash`,
    `browser_binding_hash`, `nonce`, `code_verifier`, `provider_id`,
    `link_credential_id` (nullable), `created_at`, `expires_at`,
    `consumed_at`. Expired and consumed rows are deleted by the completion
        path and, in bounded batches, whenever a new external sign-in starts.
  - `provider_id` (nullable) on `socalytics.one_time_credential` for the new
    purpose `identity-link`.
- **Rationale**: Unique constraints enforce FR-011; consuming a pending row
  with `UPDATE … WHERE consumed_at IS NULL AND expires_at > now()` enforces
  FR-007 across instances. Only hashes of `state`, the browser binding, and
  the link credential are stored; the `nonce` and `code_verifier` are needed in
  clear for the token exchange and live at most for the pending period.

## R4 — Login CSRF and session fixation

- **Decision**: Starting an external sign-in sets a short-lived
  `__Host-socalytics-external` cookie (`Secure`, `HttpOnly`, `SameSite=Lax` so
  it returns on the provider's top-level redirect) holding a random value whose
  hash is stored with the pending row. The callback requires the cookie to
  match, clears it, and ends any session presented by the browser before
  creating the new one (the existing `replaced` ending).
- **Rationale**: Binds the callback to the browser that started the flow, so
  an attacker cannot complete a sign-in in a victim's browser.

## R5 — Mapping, refusal, and timing

- **Decision**: The callback looks up `(issuer, subject)`; for a link
  redemption it consumes the link credential and inserts the link in the same
  unit of work, refusing if the identity or the member-provider pair is
  already linked (FR-011). Unlinked identities, deactivated members, invalid
  responses, provider errors, and expired or reused pending sign-ins all end
  in the same redirect to the generic failure; the audit records the cause
  category without token contents. The email and name claims are ignored
  (FR-012).
- **Rationale**: FR-006, FR-009, FR-012, FR-018.

## R6 — Link credentials

- **Decision**: Link credentials reuse `OneTimeCredentialTokenProvider` and
  `socalytics.one_time_credential` with purpose `identity-link` and the new
  `provider_id`; issuing reuses the credential endpoint pattern
  (`IssuedCredentialDto`) and the existing invalidation rules (consumed,
  revoked, expired, member deactivated, and the existing revocation of unused
  credentials when their issuer loses the Club Admin role or is deactivated).
  `IdentityAccess:OneTimeCredential` lifetimes apply.
- **Rationale**: FR-008–FR-010 mirror the set-password rules, so the same
  mechanism and audit events (`credential.issued` with `purpose`) apply.

## R7 — Provider configuration

- **Decision**: `IdentityAccess:ExternalProviders:<id>` with `DisplayName`,
  `Authority` (issuer), `ClientId`, `ClientSecret`, `Scopes` (default
  `openid`), `TokenEndpointAuthMethod`, and `IdentityAccess:ExternalSignIn` with
  `PendingLifetime` (default 5 minutes) and `WebUiBaseUrl` for the callback's
  redirects. Validated at start: identifiers are URL-safe, authorities are
  HTTPS outside Development and Test, secrets are present. Secrets come from
  protected configuration (user secrets or environment variables), never from
  committed files.
- **Rationale**: FR-001; follows `IdentityAccessOptions` binding and startup
  validation in `Api/Program.cs`.

## R8 — Test provider

- **Decision**: Integration tests and the Playwright `platform` project use the
  `ghcr.io/navikt/mock-oauth2-server` container, pinned by digest: an
  `OidcProviderContainerFixture` (Testcontainers, like the existing fixtures)
  in `SocAlytics.Platform.Integration.Tests`, and an AppHost container
  resource in `e2e` mode wired into `IdentityAccess:ExternalProviders:test`.
  Its token callback lets tests choose the subject and claims, and it can be
  told to return errors. Unit tests of the validator use locally signed tokens.
  The `simulated` Playwright project intercepts the start call and simulates
  the callback redirect.
- **Rationale**: Deterministic, offline-capable end-to-end evidence (SC-002,
  SC-003) with the container tooling CI already has; no new host tool.
- **Alternatives considered**: Keycloak (heavy startup and realm import); an
  in-process fake (would not exercise the real HTTP flow in browser tests).

## R9 — Web UI

- **Decision**: `SignInPage` lists provider buttons from
  `GET /identity-providers`; a button calls `POST /session/external` and
  assigns `window.location` to the returned URL. A new `/link-account` route
  (public, like `/set-password`) takes account name, link code, and provider.
  The callback's redirect lands on `/`, where `SessionProvider.restore()` picks
  up the new session; `?external=failed` shows the generic failure.
  `MembersPage` creation gains the credential choice when providers exist;
  `MemberPage` shows linked providers (display name, link time, "no longer
  configured") with issue-link and unlink actions for Club Admins. All
  through `sessionAdapter.ts` and `memberAdapter.ts` over the generated Kiota
  client.
- **Rationale**: FR-002, FR-014, FR-019, FR-020; reuses the existing
  display-once `CredentialDialog`.

## R10 — MFA statements and environment

- **Decision**: No MFA code; the architecture is corrected so that ASP.NET Core
  Identity's MFA support is not described as used, and MFA for external
  sign-ins is the provider's responsibility (FR-017). No environment spec is
  needed: the new production package is a NuGet dependency restored by the
  existing commands, and the test provider is a container image pulled by
  Docker, which CI already provides.
