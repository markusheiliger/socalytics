# Research: Web UI Agent Catalog Administration

All Technical Context items are resolved; no `NEEDS CLARIFICATION` remains. Each decision cites the spec, contract, or
code it rests on. The platform operations come from
[Agent Catalog Registry](../20261018-101517-agent-catalog-registry/contracts/agent-catalog-api.md); the Web UI patterns
come from [Web UI Analyst Registry Administration](../20261017-185438-web-ui-registry-admin/plan.md).

## R1. OpenAPI schemas for the catalog operations and their record documents

**Decision**: Use the agent catalog member operations exactly as
[Agent Catalog Registry](../20261018-101517-agent-catalog-registry/contracts/agent-catalog-api.md) defines them. Its task
T014 already requires typed OpenAPI schemas for the operations; the open point is the record `document` of
`registerAgentCatalogArtifact`, `getAgentCatalogArtifact`, `registerAgentVersion`, and `getAgentVersion`, whose shape
depends on the record kind. When the committed snapshot `src/platform/SocAlytics.Platform.Api/openapi/v1.json` (after
that feature merged) describes `document` as a free-form object, this feature adds schema-only DTO records in a new
`src/platform/SocAlytics.Platform.Api/AgentOrchestration/AgentCatalogDocumentContracts.cs`: one record per kind that
mirrors `contracts/agent-catalog/<kind>/v1` field for field, joined as a polymorphic `AgentCatalogRecordDocument` with
the discriminator `kind` (`[JsonPolymorphic]`/`[JsonDerivedType]`, which the ASP.NET Core OpenAPI generator renders as
`oneOf` with a discriminator mapping), plus `AgentVersionDocument`. They are attached as `.Accepts<…>()` and
`.Produces<…>()` metadata only: request bodies are still read and validated as raw JSON through `IContractValidator`,
and responses still write the stored canonical document unchanged, so the digest-bearing bytes stay identical. The
snapshot is refreshed. A Host test asserts that each kind's OpenAPI schema has exactly the property names of its JSON
Schema contract, so the two cannot drift. When the snapshot already carries typed documents, only the drift test is
added.

**Rationale**: FR-019 requires generated models for every body the area uses; Kiota generates no models for free-form
objects, and the forms need typed request bodies per kind. The stored document must be returned verbatim (its digest
covers it), so typed re-serialization on the server is avoided. The contracts in `contracts/agent-catalog/` stay
authoritative; the drift test makes the OpenAPI description follow them.

**Alternatives considered**: Leaving `document` free-form and building JSON by hand in the adapter (no
generated-client guarantee, unlike every other adapter); one operation per kind (new operations, forbidden by FR-019);
typed server-side serialization (could reorder or drop fields of a digest-bearing document).

## R2. Response metadata: `ETag` and created versus replayed

**Decision**: Reuse the `ResponseMetadataHandler`, `ResponseMetadataOption`, and `sendWithMetadataFor` that
[Web UI Analyst Registry Administration](../20261017-185438-web-ui-registry-admin/research.md#r3-reading-etag-and-the-created-status-in-the-web-ui)
adds under `src/clients/web/src/platform/`. The adapter reads the `ETag` of `getAgentVersion`, `approveAgentVersion`,
`retireAgentVersion`, and `setAgentEnablement` for the next `If-Match`, takes the enablement `etag` from
`listAgentEnablements` items, and tells `201 Created` from `200 OK` on registrations ("already registered, nothing
changed", FR-013).

**Rationale**: The agent catalog API returns versions as `ETag` and replays as `200`, exactly like the Registry API;
one mechanism keeps the two areas consistent.

**Alternatives considered**: New body fields for version and replay (changes the platform contract).

## R3. Validation problems next to their fields

**Decision**: `agentCatalogAdapter` turns `400 agent-catalog-validation-failed` into a `validationFailed` outcome with
`violations: [{ code, pointer, detail }]`. A pure helper `violationLocation.ts` maps each JSON Pointer to a form
location: top-level fields (`/model`, `/text`, `/disclosure`, `/perDimensionBound`), list entries (`/tools/2`,
`/hardBoundaries/0`, `/dimensions/3/acceptedEvidence/1`), profile rows (`/weights/<dimensionId>/weight`, `…/rationale`,
`…/provenance`, and `/weights` for the total or missing dimensions), and agent-version references
(`/artifacts/promptTemplate`). Unknown or empty pointers go to the summary above the form. Each code of
[Agent Catalog Registry research R3](../20261018-101517-agent-catalog-registry/research.md#r3--validation-pipeline-and-problem-responses)
has its own translation key (`schema-violation` shows the platform's `detail`); unknown codes show the raw code and
detail (FR-012, FR-022).

**Rationale**: The platform names every violation with a pointer; mapping pointers on the client is the only way to show
problems next to fields without changing the API.

**Alternatives considered**: Listing all violations above the form (fails US3 and US5 acceptance scenarios).

## R4. Refusals and stale state

**Decision**:

| Operation | Status and code | Outcome |
| --- | --- | --- |
| `registerAgentCatalogArtifact`, `registerAgentVersion` | `400 agent-catalog-validation-failed` | `validationFailed` (R3) |
| same | `409 agent-catalog-version-exists` | `versionExists` (immutable; new version needed) |
| same | `200` | `replayed` (stored record, nothing changed) |
| `approveAgentVersion` | `409 agent-version-not-draft`, `409 differentiation-review-required` | `transitionRefused` with code |
| `retireAgentVersion` | `409 agent-version-not-approved`, `409 agent-version-enabled` | `transitionRefused` with code |
| `setAgentEnablement` | `409 agent-version-not-enableable` (`reason`: `not-approved`, `retired`, `simulated-provider`), `409 coach-agent-enablement-unsupported`, `404` | `enablementRefused` with code and reason |
| approve, retire, enable | `412`, `428` | `changed` (reload and decide again, FR-017) |
| reads | `404` | not-found view |

`401`, `403`, offline, rate limiting, and server errors keep the shared handling (`toPlatformFailure`, `FailureAlert`,
session handling); the shared `mapProblem` is not changed.

**Rationale**: Codes and statuses come from the agent catalog API contract; the pattern matches the Registry adapter.

## R5. Routes, navigation, and shared components

**Decision**: Routes under `/agent-catalog` as listed in [contracts/routes-and-roles.md](contracts/routes-and-roles.md),
each wrapped in the existing `clubAdminOnly` guard; one navigation entry `agentCatalog` directly after the Registry
administration's `registry` entry, `allow: isClubAdmin`. Artifact kinds in addresses use the API's plural path segments
(`prompt-templates`, …). The area imports the Registry administration's generic helpers `digest.ts`, `DigestText.tsx`,
and `memberNames.ts` from `src/clients/web/src/features/registry/` instead of copying them.

**Rationale**: Same navigation, digest display, and member names as the Registry area (FR-008, spec Assumptions);
reusing them without moving files keeps the change focused.

**Alternatives considered**: Moving the helpers to a shared folder (touches the Registry feature for no behavior
gain); copies (drift).

## R6. What triage conversations offer

**Decision**: The overview combines `getAgentCatalogBaseline` (all nine agents with group, domain, display name, and
tool baseline), `listAgentVersions` (state counts), `listAgentEnablements` (enabled version, changed by and at, `etag`),
and `listAvailableAgents` (`GET /api/v1/agent-catalog/agents`, which Club Admins may call). An agent listed by
`listAvailableAgents` is "offered" with the version it reports; otherwise "not offered" with the reason "no enabled
version" or, for Coach Agents, "not available in conversations yet" (clarification 4).

**Rationale**: The available-agents read is what the triage conversation views use
([Web UI Match Triage Conversations](../archive/20261017-210231-web-ui-triage-conversations/research.md)), so the area shows
exactly what members are offered, and needs no change when Coach Agents become available.

## R7. Version proposal and starting from an existing record

**Decision**: A pure helper `versionProposal.ts` parses SemVer 2.0.0 versions without build metadata, takes the highest
registered version of the chosen identifier (from `listAgentCatalogArtifacts?id=` or `listAgentVersions?agentId=`),
and proposes its next patch version; a new identifier proposes `0.1.0` (clarification 3). Forms started with
`?fromId=&fromVersion=` (artifacts) or `?from=` (agent versions) load that record and prefill every field
(clarification 2).

**Rationale**: Revising a prompt or policy is the common case in
[Adding Or Revising A Coach Agent](../../docs/architecture/intelligence-agent-catalog.md#adding-or-revising-a-coach-agent)
and Prompt And Advice Governance; prefill avoids retyping long prompts.

## R8. Form hints and their limits

**Decision**: One module `catalogLimits.ts` holds the enumerations and limits the forms hint at (response contract,
slots, providers, name pattern `^[A-Za-z0-9._/:-]{1,128}$`, parameter ranges, routes, categories, rules, prompt text up
to 32,000 characters, rationale and provenance up to 1,000, disclosure 20–1,000, hard boundaries 1–20 of up to 500,
dimensions 1–32, weights 0–100 integers totalling 100, modifier bound 1–3 and movement 1–10, context categories). A unit
test reads the JSON Schemas under `contracts/agent-catalog/` and asserts that every enumeration and limit in
`catalogLimits.ts` equals the contract. The tool allowlist and per-agent baselines come from `getAgentCatalogBaseline`
at run time, not from constants. Hints never block submission except where a control cannot express the value (for
example a weight field accepts whole numbers only); slot detection uses the `{{slot}}` placeholders of the prompt text.

**Rationale**: FR-010; the contracts are authoritative, and the drift test keeps hints true.

## R9. Confirmations

**Decision**: Dialogs `ApproveAgentVersionDialog`, `RetireAgentVersionDialog`, and `ChangeEnablementDialog` state what
[contracts/routes-and-roles.md](contracts/routes-and-roles.md#confirmations) lists. Self-approval is detected when the
version's `registeredBy` equals the signed-in member's id. The differentiation acknowledgement is a required checkbox
shown when any distance of the version has `reviewRequired`; it sends `differentiationReviewAcknowledged: true`.
Cancelling sends nothing; confirming sends exactly one request and never repeats it.

## R10. Online only and verbatim text

**Decision**: Catalog reads use TanStack Query with in-memory caching only; form state lives in component state and is
discarded on leaving; the service worker already passes every `/api/` request to the network, and a unit test asserts
that `/api/v1/agent-catalog/*` stays unhandled. All catalog text is rendered through React text nodes in a
pre-formatted, scrollable block; no `dangerouslySetInnerHTML`, no Markdown rendering, and the Content Security Policy
and Trusted Types baseline stay unchanged (FR-020, FR-021).

## R11. Test strategy

**Decision**:

- Platform: Host test `AgentCatalogOpenApiTests` (every catalog operation the area uses has request and response
  schemas; each kind's document schema matches its contract's property names); `OpenApiSnapshotTests` covers the
  refreshed snapshot; the catalog integration tests of the Agent Catalog Registry keep passing (bodies unchanged).
- Web UI unit (Vitest): adapter mapping of baseline, agents, versions, enablements, available agents, every kind's
  document, and distances; outcome translation of every code; `violationLocation`; `versionProposal`;
  `catalogLimits` against the contracts; form hints; dialogs; translation keys; service-worker classification.
- Simulated browser (`--project simulated`): `agent-catalog.spec.ts` (overview, agents, versions, artifacts, specialist
  registration, approval, retirement, enablement, every refusal, stale conflicts), `agent-catalog-coach.spec.ts` (Coach
  Agent artifacts, profile table, distances, differentiation acknowledgement, Coach Agent enable refusal), and
  `agent-catalog-access.spec.ts` (roles, axe, keyboard, offline, no stored data, verbatim prompt text), with routes
  and state added to `tests/e2e/support/simulatedApi.ts`.
- Platform browser: a chained Playwright project `platform-agent-catalog` (`tests/e2e/platform/agent-catalog.spec.ts`)
  that runs after the last platform project, against the AppHost `e2e` mode with the Development and Test catalog seed:
  browses the seed, registers a prompt template `0.1.1` from the seeded `specialist-tactics` prompt, registers agent
  version `0.2.0` from `0.1.0` with it, approves it (self-approval note), enables it, and sees that triage
  conversations offer `0.2.0`. It runs last because it changes the enabled version.

**Rationale**: FR-023; the AppHost seed has no Coach Agent artifacts (Agent Catalog Registry FR-026), so Coach Agent
flows are evidenced in the simulated project and in the platform's own catalog tests.
