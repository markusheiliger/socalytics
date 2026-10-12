# Analyst Manager Proof Profiles

The Manager signs three kinds of compact JWS with its registered, non-exportable
ECDSA P-256 device key. The platform accepts only `alg` `ES256` and validates
each profile exactly as listed. Every `jti` is single-use per kind
(`analyst_manager_proof_replay`). Clock checks use the platform clock with the
configured skew (non-production default 60 s). Endpoint URIs are built from
`Stamp:PublicBaseUri`, never from request headers. Operations are defined in
[openapi.yaml](openapi.yaml).

## Activation proof

Sent as `proof` to `POST /api/v1/analyst-managers/pairings/activation`.

| Part | Requirement |
| --- | --- |
| Header `typ` | `analyst-manager-activation+jwt` |
| Header `alg` | `ES256` |
| Header `jwk` | Must be absent; the key is the one bound at pairing |
| Claim `aud` | Activation endpoint URI |
| Claim `challenge` | The latest challenge from the status poll; hash must match and be unexpired |
| Claim `iat` | Within skew of platform time |
| Claim `jti` | Unique (kind `activation`) |
| Signature | Verifies with the registration's stored public JWK |

Any attempt consumes the challenge. Refusals return
`analyst-manager-activation-proof-invalid` and audit
`activation_proof_refused`.

## Client assertion (`private_key_jwt`, RFC 7523)

Sent as `client_assertion` to `POST /api/v1/analyst-managers/token`.

| Part | Requirement |
| --- | --- |
| Header `alg` | `ES256` |
| Claims `iss`, `sub` | Manager identity (`client_id`) |
| Claim `aud` | Token endpoint URI (single string) |
| Claims `iat`, `exp` | `exp > now`, `exp - iat` ≤ `Registry:Tokens:MaxAssertionLifetime` |
| Claim `jti` | Unique (kind `client_assertion`) |
| Signature | Verifies with the registration's stored public JWK |

## DPoP proof (RFC 9449)

Sent in the `DPoP` header to the token endpoint and every
`/api/v1/analyst-managers/self` operation.

| Part | Requirement |
| --- | --- |
| Header `typ` | `dpop+jwt` |
| Header `alg` | `ES256` |
| Header `jwk` | Public JWK whose RFC 7638 thumbprint equals the registration `key_thumbprint` |
| Claim `htm` | Request method |
| Claim `htu` | `Stamp:PublicBaseUri` plus request path, without query or fragment |
| Claim `iat` | Within skew of platform time |
| Claim `jti` | Unique (kind `dpop`) |
| Claim `ath` | Resource requests only: base64url SHA-256 of the access token |

## Outcomes

- Token endpoint refusals use RFC 6749/9449 errors. When the assertion and DPoP
  proof are valid but the registration is `Revoked` or `Unregistered`, the
  response is `invalid_client` with `registration_state`.
- Resource refusals return `401` with `WWW-Authenticate: DPoP` and, for an
  inactive registration proven by a valid proof, problem type
  `analyst-manager-registration-inactive`.
- No proof, assertion, token, challenge, or handle value is logged, audited, or
  echoed (FR-034).

## Device fingerprint

The fingerprint lets a Registrar confirm that the pairing code belongs to the
Manager in front of them (FR-040, RFC 8628 §5.4). It is derived from the RFC
7638 SHA-256 thumbprint `T` (32 bytes) of the device public JWK:

1. Read `T[0..7]` as an unsigned big-endian 64-bit integer `n`.
2. `n = n mod 20^8`.
3. Write `n` as eight base-20 digits, most significant first, using the
   alphabet `BCDFGHJKLMNPQRSTVWXZ` (digit 0 = `B`), grouped `XXXX-XXXX`.

Comparison is case-insensitive and ignores `-` and spaces. The fingerprint is
public, not a credential; it is checked in constant time against the stored
value. The Manager and the platform compute it independently, and the pairing
response carries the platform's value so the Manager can detect a mismatch.

## Golden fixtures

Every profile above has positive and negative reference vectors in
repository-root `contracts/analyst-manager/registration/v1/` (FR-039): the
manifest `fixtures.json`, described by `registration.schema.json` (`$id`
`https://socalytics.invalid/contracts/analyst-manager/registration/v1/registration.schema.json`),
lists every vector file with its expected outcome. The vectors are signed
with the public RFC 7515 Appendix A.3 P-256 example key at the fixed clock
`2026-01-01T00:00:00Z`, stamp `fixture-stamp`, and base URI
`https://stamp.example.test`. Negative vectors cover a replayed `jti`, a wrong
`aud`, a wrong key, a stale `iat`, a wrong `htu`, and a missing `ath`; a
fingerprint vector gives the test key's thumbprint and fingerprint, and a
submission vector pairs a code with a wrong fingerprint. The
platform accepts or refuses each vector as its manifest states; the Manager
produces requests whose headers and claims match the vectors (except `jti` and
the signature). See [research.md](../research.md#r23-shared-golden-fixtures).
