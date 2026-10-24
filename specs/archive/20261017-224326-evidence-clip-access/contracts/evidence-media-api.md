# Contract: Evidence Media API

Added to the API's OpenAPI document (`/openapi/v1.json`, snapshot `src/platform/SocAlytics.Platform.Api/openapi/v1.json`)
under the tag `Analysis evidence`, mapped through `MapMemberApi()` (policy `ActiveMember`, `SessionAntiforgeryFilter`)
under `/api/v1`, with the rate-limiter policy `analysis-evidence-media`.

## `POST /api/v1/analysis/runs/{runId}/evidence-media` — `resolveAnalysisEvidenceMedia`

Request body (exactly one of `fact` and `range`):

```json
{
  "fact": { "acceptedResultId": "…", "ordinal": 3, "contextBeforeMs": 5000, "contextAfterMs": 5000 },
  "retryFailed": false
}
```

```json
{ "range": { "fromMs": 120000, "toMs": 135000 } }
```

Response `200` (`Cache-Control: no-store`, no `ETag`):

```json
{
  "run": { "runId": "…", "matchId": "…", "teamId": "…" },
  "requested": { "fromMs": 295000, "toMs": 310040 },
  "fact": { "startMs": 300000, "endMs": 305040 },
  "shortened": false,
  "parts": [
    {
      "startMs": 295000,
      "endMs": 300000,
      "state": "available",
      "media": {
        "url": "https://…",
        "expiresAt": "2026-10-17T12:05:00.000Z",
        "mediaType": "video/mp4",
        "mediaDigest": "sha-256:…",
        "mediaSizeBytes": 1234567,
        "startOffsetMs": 295040,
        "endOffsetMs": 300040,
        "recordingVersionId": "…"
      }
    },
    { "startMs": 300000, "endMs": 309000, "state": "rematerializing", "retryAfterSeconds": 5 },
    { "startMs": 309000, "endMs": 310040, "state": "not-covered" }
  ]
}
```

- `state` is one of `available`, `materializing`, `rematerializing`, `failed`, `not-covered`; `reason` accompanies
  `failed`; `retryAfterSeconds` accompanies `materializing` and `rematerializing`; `media` accompanies `available` only.
- Parts are ordered by `startMs`, contiguous, non-overlapping, and cover `requested`.
- `fact` is present only for fact requests; `shortened` is `true` when the fact window with context exceeded
  `MaxDuration`, and then `requested.toMs − requested.fromMs` equals `MaxDuration`.
- No response contains object keys, source-recording locations, credentials other than the signed `url`, or segment
  numbers and policy digests.

## Problems

| Status | Code | When |
| --- | --- | --- |
| `400` | validation (field violations) | not exactly one of `fact` and `range`; negative or missing bounds; `toMs ≤ fromMs`; range longer than `MaxDuration`; context outside `0..MaxContext`; negative `ordinal` |
| `401` | — | no valid member session |
| `403` | anti-forgery | missing or invalid anti-forgery token, as for every unsafe member request |
| `404` | not found | run missing, caller not a Club Admin of the club or a Coach or Viewer of the owning team, or fact not part of the run (no disclosure) |
| `429` | `evidence-rate-limited` | more than `ResolutionsPerMinute` resolutions by the member in the current minute; `Retry-After` set |
| `503` | `evidence-storage-unavailable` | object storage could not confirm a media checksum or read a manifest; no link issued |

## Side effects

- Materialization demand is recorded for `absent` (and, with `retryFailed`, `failed`) identities, as described in
  [data-model.md](../data-model.md#part-state-mapping), in the resolution's unit of work.
- One security audit event `analysis.evidence-media.granted` is recorded when at least one link is issued.
- A failed integrity check hands the identity to the Segment Service's invalid-media handling.
