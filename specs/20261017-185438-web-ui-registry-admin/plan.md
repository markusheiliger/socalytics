# Implementation Plan: Web UI Analyst Registry Administration

**Branch**: `20261017-185438-web-ui-registry-admin` | **Date**: 2026-10-17 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261017-185438-web-ui-registry-admin/spec.md`

## Summary

Add a connected Registry administration area for Club Admins to the Web UI at
`src/clients/web/`, over the 13 existing Analyst Registry operations.

- **Platform (documentation of existing behavior only)**: the Registry
  operations are listed in the committed OpenAPI snapshot without request or
  response schemas, so the generated client has no models for them. Typed DTO
  records in `src/platform/SocAlytics.Platform.Api/Registry/AnalystRegistryContracts.cs`
  with `Accepts`/`Produces`/`ProducesProblem` metadata describe today's bodies
  exactly; the snapshot is refreshed. No operation, field, status, or rule
  changes ([research.md § R1–R2](research.md#r1-the-registry-operations-need-openapi-schemas-not-new-operations)).
- **Client plumbing**: a `ResponseMetadataHandler` middleware and
  `sendWithMetadataFor` helper read the `ETag` row version and the
  created-or-existing status that the Registry API already returns
  ([§ R3](research.md#r3-reading-etag-and-the-created-status-in-the-web-ui)).
- **Adapter**: `registryAdapter` maps declarations (with a tolerant parse of
  the stored manifest), profile versions, workflow versions, and the default,
  and turns Registry refusals and stale versions into typed outcomes without
  changing the shared `mapProblem`
  ([§ R4](research.md#r4-refusals-conflicts-and-problem-mapping)).
- **Views**: `/registry` overview with the default workflow; image, profile,
  and workflow lists and detail views; image registration; profile creation
  from a declaration; approve and retire dialogs; workflow definition form
  (from scratch or from an existing version) with node and edge lists; the
  set-default dialog with optimistic concurrency
  ([contracts/routes-and-roles.md](contracts/routes-and-roles.md)).
- **Online only**: TanStack Query in memory; the service worker already leaves
  `/api/` to the network.
- **Evidence**: Host and integration tests for the schemas and unchanged
  bodies; Vitest unit tests; Playwright `simulated` specs; a chained
  `platform-registry` project against the AppHost `e2e` mode.

Design rationale: [research.md](research.md). Client models:
[data-model.md](data-model.md). Contracts: [contracts/](contracts/).
Validation: [quickstart.md](quickstart.md).

## Technical Context

**Language/Version**: TypeScript 5.9 (strict) on Node.js 24, as pinned by
`src/clients/web/package.json`; C# on .NET 10 (SDK from
`src/platform/global.json`) for the API metadata.

**Primary Dependencies**: the Web UI's existing pinned dependencies (React 19,
React Router 7, TanStack Query 5, MUI 7, i18next and react-i18next,
`@microsoft/kiota-bundle` and `@microsoft/kiota-http-fetchlibrary`, Vite 7,
Vitest 4, Testing Library, `@playwright/test` 1.63.0,
`@axe-core/playwright`); ASP.NET Core minimal API metadata
(`Accepts`, `Produces`, `ProducesProblem`) already used by other endpoints.
No new dependency.

**Storage**: none new. Registry data lives in the platform's existing tables;
the Web UI keeps it only in the in-memory TanStack Query cache and form state.

**Testing**:

- Platform: `AnalystRegistryOpenApiTests` (schemas present), `OpenApiSnapshotTests`
  (snapshot equals the running API), Registry integration tests in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/`
  (`AnalystImageImportTests`, `AnalystProfileTests`, `WorkflowDefinitionTests`,
  `RegistryResolutionTests`) with assertions on unchanged response property
  names.
- `npm test` (Vitest): `registryAdapter`, manifest parsing, refusal
  translation, `ResponseMetadataHandler`, digest shortening, form defaults and
  client checks, edge descriptions, components, i18n keys, service-worker
  classification of `/api/v1/registry/*`.
- `npm run test:e2e -- --project simulated`: `registry.spec.ts` and
  `registry-access.spec.ts` against the routed simulated API.
- `npm run test:e2e`: new chained `platform-registry` project
  (`tests/e2e/platform/registry.spec.ts`) after the last platform project.

**Target Platform**: Chromium-based browsers (automated: Chromium on Linux),
as for the rest of the Web UI; the API on the AppHost.

**Project Type**: web application (single-page application) consuming the
platform API, plus OpenAPI metadata in the platform API project.

**Performance Goals**: lists render the full Registry the platform returns
(tens to low hundreds of records for one stamp) without paging; one request
per user action; no polling.

**Constraints**:

- Only the 13 Registry operations plus the existing member list (FR-019).
- Response bodies equivalent in property names, nesting, and values;
  `OpenApiSnapshotTests` must stay green.
- No deletion, no editing of approved, retired, or stored records, no image
  building (FR-018).
- Platform text rendered as text only; no `dangerouslySetInnerHTML`; the
  existing Content Security Policy and Trusted Types baseline unchanged.
- English through translation keys; keyboard operable; WCAG 2.2 AA rule checks
  (FR-021).
- The shared `mapProblem` stays unchanged; Registry outcomes are derived in the
  Registry adapter.

**Scale/Scope**: 10 routes and views, 3 dialogs, 2 forms with list editors, 1
adapter, 1 middleware, 1 translation namespace, 1 API contracts file, 2
simulated browser specs, 1 platform browser spec.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Evidence |
| --- | --- | --- |
| I. Architecture is the design authority | Pass | Implements "platform configuration and administration" of [Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui) for the Registry and the operator side of [Image Metadata And Packaging](../../docs/architecture/analysts-models-and-hardware.md#image-metadata-and-packaging) and [Analyst Profiles And Container-Owned Preprocessing](../../docs/architecture/analysts-models-and-hardware.md#analyst-profiles-and-container-owned-preprocessing); stays online only per [Offline Mode](../../docs/architecture/client-applications.md#offline-mode). Design updates applied now (see Architecture Updates). |
| II. Source-area ownership | Pass | Web UI code under `src/clients/web/`; API metadata under `src/platform/SocAlytics.Platform.Api/Registry/`; no new `src/` child. |
| III. API-first control plane | Pass | The Web UI uses only `/api/v1` Registry operations via the generated client behind an adapter; the platform decides every rule; the OpenAPI document (authoritative over generated code) gains the schemas the client needs. |
| IV. Evidence over claims | Pass | Host, integration, unit, simulated browser, and platform browser tests (FR-023); nothing claims production readiness. |
| V. Focused, minimal changes | Pass | No new dependency, operation, or project; reuses `clubAdminOnly`, `FailureAlert`, `memberAdapter`, the Kiota helpers, and the middleware pattern. |
| Technology and tooling | Pass | Needs the .NET SDK, Docker, Node.js 24, Kiota 1.36.0, and Playwright 1.63.0 Chromium, all provided by `.github/actions/environment-setup` and `environment-verify` (merged [Web UI Verification Environment](../archive/20261012-145505-web-ui-environment/spec.md) and the platform environment); no environment spec is needed. |

Post-design re-check: Pass (no violations; Complexity Tracking is empty).

## Project Structure

### Documentation (this feature)

```text
specs/20261017-185438-web-ui-registry-admin/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output
│   ├── routes-and-roles.md
│   ├── registry-adapter.md
│   └── registry-openapi.md
└── tasks.md             # Phase 2 output (/speckit-tasks)
```

### Source Code (repository root)

```text
src/platform/
├── SocAlytics.Platform.Api/
│   ├── Registry/
│   │   ├── AnalystRegistryContracts.cs             # new: request and response DTO records
│   │   └── AnalystRegistryEndpoints.cs             # typed bodies, Accepts/Produces/ProducesProblem
│   └── openapi/v1.json                             # refreshed snapshot
└── Tests/
    ├── SocAlytics.Platform.Host.Tests/AnalystRegistryOpenApiTests.cs          # + schema assertions
    └── SocAlytics.Platform.Integration.Tests/Registry/
        ├── AnalystImageImportTests.cs              # + unchanged response properties
        ├── AnalystProfileTests.cs                  # + unchanged response properties
        └── WorkflowDefinitionTests.cs              # + unchanged response properties

src/clients/web/
├── src/
│   ├── router.tsx                                  # /registry routes (clubAdminOnly)
│   ├── shell/navItems.ts                           # registry entry for Club Admins
│   ├── i18n/
│   │   ├── index.ts                                # registers the registry namespace
│   │   └── en/
│   │       ├── registry.json                       # new namespace
│   │       └── shell.json                          # nav.registry
│   ├── platform/
│   │   ├── client.ts                               # registers ResponseMetadataHandler
│   │   ├── middleware/responseMetadata.ts          # new: status and ETag capture
│   │   ├── models/registry.ts                      # new: data-model.md types
│   │   └── adapters/
│   │       ├── kiota.ts                            # + sendWithMetadataFor
│   │       └── registryAdapter.ts                  # new
│   └── features/registry/
│       ├── registryKeys.ts                         # query keys and hooks
│       ├── digest.ts                               # shorten
│       ├── DigestText.tsx                          # shortened digest, full value, copy
│       ├── memberNames.ts                          # MemberRef resolution
│       ├── refusalText.ts                          # code → translation key
│       ├── edgeText.ts                             # edge and condition in words
│       ├── RegistryOverviewPage.tsx
│       ├── ImagesPage.tsx
│       ├── ImagePage.tsx
│       ├── RegisterImagePage.tsx
│       ├── ProfilesPage.tsx
│       ├── ProfileVersionPage.tsx
│       ├── ProfileCreatePage.tsx
│       ├── profileDraft.ts                         # defaults from a declaration
│       ├── ApproveProfileDialog.tsx
│       ├── RetireProfileDialog.tsx
│       ├── WorkflowsPage.tsx
│       ├── WorkflowVersionPage.tsx
│       ├── WorkflowCreatePage.tsx
│       ├── workflowDraft.ts                        # defaults, prefill, flags, checks
│       ├── SetDefaultWorkflowDialog.tsx
│       └── RefusalAlert.tsx
└── tests/
    ├── unit/
    │   ├── platform/adapters/registryAdapter.test.ts
    │   ├── platform/responseMetadata.test.ts
    │   ├── features/registry/fixtures.ts           # shared view-test fixtures and fake adapter
    │   ├── features/registry/*.test.ts(x)
    │   └── serviceWorker/shellWorker.test.ts       # registry paths stay unhandled
    └── e2e/
        ├── support/simulatedApi.ts                 # Registry routes and state
        ├── simulated/registry.spec.ts
        ├── simulated/registry-access.spec.ts
        └── platform/registry.spec.ts
src/clients/web/playwright.config.ts                # chained platform-registry project
```

**Structure Decision**: the area is a new feature folder
`src/features/registry/` following the Web UI's feature, adapter, and model
layering; the platform change stays in the API project's Registry folder and
its snapshot.

## Architecture Updates

### Narratives

- [Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui): "platform configuration and administration" gains its first implemented part, the Analyst Registry administration for Club Admins.
- [Analysts, Models, and Hardware → Image Metadata And Packaging](../../docs/architecture/analysts-models-and-hardware.md#image-metadata-and-packaging): image registration by digest gains a Web UI path for Club Admins; packaging rules unchanged.
- [Analysts, Models, and Hardware → Analyst Profiles And Container-Owned Preprocessing](../../docs/architecture/analysts-models-and-hardware.md#analyst-profiles-and-container-owned-preprocessing): profiles are created, approved, and retired by Club Admins in the Web UI; no design change.
- [Tenancy And Technology → Stamp Persistence](../../docs/architecture/tenancy-and-technology.md#stamp-persistence) and [Job Processing](../../docs/architecture/job-processing.md) (default workflow resolution): unchanged rules the area operates on (runs pin a snapshot; only runs created afterwards use a new default).
- [Platform Implementation](../../docs/architecture/platform-implementation.md): Web UI evidence and the OpenAPI snapshot from which the client is generated.
- [Client Applications → Offline Mode](../../docs/architecture/client-applications.md#offline-mode): unchanged rule (online only; no user data in the service worker) that the area follows.

### Design updates (applied now, worded as decided but not implemented)

- `client-applications.md`, Web UI: the bullet "platform configuration and administration" gains "(the Analyst Registry administration for Club Admins is decided, not yet implemented; see below)", and a paragraph that the Analyst Registry administration is decided as follows and not yet implemented: Club Admins see the default workflow and browse registered images, Analyst profile versions, and workflow versions; register images by digest, create, approve, and retire profile versions, define workflow versions as node and edge lists, and change the default workflow, each with confirmation where it changes analysis and with optimistic concurrency; the platform decides every rule, the area is online only, and it offers no deletion, image building, or graphical editing. Implementation turns it into present tense.
- `analysts-models-and-hardware.md`, Image Metadata And Packaging: the registration sentence ("enforced when an image is registered by digest (`POST /api/v1/registry/analyst-images`)") gains that Club Admins will also register images from the Web UI's Registry area (decided, not yet implemented). Implementation turns it into present tense.

### Status statements at merge

- `client-applications.md`, Web UI: the bullet "platform configuration and administration (the Analyst Registry administration for Club Admins is decided, not yet implemented; see below)" → names the Analyst Registry administration as implemented (connected workflow) and leaves other platform configuration not implemented; the decided paragraph ("The Analyst Registry administration is decided as follows and not yet implemented") becomes present tense.
- `platform-implementation.md`, Architecture Reassessment ("the online-only Web UI foundation exists as development evidence, including the Analyst Manager pairing, approval, and fleet views and the match workflow …") → includes the Analyst Registry administration views.
- `platform-implementation.md`, Acceptance Evidence ("Web UI browser tests cover Analyst Manager pairing, the Club Admin decision view, and the fleet view …") → adds the Registry administration (`simulated` and `platform` projects), and the Host-test evidence that the Registry operations carry OpenAPI schemas.
- `analysts-models-and-hardware.md`, Image Metadata And Packaging: the decided Web UI registration path becomes present tense.
- `AGENTS.md` "Current State" (outside `docs/architecture/`, kept consistent): the Web UI foundation description gains the Registry administration.
- Any other statement in `docs/architecture/` that presents Registry operation as API-only or Web UI administration as planned.

The documentation task MUST make these narratives and every status statement
this feature affects true for the merged state, re-reading
`docs/architecture/` (and `AGENTS.md` "Current State", `src/clients/README.md`,
and the root `README.md`) at implementation time; the rewrites above are
examples, not the whole job.

## Complexity Tracking

No violations.
