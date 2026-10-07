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
