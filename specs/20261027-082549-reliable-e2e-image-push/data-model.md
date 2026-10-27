# Data Model: Reliable End-to-End Analyst Image Push

In-memory test support types only; nothing is persisted and no product data model changes.

## PushedAnalystImage (existing)

`Repository`, `Digest` (`sha256:` plus 64 lowercase hex), `LocalName`; `Reference(registryHost)` is
`<host>/<repository>@<digest>`. Unchanged.

## PushResponse (input of the digest determination)

| Field | Meaning |
| --- | --- |
| `Body` | The full newline-delimited JSON push response |
| `Repository`, `Tag` | The pushed repository (without registry host where a source needs it) and tag |

Lines: progress (`status`, `id`, `progressDetail`), error (`errorDetail`, `error`), record (`aux.Digest`), final status
(`<tag>: digest: sha256:<64 hex> size: <n>`).

## DigestSource and DigestResult

- Sources in order: `ProgressRecord`, `StatusLine`, `RegistryManifest`, `EngineImageRecord`.
- `DigestResult`: the chosen digest and its source, or a failure carrying the body and the outcome of every tried
  source (`found`, `absent`, `unavailable: <reason>`).
- Rules: a digest is valid only as `^sha256:[0-9a-f]{64}$`; the status line counts only for the pushed tag; the first
  valid source ends the determination except that the record and the status line, both read from the response, must
  agree; the registry and the engine are consulted only when the response yielded none and must agree when both were
  consulted; an error line fails the push before any digest.

## EngineCallPolicy

| Name | Value | Meaning |
| --- | --- | --- |
| `PushLimit` | 15 minutes | Time limit of one push attempt |
| `BuildLimit` | 20 minutes | Time limit of one build attempt |
| `RegistryLookupLimit` | 30 seconds | Time limit of one registry or image-record lookup |
| `MaxAttempts` | 3 | Attempts of a push or build including the first |
| `RetryDelay` | 2 seconds | Bounded delay between attempts (via `TimeProvider`) |

Outcome of an attempt: `Succeeded`, `Transient(reason)`, `Deterministic(exception)`, `Cancelled`. After the last
transient failure the final exception names the limit or condition and lists every attempt's reason.

## ImageBuildGate

One process-wide gate with `MaxConcurrentBuildsAndPushes` (1). States: `Free` ↔ `Held` per acquirer; acquired with the
test's token, released in `finally` on success, failure, and cancellation.

## AnalystImageCache entry

Key: (helper, mode or variant, inputs). State machine:

```text
absent --first request--> building --success--> ready   (served to every later test of the class)
                              |
                              +--failure--> faulted for the waiting tests --> removed (a later test may retry)
```

Per test class lifetime: created with the class fixture, images removed and the class's registry disposed when the
class ends. An entry is never served a variant or inputs it was not built for.
