# Implementation Plan: Web UI Agent Catalog Administration

**Branch**: `20261018-103316-web-ui-agent-catalog` | **Date**: 2026-10-18 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261018-103316-web-ui-agent-catalog/spec.md`

## Summary

Add a connected Agent Catalog area for Club Admins to the Web UI at `src/clients/web/`, over the agent catalog member
operations of [Agent Catalog Registry](../archive/20261018-101517-agent-catalog-registry/contracts/agent-catalog-api.md), with
the navigation, page, confirmation, refusal, and response-metadata patterns of
[Web UI Analyst Registry Administration](../archive/20261017-185438-web-ui-registry-admin/plan.md).

- **Platform (OpenAPI description only)**: where the snapshot describes record documents as free-form objects,
  schema-only DTO records add per-kind document schemas (discriminator `kind`) as `Accepts`/`Produces` metadata; request
  validation and response bytes stay unchanged; a Host test keeps the schemas equal to the `contracts/agent-catalog/`
  contracts ([research R1](research.md#r1-openapi-schemas-for-the-catalog-operations-and-their-record-documents)).
- **Adapter**: `agentCatalogAdapter` maps the baseline, agents, versions, enablements, available agents, the seven
  artifact kinds, and distances, and turns validation problems, refusals, replays, and stale versions into typed
  outcomes ([research R2–R4](research.md#r2-response-metadata-etag-and-created-versus-replayed)); a pointer-to-field
  helper places each violation next to its field.
- **Views**: overview with what triage conversations offer; baseline; agent with versions and enablement; agent version
  with references and distances; artifact lists and read-only views per kind; artifact forms per kind (including the
  profile table with a running total); agent version form; approve, retire, and change-enablement dialogs
  ([contracts/routes-and-roles.md](contracts/routes-and-roles.md)).
- **Online only**: TanStack Query in memory; form state in components; the service worker already leaves `/api/` to the
  network.
- **Evidence**: Host tests; Vitest unit tests; Playwright `simulated` specs; a chained `platform-agent-catalog` project
  against the AppHost `e2e` mode.

Design rationale: [research.md](research.md). Client models: [data-model.md](data-model.md). Contracts:
[contracts/](contracts/). Validation: [quickstart.md](quickstart.md).

## Technical Context

**Language/Version**: TypeScript 5.9 (strict) on Node.js 24, as pinned by `src/clients/web/package.json`; C# on .NET 10
(SDK from `src/platform/global.json`) for the OpenAPI metadata.

**Primary Dependencies**: the Web UI's existing pinned dependencies (React 19, React Router 7, TanStack Query 5, MUI 7,
i18next and react-i18next, `@microsoft/kiota-bundle` and `@microsoft/kiota-http-fetchlibrary`, Vite 7, Vitest 4,
Testing Library, `@playwright/test` 1.63.0, `@axe-core/playwright`); ASP.NET Core minimal API metadata (`Accepts`,
`Produces`, `ProducesProblem`) and `System.Text.Json` polymorphism attributes. No new dependency.

**Storage**: none new. Catalog data lives in the platform's agent catalog tables; the Web UI keeps it only in the
in-memory TanStack Query cache and open form state.

**Testing**:

- Platform: `AgentCatalogOpenApiTests` (Host tests; schemas present and equal to the contracts' property names),
  `OpenApiSnapshotTests`; the Agent Catalog Registry's catalog integration tests unchanged.
- `npm test` (Vitest): `agentCatalogAdapter`, outcome translation, `violationLocation`, `versionProposal`,
  `catalogLimits` against `contracts/agent-catalog/`, offer composition, forms and hints, dialogs, verbatim text
  rendering, i18n keys, service-worker classification of `/api/v1/agent-catalog/*`.
- `npm run test:e2e -- --project simulated`: `agent-catalog.spec.ts`, `agent-catalog-coach.spec.ts`,
  `agent-catalog-access.spec.ts` against the routed simulated API.
- `npm run test:e2e`: chained `platform-agent-catalog` project (`tests/e2e/platform/agent-catalog.spec.ts`) after the
  last platform project.

**Target Platform**: Chromium-based browsers (automated: Chromium on Linux), as for the rest of the Web UI; the API on
the AppHost.

**Project Type**: web application (single-page application) consuming the platform API, plus OpenAPI metadata in the
platform API project.

**Performance Goals**: lists render the full catalog the platform returns (nine agents, tens of versions and artifacts
per agent) without paging; one request per user action plus the reads of the shown view; no polling; prompt text up to
32,000 characters renders without blocking input.

**Constraints**:

- Only the agent catalog member operations listed in
  [contracts/agent-catalog-openapi.md](contracts/agent-catalog-openapi.md) plus the existing member list (FR-019).
- Request validation and response bytes unchanged; `OpenApiSnapshotTests` green.
- No deletion, no editing of registered records, no baseline changes, no model-rendered previews, no evaluation results
  (FR-018).
- Platform text rendered as text only; no `dangerouslySetInnerHTML` or Markdown rendering; the Content Security Policy
  and Trusted Types baseline unchanged.
- English through translation keys; keyboard operable; WCAG 2.2 AA rule checks (FR-021).
- The shared `mapProblem` stays unchanged; catalog outcomes are derived in the catalog adapter.
- Hints never replace the platform's validation (FR-010).

**Scale/Scope**: 8 routes, 3 dialogs, 8 forms (7 artifact kinds and the agent version), 1 adapter, 1 translation
namespace, 1 schema-only API contracts file, 3 simulated browser specs, 1 platform browser spec.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Evidence |
| --- | --- | --- |
| I. Architecture is the design authority | Pass | Implements "platform configuration and administration" of [Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui) for the agent catalog and the operator side of [Prompt And Advice Governance](../../docs/architecture/intelligence-agent-catalog.md#prompt-and-advice-governance) and [Adding Or Revising A Coach Agent](../../docs/architecture/intelligence-agent-catalog.md#adding-or-revising-a-coach-agent); stays online only per [Offline Mode](../../docs/architecture/client-applications.md#offline-mode). Design updates applied now (see Architecture Updates). |
| II. Source-area ownership | Pass | Web UI code under `src/clients/web/`; API metadata under `src/platform/SocAlytics.Platform.Api/AgentOrchestration/`; no new `src/` child. |
| III. API-first control plane | Pass | The Web UI uses only `/api/v1` agent catalog operations through the generated client behind an adapter; the platform decides every rule; the OpenAPI document gains the schemas the client needs, kept equal to the authoritative contracts. |
| IV. Evidence over claims | Pass | Host, unit, simulated browser, and platform browser tests (FR-023); approval is presented as an accountable decision, not evaluation evidence; nothing claims production readiness. |
| V. Focused, minimal changes | Pass | No new dependency, operation, or project; reuses `clubAdminOnly`, `FailureAlert`, `memberAdapter`, the Kiota helpers, and the Registry area's response-metadata middleware, digest display, and member names. |
| Technology and tooling | Pass | Needs the .NET SDK, Docker, Node.js 24, Kiota 1.36.0, and Playwright 1.63.0 Chromium, all provided by `.github/actions/environment-setup` and checked by `environment-verify` for `src/clients/web/` and `src/platform/`; no environment spec is needed. |

Post-design re-check: Pass (no violations; Complexity Tracking is empty).

## Architecture Updates

### Narratives

- [Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui): "platform configuration and
  administration" gains the agent catalog administration for Club Admins.
- [Client Applications → Offline Mode](../../docs/architecture/client-applications.md#offline-mode): unchanged rule
  (online only; no user data in the service worker) that the area follows.
- [Intelligence Agent Catalog → Prompt And Advice Governance](../../docs/architecture/intelligence-agent-catalog.md#prompt-and-advice-governance)
  (the agent catalog registry paragraph) and
  [Adding Or Revising A Coach Agent](../../docs/architecture/intelligence-agent-catalog.md#adding-or-revising-a-coach-agent):
  the operator side (registering, approving, retiring, and enabling versions; the profile table and distance review)
  gains a Web UI path; the governance rules are unchanged.
- [Platform Implementation → Agent Runtime](../../docs/architecture/platform-implementation.md#agent-runtime): the
  catalog paragraph and the list of remaining future work.
- [Platform Implementation](../../docs/architecture/platform-implementation.md) Acceptance Evidence and Architecture
  Reassessment: Web UI evidence and the OpenAPI snapshot from which the client is generated.

### Design updates (applied now, worded as decided but not implemented)

1. `client-applications.md`, Web UI: the bullet "platform configuration and administration" now names "the agent
   catalog administration for Club Admins" among the decided, not yet implemented parts, and a new paragraph "The agent
   catalog administration is decided as follows and not yet implemented" (after the workflow operations paragraph)
   describes the area: overview with what triage conversations offer, read-only versions and artifacts, structured
   registration forms whose hints never replace the platform's validation, confirmed and conditional approval,
   retirement, and enablement changes that apply only to questions submitted afterwards, online only, no deletion,
   editing, model-rendered previews, or evaluation results. Implementation turns it into present tense.
2. `intelligence-agent-catalog.md`, Prompt And Advice Governance (agent catalog registry paragraph): "Club Admins will
   operate the catalog from the Web UI's Agent Catalog area (decided, not yet implemented; …)". Implementation turns it
   into present tense.
3. `platform-implementation.md`, Agent Runtime (agent catalog registry paragraph): the same sentence. Implementation
   turns it into present tense.

### Status statements at merge

- `client-applications.md`, Web UI: the bullet "(the Analyst Registry administration, the workflow operations area,
  and the agent catalog administration for Club Admins are decided, not yet implemented; see below)" → names the agent
  catalog administration as implemented (connected workflow), keeping the status of the other parts as they then are;
  "The agent catalog administration is decided as follows and not yet implemented" becomes present tense.
- `intelligence-agent-catalog.md`, Prompt And Advice Governance: "Club Admins will operate the catalog from the Web UI's
  Agent Catalog area (decided, not yet implemented …)" → present tense.
- `intelligence-agent-catalog.md`, introduction: "The catalog entries, prompt text, philosophy artifacts, model
  execution, MCP evidence tools, Hermes orchestration runtime, and user experiences are likewise design inputs rather
  than deployed behavior" (or its rewrite by the Agent Catalog Registry) → the catalog administration experience exists
  as development evidence; conversation experiences, model execution, MCP tools, and Hermes stay as they then are.
- `platform-implementation.md`, Agent Runtime: "Club Admins will operate the catalog from the Web UI's Agent Catalog
  area (decided, not yet implemented …)" → present tense; "MCP tools, prompt/profile resolution, model execution, and
  Web UI agent workflows remain future implementation work" (or its rewrite by the Agent Catalog Registry naming "Web UI
  catalog administration") → no longer lists the catalog administration as future work.
- `platform-implementation.md`, Acceptance Evidence ("Web UI browser tests cover Analyst Manager pairing, the Club Admin
  decision view, and the fleet view …") → adds the agent catalog administration (`simulated` and `platform` projects)
  and the Host-test evidence that the catalog operations' record documents carry OpenAPI schemas equal to the contracts.
- `platform-implementation.md`, Architecture Reassessment ("the online-only Web UI foundation exists as development
  evidence, including the Analyst Manager pairing, approval, and fleet views and the match workflow …") → includes the
  agent catalog administration views; "the other Web UI client workflows (analysis review and agent conversations)"
  stays accurate only if those are still unimplemented at merge.
- `AGENTS.md` "Current State" (outside `docs/architecture/`, kept consistent by the same task): the Web UI description
  gains the agent catalog administration.
- Any other statement in `docs/architecture/` that presents agent catalog operation as API-only or its Web UI
  administration as planned.

**Documentation task**: `/speckit-tasks` MUST generate an outcome-based documentation task that makes these narratives
and every status statement this feature affects in `docs/architecture/` true for the merged state, re-reading the
documents at implementation time (and `AGENTS.md` "Current State", `src/clients/README.md`, and the root `README.md`);
the rewrites above are examples, not the whole job.

## Project Structure

### Documentation (this feature)

```text
specs/20261018-103316-web-ui-agent-catalog/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output
│   ├── routes-and-roles.md
│   ├── agent-catalog-adapter.md
│   └── agent-catalog-openapi.md
├── checklists/
│   └── requirements.md
└── tasks.md             # Phase 2 output (/speckit-tasks)
```

### Source Code (repository root)

```text
src/platform/
├── SocAlytics.Platform.Api/
│   ├── AgentOrchestration/
│   │   ├── AgentCatalogDocumentContracts.cs        # new: schema-only per-kind document records (research R1)
│   │   └── AgentCatalogEndpoints.cs                # Accepts/Produces metadata where missing
│   └── openapi/v1.json                             # refreshed snapshot
└── Tests/SocAlytics.Platform.Host.Tests/
    └── AgentCatalogOpenApiTests.cs                 # new

src/clients/web/
├── src/
│   ├── router.tsx                                  # /agent-catalog routes (clubAdminOnly)
│   ├── shell/navItems.ts                           # agentCatalog entry after registry
│   ├── i18n/
│   │   ├── index.ts                                # registers the agentCatalog namespace
│   │   └── en/
│   │       ├── agentCatalog.json                   # new namespace
│   │       └── shell.json                          # nav.agentCatalog
│   ├── platform/
│   │   ├── models/agentCatalog.ts                  # new: data-model.md types
│   │   └── adapters/agentCatalogAdapter.ts         # new
│   └── features/agent-catalog/
│       ├── agentCatalogKeys.ts                     # query keys and hooks
│       ├── catalogLimits.ts                        # enumerations and limits from the contracts
│       ├── versionProposal.ts                      # next patch version
│       ├── violationLocation.ts                    # JSON Pointer → form location
│       ├── outcomeText.ts                          # code → translation key
│       ├── offer.ts                                # what conversations offer
│       ├── CatalogText.tsx                         # verbatim, scrollable plain text
│       ├── ViolationSummary.tsx                    # unlocated violations above a form
│       ├── AgentCatalogOverviewPage.tsx
│       ├── CatalogBaselinePage.tsx
│       ├── AgentPage.tsx
│       ├── AgentVersionPage.tsx
│       ├── AgentVersionCreatePage.tsx
│       ├── ArtifactsPage.tsx
│       ├── ArtifactPage.tsx                        # dispatches to the per-kind views
│       ├── ArtifactCreatePage.tsx                  # dispatches to the per-kind forms
│       ├── artifacts/                              # per-kind view and form components
│       │   ├── PromptTemplateView.tsx, PromptTemplateForm.tsx
│       │   ├── ModelSelectionView.tsx, ModelSelectionForm.tsx
│       │   ├── ToolPolicyView.tsx, ToolPolicyForm.tsx
│       │   ├── SafetyPolicyView.tsx, SafetyPolicyForm.tsx
│       │   ├── DimensionSetView.tsx, DimensionSetForm.tsx
│       │   ├── PhilosophyProfileView.tsx, PhilosophyProfileForm.tsx
│       │   ├── ModifierPolicyView.tsx, ModifierPolicyForm.tsx
│       │   └── DistanceList.tsx
│       ├── ApproveAgentVersionDialog.tsx
│       ├── RetireAgentVersionDialog.tsx
│       └── ChangeEnablementDialog.tsx
└── tests/
    ├── unit/
    │   ├── platform/adapters/agentCatalogAdapter.test.ts
    │   ├── features/agent-catalog/fixtures.ts      # shared view-test fixtures and fake adapter
    │   ├── features/agent-catalog/*.test.ts(x)
    │   └── serviceWorker/shellWorker.test.ts       # agent catalog paths stay unhandled
    └── e2e/
        ├── support/simulatedApi.ts                 # agent catalog routes and state
        ├── simulated/agent-catalog.spec.ts
        ├── simulated/agent-catalog-coach.spec.ts
        ├── simulated/agent-catalog-access.spec.ts
        └── platform/agent-catalog.spec.ts
src/clients/web/playwright.config.ts                # chained platform-agent-catalog project
```

**Structure Decision**: the area is a new feature folder `src/features/agent-catalog/` following the Web UI's feature,
adapter, and model layering, importing the generic `digest.ts`, `DigestText.tsx`, and `memberNames.ts` from
`src/features/registry/` (research R5); the platform change stays in the API project's Agent Orchestration folder and its
snapshot.

## Complexity Tracking

No violations.
