# Implementation Plan: Web UI Analysis Run Comparison

**Branch**: No branch created; planning on `main`. Feature identifier:
`20261022-190935-web-ui-run-comparison`.

**Date**: 2026-10-22 | **Spec**: [spec.md](spec.md)

**Input**: `specs/20261022-190935-web-ui-run-comparison/spec.md`

## Summary

Add one connected, read-only Web UI route,
`/matches/:matchId/analysis-comparison?left=:runId&right=:runId`, reachable
from the match and each terminal run review. Members choose two distinct
terminal runs without replacing history with the current run. Display their
identities, current/superseded metadata, recording sets, workflows, stream
counts, output coverage, input coverage, and pinned provenance, with links
back to each existing review.

Reuse the existing authorized match, team, run, stream, and recording-set
reads through application-owned adapters. Extend the run-status mapping to
retain fields the API already reports: supersession and node input coverage.
Do not add endpoints, change the platform, fetch fact payloads, or introduce
dependencies. Align streams only when capability identity and execution scope
are unique on both sides; show every other stream independently. Treat
availability and incompleteness separately from fact counts.

Fresh authorized reads form a comparison-specific display barrier on entry,
pair change, refresh, return, and reconnection. No cached successful pair is
rendered while that barrier is pending or failed. All comparison data is
memory-only; access loss clears the pair, not just its visible panel.

See [research.md](research.md), [data-model.md](data-model.md),
[UI contract](contracts/comparison-ui.md),
[read contract](contracts/member-reads.md), and [quickstart.md](quickstart.md).

## Technical Context

**Language/Version**: TypeScript 5.9.3, strict mode; Node.js 24. The platform
prerequisite uses its existing pinned .NET 10 SDK.

**Primary Dependencies**: Existing React 19.3, React Router 7.18, TanStack
Query 5.104, MUI 7.3, i18next 26/react-i18next 17, Kiota TypeScript runtime,
and Vite 7.3. Kiota 1.36.0 regenerates the client from the unchanged committed
OpenAPI snapshot. No new dependency or framework.

**Storage**: Comparison results, coverage, provenance, candidates, and request
state in memory only. URL holds match/run identifiers only. No database,
browser-storage schema, persistence plugin, or service-worker data cache.

**Testing**: Existing Vitest 4/Testing Library; Playwright 1.63.0 Chromium,
axe and keyboard helpers; `simulated` and the real-platform browser chain.
Use cancelled terminal runs for deterministic real-platform comparison
navigation and lineage evidence without requiring successful Analyst execution.
Rich result/coverage/provenance fixtures are deterministic simulated evidence.

**Target Platform**: Existing Web UI browser baseline; automated Chromium
on the current verification environment. Responsive, keyboard-accessible
layout with explicit left/right headings.

**Project Type**: Browser SPA feature consuming the existing member API.

**Performance Goals**: No fact-page or raw-object requests; load exactly two
run statuses, two stream summaries, and at most two distinct recording sets
per validated pair, plus match/team authorization context. Candidate history
is paged on demand, not downloaded as fact payloads. Rendering and alignment
account for every returned stream once per side without a Cartesian product.
No new timing SLO is adopted; acceptance is SC-001 through SC-007.

**Constraints**: Only `Completed`, `PartiallyCompleted`, `Failed`, and
`Cancelled` are terminal. Unknown states are not eligible. No automatic
terminal-run polling, semantic matching, quality ranking, run controls,
charts, media changes, export, sharing, agents, MCP, or offline data.
English ships through existing translation keys; WCAG 2.2 AA automated
checks plus keyboard evidence. Equal counts or recording sets do not prove
equal full inputs or equivalent outputs.

**Scale/Scope**: One route, match/review entry links, a small comparison hook
and pure presentation helpers in the existing analysis feature, adapter/model
extensions, translations, focused unit/browser evidence, and documentation.
No platform, Manager, SDK, root-contract, or environment-action changes.

## Constitution Check

*Pre-research gate: PASS. Post-design gate: PASS.*

| Gate | Result and evidence |
| --- | --- |
| I. Architecture authority | Implements connected review comparison in the existing client boundary. The Architecture Updates section names design/status changes and an outcome-based documentation task. Historical execution and authorization remain unchanged. |
| II. Source ownership | Product changes stay under `src/clients/web/`; no new first-level source area or project. |
| III. API-first | Existing member reads only, through Kiota-backed adapters. UI alignment is presentation, not a new business rule. No direct database/object access or media proxy. |
| IV. Evidence over claims | Unit, simulated browser, and real-platform navigation/lineage evidence are required. Rich simulated coverage is not claimed as real Analyst or production evidence. |
| V. Focused changes | No new dependencies, endpoint, persistence, background poller, or unrelated review refactor. Existing match cancellation, fact browsing, and triage behavior stay intact. |
| Tooling prerequisite | The existing setup/verify actions provide Node.js, pinned Kiota and Playwright Chromium, .NET, Docker-backed platform execution, Markdown tools, and the existing Web UI scripts. No environment spec is needed and neither action changes. |

Post-design review confirms all Technical Context questions are resolved in
[research.md](research.md). No constitution exception is needed.

## Architecture Updates

### Narratives

- [Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui):
  comparison entry, eligibility, presentation, authorization freshness,
  accessibility, and online-only storage boundary.
- [Job Processing → Run State And Recovery](../../docs/architecture/job-processing.md#run-state-and-recovery):
  terminal-run eligibility and immutable accepted history; current and
  superseded remain separate metadata.
- [Analysts, Models, and Hardware → Capability-Scoped Result Access](../../docs/architecture/analysts-models-and-hardware.md#capability-scoped-result-access):
  independently authorized stream summaries and run-pinned provenance,
  not payload downloads or a cross-run server read.
- [Platform Implementation → Acceptance Evidence](../../docs/architecture/platform-implementation.md#acceptance-evidence)
  and [Architecture Reassessment](../../docs/architecture/platform-implementation.md#architecture-reassessment):
  the browser evidence inventory and qualified development implementation
  status.
- [Match Data Pipeline → Segment And Match Analysis Scope](../../docs/architecture/match-data-pipeline.md#segment-and-match-analysis-scope):
  reviewed for its existing review-consumer statement. Comparison consumes
  metadata, not the fact payload stream; "first consumer" remains true and
  needs no prescribed rewrite.

### Design Updates Applied Now

The owning client narrative now records this comparison as **decided, not
yet implemented**, with match/review entry, exactly two distinct terminal
same-match runs, unique capability/scope alignment, honest coverage and
provenance, fresh authorized reads, and memory-only online presentation.
Existing comparison exclusions explicitly describe the current implementation,
not a rejection of the new design.

The job-processing narrative now records the decided read-only terminal
comparison consumer without changing cancellation, supersession, accepted
results, or same-recording-set behavior. The Analyst narrative now records
the decided summary consumer and its independent per-run access boundary,
with no new endpoint, payload reconciliation, or equivalence claim.

At implementation, turn those comparison status qualifiers into evidenced
present tense. No technology, deployment topology, server schema, retention
policy, execution rule, or production decision changes.

### Status Statements At Merge

| Narrative and current wording | Required merged outcome |
| --- | --- |
| Client Applications → Web UI: "run comparison is decided but not yet implemented" in the workflow inventory | Separate implemented metadata comparison from still-deferred visualizations, Coach Agent conversations and consultations. |
| Client Applications → Web UI: review "offers no ... cross-run comparison" | Preserve the single-run fact review behavior but link to the implemented separate comparison route; retain no charts/export and the independent media deferrals. |
| Client Applications → Web UI: new comparison paragraph "decided and not yet implemented" | Describe implemented connected comparison with its actual simulated and real-platform development evidence, selection, limitations and online-only lifecycle. |
| Job Processing → Run State And Recovery: new comparison consumer "decided but not yet implemented" | State that the Web UI consumes terminal history read-only, without altering state or supersession. |
| Analysts, Models, and Hardware → Capability-Scoped Result Access: new summary consumer "decided but not yet implemented" | Name implemented comparison of independently authorized stream summaries/provenance; retain per-run boundaries and no payload download. |
| Platform Implementation → Acceptance Evidence: analysis review list ends with "online-only rule" | Add comparison selection/link evidence, metadata/coverage distinctions, refusal/freshness, accessibility and non-persistence, distinguishing simulated rich fixtures from platform terminal metadata/navigation. |
| Platform Implementation → Architecture Reassessment: "connected analysis review of a run's status and indexed facts" | Include connected two-run metadata comparison in development evidence, without implying full Analyst or production readiness. |

**Required documentation task for `/speckit-tasks`**: Add an outcome-based
task referencing `docs/architecture/` that re-reads all affected narratives
at implementation time and makes every design and status statement true for
the merged implementation, including summaries and exclusions beyond the
listed examples. Update directly related README/AGENTS capability summaries
where they still exclude comparison. Preserve unrelated pending evidence
playback, visualizations, evaluation, offline data, production values, and
production-readiness deferrals. The rewrites above are examples, not a limit
on that task's responsibility.

## Project Structure

### Documentation (this feature)

```text
specs/20261022-190935-web-ui-run-comparison/
  spec.md
  plan.md
  research.md
  data-model.md
  quickstart.md
  contracts/
    comparison-ui.md
    member-reads.md
```

`tasks.md` is generated later by `/speckit-tasks`, not by this command.

### Source Code (repository root)

```text
src/clients/web/
  src/
    router.tsx
    platform/
      models/analysis.ts
      adapters/analysisRunAdapter.ts
      adapters/analysisFactAdapter.ts
    features/
      analysis/
        AnalysisRunPage.tsx
        AnalysisComparisonPage.tsx
        useAnalysisComparison.ts
        comparisonPresentation.ts
        ComparisonStreams.tsx
        ProvenanceDetails.tsx
      lineage/AnalysisRunStates.tsx
    i18n/en/analysis.json
  tests/
    unit/platform/adapters/
    unit/features/analysis/
    e2e/
      support/simulatedApi.ts
      simulated/analysis-comparison.spec.ts
      simulated/analysis-comparison-access.spec.ts
      platform/run-supersession.spec.ts
```

**Structure Decision**: Keep comparison in the existing analysis feature.
Reuse `ProvenanceDetails`, run-state labels, failure/refusal components,
session and connectivity infrastructure. Extend the existing real-platform
supersession scenario with a second terminal run and comparison assertions,
so no new test project, project ordering dependency, or fixture process is
needed. Add small components only where the page would otherwise duplicate
presentation; listed new filenames are implementation targets, not a mandate
to split simple rendering further.

## Implementation Approach

1. Preserve run-status supersession and nullable input coverage in the
   application model. Validate required metadata for comparison rather than
   using the existing summary mapper's legacy defaults. Distinguish an
   explicitly empty stream collection from an omitted collection.
2. Add a paged two-run selection form with stable left/right identities.
   Use `listByMatch` and its continuation token; never use the existing
   twenty-page `loadAllRuns` helper as proof that all history was loaded.
   An entry from review fixes that run as the initial left side; the member
   explicitly chooses the other side. A later explicit edit may change either.
3. Implement a comparison-specific load hook with a dedicated query-key
   prefix and request generation. Freshly read match/team context, then both
   run statuses, validate IDs/team/match/terminal state, then read streams
   and the exact recording sets. Do not publish a completed pair until all
   required reads for that generation succeed. Discard late completions
   after selection, session, access, lifecycle, or connection changes.
4. Revalidate on mount, browser history return, visible window return,
   explicit refresh/retry, and reconnection. Suspend display synchronously
   when revalidation is required; do not rely on invalidation alone, stale
   query data, or a successful `/alive` probe. Terminal state does not make
   mutable current/superseded labels permanently fresh.
5. On offline/access loss clear comparison data and pending work; session
   end also uses the existing global cache clear. Use comparison-scoped
   query cleanup with zero cache retention after unmount. Read through
   adapters inside this scope instead of populating reusable shared
   run/stream/lineage caches with comparison results.
6. Build deterministic unique-key alignment and per-side availability
   descriptions as pure helpers, with contract, provenance, recording-set,
   workflow, and reported input-coverage differences displayed explicitly.
   Missing metadata fails visibly; no missing value becomes zero or false.
7. Add focused fixtures and tests for every FR-018 category and SC-001
   through SC-007; preserve existing review, cancellation, and supersession
   regression coverage. Finish the architecture/documentation task below.

## Complexity Tracking

None. No constitution violations, new services, dependencies, or persistence.
