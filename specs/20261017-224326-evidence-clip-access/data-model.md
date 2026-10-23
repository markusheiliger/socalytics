# Data Model: Evidence Clip Access

This feature adds no table and no migration. It reads the run, its recording-set lineage, indexed facts, and
materialized-segment records, records materialization demand through the Segment Service of
[Materialized Video Segments](../archive/20261017-222433-materialized-segments/data-model.md), and writes one security audit
event per resolution that issues links. Links are never persisted.

## Evidence Request (API input)

| Field | Type | Rules |
| --- | --- | --- |
| `fact.acceptedResultId` | uuid | With `fact.ordinal`, identifies one indexed fact of the run; otherwise `404` |
| `fact.ordinal` | integer ≥ 0 | Position of the fact in its accepted result |
| `fact.contextBeforeMs` | integer | Optional, default `0`, `0`..`MaxContext` |
| `fact.contextAfterMs` | integer | Optional, default `0`, `0`..`MaxContext` |
| `range.fromMs` | integer ≥ 0 | Inclusive match-time start |
| `range.toMs` | integer | Exclusive end, `> fromMs`, `toMs − fromMs ≤ MaxDuration` |
| `retryFailed` | boolean | Optional, default `false`; requests materialization again for `failed` parts |

Exactly one of `fact` and `range` is present; otherwise the request is a validation problem.

## Requested Window (derived)

- For a fact: `[max(0, startMs − contextBeforeMs), endMs + contextAfterMs)`; when longer than `MaxDuration`, it becomes
  `[requestedStart, requestedStart + MaxDuration)` and `shortened = true`. The fact's own window is returned as
  `fact { startMs, endMs }`.
- For a range: the range itself; `shortened = false`.

## Evidence Part (read model)

| Field | Rule |
| --- | --- |
| `startMs`, `endMs` | Half-open match-time window; parts are ordered, contiguous, and non-overlapping, and cover the requested window |
| `state` | `available`, `materializing`, `rematerializing`, `failed`, or `not-covered` |
| `reason` | Only for `failed`: the Segment Service's sanitized reason (for example `extraction-failed`, `segment-media-invalid`, `retry-budget-exhausted`) |
| `retryAfterSeconds` | Only for `materializing` and `rematerializing` |
| `media` | Only for `available`: see Media Piece |

Part boundaries: nominal segment boundaries `(n−1)·D`, mapped-span boundaries of the assigned recording, the
boundaries of each recording's coverage, and the requested window's ends. A part belongs to one materialized identity
`(recordingVersionId, timelineMappingDigest, segmentationPolicyDigest, segmentNumber)` and one mapped span, or to no
recording (`not-covered`).

### Part state mapping

| Materialized-segment record | Effect | Part state |
| --- | --- | --- |
| missing, or `absent` without purge time | demand recorded | `materializing` |
| `absent` with purge time | demand recorded | `rematerializing` |
| `materializing` | none | `materializing`, or `rematerializing` when the record has a purge time |
| `failed` | none; with `retryFailed` demand recorded | `failed`; with `retryFailed` `materializing` |
| `available`, checks pass | link issued | `available` |
| `available`, checks fail | invalid-media handling (record `failed`, objects deleted) | `failed` (`segment-media-invalid`) |
| demand rejected before publication | none | `failed` with the rejection reason |

## Media Piece (read model, only in the response)

| Field | Rule |
| --- | --- |
| `url` | Presigned `GET` of the segment's media object; expires at `expiresAt`; never stored or logged |
| `expiresAt` | `now + GrantLifetime` (UTC) |
| `mediaType` | From the segment manifest (`video/mp4`) |
| `mediaDigest` | `sha-256:<hex>`, equal to the record's and the manifest's media digest |
| `mediaSizeBytes` | Equal to the record's media size |
| `startOffsetMs`, `endOffsetMs` | Offsets on the media's playback timeline (origin: the encoded interval's first packet) that correspond to the part's window; `endOffsetMs > startOffsetMs` |
| `recordingVersionId` | The recording version whose segment covers the part |

Offset computation for match time `t` within the manifest span `s`:
`offset(t) = s.mediaStartMs + (t − s.matchStartMs) − encodedInterval.startMs`.

## Evidence Resolution (response)

`run` (`runId`, `matchId`, `teamId`), `requested` (`fromMs`, `toMs`), `fact` (`startMs`, `endMs`, only for fact
requests), `shortened`, and `parts` (ordered Evidence Parts).

## Evidence Access Audit Event

Security audit event `analysis.evidence-media.granted`, recorded in the resolution's unit of work only when at least
one link is issued:

| Field | Content |
| --- | --- |
| actor | The member account |
| scope | Club, team, match, run |
| request | `fact` (`acceptedResultId`, `ordinal`) or `range`; the requested window; `shortened` |
| granted | The media digests of the granted parts and the latest `expiresAt` |
| excluded | Links, object keys, and signature parameters |

## Options (`Analysis:Evidence`)

| Key | Development value | Validation |
| --- | --- | --- |
| `GrantLifetime` | `00:05:00` | `00:00:30`..`01:00:00` |
| `RetryAfter` | `00:00:05` | `00:00:01`..`00:05:00` |
| `ResolutionsPerMinute` | `60` | `1`..`10000` |
| `MaxDuration` | `00:10:00` | `00:00:01`..`01:00:00` |
| `MaxContext` | `00:00:10` | `00:00:00`..`00:01:00` |

Production values are unresolved.

## Web UI playback state (memory only)

| State | Meaning |
| --- | --- |
| `resolving` | First resolution in progress |
| `playing` | A piece plays; `insideFact` tells whether the position lies in the fact's window |
| `preparing` | The next part is `materializing` or `rematerializing`; automatic re-resolution after the hint, at most 20 times |
| `partUnavailable` | The current part is `failed` or `not-covered`; names its window and reason; continues with the next playable part |
| `shortened` | Notice that the evidence was cut to the maximum duration |
| `renewing` | A link expired or media loading failed; re-resolution and resume at the current match time |
| `notFound` | The platform refused the run or fact (`404`) |
| `rateLimited` | Too many resolutions; shows the waiting time before "Try again" |
| `offline` | No connection |
| `error` | Recoverable error with a retry action, including storage unavailable (`503`) |
