# Data Model: MCP Server

The server persists nothing. Every entity below lives in process memory only; results, arguments, and problem bodies
exist only while one call is handled (FR-013).

## Tool catalog entry (package data, generated)

Source: `contracts/agent-tools/tool-allowlist/v1/examples/valid/allowlist-1.0.0.json` and
`src/platform/SocAlytics.Platform.Api/openapi/v1.json` ([research R3](research.md#r3---tool-catalog-derived-from-the-allowlist-contract-and-the-api-description)).

| Field | Rules |
| --- | --- |
| `toolId` | Allowlist tool identifier (`^[a-z][a-z0-9_]{0,63}$`); the MCP tool name. Unique. |
| `operationId` | Stable platform operation identifier (`agentTool*`); must exist in the API description. |
| `method`, `pathTemplate` | `POST`, `/api/v1/agent-tools/attempts/{attemptId}/<tool-path>`; equal to the API description. |
| `answers` | `accepted-evidence` or `analysis-unavailable`; the tool description states it. |
| `producingCapability` | Capability identifier or `null`, as in the allowlist. |
| `inputSchema` | Self-contained JSON Schema 2020-12 of the operation's `arguments` component. |
| `outputSchema` | Self-contained JSON Schema 2020-12 of the operation's `200` response body `{ callId, toolId, outcome, result }`, whose `result` is the allowlist's `resultSchema` component. |

Catalog header: `allowlistVersion` (1.0.0), `allowlistSchemaVersion`, `allowlistDigest` and `openApiDigest`
(`sha-256:` of the source files). The catalog contains exactly the eleven allowlist entries, in allowlist order.

## Session

| Field | Rules |
| --- | --- |
| `sessionId` | MCP session identifier issued at `initialize`; logged only as a SHA-256 prefix. |
| `attemptId` | UUID from `SocAlytics-Attempt-Id`; immutable. |
| `fencingToken` | From `SocAlytics-Fencing-Token`; immutable; never logged. |
| `toolPolicy` | Tool identifiers of the pinned `tool-policy` artifact ∩ catalog; immutable. |
| `openedAt`, `lastActivityAt` | For the idle timeout. |

Lifecycle:

```text
initialize --runtime secret invalid--> refused 401 (no session)
initialize --binding headers missing or malformed--> refused 400 (no session)
initialize --session limit reached--> refused 503 (no session)
initialize --pinned-artifact read 404 / 409 / failure--> refused with the platform's answer (no session)
initialize --pinned-artifact read 200--> open
open --tools/list, tools/call--> open (lastActivityAt updated)
open --different binding headers--> request refused 400, session unchanged
open --DELETE | idle timeout | server stop--> closed
```

A platform stale disposition on a tool call does not close the session; later calls are still forwarded and receive the
platform's answer.

## Forwarded call

| Field | Rules |
| --- | --- |
| `callId` | `mcp-` + UUID4, generated per forwarded call; matches the platform's `^[A-Za-z0-9._\-]{1,128}$`. |
| `toolId` | In the catalog and the session's `toolPolicy`. |
| `arguments` | Valid against the tool's `inputSchema`; forwarded unchanged. |
| outcome | `answered`, `unavailable` (platform `200`), `platform-refused` (platform non-`200`), `not-permitted`, `invalid`, `platform-unavailable`, `platform-authentication-failed` (local). |

## Workload token (memory only)

`accessToken`, `expiresAt`; renewed when less than 30 seconds remain, after a `401` DPoP challenge (once per call), or
on a readiness probe without a valid token (at most once per 5 seconds).

## Configuration (environment variables and files)

| Variable | Required | Development value | Rules |
| --- | --- | --- | --- |
| `SOCALYTICS_MCP_PLATFORM_URL` | yes | AppHost or stamp URL of the API | Absolute `http` or `https` URL without query; where requests are sent. |
| `SOCALYTICS_MCP_PLATFORM_PUBLIC_BASE_URL` | no | = `PLATFORM_URL` | Absolute origin equal to the API's `Stamp:PublicBaseUri`; used for proof `htu` and assertion `aud`. |
| `SOCALYTICS_MCP_WORKLOAD_CLIENT_ID` | yes | `mcp-server` | Matches a configured, enabled workload client. |
| `SOCALYTICS_MCP_WORKLOAD_KEY_FILE` | yes | mounted file | ECDSA P-256 private key, PKCS#8 PEM. |
| `SOCALYTICS_MCP_RUNTIME_SECRET_FILE` | yes | mounted file | At least 32 bytes after trimming a final newline. |
| `SOCALYTICS_MCP_PORT` | no | 8080 | 1024–65535. |
| `SOCALYTICS_MCP_MAX_SESSIONS` | no | 32 | 1–1024. |
| `SOCALYTICS_MCP_SESSION_IDLE_TIMEOUT_SECONDS` | no | 300 | 10–3600. |
| `SOCALYTICS_MCP_PLATFORM_TIMEOUT_SECONDS` | no | 30 | 1–120. |
| `SOCALYTICS_MCP_LOG_LEVEL` | no | `info` | `debug`, `info`, `warning`, `error`; `debug` adds no content fields. |

Invalid configuration stops the server with exit code 2 and one line per invalid variable, naming it without its
value. Production values remain open.
