# Data Model: Docker Push Digest from the Status Line

No persisted data. The parser handles these in-memory shapes.

## Push progress line

One JSON object per non-empty line of the push response body.

| Property | Type | Meaning |
| --- | --- | --- |
| `status` | string, optional | Progress text; a digest report when it matches the status-line pattern. Absent or non-string values are ignored. |
| `aux` | object, optional | When it has a string `Digest`, that is the `aux` digest report. |
| `errorDetail` | object, optional | Push failure; fails the push. |

## Digest report

- **`aux` digest**: the `Digest` string as reported; the last one wins; authoritative.
- **Status digest**: group 1 of `^[A-Za-z0-9_][A-Za-z0-9_.-]*: digest: (sha256:[0-9a-f]{64}) size: [0-9]+\z`; the last matching line wins; used only without an `aux` digest.

## Result

`PushDigestReader.Read(body)` returns the `sha256:<64 lower-case hex>` digest (status form) or the `aux` digest, or throws `InvalidOperationException` (error entry, or no digest with the engine output in the message). There are no state transitions.
