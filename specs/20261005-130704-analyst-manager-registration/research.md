# Research: Analyst Manager Registration

**Feature**: [spec.md](spec.md) | **Plan**: [plan.md](plan.md) | **Date**: 2026-10-07

Each entry records the decision, why it was chosen, and the alternatives that
were evaluated. Values called "non-production defaults" are explicit validation
values only; production-approved values remain unresolved under
[Production Deployment and Operations](../../docs/architecture/production-operations.md)
and `GOV-CRED-002`/`GOV-CRED-003` in
[Security and Data Governance](../../docs/architecture/security-and-data-governance.md).
Manager decisions (R11–R14, R19–R24) rest on the 2026-10-07 Manager spikes
M1–M6 (PKCS#11 with SoftHSM2 and tpm2-pkcs11, Windows CNG, macOS research,
Avalonia tray and headless tests, autostart, control socket).

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
  one `IUnitOfWork.BeginAsync` scope (`IUnitOfWorkScope`) and updates `WHERE id = @Id AND state = @ExpectedState`
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
  the `self` operations require the policy `AnalystManager` (scheme
  `AnalystManagerDPoP`, scope `analyst-manager`), which this feature defines
  and Durable Analysis Workflow reuses for its Manager operations. The DPoP
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
  Registry migration drops and re-adds `ck_security_audit_event_actor_kind`
  with `analyst-manager` added (`resource_type` has no check). Full mapping in
  [data-model.md](data-model.md#audit-events-security_audit_event).
- **Rationale**: FR-033, SC-008 (exactly one event per transition or decision),
  and the [Audit Events](../../docs/architecture/security-and-data-governance.md#audit-events)
  minimization rules.
- **Alternatives considered**: Separate events for code consumption and request
  creation (rejected: one transition would yield two events, contradicting
  SC-008).

## R11. Analyst Manager application shape: per-user Avalonia tray app

- **Decision**: The Manager is a per-user desktop application (no service, no
  administrator rights; FR-037) built from two production projects:
  `SocAlytics.Analysts.Manager.Core` (class library, no Avalonia reference)
  holds the .NET 10 Generic Host worker and every behavior (registration,
  restore, revocation, key providers, signed state, platform client, operating
  state machine, timed pause, preflight, autostart, local-control server and
  client); `SocAlytics.Analysts.Manager` (Avalonia 12.1.3 `WinExe`, assembly
  `socalytics-manager`, plain `net10.0`) hosts the worker in-process and adds
  the tray icon, tray menu, status window, and view models. The tray menu offers
  status, pause, timed pause (presets 30 min, 1 h, 2 h, 4 h, 8 h, and a custom
  duration), resume, safe exit, register, unregister, and a "Start at sign-in"
  toggle (FR-024). The app uses `ShutdownMode.OnExplicitShutdown` with no main
  window, creates the `TrayIcon` only after its properties are set, and opens
  the status window on demand. On Linux it checks whether
  `org.kde.StatusNotifierWatcher` owns a name on the session bus; without a
  StatusNotifierItem host (and on any tray failure) it shows the status window,
  which offers the same controls (edge case "no tray area"). View models are
  plain `INotifyPropertyChanged` classes over the worker's state API, so they are
  tested without Avalonia; windows and the tray menu are tested with
  `Avalonia.Headless.XUnit` 12.1.3, which runs on Linux without a display
  (spike M4). xUnit v3 stays on 3.2.x because 4.x breaks `[AvaloniaFact]`
  (Avalonia issue #22072).
- **Rationale**: The architecture adopts the Generic Host plus Avalonia and
  keeps the worker testable without the UI; the spec's clarification makes the
  tray app the primary surface on all three operating systems. Splitting Core
  from the UI project enforces "UI-free worker" by the reference graph rather
  than by convention, and lets every behavior test run without UI types.
- **Alternatives considered**: A Windows service or systemd/launchd daemon
  (rejected by FR-037 and the clarification); one project with UI and worker
  mixed (rejected: nothing stops UI types leaking into the worker); Electron or
  Tauri (the architecture's fallbacks, not needed because spike M4 passed);
  XEmbed tray fallback on Linux (does not exist in Avalonia; the status window
  covers it).

## R12. Single instance and local-control channel

- **Decision**: One Unix domain socket per user (`UnixDomainSocketEndPoint`,
  `SocketType.Stream`, `ProtocolType.Unspecified`) serves two purposes:
  single-instance detection and the optional scripting CLI. Paths:
  Windows `%LOCALAPPDATA%\SocAlytics\AnalystManager\run\manager.sock` in a
  directory with a protected owner-only DACL; Linux
  `$XDG_RUNTIME_DIR/socalytics/manager.sock` (fallback
  `~/.local/state/socalytics/run/manager.sock`), directory `0700`, socket `0600`;
  macOS `~/Library/Application Support/SocAlytics/AnalystManager/run/manager.sock`
  (fallback `$TMPDIR/socalytics/manager.sock` when the path exceeds 104 bytes).
  Every accepted connection is verified against the owning user: Linux
  `SO_PEERCRED` uid, Windows `SIO_AF_UNIX_GETPEERPID` then process-token SID,
  macOS `getpeereid` uid; mismatches are rejected. On start the app binds the
  socket; if the file exists it connects, and a live instance receives
  `show-status` (bringing its status window forward) while the new process
  exits; an unanswered socket is stale and is replaced. The same executable
  started with a command (`status`, `pause`, `resume`, `exit`, `register`,
  `unregister`, `autostart`) acts as a client and never starts the UI; on
  Windows it attaches to the parent console (`AttachConsole`) so output is
  visible from a `WinExe`. Messages are newline-delimited JSON as defined in
  [local-control.schema.json](contracts/local-control.schema.json).
- **Rationale**: Spike M6 confirmed sockets, owner-only directories, Windows
  ACL enforcement at `connect()`, and peer-identity checks on Windows and
  Linux. One transport means the Linux runner exercises the production IPC path
  and single-instance logic. The CLI keeps the controls scriptable and gives a
  guaranteed control surface when no tray host exists.
- **Alternatives considered**: Named mutex plus named pipes (rejected: two
  mechanisms and Windows-only); Kestrel over a socket (rejected: ASP.NET Core in
  the Manager for a handful of commands); loopback TCP (rejected: reachable by
  other local users).

## R13. Device-key providers per operating system

- **Decision**: `IDeviceKeyProvider` creates, opens, signs with, and deletes a
  per-user ECDSA P-256 key and reports `keyProtection` (sent to the platform as
  `claimedKeyProtection`, R26). JWS ES256 needs raw
  `r‖s`, which every provider returns. One fresh key per pairing attempt.
  - **Windows (CNG)**: Microsoft Platform Crypto Provider (TPM) first; the
    Microsoft Software Key Storage Provider only when stamp discovery reports
    `softwareKeysAllowed` (R26); user scope (no `MachineKey`),
    `ExportPolicy = None`, `KeyUsage = Signing`. `keyProtection` is `tpm` or
    `software`. Every private export fails with `NTE_NOT_SUPPORTED` and the
    policy cannot be loosened later (spike M2). Software keys live in the
    roaming profile, which is why they are refused unless the stamp allows
    software keys.
  - **macOS**: a small Swift library `SocAlyticsMacBridge` (universal
    arm64/x86_64 dylib with `@_cdecl` exports, bundled in the `.app`) over
    CryptoKit `SecureEnclave.P256.Signing.PrivateKey`; the SE-wrapped
    `dataRepresentation` is the key reference, usable only by that Secure
    Enclave; `.rawRepresentation` signatures are already `r‖s`.
    Without a Secure Enclave, and only when the stamp allows software keys,
    the bridge creates a non-extractable Keychain key (`keyProtection`
    `software`; DER signatures converted to `r‖s` in C#).
    Plain .NET cannot create persistent or Secure Enclave keys (spike M3). The
    dylib is built on macOS only; entitlement needs are verified in a Mac spike
    before macOS release.
  - **Linux (PKCS#11)**: `Pkcs11Interop` 5.3.0 with
    `NativeLibrary.SetDllImportResolver` mapping `libdl` to `libdl.so.2`
    (glibc ≥ 2.34). Production module tpm2-pkcs11
    (`/usr/lib/x86_64-linux-gnu/pkcs11/libtpm2_pkcs11.so`, per-user store via
    `TPM2_PKCS11_STORE` in a `0700` directory, TCTI `device:/dev/tpmrm0` with
    the `tss` group or `tabrmd`). The operator provisions the token once
    (`tpm2_ptool init` / `addtoken`, label `socalytics-device`); the Manager
    generates the key with `CKM_EC_KEY_PAIR_GEN` (private template
    `CKA_TOKEN`, `CKA_PRIVATE`, `CKA_SENSITIVE`, `CKA_SIGN` true,
    `CKA_EXTRACTABLE` false) and signs with `CKM_ECDSA` over SHA-256. It never
    sets `CKA_EXTRACTABLE=true` or `CKA_SENSITIVE=false`, because tpm2-pkcs11
    accepts such metadata changes; non-exportability comes from the TPM
    (`fixedtpm|fixedparent`). The user PIN is a random value that the operator
    chooses when provisioning the token (`tpm2_ptool addtoken --userpin`) and
    stores in the owner-only file `pkcs11.pin` in the Manager state directory;
    the Manager only reads it and never generates, changes, or initializes it
    (no `C_InitPIN`, which would need the SO PIN). A missing or non-owner-only
    PIN file or a failed `C_Login` makes the provider unavailable
    (`no-supported-key-store` at registration, `key-inaccessible` for an
    existing key). The SoftHSM2 test fixture writes the PIN it initialized the
    token with into the test state directory the same way. Device binding comes
    from the TPM. `keyProtection` is
    `pkcs11-token`. Module paths come from a built-in allow-list.
  - **SoftHSM2** is a test-only module: the test host registers it with a
    per-user token created via `SOFTHSM2_CONF` and `softhsm2-util --init-token
    --free`; production configuration cannot select it because it is not on
    the allow-list (FR-038 assumption "test-only store"). It reports
    `software`, so the Linux tests also exercise the software-key refusal.
  - Any other host, or a missing store, dylib, module, or token, refuses
    registration with an actionable explanation and creates nothing (FR-002).
- **Rationale**: Implements FR-038 with the spike-verified mechanisms (M1, M2,
  M3). SoftHSM2 exercises the same PKCS#11 code path as production on the Linux
  runner.
- **Alternatives considered**: `net10.0-macos` provider assembly (rejected:
  macOS-only builds for the whole app); raw P/Invoke to Security.framework
  (rejected: CoreFoundation memory management without precedent); Linux kernel
  keyring (rejected: no managed API, key must exist outside first); exportable
  key files (prohibited by FR-002).

## R14. Signed owner-only local state (no DPAPI)

- **Decision**: The local registration state is one file `registration.state`
  in the per-user state directory (Windows `%LOCALAPPDATA%\SocAlytics\AnalystManager`,
  macOS `~/Library/Application Support/SocAlytics/AnalystManager`, Linux
  `$XDG_STATE_HOME/socalytics/analyst-manager`), created owner-only (protected
  DACL for the current user; `0600` in a `0700` directory). Its content is a
  compact JWS (`typ` `socalytics-manager-state+jwt`, `alg` `ES256`, `kid` =
  device-key thumbprint) whose payload is the canonical JSON of
  `ProtectedRegistrationState` (UTF-8, sorted property names, no insignificant
  whitespace), signed with the device key. Start-up reads the payload, opens the
  referenced key through its provider, verifies the signature with that key's
  public key, compares the thumbprint, checks the owner-only permissions, and
  fails closed on any mismatch (FR-017, FR-019). Writes are atomic (temporary
  file plus replace) and re-signed on every intent change. The state contains no
  secret; the macOS Secure Enclave `dataRepresentation` is a key handle usable
  only by that enclave.
- **Rationale**: A signature with the non-exportable key detects edits and
  copies to another account or machine on every OS, replacing the Windows-only
  DPAPI design. Owner-only permissions keep other accounts out.
- **Alternatives considered**: DPAPI (rejected: Windows-only, and the
  clarification requires one mechanism on all three OSes); an HMAC key stored
  beside the file (rejected: one more secret to protect); encrypting the state
  (unnecessary: it holds no secret).

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

- **Decision**: Local unregister drains first and contacts the platform only
  after a successful drain (spec clarification 2026-10-07, FR-022). The Manager
  records `pendingUnregister` in its signed state, drains with purpose
  `unregister` while the registration stays Active locally and on the
  platform, and then, only when the drain completed (work finished, or the
  `StopAndCleanUp` timeout policy cleaned it up), calls
  `POST /api/v1/analyst-managers/self/unregistration`
  (`Active → Unregistering`, idempotent) followed by
  `POST /api/v1/analyst-managers/self/unregistration/completion`
  (`Unregistering → Unregistered`). When the drain is cancelled by the
  `CancelRequest` timeout policy, no platform call is made: the registration
  stays Active, `pendingUnregister` is cleared, and the Manager returns to its
  previous Running or Paused intent (and to Runtime unavailable if that was its
  prior state). `Unregistering` registrations may still obtain tokens and
  report status, are visible to Club Admins, and may be revoked; there is no
  `Unregistering → Active` transition because nothing can cancel an unregister
  after begin. A restart with `pendingUnregister` resumes the unregister: with
  the platform still `active` the drain repeats, and with the platform
  `unregistering` (which means the drain already succeeded) the Manager goes
  straight to begin and completion; a platform `unregistering` without the
  local flag is treated the same way. When the platform is unreachable after
  the drain the Manager stays `unregistering` and non-admitting, retries, and
  completes on confirmation or on observing revocation.
- **Rationale**: The clarification requires that a cancelled unregister leaves
  the platform registration Active; calling begin before the drain would leave
  it `Unregistering` with no way back. Keeping the begin call still records an
  auditable `Unregistering` state while completion may be retried after
  network failures, matching the lifecycle in the spec's key entity and the
  architecture state diagram.
- **Alternatives considered**: Calling begin before the drain (rejected by the
  2026-10-07 clarification: a cancelled drain would strand the registration in
  `Unregistering`, which admits only status and completion); adding an
  `Unregistering → Active` cancel transition (rejected: widens the platform
  lifecycle beyond the architecture); a single unregister call after drain
  (rejected: the platform would never record `Unregistering`).

## R19. Testing strategy across two solutions

- **Decision**: Platform behavior is verified in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry`
  (Testcontainers PostgreSQL, `WebApplicationFactory`, a test-side Manager
  simulator, `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing`
  10.10.0) plus architecture tests. Manager behavior is verified in
  `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests` with xUnit v3
  3.2.2 (pinned `[3.2.2,4.0)`), Shouldly, `FakeTimeProvider`, `FakeLogger`
  (`Microsoft.Extensions.Diagnostics.Testing` 10.10.0), `JsonSchema.Net` 8.0.5
  (last MIT release; validates every local-control message), and
  `Avalonia.Headless.XUnit` 12.1.3 for the tray menu and status window. On the
  Linux runner the Manager tests use the PKCS#11 provider against SoftHSM2
  (keygen template, ES256 encoding, reload by `CKA_ID`, export refusal, the
  `libdl` resolver), the real signed-state store, the socket channel with peer
  checks, the XDG autostart registrar under a temporary `XDG_CONFIG_HOME`, the
  in-process fake platform, the simulated runtime, and simulated work. Windows
  CNG, Windows autostart and socket ACL behavior, and every macOS provider are
  covered by tests marked `SkipUnless` their OS and are verified manually per
  [quickstart.md](quickstart.md); pure logic (DER to `r‖s`, plist and Run-key
  generation) runs everywhere. Both sides also run the shared golden fixtures
  (R23). Secret-leak tests (SC-007) scan captured logs, audit rows, status
  output, diagnostics, and state files for every generated secret value.
- **Rationale**: Each solution validates what it owns without a cross-area
  project reference; golden fixtures plus the manual end-to-end run catch drift
  between them (spec clarification).
- **Alternatives considered**: A cross-solution end-to-end test project
  (rejected: couples the two builds); a Windows or macOS runner (not available
  through the composite environment actions).

## R20. Build environment for the Manager solution

- **Decision**: The Manager solution is
  `src/analysts/manager/SocAlytics.Analysts.Manager.slnx`. It pins the SDK with
  its own `src/analysts/manager/global.json`, identical to
  `src/platform/global.json` (`10.0.400`, `rollForward: latestPatch`,
  `allowPrerelease: false`). The combined environment feature
  [20261007-115855-environment-verification-coverage](../20261007-115855-environment-verification-coverage/spec.md)
  installs SoftHSM2 (`softhsm2`) in `environment-setup` and extends
  `environment-verify` so the Linux runner restores, builds, and tests the
  Manager solution when `src/analysts/manager/**` or
  `contracts/analyst-manager/**` changes and in finalize mode. Avalonia headless
  tests need no X server or extra packages (spike M4). The macOS Swift bridge is
  not built on the runner (it needs Xcode); the .NET code that loads it builds
  everywhere. The environment feature must merge before this feature.
- **Rationale**: Constitution 1.1.0 Technology and Tooling Constraints and the
  spike's CI coverage table.
- **Alternatives considered**: Adding the Manager projects to the platform
  solution (rejected: crosses source-area ownership); a tpm2-pkcs11 + swtpm job
  (deferred: proven in the spike but optional; SoftHSM2 covers the provider
  logic).

## R21. Per-user autostart

- **Decision**: `IAutostartRegistrar` with three implementations, written only
  on explicit opt-in from the tray toggle, which always reads back the real
  state (FR-037):
  - Windows: value `SocAlytics.AnalystManager` = `"<exe>" --autostart` under
    `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`; the undocumented
    `StartupApproved\Run` flag is read to show a disabled state but never
    written.
  - macOS: `SMAppService.mainApp.register()` / `unregister()` through the Swift
    bridge (macOS 13+, signed `.app`); fallback
    `~/Library/LaunchAgents/<bundle-id>.plist` with `RunAtLoad`,
    `ProgramArguments`, and `LimitLoadToSessionType=Aqua`.
  - Linux: `${XDG_CONFIG_HOME:-~/.config}/autostart/socalytics-analyst-manager.desktop`
    (`Type=Application`, `Exec=… --autostart`); disabling removes the file.
  An `--autostart` start behaves like any start (FR-018) and stays in the tray
  without opening the status window.
- **Rationale**: Spike M5 found no mature cross-platform library; each
  implementation is small and per-user, needing no administrator rights.
- **Alternatives considered**: AutoLaunch 1.0.1 (rejected: single author, low
  adoption); systemd user units (unnecessary: XDG autostart is converted by
  systemd sessions); Startup-folder shortcuts (rejected: COM `IShellLink`).

## R22. Timed pause

- **Decision**: The operating intent is `Running`, `Paused`, or
  `PausedUntil(endUtc)`. A timed pause stops admission immediately, persists the
  end time in the signed state, and is evaluated by a `TimeProvider` timer with a
  1 s tick. At the end time the intent becomes `Running`; the Manager enters
  Running only when the registration is confirmed Active and the latest
  preflight passed, otherwise it stays Runtime unavailable or restoring under
  the normal rules (FR-036, edge case). An explicit resume ends it early.
  After a restart an unexpired pause continues to its original end; an expired
  one restores intent `Running`. Durations are 1 minute to
  `Operating:MaxTimedPause` (non-production default 7 days). Status shows the
  end time and remaining time. The local clock is used because the pause is a
  local operating decision, not a platform expiry.
- **Rationale**: FR-036, US5 scenarios 7–8, and SC-012 (resume within 5 s of
  the end time).
- **Alternatives considered**: Platform-side scheduling (rejected: operating
  state is local); storing a remaining duration (rejected: drifts across
  restarts).

## R23. Shared golden fixtures

- **Decision**: Committed reference examples of every registration exchange
  live under repository-root `contracts/analyst-manager/registration/v1/` with a
  manifest `fixtures.json` described by the schema `registration.schema.json`
  (`$id`
  `https://socalytics.invalid/contracts/analyst-manager/registration/v1/registration.schema.json`,
  following the architecture's path rule
  `contracts/<area>/<name>/v<major>/<name>.schema.json`). The fixed test key is the published P-256 example key of
  RFC 7515 Appendix A.3, so no real secret is committed. Fixed context: clock
  `2026-01-01T00:00:00Z`, stamp `fixture-stamp`, base URI
  `https://stamp.example.test`. Vectors: stamp discovery, pairing request and
  issued response, status responses (awaiting, pending, approved with
  challenge, rejected), activation request with a signed activation proof and
  result, token request (form fields, `private_key_jwt` assertion, DPoP proof)
  and response, inactive token error, `self/registration` request (DPoP proof
  with `ath`) and response, the inactive `401` problem, the device
  fingerprint of the test key, a pairing-code submission with the right and
  with a wrong fingerprint, and negative vectors (replayed `jti`, wrong `aud`,
  wrong key, stale `iat`, wrong `htu`, missing `ath`). The
  platform runs each request vector through the API with `FakeTimeProvider` set
  to the fixture clock and asserts the expected outcome and response shape; the
  Manager parses every response vector with its client types, builds each
  request with an in-memory signer over the fixture key, and compares it with
  the vector: JWS headers and claims except `jti` (signatures verified with the
  fixture public key, never compared), and plain JSON bodies property by
  property except the random pairing `nonce`. `SocAlytics.Platform.Contracts.Tests` validates
  the manifest against its schema through `ContractCatalog.LoadRegistry()`, and
  its existing generic checks cover the schema and its single index row in
  `contracts/README.md`; Recording Lineage and Upload creates the index, the
  project, and the catalog and merges before this feature, which only extends
  them.
- **Rationale**: FR-039 and the clarification: a format change on either side
  fails verification without a cross-solution project reference. ECDSA
  signatures are randomized, so the Manager compares structure and verifies
  rather than comparing bytes.
- **Alternatives considered**: Generated client code (rejected: Kiota adoption
  is not part of this slice); a private test key generated for the repository
  (rejected: a committed private key invites secret-scanning noise; the RFC key
  is public).

## R24. Sign-out, shutdown, and session end

- **Decision**: On operating-system session end (Avalonia `ShutdownRequested`,
  Windows `WM_QUERYENDSESSION`, macOS `applicationShouldTerminate`, Linux
  `SIGTERM`), the app requests a safe exit with a drain bound of
  `Operating:SessionEndDrainBound` (non-production default 5 s), keeps the
  registration and last intent, and exits.
- **Rationale**: Edge case "user signs out or the host shuts down".
- **Alternatives considered**: Ignoring session end (rejected: the OS kills the
  process and the intent could be stale).

## R25. Device fingerprint and pairing origin (device-code phishing)

- **Decision**: Countering RFC 8628 §5.4 (remote phishing of user codes), the
  Manager shows a short device fingerprint next to the pairing code: eight
  base-20 characters (`XXXX-XXXX`, ≈34.6 bits) derived from the RFC 7638
  thumbprint as defined in
  [proof-profiles.md](contracts/proof-profiles.md#device-fingerprint). The
  pairing response carries the platform's derivation, which the Manager checks
  against its own. The Registrar submits the code and the fingerprint read from
  the Manager's screen; the platform compares the fingerprint in constant time
  with the stored value. A mismatch gets the same indistinguishable refusal as
  an invalid code (FR-040), records `analyst-manager.submission-refused`
  (reason `fingerprint-mismatch`) independently, and does not consume the code;
  after `Registry:Pairing:MaxFingerprintMismatches` (default 3) the pairing
  expires (reason `fingerprint-mismatch-limit`). The registration stores the
  connection source address and the time of the pairing request; the
  registration representation used by decision views returns the fingerprint,
  `pairingOrigin`, and a fixed `approvalNotice` ("Approve only an Analyst
  Manager you can physically identify by the device fingerprint shown on it")
  while a decision is pending.
- **Rationale**: A phished pairing code alone no longer creates a request; the
  attacker must also persuade the Registrar to type a fingerprint that is not
  on the Registrar's machine, and approvers see an unexpected origin and the
  warning. The fingerprint is public (derived from the public key), so it needs
  no protection. Its 34.6 bits suffice: blind guessing is bounded by the
  mismatch limit and rate limits, and forging a key whose fingerprint matches
  a genuine Manager would require knowing that Manager's fingerprint and about
  2^34 key generations within one pairing window.
- **Alternatives considered**: Showing the request origin only (rejected: easy
  to overlook); a QR code that binds code and fingerprint (deferred with QR
  rendering by the spec); a longer fingerprint (rejected: harder to read aloud,
  little gain given the mismatch limit). Residual risk: an attacker who
  persuades the Registrar to type both values from a message (Risk Register
  R-12).

## R26. Claimed key protection and the software-key gate

- **Decision**: The Manager sends `claimedKeyProtection` (`tpm`,
  `secure-enclave`, `pkcs11-token`, `software`) at pairing; the platform stores
  it, and API representations, audit details, and decision views label it as
  claimed. It is never an input to authorization or approval logic (FR-041).
  Its single effect: `software` is refused at pairing with `409
  analyst-manager-software-key-not-allowed` unless `Registry:AllowSoftwareKeys`
  is true. The setting defaults to `false` and is meant to be true only in
  development and test (the AppHost sets it; integration tests set it per
  test). Stamp discovery returns `softwareKeysAllowed`, and the Manager falls
  back to the Windows software KSP or a non-Secure-Enclave Keychain key only
  when it is true; otherwise it refuses registration before creating any key.
  Hardware key attestation (Windows Platform Crypto Provider key attestation,
  `TPM2_Certify` for tpm2-pkcs11 keys; macOS offers no general attestation for
  Secure Enclave keys) is Deferred to the credential and key authority under
  `GOV-CRED-002`.
- **Rationale**: Without attestation a modified Manager can claim any value, so
  the claim must not grant anything; refusing honest software claims by
  default still removes roaming software keys from production stamps.
- **Alternatives considered**: Trusting the claim for policy (rejected by
  FR-041); implementing attestation now (rejected: per-OS evidence, attestation
  CA trust, and policy are unresolved, and macOS lacks a mechanism); always
  allowing software keys (rejected: roaming-profile copies, edge case in the
  spec).
