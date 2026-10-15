# Contract: Registry Pull Credentials

`POST /api/v1/analyst-managers/self/registry-credentials`
(operation `issueAnalystManagerRegistryCredentials`), research R4.

Machine-readable form: a new JSON Schema family
`contracts/analyst-manager/registry-credentials/v1/` with examples, validated by
`SocAlytics.Platform.Contracts.Tests`, and the operation in the OpenAPI
snapshot `src/platform/SocAlytics.Platform.Api/openapi/v1.json`.

## Request

- DPoP-bound Analyst Manager access token (policy `AnalystManager`), HTTPS.
- Empty JSON body `{}`.

## Response `201 Created`

```json
{
  "registry": "ghcr.io",
  "repositories": ["<owner>/<repo>/test-analyst"],
  "kind": "bearer",
  "token": "<opaque registry token>",
  "expiresAt": "2026-10-15T09:35:00Z"
}
```

- `kind` is `bearer` or `anonymous`; `token` is present only for `bearer`.
- `repositories` are exactly the distinct image repositories of the resolved
  default workflow, all inside the stamp's `RepositoryPrefix`.
- `expiresAt` is at most `Registry:Images:PullCredentialLifetime` after
  issuance and never later than the registry token's own expiry.
- The token is never written to logs, audit records, or telemetry.

## Errors (problem details)

| Status | `type` suffix | When |
| --- | --- | --- |
| 401 | — | Missing or invalid DPoP-bound token. |
| 403 | `manager-not-active` | The registration is revoked, rejected, pending, or unregistered. |
| 409 | `workflow-not-configured` | No valid default workflow resolves. |
| 503 | `registry-unavailable` | The registry's token service cannot be reached or refuses the platform's credentials. |
