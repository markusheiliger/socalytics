# Contract: Analyst Manager key rotation (draft for `contracts/analyst-manager/key-rotation/v1/`)

The executable contract is created by the implementation under the repository-root
`contracts/` folder (JSON Schema 2020-12 `key-rotation.schema.json`, `fixtures.json`,
valid and `invalid/` examples), validated by `SocAlytics.Platform.Contracts.Tests`
and shared with the Manager tests. This document fixes its content.

## Operation

`POST /api/v1/analyst-managers/self/key-rotation` — `rotateAnalystManagerKey`, tag
`Analyst Manager self`. Machine authentication only (access token plus DPoP proof of the
registered key); `Cache-Control: no-store`; `application/json` only; HTTPS only.

### Request body

| Field | Type | Rules |
| ------- | ------ | ------- |
| `replacementPublicJwk` | object | EC P-256 public JWK, no private members |
| `claimedKeyProtection` | string | same vocabulary as registration; recorded as claimed only |
| `oldKeyProof` | string | compact JWS, signed by the registered key, claims in the data model |
| `replacementKeyProof` | string | compact JWS, signed by the replacement key |

### Success `200`

```json
{
  "outcome": "switched",
  "managerId": "fixture-manager-0001",
  "oldKeyThumbprint": "<jkt>",
  "newKeyThumbprint": "<jkt>",
  "rotationCount": 1,
  "switchedAt": "2026-01-01T00:00:00Z"
}
```

`outcome` is `already-switched` for a retry of a rotation within
`Registry:KeyRotation:RetryWindow` with the same proofs, authenticated with the old key;
the key is not switched again.

### Refusals (problem responses, stable `code`)

| Status | `code` | Cause |
| -------- | -------- | ------- |
| 400 | `key-rotation-old-proof-invalid` | missing, wrong signer, expired, wrong audience, bad binding |
| 400 | `key-rotation-replacement-proof-invalid` | same, for the replacement key |
| 400 | `key-rotation-proof-replayed` | `jti` of either proof already used |
| 400 | `key-rotation-not-a-replacement` | replacement equals the registered key |
| 400 | `key-rotation-key-previously-retired` | key retired by an earlier rotation of this registration |
| 400 | `key-rotation-replacement-unavailable` | key of any other registration (no detail) |
| 400 | `key-rotation-key-type-unsupported` | not an accepted key type |
| 403 | `key-rotation-software-key-refused` | software-backed claim on a stamp that forbids it |
| 409 | `key-rotation-not-active` | registration not Active |
| 409 | `key-rotation-stale` | lost an optimistic-concurrency race |
| 401 | `key-rotation-retired-key` | retired key used outside the same-proofs retry |
| 429 | `analyst-manager-rate-limited` | per-registration limit (`Retry-After` set) |

### Audit reasons

Every code above is also the stable audit reason of the refusal. Refusals of the outer
machine authentication on the rotation path that identify a registration add the reasons
`key-rotation-token-key-unknown` (token bound to an unknown key) and
`key-rotation-dpop-invalid` (DPoP proof invalid); a token bound to a retired key outside the
retry is audited as `key-rotation-retired-key`. See the audit-reason table in the
[data model](../data-model.md).

Responses never contain key material, assertions, or tokens beyond thumbprints.

## Fixtures

`fixtures.json` follows the registration fixture shape (`vectors` with `exchange`, `file`,
`seedState`, `expected`): success, retry, and one vector per refusal code above, using the
registration contract's test key plus a second fixed test key for the replacement.
