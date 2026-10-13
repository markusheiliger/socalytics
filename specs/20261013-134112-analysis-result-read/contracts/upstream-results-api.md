# Contract: Upstream Results Read API

`GET /api/v1/analysis/attempts/{attemptId}/upstream-results` — Manager-facing,
policy `AnalystManager` (DPoP-bound token). Operation id
`listAnalysisUpstreamResults`. `Cache-Control: no-store`.

## Query

See [data-model.md § Read query](../data-model.md#read-query).

## Response `200`

The envelope `contracts/analysis/upstream-results/v1/` (research.md § R4):

```json
{
  "contractVersion": "1.0.0",
  "upstream": { "capability": { "id": "dev-segment-capability", "version": "1" }, "schemaVersion": "1", "edgeKind": "required" },
  "results": [
    {
      "acceptedResultId": "…",
      "scope": { "segment": { "segmentId": "…", "windowStartMs": 0, "windowEndMs": 300000 } },
      "manifestChecksum": "sha-256:…",
      "manifest": { "contractVersion": "1.0.0", "…": "result-manifest v1 object" },
      "grants": [
        { "name": "manifest.json", "url": "<presigned GET>", "expiresAt": "…" },
        { "name": "facts.json", "url": "<presigned GET>", "expiresAt": "…" }
      ]
    }
  ],
  "coverage": [ { "segmentId": "…", "state": "accepted" } ],
  "continuationToken": null
}
```

## Refusals

| Status and code | When | Audited |
| --- | --- | --- |
| `400` (field errors) | Invalid query fields, both segment list and range | No |
| `400 upstream-dependency-unknown` | Capability or schema version not an upstream dependency of the job | Yes, `analysis.upstream-read-refused` (`out-of-scope`) |
| `401` | Authentication failed, or revoked or ended registration | Existing authentication audit |
| `403` | Attempt owned by another Manager | Yes, `analysis.upstream-read-refused` (`foreign-attempt`) |
| `404` | Unknown attempt | No |
| `409 attempt-obsolete` | Fencing token not current, attempt fenced or completed | No |
| `503 object-storage-unavailable` | Storage cannot presign or verify the manifest | No |

Grant URLs never appear in logs or audit events.
