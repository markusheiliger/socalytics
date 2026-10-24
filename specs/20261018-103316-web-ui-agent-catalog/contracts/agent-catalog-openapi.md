# Contract: Agent Catalog OpenAPI Description

The operations, paths, statuses, bodies, and rules are those of
[Agent Catalog Registry → Agent Catalog API](../../archive/20261018-101517-agent-catalog-registry/contracts/agent-catalog-api.md)
and do not change. This feature only ensures that the committed OpenAPI snapshot
`src/platform/SocAlytics.Platform.Api/openapi/v1.json` describes every body the Web UI uses, so Kiota generates models
for them (FR-019, [research R1](../research.md#r1-openapi-schemas-for-the-catalog-operations-and-their-record-documents)).

## Operations used by the Web UI

`getAgentCatalogBaseline`, `registerAgentCatalogArtifact`, `listAgentCatalogArtifacts`, `getAgentCatalogArtifact`,
`registerAgentVersion`, `listAgentVersions`, `getAgentVersion`, `approveAgentVersion`, `retireAgentVersion`,
`listAgentEnablements`, `setAgentEnablement`, `listAvailableAgents`, and the existing member list.

## Required schema content

| Body | Required description |
| --- | --- |
| Record documents in `registerAgentCatalogArtifact` requests and in artifact responses | `AgentCatalogRecordDocument`: `oneOf` the seven kind documents with discriminator `kind`, each with exactly the properties of `contracts/agent-catalog/<kind>/v1/<kind>.schema.json` |
| Agent version documents in `registerAgentVersion` requests and in agent version responses | `AgentVersionDocument` with exactly the properties of `contracts/agent-catalog/agent-version/v1/agent-version.schema.json` |
| Profile distances | `{ agentId, agentVersion, profileId, profileVersion, distance, reviewRequired }` |
| Validation problem | problem details with `violations: [{ code, pointer, detail }]` |
| Enablement refusal | problem details with `reason` |

Schema-only DTO records (`src/platform/SocAlytics.Platform.Api/AgentOrchestration/AgentCatalogDocumentContracts.cs`)
are attached as `Accepts`/`Produces` metadata where the snapshot lacks them; request bodies are still read as raw JSON
and validated against the contracts, and responses still write the stored canonical document unchanged.

## Evidence

- `AgentCatalogOpenApiTests` (Host tests): every operation above has its request (where it has one) and response
  schemas, and each kind's document schema has exactly the property names of its JSON Schema contract.
- `OpenApiSnapshotTests`: the refreshed snapshot equals the running API.
- The Agent Catalog Registry's catalog integration tests keep passing unchanged.
