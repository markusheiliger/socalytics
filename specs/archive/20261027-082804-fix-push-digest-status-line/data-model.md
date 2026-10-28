# Data Model: Push Stream Lines

The feature persists nothing. It interprets the engine's push response body: newline-separated JSON objects.

## Push stream line (input)

| Property | Type | Use |
| -------- | ---- | --- |
| `errorDetail` | object | Present on a failed push; any line with it fails the helper with `Pushing the Test Analyst image failed: <raw errorDetail>`. |
| `aux.Digest` | string | Structured digest record; when present it is returned as is. |
| `status` | string | Progress text; only the form `<tag>: digest: sha256:<64 lowercase hex> size: <n>` yields a digest. |

All other properties and lines (progress, layer status, non-JSON text) are ignored.

## Result

A string `sha256:<hex>`, or a thrown `InvalidOperationException`.

## Resolution rules

1. Any `errorDetail` line: throw the push-failure error (wins over any digest).
2. Else an `aux.Digest` value: return it, where `aux` counts only when it is a JSON object and `Digest` only when it is a JSON string (otherwise the line is ignored for this rule, without an exception); when several lines carry a string `aux.Digest`, the last one wins (as today).
3. Else a status-line digest matching `^\S+: digest: (sha256:[0-9a-f]{64}) size: \d+$`: return the captured `sha256:<hex>`.
4. Else throw `The push did not report a manifest digest:` followed by a newline and the body.
