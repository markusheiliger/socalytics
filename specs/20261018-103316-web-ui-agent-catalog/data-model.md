# Data Model: Web UI Agent Catalog Administration

The Web UI stores nothing durable. These are the application-owned client models in
`src/clients/web/src/platform/models/agentCatalog.ts` that `agentCatalogAdapter` maps from the generated client; they
live only in the in-memory query cache and in open form state. Field meanings and limits are defined by
[Agent Catalog Registry](../archive/20261018-101517-agent-catalog-registry/data-model.md) and the contracts under
`contracts/agent-catalog/`; unknown enumeration values are kept as raw strings (FR-022).

## Read models

| Model | Fields | Source operation |
| --- | --- | --- |
| `CatalogBaseline` | `version`, `tools: string[]` (allowlist), `agents: BaselineAgent[]` | `getAgentCatalogBaseline` |
| `BaselineAgent` | `agentId`, `group` (`coach` \| `specialist` \| raw), `domain`, `displayName`, `toolBaseline: string[]` | baseline |
| `CatalogAgentSummary` | `BaselineAgent` + `enablement: Enablement`, `stateCounts: { draft, approved, retired }`, `offer: Offer` | overview composition (research R6) |
| `Enablement` | `agentId`, `enabledVersion?`, `changedBy?: MemberRef`, `changedAt?`, `etag` | `listAgentEnablements`, `setAgentEnablement` |
| `Offer` | `{ kind: 'offered', version }` \| `{ kind: 'notOffered', reason: 'noEnabledVersion' \| 'coachNotAvailable' }` | `listAvailableAgents` + baseline group |
| `AgentVersionSummary` | `agentId`, `version`, `digest`, `group`, `state` (`draft` \| `approved` \| `retired` \| raw), `enabled`, `registeredAt` | `listAgentVersions` |
| `AgentVersion` | summary + `displayName`, `disclosure?`, `artifacts: ArtifactRef[]`, `registeredBy`, `approvedBy?`, `approvedAt?`, `selfApproval`, `differentiationAcknowledged`, `retiredBy?`, `retiredAt?`, `distances?: Distance[]`, `etag` | `getAgentVersion` |
| `ArtifactRef` | `kind`, `id`, `version`, `digest` | agent version `artifacts` |
| `ArtifactSummary` | `kind`, `id`, `version`, `digest`, `registeredBy: MemberRef`, `registeredAt` | `listAgentCatalogArtifacts` |
| `ArtifactRecord<K>` | summary + `document: DocumentOf<K>`, `distances?: Distance[]` (profiles) | `getAgentCatalogArtifact`, registration |
| `Distance` | `agentId`, `agentVersion`, `profileId`, `profileVersion`, `distance` (number), `reviewRequired` | profile and Coach Agent version reads |
| `MemberRef` | `id`, display name resolved as in the Registry area, or "Development seed" for `system:seed` | `memberNames.ts` |

## Documents (per kind)

| Kind (`DocumentOf<K>`) | Fields shown and entered |
| --- | --- |
| `prompt-template` | `group`, `responseContract`, `text`, `slots[]`, `dimensionSetMajor?` |
| `model-selection` | `provider`, `model`, `deployment`, `parameters { temperature?, topP?, maxOutputTokens?, reasoningEffort? }` |
| `tool-policy` | `tools[]` |
| `safety-policy` | `group`, `routes[]`, `hardBoundaries[]`, `escalationCategories[]`, `refusalCategories[]`, `rules[]` |
| `dimension-set` | `dimensions[] { id, meaning, acceptedEvidence[] }` |
| `philosophy-profile` | `dimensionSet { id, version }`, `weights { <dimensionId>: { weight, rationale, provenance } }`, `author`, `reviewers { architecture[], footballDomain[] }`, `supersedes? { version }` |
| `modifier-policy` | `dimensionSetMajor`, `perDimensionBound`, `maxPositiveMovement`, `contextCategories[]`, `budgetNeutral` |
| `agent-version` | `agentId`, `group`, `displayName`, `artifacts { promptTemplate, modelSelection, toolPolicy, safetyPolicy, philosophyProfile?, modifierPolicy? }`, `disclosure?` |

Every artifact document also carries `schemaVersion`, `kind`, `id`, and `version`, which the forms set. An `agent-version` document carries `schemaVersion` and `kind` but no `version` (its schema forbids additional properties): the agent version is supplied beside the document as the `version` query parameter of `registerAgentVersion`, and the platform refuses a missing or non-SemVer value with a violation at `/version`.

Derived view values (never sent): a profile's row order follows its dimension set's order; the weights' total; each
tool's `withinBaseline` flag for the agent in context.

## Form drafts

| Draft | Behavior |
| --- | --- |
| `ArtifactDraft<K>` | Starts empty or from an existing record (`fromId`, `fromVersion`); `version` proposed by `versionProposal.ts` (research R7); hints from `catalogLimits.ts` and the baseline (research R8). |
| `ProfileDraft` | Rows generated from the chosen dimension-set version, one per dimension; changing the dimension set keeps values of matching dimension ids and drops the others with a notice. |
| `AgentVersionDraft` | Agent from the baseline fixes `group`; `displayName` prefilled from the baseline; reference pickers filtered by kind and group; Coach Agent profile and modifier policies with the prompt template's `dimensionSetMajor` listed first. |

## Outcomes (`CatalogOutcome`)

| Outcome | Meaning |
| --- | --- |
| `created` | `201`: the new record or version. |
| `replayed` | `200` on registration: stored record, nothing changed. |
| `validationFailed` | `violations: [{ code, pointer, detail, location }]`, `location` from `violationLocation.ts`. |
| `versionExists` | Identifier and version exist with different content. |
| `transitionRefused` | `code`: `agent-version-not-draft`, `differentiation-review-required`, `agent-version-not-approved`, `agent-version-enabled`, or raw. |
| `enablementRefused` | `code`: `agent-version-not-enableable` (with `reason`), `coach-agent-enablement-unsupported`, `404` (unknown agent), or raw. |
| `changed` | `412`/`428`: state changed since loaded; reload and decide again. |

## State transitions shown

```text
agent version:  draft --approve--> approved --retire--> retired   (platform-decided; the UI only offers the next step)
enablement:     none <--enable/disable--> approved version          (one per agent; conditional on etag)
```
