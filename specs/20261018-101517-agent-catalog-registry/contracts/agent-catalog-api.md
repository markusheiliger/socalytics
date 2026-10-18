# Contract: Agent Catalog API (`/api/v1`)

Every operation is in the OpenAPI document with typed request and response schemas and uses the existing problem
details (`urn:socalytics:problem:<code>`). Field names are camelCase JSON. `{kind}` is one of `prompt-templates`,
`model-selections`, `tool-policies`, `safety-policies`, `dimension-sets`, `philosophy-profiles`, `modifier-policies`.

## Member operations

Session-authenticated (`MapMemberApi()`, policy `ActiveMember`); state-changing operations require `X-CSRF-Token`.
Catalog operations require Club Admin (`ClubPermission.Administer`); other members get `403` and an
`agent.catalog.access.refused` audit event.

| Operation id | Method and path | Request | Success | Refusals |
| --- | --- | --- | --- | --- |
| `getAgentCatalogBaseline` | `GET /api/v1/agent-catalog/baseline` | — | `200` `catalog-baseline/v1` document | `403` |
| `registerAgentCatalogArtifact` | `POST /api/v1/agent-catalog/artifacts/{kind}` | the record document | `201` `{ kind, id, version, digest, registeredAt, document, distances? }`, `Location`; equal replay `200` | `400 agent-catalog-validation-failed` (`violations: [{ code, pointer, detail }]`), `403`, `409 agent-catalog-version-exists` |
| `listAgentCatalogArtifacts` | `GET /api/v1/agent-catalog/artifacts/{kind}?id=` | — | `200` `{ items: [{ kind, id, version, digest, registeredBy, registeredAt }] }` | `403` |
| `getAgentCatalogArtifact` | `GET /api/v1/agent-catalog/artifacts/{kind}/{id}/versions/{version}` | `If-None-Match` | `200` record with `document`; profiles add `distances: [{ agentId, agentVersion, profileId, profileVersion, distance, reviewRequired }]`; `ETag`; `304` | `403`, `404` |
| `registerAgentVersion` | `POST /api/v1/agent-catalog/agent-versions` | the `agent-version/v1` document | `201` `{ agentId, version, digest, state: "draft", document }`, `ETag`, `Location`; equal replay `200` | `400 agent-catalog-validation-failed`, `403`, `409 agent-catalog-version-exists` |
| `listAgentVersions` | `GET /api/v1/agent-catalog/agent-versions?agentId=&state=` | — | `200` `{ items: [{ agentId, version, digest, group, state, enabled, registeredAt }] }` | `403` |
| `getAgentVersion` | `GET /api/v1/agent-catalog/agent-versions/{agentId}/versions/{version}` | `If-None-Match` | `200` `{ agentId, version, digest, group, state, enabled, document, registeredBy, registeredAt, approvedBy, approvedAt, selfApproval, differentiationAcknowledged, retiredBy, retiredAt, distances? }`, `ETag`; `304` | `403`, `404` |
| `approveAgentVersion` | `POST /api/v1/agent-catalog/agent-versions/{agentId}/versions/{version}/approval` | `If-Match`; `{ differentiationReviewAcknowledged? }` | `200` agent version, `ETag` | `403`, `404`, `409 agent-version-not-draft`, `409 differentiation-review-required`, `412`/`428` |
| `retireAgentVersion` | `POST /api/v1/agent-catalog/agent-versions/{agentId}/versions/{version}/retirement` | `If-Match` | `200` agent version, `ETag` | `403`, `404`, `409 agent-version-not-approved`, `409 agent-version-enabled`, `412`/`428` |
| `listAgentEnablements` | `GET /api/v1/agent-catalog/enablements` | — | `200` `{ items: [{ agentId, group, enabledVersion, changedBy, changedAt, etag }] }` | `403` |
| `setAgentEnablement` | `PUT /api/v1/agent-catalog/enablements/{agentId}` | `If-Match`; `{ version }` (null disables) | `200` enablement, `ETag` | `400`, `403`, `404` (unknown agent id), `409 agent-version-not-enableable` (`reason`: `not-approved`, `retired`, `simulated-provider`), `409 coach-agent-enablement-unsupported`, `412`/`428` |
| `listAvailableAgents` (changed) | `GET /api/v1/agent-catalog/agents` | — | `200` `{ agents: [{ agentId, group, domain, displayName, enabledVersion }] }` from the enablement | `403` without Club Admin or team role (unchanged) |

Changed Agent Orchestration member reads (owner only, unchanged authorization):

- `getAgentInvocation` and `getAgentInvocationLineage` add `agent: { version, digest, artifacts: [{ kind, id, version,
  digest }] }`, or `agent: null` for unpinned invocations.
- `addAgentConversationParticipant` and `submitAgentInvocation` answer `409 agent-not-available` when the agent has no
  enabled version.

## Workload operations

Token-authenticated (policy `AgentWorkload`); member sessions and Analyst Manager tokens get `401`.

| Operation id | Method and path | Request | Success | Refusals |
| --- | --- | --- | --- | --- |
| `readPinnedAgentArtifacts` (new) | `POST /api/v1/agent-orchestration/attempts/{attemptId}/pinned-artifacts` | `{ fencingToken }` | `200` `{ agentVersion: { agentId, version, digest, document }, artifacts: [{ kind, id, version, digest, document }] }` (Coach Agent versions add the profile's dimension set) | `404`, `409 agent-attempt-stale` (`disposition`) |
| `claimAgentInvocation` (changed) | unchanged path | unchanged | envelope `execution-envelope` 1.1.0 with `agent` | adds: unpinned invocation → `409 claim-obsolete` and the invocation fails `agent-not-available` |
| `completeAgentAttempt` (changed) | unchanged path | unchanged | unchanged | adds `409 execution-references-mismatch` when `promptRef`, `toolPolicyRef`, or `safetyPolicyRef` differs from the pinned digest |

## Machine-readable contracts

New, under `contracts/agent-catalog/<name>/v1/<name>.schema.json` (each with `releases/1.0.0.schema.json` and valid and
invalid fixtures under `examples/`): `catalog-baseline`, `prompt-template`, `model-selection`, `tool-policy`,
`safety-policy`, `dimension-set`, `philosophy-profile`, `modifier-policy`, `agent-version`. Fields are listed in
[research.md R5](../research.md#r5--artifact-formats-wave-0). Required fixtures include: the baseline 1.0.0; the
dimension set `coach-philosophy-dimensions` 1.0.0 and the three provisional profiles from Base Philosophy Profiles; the
two seed specialists; invalid fixtures for a model selection with an extra `apiKey` field, a tool id with `()`, a profile
with a positional weight array, and a modifier policy with `perDimensionBound` 4.

Changed: `contracts/agent-orchestration/execution-envelope/v1` → 1.1.0 adds the optional property
`agent { agentId, version, digest, artifacts: [{ kind, id, version, digest }] }` and `releases/1.1.0.schema.json`; the
1.0.0 release stays unchanged.
