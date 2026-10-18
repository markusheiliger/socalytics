# Implementation Plan: Web UI Workflow Operations for Club Admins

**Branch**: `20261017-215112-web-ui-workflow-operations` | **Date**: 2026-10-17 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261017-215112-web-ui-workflow-operations/spec.md`

**Note**: This template is filled in by the `/speckit-plan` command; its definition describes the execution workflow.

## Summary

Add a connected Operations area to the Web UI (`src/clients/web`) for Club Admins over the five existing workflow
operations under `/api/v1/operations` (`getAnalysisOperationalSummary`, `listOutboxMessages`, `getOutboxMessage`,
`listNotificationReceipts`, `reconcileAnalysisPublication`, all in
`src/platform/SocAlytics.Platform.Api/Operations/WorkflowOperationsEndpoints.cs`). The area has four routes under
`/operations` (summary with the reconciliation action, outgoing messages list, outgoing message detail, notification
receipts list), one navigation entry for Club Admins, an application-owned `operationsAdapter` over the generated
Kiota client, and an `operations` translation namespace. The committed OpenAPI snapshot already carries typed schemas
for all five operations ([research R1](research.md#r1-openapi-schemas-for-the-operations)), so no platform operation
changes; a focused Host test pins those schemas so the generated client keeps its models. Evidence: Vitest unit tests
for the adapter, views, paging, and dialog; Playwright `simulated` tests with operational fixtures in the simulated
API; and one Playwright `platform` test (chained project `platform-operations`) against the AppHost.

## Technical Context

**Language/Version**: TypeScript (strict) with React 19 for the Web UI (Node.js 24); C# on .NET 10 only for one Host
test

**Primary Dependencies**: Existing Web UI stack only: React Router 7, TanStack Query 5, MUI 7, i18next, and the Kiota
1.36.0 generated client (`src/clients/web/src/generated/platform-api`); no new packages

**Storage**: N/A (operational data stays in memory in the TanStack Query cache; nothing in browser storage or
service-worker caches)

**Testing**: Vitest with Testing Library (`npm test`), Playwright 1.63.0 `simulated` and `platform` projects with
axe-core (`npm run test:e2e`), xUnit Host tests (`dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests`)

**Target Platform**: Evergreen browsers and the installed PWA, served on one origin with the API by the AppHost

**Project Type**: Web application (SPA client of the platform API)

**Performance Goals**: One summary read per view open, refresh, or completed reconciliation; one list page per
navigation (platform default page size 50); no polling

**Constraints**: Online only (FR-019); Club Admin only, with the platform authoritative (FR-001, FR-002); no new or
changed platform operations (FR-018); WCAG 2.2 AA (FR-020); platform text rendered as text

**Scale/Scope**: 4 routes, 1 dialog, 1 adapter, 1 translation namespace, 1 navigation entry; up to 50 outgoing messages
or receipts per page

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Gate | Result |
| --- | --- | --- |
| I. Architecture Is the Design Authority | Conforms to [Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui), [API And Identity](../../docs/architecture/platform-implementation.md#api-and-identity), and [Production Queue Recovery and Backpressure](../../docs/architecture/job-processing.md#production-queue-recovery-and-backpressure); `## Architecture Updates` names narratives, design updates, and status statements | Pass |
| II. Respect Source-Area Ownership | Web UI code in `src/clients/web`; one Host test in `src/platform/Tests`; no new first-level area | Pass |
| III. API-First Control Plane | The Web UI calls only existing API operations through adapters over the generated client; the OpenAPI snapshot stays the contract; the reconciliation's effect, filter validation, and paging are decided by the platform | Pass |
| IV. Evidence Over Claims | Vitest, Playwright `simulated` and `platform`, and a Host test; no production-readiness claim | Pass |
| V. Focused, Minimal Changes | No new dependencies, no platform behavior change, no shared abstractions beyond the area's own paging hook | Pass |
| Tooling | Node.js 24, Kiota 1.36.0, Playwright 1.63.0 Chromium, .NET 10, and Docker are provided by `.github/actions/environment-setup` and checked by `environment-verify`; no new tool | Pass |

Post-design re-check: Pass (the design adds no operation, dependency, or storage).

## Project Structure

### Documentation (this feature)

```text
specs/20261017-215112-web-ui-workflow-operations/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output (client view models)
├── quickstart.md        # Phase 1 output
├── contracts/
│   ├── routes-and-roles.md        # Routes, navigation, confirmation
│   └── operations-adapter.md      # Adapter surface over the five operations
└── tasks.md             # Phase 2 output (/speckit-tasks)
```

### Source Code (repository root)

```text
src/clients/web/
├── src/
│   ├── router.tsx                               # four /operations routes behind clubAdminOnly
│   ├── shell/navItems.ts                        # `operations` entry for Club Admins
│   ├── i18n/index.ts                            # registers the `operations` namespace
│   ├── i18n/en/operations.json                  # new namespace
│   ├── i18n/en/shell.json                       # nav.operations label
│   ├── platform/models/operations.ts            # view models (data-model.md)
│   ├── platform/adapters/operationsAdapter.ts   # contracts/operations-adapter.md
│   └── features/operations/
│       ├── OperationsSummaryPage.tsx            # /operations: summary, refresh, reconcile action
│       ├── ReconcileDialog.tsx                  # confirmation and outcome
│       ├── OutboxMessagesPage.tsx               # /operations/outbox-messages
│       ├── OutboxMessagePage.tsx                # /operations/outbox-messages/:messageId
│       ├── NotificationReceiptsPage.tsx         # /operations/notification-receipts
│       ├── operationsFilters.ts                 # known filter values, address parsing
│       ├── useContinuationPages.ts              # forward/back over continuation tokens (in memory)
│       └── formatOperations.ts                  # durations, absent values, raw-value fallback
└── tests/
    ├── unit/platform/adapters/operationsAdapter.test.ts
    ├── unit/features/operations/summary.test.tsx
    ├── unit/features/operations/outbox.test.tsx
    ├── unit/features/operations/outboxMessage.test.tsx
    ├── unit/features/operations/receipts.test.tsx
    ├── unit/features/operations/reconcile.test.tsx
    ├── unit/features/operations/paging.test.ts
    ├── unit/features/operations/format.test.ts
    ├── unit/features/operations/filters.test.ts
    ├── unit/shell/navigation.test.tsx           # extended: Operations entry and position
    ├── unit/serviceWorker/shellWorker.test.ts   # extended: operations requests never cached
    ├── e2e/support/simulatedApi.ts              # operational fixtures and handlers
    ├── e2e/simulated/operations.spec.ts
    └── e2e/platform/operations.spec.ts          # chained project `platform-operations` in playwright.config.ts

src/platform/Tests/SocAlytics.Platform.Host.Tests/
└── WorkflowOperationsOpenApiTests.cs            # typed parameters and response schemas of the five operations
```

**Structure Decision**: The feature extends the existing Web UI in `src/clients/web` with one feature folder, one
adapter, one model file, and one translation namespace, following the existing `features/managers` and adapter
patterns. The platform gets only a Host test that pins the existing OpenAPI schemas; no platform production code
changes, so the OpenAPI snapshot is not refreshed.

## Architecture Updates

### Narratives

- [Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui): "platform configuration and administration" gains the workflow operations area for Club Admins.
- [Platform Implementation → API And Identity](../../docs/architecture/platform-implementation.md#api-and-identity): the operations under `/api/v1/operations`, restricted to Club Admins until a stamp-operator role exists, gain a Web UI consumer; authorization and audit unchanged.
- [Job Processing → Reliable Event Publication](../../docs/architecture/job-processing.md#reliable-event-publication) and [Production Queue Recovery and Backpressure](../../docs/architecture/job-processing.md#production-queue-recovery-and-backpressure): unchanged rules the area shows and explains (observable outbox age, retries, and failure state; reconciliation never authorizes work or overrides accepted state).
- [Client Applications → Offline Mode](../../docs/architecture/client-applications.md#offline-mode): unchanged rule (online only; no user data in the service worker) that the area follows.
- [Platform Implementation](../../docs/architecture/platform-implementation.md) (Architecture Reassessment and Acceptance Evidence): Web UI evidence lists that gain the Operations area.

### Design updates (applied now, worded as decided but not implemented)

- `client-applications.md`, Web UI: the bullet "platform configuration and administration" now names "the Analyst Registry administration and the workflow operations area for Club Admins" as decided, not yet implemented, and a new paragraph "The workflow operations area is decided as follows and not yet implemented" states the area's summary, filtered and paged outgoing messages and receipts, message detail without payload, confirmed reconciliation with its outcome, and what it does not offer. Implementation turns it into present tense.
- `platform-implementation.md`, API And Identity: the `/api/v1/operations` sentence gains "Club Admins will use them from the Web UI's Operations area (decided, not yet implemented …)". Implementation turns it into present tense.

### Status statements at merge

- `client-applications.md`, Web UI: "platform configuration and administration (the Analyst Registry administration and the workflow operations area for Club Admins are decided, not yet implemented …)" → names the workflow operations area as implemented (connected workflow), keeping the Registry's status as it is at merge time; "The workflow operations area is decided as follows and not yet implemented" → present tense.
- `platform-implementation.md`, API And Identity: "Club Admins will use them from the Web UI's Operations area (decided, not yet implemented …)" → present tense.
- `platform-implementation.md`, Architecture Reassessment: "the online-only Web UI foundation exists as development evidence, including the Analyst Manager pairing, approval, and fleet views and the match workflow …" → includes the workflow operations area.
- `platform-implementation.md`, Acceptance Evidence: "Web UI browser tests cover Analyst Manager pairing, the Club Admin decision view, and the fleet view …" → adds the Operations area (`simulated` and `platform` projects), and the Host-test evidence that the operations carry typed OpenAPI schemas.
- `AGENTS.md` "Current State" (outside `docs/architecture/`, kept consistent): the Web UI foundation description gains the workflow operations area.
- Any other statement in `docs/architecture/` that presents workflow inspection or reconciliation as API-only or its Web UI as planned.

The documentation task MUST make these narratives and every status statement
this feature affects true for the merged state, re-reading
`docs/architecture/` (and `AGENTS.md` "Current State", `src/clients/README.md`,
and the root `README.md`) at implementation time; the rewrites above are
examples, not the whole job.

## Complexity Tracking

No violations.
