# Research: Analyst Manager Device Key Rotation

All Technical Context items are resolved by the implemented registration,
work-execution, and fleet-status features; no `NEEDS CLARIFICATION` remains.

## R1. Operation shape and authentication

- **Decision**: `POST /api/v1/analyst-managers/self/key-rotation`
  (`rotateAnalystManagerKey`, tag `Analyst Manager self`), policy
  `AuthorizationPolicyNames.AnalystManager`, `RequireHttpsEndpointFilter`,
  `JsonOnlyFilter`, `Cache-Control: no-store`. Success is `200` with the
  rotation outcome (`switched` or `already-switched`).
- **Rationale**: Every other Manager self operation uses the same pattern, the
  outer DPoP proof already proves possession of the registered key, and member
  sessions cannot satisfy the `AnalystManager` policy (FR-010, SC-008).
- **Alternatives**: A member/administrator operation (rejected: FR-010); a
  separate unauthenticated endpoint (rejected: recovery needs the registration
  binding of machine authentication).

## R2. The two key proofs

- **Decision**: The body carries `replacementPublicJwk`,
  `claimedKeyProtection`, `oldKeyProof`, and `replacementKeyProof`. Both
  proofs are compact JWS client assertions (the registration's existing
  assertion format, `ES256`) with audience = the rotation URI of the stamp
  (`<base>/api/v1/analyst-managers/self/key-rotation`), `sub` = Manager ID,
  `jti`, `iat`/`exp` (short development lifetime, judged by platform time),
  and a binding claim: the old-key proof carries `new_jkt` (thumbprint of the
  replacement key), the replacement-key proof carries `old_jkt`. Binding makes
  each proof useless for any other pairing. Jti hashes are recorded in the
  existing `analyst_manager_proof_replay` store under a new kind
  `key_rotation_old` / `key_rotation_new`.
- **Rationale**: Reuses `IManagerProofValidator.ValidateClientAssertionAsync`
  (extended with an expected-claims check) and the replay store; no new
  cryptography. The outer DPoP is separate from the old-key proof, so the
  old-key proof is a deliberate, bound statement rather than a transport
  artifact.
- **Alternatives**: Reusing the DPoP proof as the old-key proof (rejected: not
  bound to the replacement key); one proof by the new key only (rejected:
  FR-002).

## R3. Retired-key memory and the switch

- **Decision**: A new table `socalytics.analyst_manager_key_rotation` (one row
  per completed rotation: registration, old and new thumbprints, the two proof
  jti hashes, claimed protection, `switched_at`, correlation) is the durable
  record. The registration row gets its `public_jwk`, `key_thumbprint`, and a
  new `key_rotation_count` changed in one UPDATE guarded by the registration's
  `version` and the expected old thumbprint (`SwitchKeyAsync`); the rotation row
  is inserted in the same `IUnitOfWork`. `UNIQUE (registration_id, new_thumbprint)`
  and a lookup index on old thumbprints give the "previously retired" check
  (FR-004) and the retry lookup (FR-011).
- **Rationale**: The `version` trigger already provides optimistic concurrency
  (FR-006); at most one of two racers matches the expected version/thumbprint.
  The domain entity changes `PublicJwk` and `KeyThumbprint` from immutable to
  changed only through `RotateKey`, which also requires `Active`.
- **Alternatives**: A JSON array of retired fingerprints on the registration
  (rejected: unindexable, grows unbounded in one row); deleting access tokens at
  the switch (rejected: the retry path needs the old-key token to authenticate
  once; refusal by `jkt` mismatch is already immediate).

## R4. Immediate refusal of the old key, with a bounded retry

- **Decision**: Existing authentication already refuses every access token
  whose `jkt` differs from the registration's `KeyThumbprint` and every client
  assertion not signed by the stored key, so the old key is refused with no
  grace (FR-007). The only exception is the retry: `AuthenticateManagerRequest`
  gains one narrow branch, evaluated only for the rotation request path, where
  a token bound to a retired thumbprint whose rotation row is within
  `Registry:KeyRotation:RetryWindow` and whose DPoP proof validates with that
  retired key yields a principal marked `RetiredKeyRetry`. The rotation handler
  accepts a `RetiredKeyRetry` principal only when both proof jti hashes equal
  the recorded rotation's; otherwise it refuses `retired-key`. All other
  endpoints do not honor the mark.
- **Rationale**: Delivers the clarified "same-proofs retry with the old key
  within the window" without weakening any other operation. The proof replay
  store would refuse the replayed jti, so the handler recognizes the retry by
  comparing the recorded hashes before recording. Within `RetryWindow` the already-switched path compares only the recorded jti hashes and the rotation row, with no expiry re-check of the proofs; `RetryWindow` must not exceed `ProofLifetime` (development defaults: 2 minutes each).
- **Alternatives**: Recovery only through the replacement key (rejected by the
  clarification: both paths are supported).

## R5. Queue credentials at the switch

- **Decision**: The switch transaction calls `IQueueCredentialStore.MarkRevokedAsync`
  exactly as revoke and unregister do, and the existing `IQueueAccessRevoker`
  pushes the NATS account revocations after commit. The Manager obtains new
  credentials through `issueAnalystManagerQueueCredentials` under the new key.
- **Rationale**: Reuses the implemented revocation path (FR-008, SC-002).
- **Alternatives**: Leaving credentials to expire (rejected: FR-008).

## R6. Audit

- **Decision**: Add `analyst-manager.key-rotation-requested`, `-succeeded`, and
  `-refused` to `RegistryAuditEvents`; add `oldKeyThumbprint`,
  `newKeyThumbprint`, and `rotationCount` to `AllowedDetailKeys`; stable
  refusal codes are listed in the contract. Outer authentication refusals are
  audited from `AnalystManagerDPoPAuthenticationHandler` only when the request
  is for the rotation path and the token or proof identifies a registration
  (token hash lookup or the thumbprint lookup that already exists); otherwise
  only ordinary logging.
- **Rationale**: Audit details are an allow-list, so key material cannot leak
  by construction (FR-012, SC-007).
- **Alternatives**: Auditing every outer-authentication failure (rejected by the
  clarification).

## R7. Rate limit

- **Decision**: New policy `registry-key-rotation` in `RegistryRateLimiting`,
  partitioned by registration ID (the claim) with
  `RegistryOptions.RateLimits.KeyRotationPerMinute` (development default 5),
  using the existing `analyst-manager-rate-limited` problem code. Because the
  limiter runs after authentication, the throttle is audited by an
  `OnRejected` branch that reads the registration claim (FR-023).
- **Rationale**: The existing limiter infrastructure already returns `429` with
  `Retry-After`; per-registration partitioning needs no new dependency.
- **Alternatives**: Per address (rejected: the clarification requires
  per registration).

## R8. Minimum-version independence

- **Decision**: The rotation handler does not call `ManagerVersionGate` (FR-021).
- **Rationale**: Rotation is a security action of the Manager's own
  registration; the gate only guards queue credentials and claims.

## R9. Manager-side rotation flow and recovery

- **Decision**: A new `KeyRotationCoordinator` in
  `SocAlytics.Analysts.Manager.Core/Registration` is run by the running Manager
  and served by a new local-control command `rotate-key` (CLI
  `socalytics-manager rotate-key`; the CLI refuses with an actionable message
  when no Manager process answers, FR-015). Sequence:
  1. Take a single-instance rotation lock; refuse when another runs or no
     Active registration exists.
  2. `IDeviceKeyProvider.CreateKeyAsync` in the same provider as the registered
     key; refuse when it cannot create a non-exportable key (FR-014).
  3. Write a **pending rotation** entry (replacement key reference and
     fingerprint) into the signed local registration file, still signed with
     the old key, atomically (temp file, flush, rename).
  4. Build both proofs (`ProofFactory`) and call the operation with the old
     key's token and DPoP.
  5. On success: write the new registration file signed with the new key
     (atomic replace), drop the pending entry, delete the old key, clear the
     token cache, and request fresh queue credentials.
  6. On a transport failure: retry within the window with the same proofs.
- **Recovery after a restart**: if the file carries a pending entry, the Manager
  first requests a token with the replacement key. Success means the platform
  switched: complete step 5. A refusal that is not a valid-key-proof refusal
  means it did not switch: request a token with the old key, delete the
  replacement key, drop the pending entry, and keep the identity (FR-017).
  Neither outcome clears the protected registration (FR-018): the existing
  inactive-state interpretation applies only after the platform answers a valid
  proof of the registered key.
- **Rationale**: A pending entry written before the request makes every crash
  point recoverable without re-registration, and only one key is ever deleted
  after the other is confirmed.
- **Alternatives**: Deleting the old key first (rejected: strands the Manager);
  operating on a stopped Manager's protected registration (rejected by the
  clarification).

## R10. Contracts and fixtures

- **Decision**: New versioned contract folder
  `contracts/analyst-manager/key-rotation/v1/` with a JSON Schema 2020-12 file,
  `fixtures.json`, request/response/problem examples (valid and `invalid/`),
  validated by a new test in `SocAlytics.Platform.Contracts.Tests` and
  consumed by the Manager tests through the existing contract copy
  (`src/analysts/manager/contracts`). The OpenAPI snapshot is refreshed with
  the documented command. The draft schema for this plan is in
  [contracts/key-rotation.md](contracts/key-rotation.md).
- **Rationale**: Follows the registration and queue-depth contract pattern
  (FR-019).

## R11. Rotation of the Manager's local-control surface and tests

- **Decision**: Platform behavior is tested in `Integration.Tests` (Docker:
  PostgreSQL, NATS) against the real handlers and API with the software key
  proof helpers already used by the registration tests; Manager behavior in
  `SocAlytics.Analysts.Manager.Tests` with the SoftHSM2 PKCS#11 token and a
  scripted platform; one Docker-backed end-to-end test in
  `SocAlytics.Platform.EndToEnd.Tests` covers a real Manager rotating against
  the AppHost-style stamp. Windows and macOS stores are verified manually, as
  for registration.
- **Rationale**: Matches Principle IV and the existing test locations.
