# Contract: Result Write Grants API

`POST /api/v1/analysis/attempts/{attemptId}/result-grants` — Manager-facing,
authorization policy `AnalystManager` (DPoP-bound token), JSON only. Operation
id `issueAnalysisResultGrants`.

## Request

```json
{
  "fencingToken": 7,
  "objects": [
    { "name": "manifest.json", "sizeBytes": 2048, "digest": "sha-256:<64 hex>" },
    { "name": "facts.json", "sizeBytes": 10240, "digest": "sha-256:<64 hex>" }
  ]
}
```

## Response `200`

```json
{
  "resultLocation": "results/<runId>/<logicalJobId>/<attemptId>/",
  "grants": [
    {
      "name": "manifest.json",
      "method": "PUT",
      "url": "<presigned URL>",
      "requiredHeaders": { "Content-Length": "2048", "x-amz-checksum-sha256": "<base64>" },
      "expiresAt": "2026-10-13T13:20:00Z"
    }
  ]
}
```

`Cache-Control: no-store`. Grant URLs and headers are never logged or audited.

## Refusals

| Status and code | When | Audited |
| --- | --- | --- |
| `400 result-objects-invalid` (field errors) | Invalid or duplicate names, invalid digest format, size 0 or above the limits, too many objects or bytes | No |
| `401` | Missing or invalid Manager authentication, or a revoked or otherwise ended registration (the existing authentication admits only `Active` and `Unregistering`) | Existing authentication audit |
| `403` | The attempt belongs to another Manager | Yes, `analysis.result-grant-refused` (`foreign-attempt`) |
| `404` | Unknown attempt | No |
| `409 attempt-obsolete` | Fencing token not current, attempt fenced or completed | No |
| `409 result-object-redeclared` | A declared name with a different digest | No |
| `503 object-storage-unavailable` | Storage cannot presign | No |

## Fixtures

Golden request and response examples under
`contracts/analysis/result-grants/v1/` (request, response, invalid examples),
validated by `SocAlytics.Platform.Contracts.Tests` and the Manager tests.
