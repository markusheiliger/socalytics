# Contract: Update Offer

## Operation

`GET /api/v1/analyst-managers/self/update-offer` (`getAnalystManagerUpdateOffer`)

- Authentication: the Analyst Manager DPoP machine authentication (policy `AnalystManager`), HTTPS only,
  `Cache-Control: no-store`. No query, no body. Human sessions and other token kinds are refused as for the other
  Manager self operations.
- Not refused for a Manager version below the stamp minimum.

## Responses

| Status | Meaning |
| --- | --- |
| `200` | The offer or the statement that none is available (below). |
| `401` | No valid token or proof, or an unknown registration (existing problem vocabulary). |
| `403` | Registration not active (`manager-not-active`) or token of another audience or scope. |
| `503` | `update-feed-unavailable`: the release source failed and no verified release is still cached. |

## 200 body

```json
{
  "observedAt": "2026-10-23T08:00:00Z",
  "channel": "preview",
  "available": true,
  "release": {
    "version": "0.2.0",
    "channel": "stable",
    "releaseNotesUrl": "https://example.invalid/releases/0.2.0",
    "packages": [
      { "name": "socalytics-manager-0.2.0-linux-x64.AppImage", "sizeBytes": 73400320, "sha256": "<64 lowercase hex>" }
    ]
  }
}
```

An unavailable answer has `"available": false`, no `release`, and `reason` set to one of `no-release-in-channel`,
`no-verified-release`, `no-release-satisfies-minimum`, `no-release-for-pin`.

Rules the schema and tests enforce:

- `additionalProperties: false` everywhere; `release` present exactly when `available` is `true`, `reason` present
  exactly when it is `false`.
- `version` matches `MAJOR.MINOR.PATCH`; the stamp `channel` and the release `channel` are `stable` or `preview`; the
  release channel may be `stable` while the stamp channel is `preview`.
- Every package has `name`, `sizeBytes` (integer at least 1), and `sha256` (64 lowercase hex characters); at least one
  package; all packages of the release are listed regardless of the Manager's platform.
- `releaseNotesUrl` is data only (`https` URI); the platform never fetches it.
- No registration data, credentials, or other stamp configuration appears.

## Local-control status addition (Manager)

`status.updateAvailable` (optional, present only while the offer names a newer release than the build):
`{ "offeredVersion": "<MAJOR.MINOR.PATCH>", "managerVersion": "<build>", "channel": "stable|preview" }`. It is
independent of the existing `status.update` (minimum notice); both may be present.

## Golden fixtures

Valid: available offer (stable channel), available offer (preview channel serving a stable release), each reason
class. Invalid: unavailable with a `release`, available without `release`, malformed version, package without hash,
non-hex hash, unknown property, unknown reason.
