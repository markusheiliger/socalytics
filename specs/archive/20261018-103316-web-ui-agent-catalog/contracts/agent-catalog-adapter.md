# Contract: Agent Catalog Adapter

`src/clients/web/src/platform/adapters/agentCatalogAdapter.ts` is the only code in the area that calls the generated
client. Views and hooks (`src/clients/web/src/features/agent-catalog/agentCatalogKeys.ts`) use only this interface.
Models and outcomes are defined in [data-model.md](../data-model.md); refusal handling in
[research R4](../research.md#r4-refusals-and-stale-state).

| Function | Operation | Returns |
| --- | --- | --- |
| `getBaseline()` | `getAgentCatalogBaseline` | `CatalogBaseline` |
| `listAvailableAgents()` | `listAvailableAgents` | `{ agentId, version }[]` |
| `listEnablements()` | `listAgentEnablements` | `Enablement[]` |
| `setEnablement(agentId, version \| null, etag)` | `setAgentEnablement` (`If-Match`) | `Enablement` or `enablementRefused` \| `changed` |
| `listAgentVersions({ agentId?, state? })` | `listAgentVersions` | `AgentVersionSummary[]` |
| `getAgentVersion(agentId, version)` | `getAgentVersion` | `AgentVersion` (with `etag`) |
| `registerAgentVersion(version, document)` | `registerAgentVersion` (`version` query parameter; the document has no `version` property) | `created` \| `replayed` \| `validationFailed` \| `versionExists` |
| `approveAgentVersion(agentId, version, etag, { differentiationReviewAcknowledged? })` | `approveAgentVersion` (`If-Match`) | `AgentVersion` or `transitionRefused` \| `changed` |
| `retireAgentVersion(agentId, version, etag)` | `retireAgentVersion` (`If-Match`) | `AgentVersion` or `transitionRefused` \| `changed` |
| `listArtifacts(kind, { id? })` | `listAgentCatalogArtifacts` | `ArtifactSummary[]` |
| `getArtifact(kind, id, version)` | `getAgentCatalogArtifact` | `ArtifactRecord<K>` |
| `registerArtifact(kind, document)` | `registerAgentCatalogArtifact` | `created` \| `replayed` \| `validationFailed` \| `versionExists` |

Rules:

- Every state change carries the anti-forgery header through the existing middleware and is sent once; the adapter
  never retries a state change.
- `401`, `403`, offline, `429`, and `5xx` are left to the shared failure handling (`toPlatformFailure`); `404` on reads
  becomes the not-found view.
- Documents are passed through without reordering or adding fields beyond what the form set; unknown fields in read
  documents are kept and shown raw.
- Member ids are resolved for display by `memberNames.ts`; `system:seed` is shown as the development seed.
- Nothing is written to browser storage.
