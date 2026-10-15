# Contract: Workflow Capabilities for the Manager's Offer

`GET /api/v1/analyst-managers/self/workflow-capabilities`
(operation `getAnalystManagerWorkflowCapabilities`), research R5.

Machine-readable form: a new JSON Schema family
`contracts/analyst-manager/workflow-capabilities/v1/` with examples, validated
by `SocAlytics.Platform.Contracts.Tests`, and the operation in the OpenAPI
snapshot.

## Request

- DPoP-bound Analyst Manager access token (policy `AnalystManager`), HTTPS.

## Response `200 OK`

```json
{
  "workflow": { "id": "default", "version": "3" },
  "capabilities": [
    {
      "id": "test-analyst",
      "version": "1.0.0",
      "accelerators": [],
      "cpus": "1",
      "memoryBytes": 536870912
    }
  ]
}
```

- One entry per distinct `(id, version)` of the resolved default workflow's
  nodes, with the pinned requirements of its approved Analyst profile.
- `cpus` is a decimal string (the contracts carry no floating-point numbers).
- The response carries no image or model digests and no registry details;
  jobs pin those.

## Errors (problem details)

| Status | `type` suffix | When |
| --- | --- | --- |
| 401 | — | Missing or invalid DPoP-bound token. |
| 403 | `manager-not-active` | The registration is not Active. |
| 409 | `workflow-not-configured` | No valid default workflow resolves. |
| 503 | `registry-unavailable` | The Analyst Registry cannot be read. |
