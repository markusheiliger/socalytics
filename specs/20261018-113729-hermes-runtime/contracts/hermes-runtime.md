# Contract: Hermes Runtime Interface

Hermes' interfaces to the orchestrator (health), the platform and the MCP server (outbound calls), and the operator
(configuration, logs, image). Platform semantics are those of
[Agent Orchestration API](../../archive/20261017-204332-agent-orchestration-invocations/contracts/agent-orchestration-api.md),
[Agent Catalog API](../../20261018-101517-agent-catalog-registry/contracts/agent-catalog-api.md), and the
[MCP server interface](../../20261018-111804-mcp-server/contracts/mcp-server.md); this contract adds none except the
completion change in [attempt-report-1.1.0.md](attempt-report-1.1.0.md). Values marked *profile* stay open in
[Production Deployment and Operations](../../../docs/architecture/production-operations.md#versioned-production-profile).

## 1. Health (orchestrator → Hermes)

| Request | Response |
| --- | --- |
| `GET /alive` | `200 { "status": "alive" }` while the process serves requests |
| `GET /health` | `200 { "status": "ready" }` while a workload token is held or obtained and the work notification subscription is connected; otherwise `503 { "status": "not-ready" }` |

Port `SOCALYTICS_HERMES_PORT` (8080, HTTP). No other endpoint; Hermes accepts no work over HTTP.

## 2. Work intake (NATS JetStream → Hermes)

| Item | Value |
| --- | --- |
| Stream, subject, consumer | `AGENT_WORK`, `agents.invocations.ready`, durable pull consumer `agent-workload` |
| Credentials | From `issueAgentWorkloadQueueCredentials` (NATS user JWT for an in-memory NKey); in mode `None` (Development and Test) none, at `SOCALYTICS_HERMES_NATS_URL` |
| Message | `work-notification` 1.0.0; anything else is acknowledged and logged as `notification.invalid` |
| Fetch | At most the free attempt slots (`SOCALYTICS_HERMES_MAX_CONCURRENT_ATTEMPTS`) |
| Acknowledge | After a definitive claim answer (`201`/`200`, `409 claim-obsolete`, `409 agent-scope-lost`, `404`); negative acknowledgement with a 5-second delay after transient failures |

## 3. Outbound calls (Hermes → platform and MCP server)

Platform operations at `SOCALYTICS_HERMES_PLATFORM_URL`, DPoP-authenticated with Hermes' own workload client; proofs
and the assertion audience use `SOCALYTICS_HERMES_PLATFORM_PUBLIC_BASE_URL`. The HTTP client refuses any other path.

| Operation | Use |
| --- | --- |
| `issueAgentWorkloadToken` | `private_key_jwt` + DPoP; scope `agent-workload` |
| `issueAgentWorkloadQueueCredentials` | NATS user JWT for the work consumer |
| `claimAgentInvocation` | One claim per notification delivery, operation id `hermes-claim-<invocationId>-<uuid4>` |
| `readPinnedAgentArtifacts` | Once per attempt, with the fencing token |
| `renewAgentAttemptLease` | Every `leaseSeconds / 3` while working |
| `recordAgentCheckpoint` | `artifacts-resolved`, `evidence-session-opened`, `model-turn` |
| `completeAgentAttempt` | `attempt-report` 1.1.0 completion |
| `failAgentAttempt` | `attempt-report` failure with a reason code of [data-model.md](../data-model.md#failure-classification) |

MCP server at `SOCALYTICS_HERMES_MCP_URL`: one Streamable HTTP session per attempt with
`Authorization: Bearer <runtime secret>`, `SocAlytics-Attempt-Id`, and `SocAlytics-Fencing-Token` on `initialize`;
`tools/list` and `tools/call` only; `DELETE` at the end of the attempt.

The configured model provider: the OpenAI API (`openai`, default endpoint `https://api.openai.com/v1`) or GitHub Models
(`github-models`, default endpoint `https://models.github.ai/inference`) with the credential file; the scripted
provider (`simulated`) makes no network call. Hermes never calls member operations and never sends member credentials,
the MCP secret to the platform, or workload tokens to the MCP server or the provider.

## 4. Rendering

System context = pinned prompt template with its declared slots filled (`agent_id`, `display_name`, `tool_policy`,
`evidence_context`) + runtime section `hermes-runtime-section/1` (safety policy hard boundaries, refusal and escalation
categories, evidence citation rule, and the five response-contract headings). History (oldest first, assistant turns
attributed to their agent), the question, and tool results are separate user, assistant, and tool messages.

## 5. Logs

Single-line JSON on standard output with only: `event`, `timestamp`, `level`, `invocationId`, `attemptId`,
`correlationId`, `operationId`, `callId`, `agentId`, `provider`, `model`, `toolId`, `outcome`, `reasonCode`,
`retryable`, `disposition`, `status`, `turns`, `toolCalls`, `durationMs`, and for configuration errors `setting`.
Events: `notification.received`, `notification.invalid`, `claim`, `attempt.started`, `attempt.completed`,
`attempt.failed`, `attempt.abandoned`, `model.turn`, `tool.call`, `lease.renewed`, `runtime.stopping`. Never prompts,
rendered context, questions, history, tool arguments or results, links, advice, fencing or access tokens, proofs,
keys, credentials, or secrets.

## 6. Image

| Property | Value |
| --- | --- |
| Dockerfile | `src/agents/hermes/Dockerfile` with `Dockerfile.dockerignore`; build context: repository root |
| Build arguments | `SOCALYTICS_REVISION`, `SOCALYTICS_VERSION` (required, as in the image contract) |
| Labels | `org.opencontainers.image.revision`, `.version`, `.source`, `.title` = `socalytics-hermes`, `.description` |
| Base | `python:3.12-slim-bookworm` pinned by digest; no CPU architecture named |
| User | `65532:65532` (numeric, non-root); any orchestrator-assigned non-root UID works |
| Port | 8080 (HTTP, health only) |
| Root filesystem | read-only supported; writable location: tmpfs at `/tmp` |
| Health probe | none in the image; orchestrators probe `/alive` and `/health` |
| Configuration | environment variables and mounted files only ([data-model.md](../data-model.md#configuration)); no stamp, profile, credential, or secret values in the image |
| Lifetime | long-running; `SIGTERM` ends active attempts with `runtime-stopping` within `SOCALYTICS_HERMES_SHUTDOWN_SECONDS` |

## 7. MCP server addition

The MCP server gains one optional setting, `SOCALYTICS_MCP_PLATFORM_CA_FILE` (PEM bundle of additional trusted
authorities for its platform connection); nothing else in its interface changes.
