# Contract: Agent Catalog Routes and Roles

Extends the Web UI's route table (`src/clients/web/src/router.tsx`) and navigation
(`src/clients/web/src/shell/navItems.ts`). Every route below is wrapped in the existing `clubAdminOnly` guard
(`RequireRole allow={isClubAdmin}`): members without the Club Admin role get the not-permitted view and no catalog
request is sent (FR-001). The platform stays authoritative: a `403` from any catalog operation shows the not-permitted
view, a `404` on a record read shows the not-found view (FR-002).

## Navigation

| Key | Target | Shown when | Position |
| --- | --- | --- | --- |
| `agentCatalog` | `/agent-catalog` | `isClubAdmin(member)` | directly after `registry` |

The label comes from `shell.json` (`nav.agentCatalog`, English "Agent catalog").

## Routes

`:kind` is one of `prompt-templates`, `model-selections`, `tool-policies`, `safety-policies`, `dimension-sets`,
`philosophy-profiles`, `modifier-policies`; any other value, an identifier outside the catalog's kebab-case form, or a
version that is not SemVer shows the not-found view without a request.

| Path | View | Purpose | Spec |
| --- | --- | --- | --- |
| `/agent-catalog` | `AgentCatalogOverviewPage` | Nine agents with kind, domain, display name, enabled version, state counts, and what conversations offer; links to artifacts and baseline | US1, FR-003 |
| `/agent-catalog/baseline` | `CatalogBaselinePage` | Tool allowlist and per-agent tool baselines | FR-007 |
| `/agent-catalog/agents/:agentId` | `AgentPage` | Versions (filter `state`), tool baseline, enablement with "Change enabled version" and "Disable" | FR-004, US2 |
| `/agent-catalog/agents/:agentId/versions/new` | `AgentVersionCreatePage` | Agent version form; optional `?from=<version>` | US3, FR-011 |
| `/agent-catalog/agents/:agentId/versions/:version` | `AgentVersionPage` | Version details, artifact references, distances, Approve, Retire, Enable | FR-005, US2, US4 |
| `/agent-catalog/artifacts/:kind` | `ArtifactsPage` | Records of one kind (filter `id`); kind switcher | FR-006 |
| `/agent-catalog/artifacts/:kind/new` | `ArtifactCreatePage` | Form of the kind; optional `?fromId=&fromVersion=`; optional `?agentId=` for baseline hints | US3, US5, FR-009, FR-010 |
| `/agent-catalog/artifacts/:kind/:artifactId/versions/:version` | `ArtifactPage` | Read-only record of the kind; "New version from this" | FR-006 |

Path parameters are URL-encoded.

## Confirmations

| Action | Dialog | Must state |
| --- | --- | --- |
| Approve agent version | `ApproveAgentVersionDialog` | agent, version, digest, artifact digests; approved versions never change; approval is an accountable decision, not evaluation evidence; self-approval note when `registeredBy` is the signed-in member; every distance with `reviewRequired` and a required differentiation-review acknowledgement |
| Retire agent version | `RetireAgentVersionDialog` | agent and version; a retired version can no longer be enabled; invocations that pinned it keep it |
| Enable a version or disable the agent | `ChangeEnablementDialog` | agent, current enabled version (or none), new enabled version (or none); only questions submitted afterwards use the change; existing invocations, their retries, and their history keep their pinned versions; when disabling, members can no longer add or ask the agent until a version is enabled again; for a Coach Agent version, that the platform may refuse while Coach Agents are not available in conversations |

Cancelling a dialog sends nothing. Confirming sends exactly one request with the `If-Match` value last seen; the dialog
shows the outcome or the refusal and never repeats the request.
