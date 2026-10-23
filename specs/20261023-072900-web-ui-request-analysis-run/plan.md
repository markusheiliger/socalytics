# Implementation Plan: Web UI Request of a New Analysis Run

**Branch**: `20261023-072900-web-ui-request-analysis-run` | **Date**: 2026-10-23 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261023-072900-web-ui-request-analysis-run/spec.md`

## Summary

Add a connected "Request new analysis" action to the analysis run list of the match page
(`AnalysisRunStates`) for Club Admins and Coaches of the owning team. The action opens a confirmation dialog, then calls
the member operation `requestAnalysisRun` (`POST /api/v1/matches/{matchId}/analysis-runs`, added and fully specified by
[Explicit Analysis Run Request](../20261023-015644-explicit-analysis-rerun/spec.md)) through the generated Kiota client
with an in-memory `Idempotency-Key` per confirmation. The run list is read again after an accepted request; every
refusal is mapped from the platform's problem result to a plain-language message. The Web UI decides no rule and adds no
platform operation, contract, or schema. Delivery is a Web UI adapter method, a small failure-mapping extension, one
dialog and outcome component, a shared intent-key hook, English texts, the fake platform's operation, unit tests, and
`simulated` and `platform` browser tests.

## Technical Context

**Language/Version**: TypeScript 5 with React 19 and MUI in `src/clients/web` (Node.js 24)

**Primary Dependencies**: existing TanStack Query, react-i18next, Kiota-generated client; no new package

**Storage**: N/A (memory only; no durable storage, service-worker cache, or logging of run data or keys)

**Testing**: Vitest unit tests for the adapter, outcome and refusal mapping, and the intent-key rule; Playwright
`simulated` project (fake platform in `tests/e2e/support/simulatedApi.ts`) and `platform` project (real AppHost) with the
existing axe and keyboard helpers

**Target Platform**: Browser Web UI (installable PWA shell), served on one origin with the API

**Project Type**: web-application client (one feature inside the existing Web UI; no new project)

**Performance Goals**: none beyond the platform operation's own; the run list is read once after an accepted request
and then follows the existing refresh rule

**Constraints**: online only; no queueing or background retry; no run data, outcome, or key beyond page memory; no
client-side eligibility, deduplication, supersession, or authorization rule; accessible without colour alone

**Scale/Scope**: one action, one dialog, one outcome area, one adapter method, roughly eight refusal explanations

**Dependency**: the generated client and committed OpenAPI snapshot are refreshed by the not-yet-implemented
[Explicit Analysis Run Request](../20261023-015644-explicit-analysis-rerun/spec.md); this feature starts only after that
feature is merged (twin dependency) and changes neither. All items are resolved; see [research.md](research.md).

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Assessment |
| --------- | ---------- |
| I. Architecture is the design authority | Pass. The Web UI narrative and the run state description are corrected in the documentation task; see Architecture Updates. |
| II. Source-area ownership | Pass. All code is in `src/clients/web` (`features/lineage`, `platform`, `i18n`, `tests`); no new `src/` child, project, or directory beyond files of the feature. |
| III. API-first control plane | Pass. The Web UI is a consumer of the `/api/v1` operation through the generated client; no business rule, database access, or video bytes in the client. |
| IV. Evidence over claims | Pass. Unit, `simulated`, and `platform` browser tests are planned; nothing claims production readiness. |
| V. Focused, minimal changes | Pass. No new dependency; the existing intent-key rule is extracted for reuse instead of duplicated; no API change. |
| Tooling | Pass. Node.js, Kiota, and Playwright are provided by the existing `environment-setup` and `environment-verify` actions; no environment spec is needed. |

**Post-design re-check**: Pass. The design adds no dependency, contract, persisted state, or platform change.

## Architecture Updates

### Narratives

- [Client Applications](../../docs/architecture/client-applications.md#web-ui): the Web UI's capability list (the
  match page's analysis run list gains the confirmed "Request new analysis" action for Club Admins and Coaches of the
  owning team), the connected analysis review paragraph, and the run comparison paragraph's list of exclusions.
- [Job Processing](../../docs/architecture/job-processing.md#run-state-and-recovery) (Run State And Recovery) and
  [Durable Workflow Model](../../docs/architecture/job-processing.md#durable-workflow-model): the member-initiated run
  controls described there (cancellation, supersession) gain the member's explicit request as a Web UI action over the
  operation added by the explicit-request feature.
- [Platform Implementation](../../docs/architecture/platform-implementation.md): the Web UI implementation evidence, if
  it lists the browser-test coverage of the match page's run list.

### Design updates

None applied to `docs/architecture/` now: the feature adds no component, technology, topology, protocol, or contract. The
platform operation and its architecture statements belong to the explicit-request feature; the Web UI's mapping of
answers to texts ([contracts/ui-outcomes.md](contracts/ui-outcomes.md)) and the shared intent-key rule are
implementation-level.

### Status statements at merge

- `client-applications.md`, Web UI capability list: the match-page statement ends with the cancel action, current and
  superseded markers; it must also say that the run list offers Club Admins and Coaches of the owning team a confirmed
  request of a new analysis run (online only, outcomes and refusals explained from the platform's answer, idempotency key
  in memory only), marked implemented.
- `client-applications.md`, connected analysis review paragraph: "the current implementation offers no run controls,
  visualizations, cross-run comparison, or export" must distinguish the review (which has none) from the match page's
  run list, which offers cancel and request actions.
- `job-processing.md`, Run State And Recovery and Durable Workflow Model: the descriptions of member-initiated run controls
  must name the Web UI's request action next to the cancel action, without claiming platform behavior this feature does
  not add.
- `client-applications.md`, run comparison paragraph ("no ... run controls"): stays true; check it is not read as
  contradicting the request action.

The documentation task MUST make the listed narratives and every status statement this feature affects true for the
merged state, re-reading `docs/architecture/` at implementation time (including summaries, introductions, and lists of
what does not exist); the rewrites above are examples, not the whole job.

## Project Structure

### Documentation (this feature)

```text
specs/20261023-072900-web-ui-request-analysis-run/
├── plan.md              # This file (/speckit-plan command output)
├── research.md          # Phase 0 output (/speckit-plan command)
├── data-model.md        # Phase 1 output (/speckit-plan command)
├── quickstart.md        # Phase 1 output (/speckit-plan command)
├── contracts/
│   └── ui-outcomes.md   # Phase 1 output: what the Web UI shows per platform answer
└── tasks.md             # Phase 2 output (/speckit-tasks command - NOT created by /speckit-plan)
```

### Source Code (repository root)

```text
src/clients/web/
├── src/
│   ├── features/
│   │   ├── lineage/
│   │   │   ├── AnalysisRunStates.tsx          # action, outcome area, pending state, re-read
│   │   │   ├── RequestAnalysisDialog.tsx      # new: confirmation (like CancelRunDialog)
│   │   │   └── requestAnalysisOutcome.ts      # new: answer and refusal mapping to message keys
│   │   └── shared/intentKey.ts                # new: the idempotency-key rule extracted from useQuestionDraft
│   ├── features/triage/useQuestionDraft.ts    # uses the shared rule (behavior unchanged)
│   ├── platform/
│   │   ├── adapters/analysisRunAdapter.ts     # requestRun(matchId, key)
│   │   ├── models/recordings.ts               # AnalysisRunRequestOutcome model
│   │   └── failure.ts                         # keeps the problem category for workflow-unresolvable
│   └── i18n/en/lineage.json                   # confirmation, outcome, and refusal texts
└── tests/
    ├── unit/{features/lineage,platform}/      # mapping, adapter, key rule
    └── e2e/
        ├── support/simulatedApi.ts            # fake `POST .../analysis-runs` with scripted answers
        ├── simulated/run-request.spec.ts
        └── platform/run-request.spec.ts
```

**Structure Decision**: Extend the existing lineage feature folder and adapter layer, mirroring the cancel action
(`CancelRunDialog`, `analysisRunAdapter.cancel`). The intent-key rule moves from `useQuestionDraft.ts` to a shared module
so both features use one implementation; triage behavior and tests stay unchanged.

## Complexity Tracking

No constitution violations.
