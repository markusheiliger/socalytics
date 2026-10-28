# Data Model: Analyst Manager Device Key Rotation

## Registration (`socalytics.analyst_manager_registration`, changed)

| Field | Change | Notes |
| ------- | -------- | ------- |
| `public_jwk` | now changes only through `RotateKey` | the current device public key |
| `key_thumbprint` | now changes only through `RotateKey` | the current fingerprint (`jkt`) shown by the fleet read |
| `key_rotation_count` | new, `integer NOT NULL DEFAULT 0` | number of completed rotations |
| `version` | unchanged | the `version` trigger gives optimistic concurrency |

The identity (`id`, `manager_id`), stamp, label, state, and status history do not
change. Domain rule `AnalystManagerRegistration.RotateKey(newJwk, newThumbprint, now)`
requires `State == Active` and `newThumbprint != KeyThumbprint`; otherwise it returns
`invalid-state-transition` or `rotation-not-a-replacement`; the handler maps these domain
results to the contract codes `key-rotation-not-active` and `key-rotation-not-a-replacement`.

## Key rotation record (`socalytics.analyst_manager_key_rotation`, new)

One row per completed rotation, written in the switch's unit of work.

| Column | Type | Notes |
| -------- | ------ | ------- |
| `id` | `uuid` PK | |
| `registration_id` | `uuid NOT NULL` FK to the registration | |
| `sequence` | `integer NOT NULL` | 1-based, `UNIQUE (registration_id, sequence)` |
| `old_thumbprint` | `text NOT NULL` | indexed; retired key fingerprint |
| `new_thumbprint` | `text NOT NULL` | `UNIQUE (registration_id, new_thumbprint)` |
| `old_proof_jti_hash` | `bytea NOT NULL` | SHA-256 of the old-key proof `jti`; never the proof |
| `new_proof_jti_hash` | `bytea NOT NULL` | SHA-256 of the replacement-key proof `jti` |
| `claimed_key_protection` | `text NOT NULL` | recorded as claimed only |
| `correlation_id` | `text NOT NULL` | |
| `switched_at` | `timestamptz NOT NULL` | |

No public key, proof, or token is stored. The row is immutable (no `version`); it
gets an entry in `PersistedTableClassifications`. Migration
`0033_registry_analyst_manager_key_rotation.sql` (next free number at implementation
time) adds the column and the table.

### Derived rules

- **Retired**: `new_thumbprint` is acceptable only if it is not the registration's
  current thumbprint, not any `old_thumbprint` of this registration (`key-previously-retired`),
  and not the thumbprint of any other registration or any other registration's
  retired key (`key-rotation-replacement-unavailable`, the same answer for all of them so no
  other registration is revealed).
- **Retry window**: the latest rotation row of the registration with
  `switched_at + RetryWindow > now` and equal proof jti hashes.

## Key proof (transient)

Compact JWS client assertion, never stored; only the `jti` hash goes to
`analyst_manager_proof_replay` (kinds `key_rotation_old`, `key_rotation_new`) with the
proof's expiry.

| Claim | Old-key proof | Replacement-key proof |
| ------- | --------------- | ----------------------- |
| `iss`, `sub` | Manager ID | Manager ID |
| `aud` | rotation URI | rotation URI |
| `jti`, `iat`, `exp` | required | required |
| binding | `new_jkt` = replacement thumbprint | `old_jkt` = registered thumbprint |
| signer | registered key | replacement key (JWK in the request body) |

## Rotation outcome (response)

`outcome` (`switched` | `already-switched`), `managerId`, `oldKeyThumbprint`,
`newKeyThumbprint`, `rotationCount`, `switchedAt`. Refusals are problem responses
(see the [contract](contracts/key-rotation.md)).

## Rotation audit events (existing audit store)

`analyst-manager.key-rotation-requested`, `analyst-manager.key-rotation-succeeded`,
`analyst-manager.key-rotation-refused`; details from the allow-list only:
`managerId`, `stampId`, `oldKeyThumbprint`, `newKeyThumbprint`, `rotationCount`,
`claimedKeyProtection`, `registrationCorrelationId`; refusals carry the stable reason code.

### Audit reasons (stable)

| Reason | Source |
| -------- | -------- |
| every `code` of the [contract](contracts/key-rotation.md) refusals table | refusals of the operation itself |
| `key-rotation-retired-key` | token bound to a retired key outside the same-proofs retry (401) |
| `key-rotation-token-key-unknown` | outer authentication: token bound to an unknown key |
| `key-rotation-dpop-invalid` | outer authentication: DPoP proof invalid |
| `analyst-manager-rate-limited` | throttled request |

Requests tied to no registration produce no audit event.

## Local registration file (Manager, changed)

`ProtectedRegistrationState` gains an optional `pendingRotation`
(`replacementKeyReference`, `replacementFingerprint`, `startedAt`), written
atomically before the request and removed when the rotation is confirmed or
abandoned. The file is signed with the key registered at the time of writing
(old key while pending; replacement key after the switch).

### Manager rotation state machine

```text
Idle -> ReplacementCreated -> PendingRecorded -> Requested
Requested -> Switched -> LocalUpdated -> OldKeyRemoved -> Idle
Requested -> NotSwitched (refused or never arrived) -> ReplacementRemoved -> Idle
(restart in PendingRecorded|Requested) -> Probe with replacement key
    accepted -> Switched ...      refused -> NotSwitched ...
```
