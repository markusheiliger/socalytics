# Contract: Local Control: Active Attempts and Concurrency

This is a component-local contract of the Manager:
`src/analysts/manager/contracts/local-control.schema.json` gains the additions
below in a backward-compatible minor version.

## Status response additions

```json
{
  "concurrency": { "hostLocal": 2, "stampMaximum": 4, "runtimeMaximum": 3, "effective": 2 },
  "activeAttempts": [
    {
      "logicalJobId": "5f0d…",
      "attemptNumber": 1,
      "capability": "player-detection",
      "executionState": "running",
      "startedAt": "2026-10-12T14:20:01Z",
      "lastConfirmedHeartbeatAt": "2026-10-12T14:20:41Z"
    }
  ]
}
```

- `stampMaximum` and `runtimeMaximum` are `null` until they are known.
- `effective` is the lowest known bound.
- `lastConfirmedHeartbeatAt` is `null` before the first confirmed heartbeat.
- Fencing tokens, credentials, and result checksums never appear (FR-025).

## New command `concurrency`

| Request | Response |
| --- | --- |
| `{ "command": "concurrency" }` | Current `concurrency` object |
| `{ "command": "concurrency", "hostLocal": 3 }` | Updated `concurrency` object; persisted; effective for new pulls only |
| `hostLocal` < 1 or not an integer | Error `invalid-concurrency`; nothing changes |

## CLI

- `socalytics-manager concurrency [<n>]` shows the limit, or sets the
  host-local limit.
- `socalytics-manager status` lists the effective limit and the active
  attempts. `--json` returns the same objects as above.

## Status window

The status window shows the effective limit and one row per active attempt
with the fields above. The tray menu is unchanged.
