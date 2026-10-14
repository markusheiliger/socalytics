# Contract: Registration Adapter (addition)

Extends the foundation's
[platform-adapter.md](../../archive/20261012-150657-web-ui-foundation/contracts/platform-adapter.md).

## Adapter

| Operation | OpenAPI operation id | Returns |
| --- | --- | --- |
| `submitPairingCode(code, fingerprint)` | `submitAnalystManagerPairingCode` | `Registration` |
| `list({ state?, continuationToken? })` | `listAnalystManagerRegistrations` | `Page<Registration>` |
| `listAll({ state? })` | same, following `continuationToken` until done | `Registration[]` |
| `get(id)` | `getAnalystManagerRegistration` | `Registration` |
| `approve(id)` | `approveAnalystManagerRegistration` | `Registration` |
| `reject(id, reason)` | `rejectAnalystManagerRegistration` | `Registration` |
| `revoke(id, reason)` | `revokeAnalystManagerRegistration` | `Registration` |

Wire values are mapped to the client model of [data-model.md](../data-model.md);
unknown values become `unknown` (FR-014).

## Error mapping additions (`PlatformFailure`)

| Platform response | `PlatformFailure.kind` | Web UI behavior |
| --- | --- | --- |
| `400 analyst-manager-pairing-code-invalid`, `404` on submission | `pairingRefused` | One generic message (FR-004) |
| `429 analyst-manager-rate-limited` | `rateLimited` (with `retryAfterSeconds` from `Retry-After`) | "Too many attempts", wait time |
| `409 invalid-state-transition`, `409 analyst-manager-registration-expired` | `decisionConflict` | Show the refusal, reload the registration |
| `400` with field `reason` | `validation` | Reason shown next to the field |
| `400` with field `state` or `continuationToken` | `programmingError` | Generic error; covered by unit tests |

The foundation's mappings (`sessionEnded`, `notPermitted`, `unreachable`,
`unexpected`, `incompatible`) apply unchanged.
