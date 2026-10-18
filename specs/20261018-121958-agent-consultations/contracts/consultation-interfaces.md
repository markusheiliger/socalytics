# Contract: Consultation Interfaces

Changes to the interfaces of [Agent Orchestration API](../../20261017-204332-agent-orchestration-invocations/contracts/agent-orchestration-api.md),
[Agent Catalog API](../../20261018-101517-agent-catalog-registry/contracts/agent-catalog-api.md),
[Coach Agent Interfaces](../../20261018-115720-coach-agent-conversations/contracts/coach-agent-interfaces.md), the
[Hermes Runtime Interface](../../20261018-113729-hermes-runtime/contracts/hermes-runtime.md), and the Web UI adapters of
[Web UI Match Triage Conversations](../../20261017-210231-web-ui-triage-conversations/contracts/agent-conversation-adapter.md)
and [Web UI Agent Catalog Administration](../../20261018-103316-web-ui-agent-catalog/contracts/agent-catalog-adapter.md).
Everything not listed here is unchanged. The OpenAPI document and its committed snapshot are refreshed.

## Machine-readable contracts

| Contract | New release | Change |
| --- | --- | --- |
| `contracts/agent-catalog/consultation-policy/v1` | 1.0.0 (new) | Fields of [data-model.md](../data-model.md#catalog-artifacts-existing-tables-new-kind). |
| `contracts/agent-catalog/agent-version/v1` | 1.1.0 | Optional `artifacts.consultationPolicy { id, version, digest }`. |
| `contracts/agent-orchestration/work-notification/v1` | 1.2.0 | `route` enum adds `coach-to-specialist`. |
| `contracts/agent-orchestration/execution-envelope/v1` | 1.3.0 | `route` enum adds `coach-to-specialist`; `agent.artifacts[].kind` adds `consultation-policy`; optional `consultations` and `consultation` (below). |
| `contracts/agent-orchestration/attempt-report/v1` | 1.3.0 | Optional `completion.consultationInfluence` (below). |
| `contracts/agent-orchestration/consultation-request/v1` | 1.0.0 (new) | Request body of `requestAgentConsultation` (below). |

Each keeps its earlier releases under `releases/` and gains valid and invalid fixtures, among them a notification with
`route: "coach-to-coach"`, an envelope with both `consultations` and `consultation`, a request with an extra property,
and an influence item with `use: "ignored"`. `contracts/README.md` index rows name the new versions.

```text
consultation-request {
  operationId:          ^[A-Za-z0-9._\-]{1,128}$
  fencingToken:         string
  agentId:              kebab-case 3–100
  question:             string 1–2000
  decisionSupport:      string 1–1000
  knownEvidenceCallIds: array 0–10 unique of ^[A-Za-z0-9._\-]{1,128}$
}

envelope.consultations[] {            # parents only, 0–6, sequence order
  consultationId, sequence, agentId, agentVersion, question, decisionSupport,
  state: pending | answered | unavailable | ended,
  response?: string                   # answered only
  unavailableReason?: string          # unavailable only
}

envelope.consultation {               # children only
  consultationId, parentInvocationId, requestingAgentId, decisionSupport, initiatingQuestion,
  knownEvidence: [{ callId, toolId, outcome, evidenceReferences: [...] }]
}

completion.consultationInfluence[] {  # 0–6
  consultationId,
  use: adopted | partially-adopted | not-adopted | unavailable,
  agreement: agrees | conflicts | not-applicable,
  note: string 1–300
}
```

Length limits in the schemas are the outer bounds; the pinned consultation policy's limits are platform checks.

## Workload operations

| Operation id | Method and path | Request | Success | Refusals |
| --- | --- | --- | --- | --- |
| `requestAgentConsultation` | `POST /api/v1/agent-orchestration/attempts/{attemptId}/consultations` | `consultation-request` 1.0.0 | `201 { consultationId, childInvocationId, sequence, state: "pending" }`; replay `200` | `400 consultation-request-invalid` (`violations`), `404`, `409 agent-attempt-stale` (`disposition`, including `scope-lost`), `409 operation-id-reused`, `409 consultation-refused` (`reason`: `route-not-supported`, `not-allowed`, `agent-not-available`, `not-consultable`, `tool-policy-not-inherited`, `limit-reached`) |
| `readAgentConsultationOutcomes` | `POST /api/v1/agent-orchestration/attempts/{attemptId}/consultation-outcomes` | `{ fencingToken }` | `200 { invocationState, consultations }` (envelope shape) | `404`, `409 agent-attempt-stale` |
| `claimAgentInvocation` | unchanged | — | Returns `execution-envelope` 1.3.0; parents with only resolved consultations become `synthesizing` | unchanged; `waiting-for-consultations` invocations are not claimable (`409 claim-obsolete`) |
| `completeAgentAttempt` | unchanged | `attempt-report` 1.3.0 | unchanged | adds `409 consultations-pending` and `409 consultation-influence-invalid` (`violations`: `missing`, `duplicate`, `unknown-consultation`, `unavailable-mismatch`, `not-a-parent`) |
| `readPinnedAgentArtifacts` | unchanged | — | Coach Agent versions with a consultation policy return it (kind `consultation-policy`) | unchanged |
| Agent tool operations, MCP server | unchanged | — | Serve `coach-to-specialist` attempts exactly as `user-to-specialist` attempts | unchanged |

Children use `renewAgentAttemptLease`, `recordAgentCheckpoint`, `completeAgentAttempt`, and `failAgentAttempt`
unchanged; a child's calls after its parent ended receive `409 agent-attempt-stale` with `invocation-ended`.

## Member operations

| Operation | Change |
| --- | --- |
| Catalog registration and reads | Kind `consultation-policy`; agent versions show `artifacts.consultationPolicy`; violation codes `consultation-target-invalid`, `consultation-policy-not-allowed`, `consultation-route-missing`. |
| `listAgentConversationTurns` | Only top-level invocations; per turn `consultations` (below); `state` may be `waiting-for-consultations` or `synthesizing`. |
| `getAgentInvocation` | Parents: `consultations`; children (owner only, after the parent ended): `route: "coach-to-specialist"`, `parentInvocationId`, `consultationId`. |
| `getAgentInvocationLineage` | Parents: `consultations`; entry kinds `consultation-requested`, `consultation-answered`, `consultation-unavailable`, `consultation-ended`; children: `consultation-created`. |

```text
consultation (member read) {
  consultationId, sequence, agentId, agentDisplayName, agentVersion,
  question, decisionSupport, knownEvidence, state, childInvocationId, childState,
  # only once the parent has succeeded or failed:
  response?, evidence?: [{ callId, toolId, outcome, evidenceReferences }],
  executionReferences?, unavailableReason?,
  influence?: { use, agreement, note }
}
```

Authorization is unchanged (owner only, re-evaluated on every read); a child read before its parent ended answers
`404`.

## Hermes

| Item | Change |
| --- | --- |
| Routes | `user-to-specialist`, `user-to-coach`, `coach-to-specialist`; anything else fails `unsupported-route`. |
| Copied releases | `consultation-policy` 1.0.0, `agent-version` 1.1.0, `work-notification` 1.2.0, `execution-envelope` 1.3.0, `attempt-report` 1.3.0, `consultation-request` 1.0.0. |
| Local function tools | `request_consultation` and `declare_consultation_influence` (Coach Agent attempts with a pinned consultation policy only). |
| Rendering | `hermes-coach-section/2` for consulting Coach Agents; `hermes-consultation-section/1` for children. |
| Checkpoints | `consultation-requested`, `consultation-refused`, `consultations-resolved` (content-free notes). |
| Configuration | `SOCALYTICS_HERMES_MAX_WAITING_ATTEMPTS`, `SOCALYTICS_HERMES_CONSULTATION_POLL_SECONDS`. |
| Logs | Adds `consultationId` and `consultationOutcome`; never questions, decision support, responses, or notes. |
| Script (scripted provider) | Unchanged schema; scenarios use `toolCalls` with `toolId: "request_consultation"` or `"declare_consultation_influence"`; scenarios for children match by `agentId` and the question marker. |

## Web UI adapters

- `agentConversationAdapter`: maps the two new states, `consultations` with their fields, child references, and the
  new lineage kinds; unknown values stay raw.
- `agentCatalogAdapter`: maps the `consultation-policy` kind, the agent version's optional reference, and the three
  new violation codes.
