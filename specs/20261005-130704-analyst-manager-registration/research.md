# Research: Analyst Manager Registration

**Feature**: [spec.md](spec.md) | **Plan**: [plan.md](plan.md) | **Date**: 2026-10-07

Each entry records the decision, why it was chosen, and the alternatives that
were evaluated. Values called "non-production defaults" are explicit validation
values only; production-approved values remain unresolved under
[Production Deployment and Operations](../../docs/architecture/production-operations.md)
and `GOV-CRED-002`/`GOV-CRED-003` in
[Security and Data Governance](../../docs/architecture/security-and-data-governance.md).

## R1. Machine token issuance: in-API token endpoint vs. an OAuth server library

- **Decision**: Implement one minimal OAuth 2.0 token endpoint inside
  `SocAlytics.Platform.Api` (Registry functional area) that supports exactly one
  grant: `client_credentials` with `private_key_jwt` client authentication
  (RFC 7523 client assertion, ES256 only) and a mandatory DPoP proof (RFC 9449).
  JWT/JWS parsing and signature validation use `Microsoft.IdentityModel.JsonWebTokens`
  8.23.0 (`JsonWebTokenHandler`, `JsonWebKey.ComputeJwkThumbprint`); all OAuth
  semantics (audience, lifetime, replay, registration state) are explicit
  application code with focused tests.
- **Rationale**: The architecture requires `private_key_jwt` plus DPoP-bound
  tokens ([Analyst Manager](../../docs/architecture/analyst-manager.md#machine-authentication)).
  OpenIddict supports `private_key_jwt` (5.0+) but has no DPoP support in its
  client, server, or validation stacks; its maintainer confirmed this in
  `openiddict/openiddict-core#2483` (closed 2026-06-17). Adopting it would add a
  large framework, its own stores (which would need Dapper/PostgreSQL adapters
  because the platform has no EF Core), and still require custom DPoP handlers.
  Duende IdentityServer supports DPoP but is commercially licensed and is a full
  authorization server. The required surface is one grant for one client type
  whose client registry is the Registry table that already exists for this
  feature, which fits the constitution's focused, minimal-change principle.
- **Alternatives considered**: OpenIddict 7.x with custom DPoP event handlers
  (rejected: framework size, store adapters, DPoP still custom); Duende
  IdentityServer (rejected: licensing, scope); a separate token service
  deployable (rejected: architecture keeps one control-plane deployment);
  hand-rolled JOSE parsing (rejected: security-critical parsing should use the
  vetted Microsoft library; only one algorithm is accepted either way).

## R2. Access-token format and per-request validation

- **Decision**: Access tokens are opaque 256-bit random references
  (`token_type` `DPoP`), stored only as SHA-256 hashes in
  `analyst_manager_access_token` with the registration, DPoP key thumbprint
  (`jkt`), scope `analyst-manager`, issue time, and expiry. A custom ASP.NET Core
  authentication handler (scheme `AnalystManagerDPoP`) validates on every request:
  the `Authorization: DPoP` token hash, the DPoP proof (`typ` `dpop+jwt`, ES256,
  `htm`, `htu`, `iat` window, `jti` replay, `ath`), and the joined authoritative
  registration (state `Active` or `Unregistering`, configured stamp, thumbprint
  equals `jkt`). Non-production default lifetime: 5 minutes.
- **Rationale**: FR-014 and the architecture require each call to check the
  authoritative registration state anyway, so a self-contained JWT saves no
  database work. Opaque references avoid introducing a platform signing key
  (`GOV-CRED-007`, unresolved) and make revocation immediate by construction
  (state join plus deleting outstanding token rows in the revocation unit of
  work), satisfying FR-020 and SC-003.
- **Alternatives considered**: Signed JWT access tokens with `cnf.jkt`
  (rejected: needs signing-key custody and rotation now, gives no latency benefit
  because state is checked per call); ASP.NET Core Data Protection-protected
  tokens (rejected: key-ring management across instances is another unresolved
  key class); reusing ASP.NET Core Identity bearer tokens (rejected: human
  identity scheme, not DPoP-bound, would blur FR-013 separation).

## R3. DPoP key and device key

- **Decision**: The DPoP proof key must be the registered device key: the token
  endpoint and resource validation require the DPoP header `jwk` thumbprint to
  equal the registration's stored thumbprint. Client assertion `iss`/`sub` is the
  Manager identity (`client_id`), `aud` is the configured token endpoint URI,
  `exp - iat` ≤ 60 s (non-production default), and `jti` is unique.
- **Rationale**: Binding DPoP to the non-exportable device key makes every token
  request and API call a proof of device-key possession (FR-013, FR-014), so a
  stolen token is useless without the key, and copied protected state cannot be
  used elsewhere (edge case "copied to another machine").
- **Alternatives considered**: An ephemeral per-session DPoP key (rejected:
  weaker binding and requires an extra protected secret in Manager memory with
  no benefit in this slice).

## R4. Replay protection for proofs and assertions

- **Decision**: One table `analyst_manager_proof_replay (kind, jti_hash,
  expires_at)` with primary key `(kind, jti_hash)`; `INSERT … ON CONFLICT DO
  NOTHING` and zero affected rows means replay. Kinds: `client_assertion`,
  `dpop`, `activation`. Entries live until the proof's acceptance window closes
  and are purged by the expiry worker. DPoP `iat` must be within ±60 s of the
  platform clock (non-production default); server-issued DPoP nonces are not
  used in this slice.
- **Rationale**: The platform may run several API instances, so an in-memory
  cache is insufficient; PostgreSQL is already the stamp's shared store. The
  single table keeps the change small.
- **Alternatives considered**: In-memory replay cache (rejected: per-instance);
  DPoP server nonces (deferred: optional in RFC 9449, adds a round trip and state
  without being required by the spec); Redis (rejected: not in the adopted stack).

## R5. Pairing code, polling handle, and activation challenge

- **Decision**: Device-authorization-style pairing modeled on RFC 8628 but on the
  platform's own REST resources. The pairing code is 12 characters from the
  RFC 8628 §6.1 consonant alphabet `BCDFGHJKLMNPQRSTVWXZ` (≈51.9 bits), shown as
  `XXXX-XXXX-XXXX`, normalized case-insensitively without separators. The polling
  handle and every activation challenge are 256-bit random base64url values. All
  three are stored only as SHA-256 hashes, generated with
  `RandomNumberGenerator`. Pairing window 10 minutes, approval window 24 hours
  (architecture values), activation window 10 minutes from approval, challenge
  lifetime 60 s, polling interval 5 s (non-production defaults). A challenge is
  issued on each status poll in state `Approved`, replaces the previous one, and
  is consumed by any activation attempt (success or failure).
- **Rationale**: Matches the architecture's browser device-code pairing,
  FR-004, FR-007, FR-010, and the "never persisted by the Manager" rule. Hash-only
  storage keeps secrets out of the database, logs, and backups (DAT-002).
- **Alternatives considered**: 8-character RFC 8628 example codes (rejected: the
  spec asks for high-entropy codes; 12 characters remain quick to type for
  SC-001); storing codes encrypted (rejected: verification needs only equality).

## R6. Indistinguishable refusals and rate limiting

- **Decision**: Every invalid, expired, consumed, unknown, or concurrently lost
  pairing-code submission returns the same `400` problem
  (`analyst-manager-pairing-code-invalid`) after the same hash lookup; every
  invalid polling handle returns one `400` problem
  (`analyst-manager-polling-handle-invalid`). Problems use the shared shape
  `type = urn:socalytics:problem:<code>` with `code` and `correlationId`.
  Authorization (`ClubPermission.Register`) is checked before the code is looked
  up. Rate limiting uses the in-box
  ASP.NET Core rate limiter (`Microsoft.AspNetCore.RateLimiting`, no package) with
  named fixed-window policies: pairing creation and status/activation partitioned
  by client address, code submission partitioned by authenticated account.
  Rejections return `429` problem details (`analyst-manager-rate-limited`)
  without revealing request existence. Club and Identity deferred IP-based
  limiting for its own endpoints; Registry needs it because pairing endpoints
  are anonymous.
  Non-production defaults: pairing creation 10/min, status and activation
  60/min, code submission 10/min, token 30/min.
- **Rationale**: FR-007, SC-006, and the "guessing or polling floods" edge case.
  The in-box limiter needs no dependency.
- **Alternatives considered**: Distributed rate limiting in PostgreSQL (rejected
  for this slice: per-instance limits plus code entropy already bound guessing;
  production values are unresolved anyway); per-handle `slow_down` responses
  (rejected: unnecessary complexity).

## R7. Lifecycle commands, expiry, and concurrency

- **Decision**: One aggregate `analyst_manager_registration` carries the whole
  lifecycle from `Pairing` to a terminal state. Every transition handler runs in
  one `IUnitOfWork` and updates `WHERE id = @Id AND state = @ExpectedState`
  (state guard, no `If-Match`); zero rows means a `409` conflict, except for the
  pairing-code submission, which returns the indistinguishable refusal. Expiry is
  evaluated by the platform clock (`TimeProvider`) inside each command and, for
  untouched records, by a `BackgroundService` in the API
  (`RegistrationExpiryWorker`, non-production interval 30 s) that executes the
  `ExpireDueRegistrations` command; state guards make concurrent instances safe.
  The worker also purges expired access-token and replay rows.
- **Rationale**: Follows the shared persistence conventions and
  [Contracts and Compatibility](../../docs/architecture/contracts-and-compatibility.md)
  (transitions are state-guarded). FR-009 and FR-033 require recorded expiry with
  an audit event even when nobody touches the request.
- **Alternatives considered**: Expiry computed only at read time (rejected: no
  expiry audit event); NATS-scheduled expiry (rejected: NATS not adopted by this
  feature).

## R8. Human operations and authorization

- **Decision**: Registrar submission and Club Admin approval, rejection,
  revocation, list, and read are REST operations under
  `/api/v1/analyst-manager-registrations`, mapped with the Club and Identity
  building blocks: the `MapMemberApi` route group (scheme `SocAlyticsSession`,
  cookie `__Host-socalytics-session`, policy `ActiveMember`), the
  `SessionAntiforgeryFilter` (`X-CSRF-Token`, `403 antiforgery-failed`), and
  `ProblemResults` mapping from `OperationResult<T>`/`OperationFailure`.
  Authorization is decided inside each Application command with
  `IAccessAuthorizer.AuthorizeClubAsync`: `ClubPermission.Register` for
  submission and `ClubPermission.Administer` for every other human operation,
  evaluated against current membership and roles at the time of the operation.
  `IAccessAuthorizer` already records each denial once as `authorization.denied`
  (independent transaction), so Registry adds no second denial event. Actor and
  correlation come from `IRequestContext`. No `ITeamScopeResolver` call is
  needed because registrations are club-scoped, not team-owned.
  Manager-facing operations are mapped outside `MapMemberApi`: the anonymous
  ones (stamp discovery, pairing, status, activation, token) are added to the
  Club and Identity anonymous allow list checked by the architecture tests, and
  the `self` operations require only the `AnalystManagerDPoP` scheme. The DPoP
  scheme is never accepted by human operations, and the session cookie is never
  accepted by Manager operations.
- **Rationale**: FR-005, FR-006, FR-008, FR-013, and the architecture's
  separately authorized and audited submit/approve operations; reusing the
  shared types follows the Club and Identity rule that later plans must not
  introduce parallel types.
- **Alternatives considered**: A Registry-specific admin UI (deferred to
  `src/clients` by the spec); Registry-specific denial events (rejected: the
  shared authorizer already audits each denial, and a second event would break
  SC-008).

## R9. Stamp binding and transport security

- **Decision**: The platform gains configuration `Stamp:Id` (opaque, required
  when Registry endpoints are mapped) and `Stamp:PublicBaseUri` (HTTPS origin used
  for the token audience, DPoP `htu`, activation audience, and the verification
  URI). A public discovery operation returns the stamp identifier and endpoint
  URIs. Pairing requests must name the same `stampId`; registrations store it;
  token issuance and every Manager request compare it with configuration
  (FR-003, FR-014). Manager endpoints carry an endpoint filter that refuses
  requests where `HttpRequest.IsHttps` is false; the Manager refuses non-`https`
  platform endpoints. Local validation uses the HTTPS endpoint that Club and
  Identity adds to the AppHost (`WithHttpsEndpoint()`); integration tests use the
  in-process test server with an `https://localhost` base address.
- **Rationale**: One database per stamp already prevents cross-stamp lookups;
  explicit stamp identifiers make wrong-stamp configuration fail loudly and keep
  audit events attributable. Configured public URIs avoid trusting proxy-rewritten
  host headers for `htu` and `aud` checks.
- **Alternatives considered**: Using the request host as stamp identity
  (rejected: proxy-dependent); forwarded-headers configuration (deferred to
  deployment work).

## R10. Audit events

- **Decision**: Reuse `IAuditTrail` and `AuditEvent` (Club and Identity) on
  table `security_audit_event`: `RecordAsync` in the same unit of work as each
  change, `RecordIndependentAsync` for refusals that change nothing. Event types
  follow the `area.verb-ed` style: `analyst-manager.pairing-issued`,
  `analyst-manager.pending-request-created` (records pairing-code consumption and
  request creation, one lifecycle transition), `analyst-manager.approved`,
  `analyst-manager.rejected`, `analyst-manager.expired`,
  `analyst-manager.activation-refused`, `analyst-manager.activated`,
  `analyst-manager.credential-issued`, `analyst-manager.credential-refused`,
  `analyst-manager.revoked`, `analyst-manager.unregistration-started`,
  `analyst-manager.unregistered`; authorization denials are the shared
  `authorization.denied`. Resource `analyst-manager-registration` with the
  registration id; actor `member` (from `IRequestContext`), `analyst-manager`
  (new `actor_kind` value), `anonymous`, or `system`; stable `reason_code`s; and
  allow-listed `details` (Manager id, stamp, key thumbprint, from/to state,
  window expiry, minimized device metadata without `hostLabel`, registration
  correlation id). Free-text reasons stay on the registration row and are
  referenced by `resource_id`, because `details` accepts no free text. The
  Registry migration extends the `actor_kind` check (and `resource_type` values
  if constrained). Full mapping in
  [data-model.md](data-model.md#audit-events-security_audit_event).
- **Rationale**: FR-033, SC-008 (exactly one event per transition or decision),
  and the [Audit Events](../../docs/architecture/security-and-data-governance.md#audit-events)
  minimization rules.
- **Alternatives considered**: Separate events for code consumption and request
  creation (rejected: one transition would yield two events, contradicting
  SC-008).

## R11. Analyst Manager host shape (no Avalonia in this slice)

- **Decision**: One executable project `SocAlytics.Analysts.Manager`
  (assembly `socalytics-manager`) using the .NET 10 Generic Host for `run`, and
  `System.CommandLine` 2.0.12 for the command surface (`run`, `register`,
  `status`, `pause`, `resume`, `exit`, `unregister`). All commands except `run`
  are thin local-control clients of the running host.
- **Rationale**: The architecture adopts the Generic Host and keeps the worker
  testable without the UI; the spec excludes the desktop interface. A single
  production project keeps the change minimal; `System.CommandLine` is the
  Microsoft-supported stable parser (2.0 GA with .NET 10).
- **Alternatives considered**: Avalonia tray now (out of scope); a separate CLI
  project (rejected: no reuse benefit); hand-written argument parsing (rejected:
  more code, worse help output).

## R12. Local operating controls transport

- **Decision**: Newline-delimited JSON requests and responses (one request per
  connection, schema in [local-control.schema.json](contracts/local-control.schema.json))
  over a Unix domain socket on every operating system (`AF_UNIX`, supported by
  .NET on Windows 10 1803+/Server 2019+, Linux, and macOS). The socket file
  `control.sock` lives in the Manager state directory, which is created with an
  owner-only DACL on Windows and mode `0700` on Linux/macOS; the socket is mode
  `0600` where applicable. Only the Manager's process account (the host operator
  in the autostart model) can connect.
- **Rationale**: One transport means the Linux CI runner exercises exactly the
  production IPC code path. Owner-only filesystem permissions provide the
  authorization boundary without credentials (FR-005, FR-024).
- **Alternatives considered**: Windows named pipes plus Unix sockets (rejected:
  two transports, Windows path untestable on the Linux runner); Kestrel HTTP over
  a socket (rejected: pulls the ASP.NET Core framework into the Manager for four
  commands); gRPC (rejected: dependencies and code generation); loopback TCP
  (rejected: reachable by any local user).

## R13. Device-key store per operating system

- **Decision**: ECDSA P-256 (ES256) device keys behind `IDeviceKeyStore`.
  Windows: Windows CNG `Microsoft Software Key Storage Provider`, user-scoped
  persisted key (scoped to the Manager process account), `ExportPolicy = None`,
  one fresh key per pairing attempt, key name recorded as the device-key
  reference. This is the only built-in store in this slice and is an
  implementation candidate, not an approved production store (`GOV-CRED-002`).
  Linux and macOS: an `UnsupportedDeviceKeyStore` reports "no supported protected
  key store on this operating system" with remediation text; registration is
  refused and nothing is created (FR-002, acceptance scenario US1-10). The
  test-only store (in-memory ECDSA, no export API) exists only in the test
  assembly and is registered only by test hosts, so it cannot be selected
  outside validation. TPM-backed keys (`Microsoft Platform Crypto Provider`) are
  deferred; hardware backing is optional per architecture.
- **Rationale**: The first supported Analyst execution host is Windows
  ([Analyst Runtime and Recovery](../../docs/architecture/analyst-runtime-and-recovery.md#oci-container-runtime-boundary)).
  .NET has managed CNG APIs (`CngKey.Create`, `ECDsaCng`) for non-exportable
  persisted keys but no managed API for macOS Keychain/Secure Enclave key
  creation or a standard Linux non-exportable key store; adding native interop or
  `tpm2-pkcs11` now would exceed this slice, and the spec defers per-OS evidence.
- **Alternatives considered**: Linux kernel keyring `asymmetric` keys
  (rejected: key must exist outside first; no managed API); `tpm2-pkcs11`
  (deferred: native dependency and evidence spike); macOS Keychain via
  Security.framework P/Invoke (deferred); exportable PEM file with file
  permissions (prohibited by FR-002).

## R14. Protected local registration state

- **Decision**: `IProtectedStateStore` persists one versioned JSON document
  (`ProtectedRegistrationState`) protected with Windows DPAPI
  (`System.Security.Cryptography.ProtectedData` 10.0.12, `CurrentUser` scope,
  fixed application entropy) to `registration.state` in the state directory
  (default `%LOCALAPPDATA%\SocAlytics\AnalystManager`), written atomically
  (temporary file plus replace). DPAPI's authenticated encryption is the
  integrity check; restore additionally requires the referenced key to open and
  its thumbprint to equal the recorded thumbprint. Linux/macOS have no supported
  state store in this slice (consistent with R13). Tests use a test-only AES-GCM
  store to exercise tamper, missing, and unreadable cases on any OS.
- **Rationale**: FR-017, FR-019, SC-005. DPAPI user scope prevents use by other
  accounts or machines and needs no extra key management.
- **Alternatives considered**: ASP.NET Core Data Protection (rejected: key ring
  on disk is another secret to protect); Windows Credential Manager (rejected:
  size limits, no added benefit); signing the state with the device key
  (rejected: thumbprint comparison plus DPAPI integrity already bind state and
  key).

## R15. Container-runtime preflight in a slice without runtime integrations

- **Decision**: Define `IContainerRuntimeAdapter` (probe identity, version,
  endpoint locality and authorization, backend, registry authentication with a
  configured probe image reference, image-platform compatibility, resource
  feasibility, accelerator functional probes) and a `PreflightRunner` that
  evaluates the probe against an embedded, versioned `runtime-compatibility.json`
  shipped inside the Manager assembly. Configuration `Runtime:Profile` selects a
  built-in adapter by key. This slice ships **no** production adapter and an
  empty compatibility list, so production builds always remain
  `RuntimeUnavailable` with check `runtime-integration-configured` failed
  (FR-032). Tests register a `SimulatedRuntimeAdapter` and a test compatibility
  list through the test host. Preflight runs after activation, at every start,
  and on a periodic interval (non-production default 60 s) to detect runtime loss
  or out-of-list upgrades (FR-031).
- **Rationale**: The spec puts production runtime integrations out of scope and
  validates preflight against a simulated runtime. Embedding the list prevents a
  host operator from declaring an untested runtime supported.
- **Alternatives considered**: Shipping the Docker Engine adapter now (rejected:
  out of scope); `Docker.DotNet` (rejected for the future adapter too:
  `HttpClient` over a socket suffices); a user-editable compatibility file
  (rejected: detection must not imply support).

## R16. Drain, timeout policy, and simulated work

- **Decision**: `IManagerWorkTracker` exposes active Manager-owned work and
  cleanup; production registers an empty tracker (no work exists in this slice);
  tests register simulated work. Drain stops admission immediately, waits up to
  `Operating:DrainTimeout` (non-production default 5 minutes), then applies
  `Operating:DrainTimeoutPolicy` (`CancelRequest` restores the prior state;
  `StopAndCleanUp` cancels remaining work within `Operating:CleanupBound`,
  default 30 s, then completes the exit or unregister).
- **Rationale**: FR-027 and SC-009 without introducing containers.
- **Alternatives considered**: Infinite drain (prohibited).

## R17. Revocation detection and platform error signals

- **Decision**: The Manager checks `GET /api/v1/analyst-managers/self/registration`
  every `Platform:StatusCheckInterval` (non-production default 60 s) and retries
  failures with exponential backoff from 2 s up to 60 s. It clears local stamp
  state only on an explicit, proof-authenticated inactive signal: a token
  response `invalid_client` with `registration_state` `revoked` or
  `unregistered`, or a `401` with `WWW-Authenticate: DPoP error="invalid_token"`
  and problem type `analyst-manager-registration-inactive`. Transport errors,
  `5xx`, and other refusals keep the protected registration and stay
  non-admitting.
- **Rationale**: FR-021, SC-003, and acceptance scenario US2-7 (unreachable
  platform must not wipe the identity). Disclosing the inactive state is safe
  because the caller has just proven device-key possession.
- **Alternatives considered**: Treating any `401` as revocation (rejected: a
  clock or configuration fault would destroy a valid identity).

## R18. Platform-side `Unregistering`

- **Decision**: Local unregister calls
  `POST /api/v1/analyst-managers/self/unregistration` before draining
  (`Active → Unregistering`, idempotent), drains locally, then calls
  `POST /api/v1/analyst-managers/self/unregistration/completion`
  (`Unregistering → Unregistered`). `Unregistering` registrations may still obtain
  tokens and report status but are visible to Club Admins and may be revoked.
  The Manager persists a pending-unregister flag with its operating intent so a
  restart resumes the unregister; when the platform is unreachable it retries and
  completes on confirmation or on observing revocation.
- **Rationale**: Matches the lifecycle states in the spec's key entity and the
  architecture state diagram, and keeps drain-time token use possible for later
  work slices.
- **Alternatives considered**: A single unregister call after drain (rejected:
  the platform would never record `Unregistering`).

## R19. Testing strategy across two solutions

- **Decision**: Platform behavior is verified in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry`
  (Testcontainers PostgreSQL, `WebApplicationFactory`, a test-side Manager
  simulator signing with in-memory ECDSA, `FakeTimeProvider` from
  `Microsoft.Extensions.TimeProvider.Testing` 10.10.0) plus architecture tests.
  Manager behavior is verified in `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests`
  (xUnit v3, Shouldly, `FakeTimeProvider`, `FakeLogger` from
  `Microsoft.Extensions.Diagnostics.Testing` 10.10.0, and `JsonSchema.Net` 8.0.5 (last MIT-licensed line, as in the platform contract tests)
  to validate every local-control message against the component-local schema)
  against an in-process fake
  platform implementing [openapi.yaml](contracts/openapi.yaml), the test-only key
  and state stores, and the simulated runtime. Windows CNG and DPAPI adapter
  tests use xUnit v3 `SkipUnless` on Windows and report as skipped on the Linux
  runner. Secret-leak tests (SC-007) scan captured logs, audit rows, status
  output, and state files for every generated secret value.
- **Rationale**: Each solution validates what it owns without a cross-area
  project reference; both sides are tied to the same contract.
- **Alternatives considered**: A cross-solution end-to-end test project
  (rejected: couples the platform and analysts builds; the manual quickstart
  covers the composed flow on Windows).

## R20. Build environment for the new Manager solution

- **Decision**: The Manager solution is
  `src/analysts/manager/SocAlytics.Analysts.Manager.slnx`. It pins the SDK with
  its own `src/analysts/manager/global.json`, whose content is identical to
  `src/platform/global.json` (`10.0.400`, `rollForward: latestPatch`,
  `allowPrerelease: false`), so running `dotnet` inside `src/analysts/manager`
  resolves the same SDK without depending on the platform folder. The combined
  environment feature
  [20261007-115855-environment-verification-coverage](../20261007-115855-environment-verification-coverage/spec.md)
  extends `.github/actions/environment-verify` so the Linux runner restores,
  builds, and tests that solution and covers `^src/analysts/manager/`; setup
  keeps installing the SDK from `src/platform/global.json`, which the identical
  Manager pin satisfies. It must merge before this feature.
- **Rationale**: Constitution 1.1.0 Technology and Tooling Constraints; the
  current verify action builds only the platform solution and would report every
  Manager file as uncovered.
- **Alternatives considered**: Adding the Manager projects to the platform
  solution (rejected: crosses source-area ownership and the shared conventions);
  a Windows runner job (rejected: environment features change only the two
  composite actions, not the runner).
