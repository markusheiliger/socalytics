# Data Model: Hermes Agent Runtime

Hermes persists nothing. Every entity below lives in process memory for the life of one attempt or of the process
(FR-002). The only persisted changes are the platform's: the completion contract's release 1.1.0 and the values it
stores in existing `jsonb` columns (no migration).

## Attempt execution (memory, one per claimed attempt)

| Field | Rules |
| --- | --- |
| `envelope` | Claim response, valid against `execution-envelope` 1.1.0; must carry `agent` pins. |
| `attemptId`, `fencingToken`, `leaseExpiresAt`, `deadlineAt` | From the envelope; the lease expiry is updated by renewals; the fencing token is never logged. |
| `artifacts` | The pinned-artifact read's agent version, prompt template, model selection, tool policy, and safety policy, each verified (R5). |
| `resolvedModel` | `provider`, `model`, `deployment`, `parameters` from the pinned model selection. |
| `session` | The attempt's single MCP session and its listed tools ∩ pinned tool policy. |
| `messages` | Rendered system context, history, question, model and tool messages; discarded at the end. |
| `turns` | Count of model turns; bounded by `MAX_MODEL_TURNS`. |
| `checkpointSequence` | Next checkpoint number for operation identifiers. |
| `state` | See lifecycle. |

Lifecycle:

```text
claimed --envelope invalid or unpinned--> failing(invalid-artifact)
claimed --pinned read 200, artifacts valid--> resolved
claimed --pinned read 404 / 409 stale--> abandoned (no report)
resolved --provider mismatch / unsupported route / incompatible--> failing(<reason>)
resolved --MCP session opened--> running
resolved --MCP session refused 409 stale--> abandoned
resolved --MCP unavailable--> failing(mcp-unavailable)
running --model answer valid--> completing
running --model answer empty, too long, or sections missing--> failing(advice-invalid)
running --turn bound / deadline / provider error / MCP error--> failing(<reason>)
running --stale from renewal, checkpoint, or tool--> abandoned
running --SIGTERM--> failing(runtime-stopping)
completing --200--> completed
completing --409 stale--> abandoned
completing --409 execution-references-mismatch or 400--> failing(runtime-error)
failing --200 or 409 stale--> ended
```

`abandoned` sends no further report (FR-022). Every terminal state closes the MCP session and discards `messages`.

## Failure classification

| Reason code | Retryable | When |
| --- | --- | --- |
| `invalid-artifact` | no | Envelope without pins; artifact missing, schema-invalid, or digest mismatch. |
| `incompatible-artifact` | no | Wrong agent group, safety policy without `user-to-specialist`, unsupported prompt slot. |
| `unsupported-route` | no | Route other than `user-to-specialist`, Coach Agent version, or profile and modifier artifacts. |
| `model-provider-mismatch` | no | Pinned provider differs from the configured provider. |
| `provider-authentication-failed` | no | Provider answered `401` or `403`. |
| `provider-refused` | no | Provider answered another `4xx` or refused the content. |
| `model-turn-limit-reached` | no | `MAX_MODEL_TURNS` reached without a final answer. |
| `provider-unavailable` | yes | Connection failure, model timeout, `429`, or `5xx`. |
| `mcp-unavailable` | yes | Session cannot be opened (other than stale), session lost, or MCP server platform-unavailable or authentication errors. |
| `advice-invalid` | yes | Empty answer, longer than `limits.maxAdviceCharacters`, or response-contract sections missing or out of order. |
| `attempt-deadline` | yes | Work not finished `DEADLINE_MARGIN_SECONDS` before `deadlineAt`. |
| `runtime-stopping` | yes | Hermes received `SIGTERM` during the attempt. |
| `runtime-error` | yes | Unexpected exception, or the platform refused Hermes' completion as malformed or mismatched. |

## Response contract check

The final answer passes when it is non-empty, at most `limits.maxAdviceCharacters` characters, and contains the five
section headings `Assessment`, `Evidence`, `Uncertainty and alternatives`, `Recommendations`, and
`Next questions or consultations` in this order, each at the start of a line, case-insensitive, optionally preceded by
Markdown heading or emphasis markers and followed by a colon or line end.

## Execution references (completion, `attempt-report` 1.1.0)

| Field | Value |
| --- | --- |
| `runtime` | `hermes/<package version>+hermes-runtime-section/1` |
| `modelProvider`, `model`, `deployment` | The pinned model selection's values. |
| `modelSelectionRef` | Pinned model selection digest (new, optional in the contract; always sent by Hermes). |
| `parameters` | The pinned parameters exactly as sent (new, optional in the contract; `{}` when none are pinned). |
| `promptRef`, `toolPolicyRef`, `safetyPolicyRef` | Pinned digests. |

Platform checks on completion (new): `modelProvider`, `model`, `deployment` equal the pinned model selection;
`modelSelectionRef` and `parameters`, when present, equal the pinned digest and parameters; otherwise
`409 execution-references-mismatch` and the attempt stays active.

## Operation identifiers

| Request | Identifier |
| --- | --- |
| Claim | `hermes-claim-<invocationId>-<uuid4>` (one per notification delivery; reused only for its retries) |
| Checkpoint | `hermes-<attemptId>-checkpoint-<n>` |
| Completion | `hermes-<attemptId>-complete` |
| Failure | `hermes-<attemptId>-fail` |

All match the platform's `^[A-Za-z0-9._\-]{1,128}$`.

## Checkpoints

| Kind | Note (counts and identifiers only) |
| --- | --- |
| `artifacts-resolved` | `agent <agentVersion>; provider <provider>` |
| `evidence-session-opened` | `<n> tools` |
| `model-turn` | `turn <n>; tools <toolIds or none>; results <answered>/<unavailable>/<refused>` |

## Configuration

| Variable | Required | Development value | Rules |
| --- | --- | --- | --- |
| `SOCALYTICS_HERMES_ENVIRONMENT` | no | `Development` | `Development`, `Test`, or `Production` (default `Production`). |
| `SOCALYTICS_HERMES_PLATFORM_URL` | yes | API URL | Absolute `https` URL (`http` only in Development and Test); where requests are sent. |
| `SOCALYTICS_HERMES_PLATFORM_PUBLIC_BASE_URL` | no | = `PLATFORM_URL` | Origin equal to the API's `Stamp:PublicBaseUri`; proof `htu` and assertion `aud`. |
| `SOCALYTICS_HERMES_PLATFORM_CA_FILE` | no | — | PEM bundle of additional trusted authorities. |
| `SOCALYTICS_HERMES_WORKLOAD_CLIENT_ID` | yes | `hermes` | Configured, enabled workload client; distinct from the MCP server's. |
| `SOCALYTICS_HERMES_WORKLOAD_KEY_FILE` | yes | mounted file | ECDSA P-256 private key, PKCS#8 PEM. |
| `SOCALYTICS_HERMES_NATS_URL` | no | AppHost NATS URL | Used only when the platform issues no queue credentials (mode `None`). |
| `SOCALYTICS_HERMES_MCP_URL` | yes | MCP server `/mcp` URL | Absolute URL. |
| `SOCALYTICS_HERMES_MCP_SECRET_FILE` | yes | mounted file | The MCP server's runtime shared secret (at least 32 bytes). |
| `SOCALYTICS_HERMES_MODEL_PROVIDER` | no | `simulated` | `openai` (default), `github-models`, or `simulated` (Development and Test only). |
| `SOCALYTICS_HERMES_MODEL_ENDPOINT` | no | provider default | Absolute `https` URL; not allowed for `simulated`. |
| `SOCALYTICS_HERMES_MODEL_CREDENTIAL_FILE` | for `openai`, `github-models` | mounted file | API key or token; forbidden for `simulated`. |
| `SOCALYTICS_HERMES_MODEL_SCRIPT_FILE` | for `simulated` | mounted file | Valid against the script schema; forbidden otherwise. |
| `SOCALYTICS_HERMES_MAX_CONCURRENT_ATTEMPTS` | no | 2 | 1–64. |
| `SOCALYTICS_HERMES_MAX_MODEL_TURNS` | no | 8 | 1–32. |
| `SOCALYTICS_HERMES_MODEL_TIMEOUT_SECONDS` | no | 60 | 5–600. |
| `SOCALYTICS_HERMES_PLATFORM_TIMEOUT_SECONDS` | no | 30 | 1–120. |
| `SOCALYTICS_HERMES_DEADLINE_MARGIN_SECONDS` | no | 10 | 1–120. |
| `SOCALYTICS_HERMES_SHUTDOWN_SECONDS` | no | 20 | 1–300. |
| `SOCALYTICS_HERMES_PORT` | no | 8080 | 1024–65535. |
| `SOCALYTICS_HERMES_LOG_LEVEL` | no | `info` | `debug`, `info`, `warning`, `error`; `debug` adds no content fields. |

Invalid configuration stops Hermes with exit code 2 and one line per invalid variable, naming it without its value.
Production values remain open.

## Script (scripted provider, component-local contract)

`{ schemaVersion: "1.0.0", scenarios: [{ name, agentId, marker?, steps: [ { toolCalls: [{ toolId, arguments }] } |
{ answer } | { error: "unavailable" | "refused" | "authentication" } ] }] }`; additional properties are refused. A
scenario applies when its `agentId` matches and, if `marker` is set, the question starts with `[script:<marker>]`;
the first matching scenario wins, and no matching scenario fails the attempt with `provider-refused`.
