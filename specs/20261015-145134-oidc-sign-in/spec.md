# Feature Specification: External OpenID Connect Sign-In

**Feature Branch**: `20261015-145134-oidc-sign-in`

**Created**: 2026-10-15

**Status**: Draft

**Input**: User description: "External OpenID Connect Sign-In: a club that already runs an identity provider lets members sign in with it, including that provider's MFA, so SocAlytics keeps no password for them and the club controls access in one place. Scope: the stamp configures zero or more OpenID Connect providers in protected deployment configuration (issuer, client id and secret, display name, scopes); the sign-in page shows a button per configured provider and none when nothing is configured; server-side authorization code flow with PKCE, state, and nonce through the existing BFF endpoints; ID tokens validated (issuer, audience, signature, expiry, nonce); no provider token reaches the browser or is stored after sign-in; success establishes the same server-validated session as a local sign-in; membership stays platform data: a Club Admin issues a single-use, time-limited link credential for a member, which the member redeems by signing in through the provider, binding that provider's issuer and subject to the member; later sign-ins with that identity map to the member; unlinked external identities are refused, with no automatic member creation; the member page shows the linked provider, a Club Admin can unlink it (ending the member's sessions), a member can have a local password, an external identity, or both; bootstrap and break-glass recovery stay local; SocAlytics offers no MFA for local accounts, MFA for external sign-ins is the identity provider's responsibility, and the platform does not evaluate MFA claims; external sign-in success and failure, linking, and unlinking are audited without tokens or secrets; failures are generic; external identity credentials follow GOV-CRED-006 with configurable values; OpenAPI operations for provider listing, sign-in start and completion, link credential issue and redeem, and unlink, used by the Web UI through the generated client; an OpenID Connect test provider container via Testcontainers in integration and platform browser tests. Out of scope: just-in-time provisioning, group or role mapping from claims, single or front-channel logout, SAML, self-service linking without a Club Admin, MFA for local accounts. Depends on Club and Identity Foundation and Web UI Foundation. Implements and updates docs/architecture/platform-implementation.md § API And Identity, docs/architecture/client-applications.md § Web UI, and docs/architecture/security-and-data-governance.md (ACT-006, BND-007, GOV-CRED-006), adding the linking model and correcting every MFA statement."

## Clarifications

### Session 2026-10-15

- Q: How should a provider identity become linked to a SocAlytics member? → A: A Club Admin issues a single-use link credential for the member, handed over outside the platform, and the member redeems it by signing in at the provider; there is no matching by email and no automatic provisioning.
- Q: When a Club Admin creates a member who will sign in only through the provider, what should creation hand out? → A: Creation lets the Club Admin choose: a set-password credential (local, as today) or a link credential for a configured provider (external); the other path's credential can be issued later.
- Q: When a club disables someone at its identity provider, how quickly must their SocAlytics sessions end? → A: Through the existing session limits, sign-out, unlinking, or deactivation only; the platform keeps no refresh token and does not re-check the provider during a session. Clubs cut access immediately by deactivating or unlinking the member in SocAlytics.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - A linked member signs in with the club's identity provider (Priority: P1)

A member's account is linked to their identity at the club's identity provider. On the sign-in page they choose the provider's button, authenticate at the provider (including any MFA the provider requires), and return to SocAlytics signed in, with the same session, roles, and team access as after a local sign-in. SocAlytics never sees their provider password and keeps no provider token.

**Why this priority**: Signing in through the provider is the value of the feature; linking and management only exist to make it possible and safe.

**Independent Test**: Can be fully tested by configuring a test identity provider, linking a member to a test user, choosing the provider on the sign-in page, authenticating at the provider, and confirming that the member is signed in with their roles, that no provider token reached the browser, and that an unlinked provider user is refused.

**Acceptance Scenarios**:

1. **Given** a stamp with configured providers, **When** a visitor opens the sign-in page, **Then** it shows the local sign-in form and one button per configured provider with its display name; **Given** no configured provider, **Then** it shows only the local form.
2. **Given** a linked member, **When** they choose the provider and authenticate there, **Then** they return to SocAlytics with a server-validated session whose identity, roles, and team access are those of the linked member, exactly as after a local sign-in.
3. **Given** a provider user who is not linked to any member, **When** they authenticate at the provider, **Then** no session is established, no member is created, the Web UI shows a generic failure, and the attempt is audited.
4. **Given** a linked member whose membership is deactivated, **When** they sign in through the provider, **Then** sign-in is refused with the same generic failure.
5. **Given** a sign-in response that fails validation (wrong issuer or audience, bad signature, expired token, mismatched state or nonce, replayed response, or an error returned by the provider), **When** it reaches the platform, **Then** no session is established, the Web UI shows a generic failure, and the failure is audited without token contents.
6. **Given** a completed external sign-in, **When** the browser's storage and responses are inspected, **Then** they contain only the platform's session cookie and anti-forgery token, never a provider token, code, or secret.

---

### User Story 2 - A Club Admin links and unlinks a member's external identity (Priority: P2)

A Club Admin opens a member's page and issues a link credential, which they hand to the member outside the platform, as for today's set-password credential. The member opens the link, chooses the provider, authenticates there, and their provider identity becomes linked to their account. The member page shows the linked provider. When a member leaves the provider's organization or must use another identity, the Club Admin unlinks it, which ends the member's sessions.

**Why this priority**: Membership and roles stay platform data, so a person decides which provider identity belongs to which member. This story makes Story 1 possible for real members.

**Independent Test**: Can be fully tested by issuing a link credential for a member, redeeming it with a test provider user, confirming that the member can now sign in through the provider, unlinking it, and confirming that the member's sessions ended and the provider user is refused again.

**Acceptance Scenarios**:

1. **Given** a Club Admin on a member's page, **When** they issue a link credential for a configured provider, **Then** a single-use, time-limited credential is shown once for handing over outside the platform, and the issue is audited without the credential value.
2. **Given** a valid link credential, **When** the member opens it, chooses the named provider, and authenticates there, **Then** the provider's issuer and subject are linked to the member, the credential is used up, the member is signed in, and the link is audited.
3. **Given** a link credential that is expired, already used, issued by someone who has since lost the Club Admin role, or issued for a member who has since been deactivated, **When** it is redeemed, **Then** nothing is linked and the Web UI shows a generic failure.
4. **Given** a provider identity that is already linked to another member, **When** a link credential is redeemed with it, **Then** nothing is linked, the existing link stays, and the attempt is audited.
5. **Given** a member with a linked identity, **When** a Club Admin opens the member page, **Then** it shows the provider's display name and when it was linked, never the provider's subject or any token.
6. **Given** a linked member, **When** a Club Admin unlinks the identity, **Then** the link is removed, all of the member's sessions end, later sign-ins with that provider identity are refused, and the unlink is audited.
7. **Given** a member who is not a Club Admin, **When** they try to issue a link credential or unlink an identity, **Then** the request is refused.

---

### User Story 3 - Members choose how they sign in (Priority: P3)

A member may sign in with a local password, with a linked external identity, or with either when they have both. A member linked to a provider may never have had a local password at all. Bootstrap of the first Club Admin and break-glass recovery stay local, so a club whose provider is unavailable still has a way in.

**Why this priority**: It defines how local and external accounts coexist without weakening the existing recovery paths; it builds on Stories 1 and 2.

**Independent Test**: Can be fully tested by linking a member without a local password and confirming they can only sign in through the provider, linking a member who has a password and confirming both paths work, and applying a break-glass directive to a linked Club Admin and confirming the local recovery still works.

**Acceptance Scenarios**:

1. **Given** a Club Admin creating a member with a configured provider, **When** they choose the external path, **Then** creation issues a link credential for that provider instead of a set-password credential, and the member, once linked, can sign in only through the provider while the local form refuses them with the generic failure.
2. **Given** a member with both a local password and a linked identity, **When** they sign in either way, **Then** both establish the same kind of session.
3. **Given** a linked member, **When** they change their local password or a Club Admin issues them a reset credential, **Then** the external link is unaffected.
4. **Given** a linked Club Admin whose provider is unavailable, **When** an operator applies a break-glass recovery directive for that account, **Then** the existing local recovery works as before and the external link is unaffected.

---

### Edge Cases

- A provider is removed from the stamp configuration while members are linked to it: its button disappears, its links stay recorded but cannot be used, and the member page shows the provider as no longer configured.
- A provider's signing keys rotate: the platform refreshes the provider's published keys and keeps validating; a token signed with an unknown key after a refresh is refused.
- A provider is unreachable during sign-in: the Web UI shows that the provider is currently unavailable, and the local form still works.
- A sign-in is started but never completed, or completed twice from a copied URL: the pending external sign-in expires after a short configurable period and can be completed at most once.
- The provider returns a different subject for the same person (for example after an account migration at the provider): the identity is treated as unlinked until a Club Admin links it again.
- The provider asserts an email address or name: neither is used to find, create, or link a member; only the issuer and subject of a redeemed link credential are.
- A member's provider account is disabled at the provider: new sign-ins fail at the provider; existing SocAlytics sessions continue until the configured idle or absolute session limit, sign-out, or a Club Admin's unlink or deactivation ends them, since the platform keeps no refresh token and does not re-check the provider during a session.
- A link credential is redeemed while the member is already signed in as someone else in the same browser: the existing session is ended first, and the link applies only to the credential's member.
- Provider client secrets, tokens, authorization codes, state, nonce, and link credentials never appear in logs, audit records, telemetry, or read responses.

## Requirements *(mandatory)*

### Functional Requirements

#### Providers

- **FR-001**: The stamp MUST be able to configure zero or more OpenID Connect providers in protected deployment configuration, each with an identifier, issuer, client identifier and secret, display name, and requested scopes; client secrets MUST be treated as protected configuration.
- **FR-002**: The platform MUST list the configured providers' identifiers and display names to anonymous visitors so the sign-in page can show one button per provider, and nothing when none is configured.

#### External sign-in

- **FR-003**: The platform MUST perform the OpenID Connect authorization code flow server-side with PKCE, state, and nonce, and MUST validate each ID token's issuer, audience, signature (from the provider's published keys), expiry, and nonce.
- **FR-004**: A successful external sign-in for a linked, active member MUST establish the same server-validated browser session, with the same revalidation of membership, roles, and security stamp, as a local sign-in.
- **FR-005**: The platform MUST NOT send any provider token, authorization code, or client secret to the browser, and MUST NOT keep provider tokens after the sign-in completes.
- **FR-006**: Sign-in with an unlinked provider identity, a deactivated member, or a response that fails validation MUST establish no session, create no member, and return one generic failure that does not reveal the cause.
- **FR-007**: A started external sign-in MUST expire after a short configurable period and MUST be completable at most once.

#### Linking

- **FR-008**: A Club Admin MUST be able to issue, for a member and a configured provider, a single-use, time-limited link credential, shown once; the platform MUST store only verification data for it.
- **FR-020**: When a Club Admin creates a member, they MUST be able to choose whether creation issues a set-password credential (as today) or a link credential for a configured provider; when no provider is configured, creation MUST behave exactly as today. The credential for the other path MUST be issuable later.
- **FR-009**: Redeeming a valid link credential through the named provider MUST bind that provider's issuer and subject to the member, use up the credential, and sign the member in.
- **FR-010**: A link credential MUST stop working when it expires, is used, its issuer loses the Club Admin role or is deactivated, or its member is deactivated, mirroring the existing set-password credential rules.
- **FR-011**: One provider identity (issuer and subject) MUST be linked to at most one member, and a member MUST have at most one linked identity per provider.
- **FR-012**: The platform MUST NOT find, create, or link members from provider claims such as email or name; only a redeemed link credential creates a link.
- **FR-013**: A Club Admin MUST be able to unlink a member's external identity, which removes the link and ends all of the member's sessions.
- **FR-014**: The member page MUST show Club Admins each linked provider's display name and link time, and whether the provider is still configured, never the subject or any token.

#### Coexistence and recovery

- **FR-015**: A member MUST be able to have a local password, linked external identities, or both; a member without a local password MUST be refused by the local sign-in form with the generic failure.
- **FR-016**: Local password changes, Club Admin reset credentials, bootstrap, and break-glass recovery MUST keep working as before and MUST NOT change external links.

#### MFA, audit, and interfaces

- **FR-017**: The platform MUST NOT offer MFA for local accounts and MUST NOT evaluate MFA or authentication-context claims from providers; MFA for external sign-ins is the identity provider's responsibility.
- **FR-018**: The platform MUST record security audit events for external sign-in success and failure, link credential issue and redemption, refused redemption, and unlink, without any token, code, state, nonce, client secret, or link credential value.
- **FR-019**: Every operation of this feature MUST be available through the versioned platform API and described in its OpenAPI document; the Web UI MUST use them through its generated client for the provider buttons, the sign-in return, link redemption, and the Club Admin's link and unlink actions on the member page, keyboard accessible like the existing Web UI.

### Key Entities

- **Identity Provider**: A configured OpenID Connect provider: identifier, issuer, client identifier and protected secret, display name, scopes; configuration only.
- **External Identity Link**: The binding of one provider's issuer and subject to one member, with link time and the issuing Club Admin; unique per identity and per member and provider.
- **Link Credential**: A single-use, time-limited credential for one member and one provider, stored only as verification data, with its issuer and expiry.
- **Pending External Sign-In**: The state, nonce, PKCE verifier, provider, optional link credential, and expiry of one started external sign-in; completed at most once.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A linked member signs in through the club's provider in under 30 seconds plus the time spent at the provider.
- **SC-002**: In automated tests against a test provider, 100% of external sign-ins of linked, active members establish a session with the member's roles, and 100% of sign-ins with unlinked identities, deactivated members, or invalid responses are refused with the generic failure.
- **SC-003**: No provider token, authorization code, client secret, state, nonce, or link credential appears in any browser storage, log, audit record, telemetry signal, or read response in the automated tests.
- **SC-004**: A Club Admin links a member in one hand-over: issuing the credential takes under a minute, and redeeming it signs the member in.
- **SC-005**: With no provider configured, sign-in, bootstrap, and recovery behave exactly as before this feature.

## Assumptions

- **Dependencies**: Club and Identity Foundation (local accounts, server-validated sessions, set-password and reset credentials, bootstrap, break-glass recovery, security audit, roles) and Web UI Foundation (sign-in, member pages, generated client), both implemented.
- **Provider requirements**: Providers support OpenID Connect discovery, the authorization code flow with PKCE, and signed ID tokens; confidential clients with a client secret are used. Front-channel and back-channel logout, SAML, and other protocols are out of scope.
- **Linking model**: Linking requires a Club Admin's link credential, handed over outside the platform like today's set-password credential; automatic provisioning, matching by email, and group or role mapping from claims are out of scope.
- **MFA**: SocAlytics offers no MFA for local accounts; clubs that need MFA use an external provider that enforces it. This replaces the architecture's earlier "MFA enforcement" target.
- **Configuration**: The pending sign-in period and the link credential lifetime are configurable with secure defaults; external identity credential lifetimes, rotation, and failure handling follow `GOV-CRED-006`, whose production values remain unresolved.
- **Verification**: Automated tests use a containerized test OpenID Connect provider started by the test infrastructure the repository already uses for containers; real providers are configured per deployment.
- **Architecture**: This feature implements the external sign-in described in `docs/architecture/platform-implementation.md` (API And Identity) and `docs/architecture/client-applications.md` (Web UI), adds the linking model and the MFA decision there and in `docs/architecture/security-and-data-governance.md` (`ACT-006`, `BND-007`, `GOV-CRED-006`), and corrects every statement about MFA enforcement and missing OpenID Connect sign-in.
