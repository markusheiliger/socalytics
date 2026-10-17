# Contract: Member Fact API

Both operations are added to the API's OpenAPI document (`/openapi/v1.json`,
snapshot `src/platform/SocAlytics.Platform.Api/openapi/v1.json`) under the tag
`Analysis facts`, mapped through `MapMemberApi()` under `/api/v1`.

## `GET /api/v1/analysis/runs/{runId}/fact-streams` — `listAnalysisRunFactStreams`

Response `200`:

```json
{
  "run": { "runId": "…", "teamId": "…", "matchId": "…", "state": "completed" },
  "items": [
    {
      "nodeKey": "detect-players",
      "capability": { "id": "player-detection", "version": "1.0.0" },
      "schemaVersion": "1.0.0",
      "outputType": "foundational-facts",
      "executionScope": "segment",
      "required": true,
      "factCount": 54,
      "provenance": {
        "workflow": { "id": "default-workflow", "version": "1" },
        "analystProfile": { "id": "…", "digest": "sha-256:…" },
        "imageDigest": "sha-256:…",
        "modelDigest": "sha-256:…"
      },
      "coverage": { "accepted": 18, "missing": 0, "failed": 0, "blocked": 0, "skipped": 0 }
    }
  ]
}
```

Not paginated (one entry per workflow node). Weak `ETag`; `If-None-Match` →
`304`.

## `GET /api/v1/analysis/runs/{runId}/facts` — `listAnalysisRunFacts`

Query parameters:

| Name | Required | Rules |
| --- | --- | --- |
| `capabilityId` | yes | common `key` |
| `capabilityVersion` | yes | semantic version |
| `schemaVersion` | yes | semantic version |
| `nodeKey` | only when ambiguous | node key of the run's workflow |
| `fromMs` | no | integer ≥ 0 |
| `toMs` | no | integer > `fromMs` (when both given) |
| `pageSize` | no | 1..`MaxPageSize` (default `DefaultPageSize`) |
| `continuationToken` | no | opaque; valid only for the same run and selection |

Response `200`:

```json
{
  "run": { "runId": "…", "teamId": "…", "matchId": "…", "state": "running" },
  "stream": {
    "nodeKey": "detect-players",
    "capability": { "id": "player-detection", "version": "1.0.0" },
    "schemaVersion": "1.0.0",
    "outputType": "foundational-facts",
    "executionScope": "segment",
    "provenance": { "workflow": { "id": "default-workflow", "version": "1" }, "analystProfile": { "id": "…", "digest": "sha-256:…" }, "imageDigest": "sha-256:…" }
  },
  "range": { "fromMs": 0, "toMs": 600000 },
  "items": [
    { "acceptedResultId": "…", "ordinal": 0, "kind": "player-detected", "startMs": 120000, "endMs": 120040, "confidence": 0.93, "payload": { } }
  ],
  "coverage": [
    { "startMs": 0, "endMs": 300000, "state": "accepted" },
    { "startMs": 300000, "endMs": 600000, "state": "missing" }
  ],
  "continuationToken": "…"
}
```

Items ordered by `startMs`, `endMs`, `acceptedResultId`, `ordinal`. Coverage is
returned in full (for the requested range) on every page. Weak `ETag`;
`If-None-Match` → `304`.

## Problems (both operations)

| Status | Code | When |
| --- | --- | --- |
| `400` | validation (field violations) | bad `pageSize`, range, or `continuationToken`; missing required parameter |
| `400` | `analysis-capability-ambiguous` | several nodes carry the capability version and no `nodeKey` |
| `401` | — | no valid member session |
| `404` | not found | run missing, or caller lacks team read access (no disclosure) |
| `404` | `analysis-capability-not-in-run` | capability, version, schema version, or node key not in the run's workflow |

No response contains storage locations, object names, segment, recording,
timeline-mapping, or segmentation-policy identifiers.
