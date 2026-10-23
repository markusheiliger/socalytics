# Validation Guide: Web UI Analysis Run Comparison

This guide describes implementation acceptance, not evidence that the
currently planned feature already exists. Model and state expectations are
in [data-model.md](data-model.md) and [contracts/](contracts/).

## Prerequisites

Use Node.js 24, Kiota 1.36.0 on `PATH`, the repository-pinned .NET SDK, and
Playwright 1.63.0 Chromium. Real-platform browser evidence requires a running
Docker-compatible runtime and the existing AppHost e2e composition.
Never point these tests at developer or production databases.

From the repository root, restore Web UI dependencies when setting up a fresh
checkout:

```powershell
Set-Location 'src\clients\web'
npm ci
npm run build
npm test
```

The build generates Kiota from the unchanged committed snapshot. No OpenAPI
snapshot refresh, new environment action, or new package is required.

## Simulated Comparison

From `src\clients\web`:

```powershell
npm run test:e2e -- --project simulated
```

Add comparison coverage to the existing simulated API fixture. The acceptance
matrix must include:

| Scenario | Required observable outcome |
| --- | --- |
| Admin, owning-team Coach and Viewer; match and terminal-review entry | Two explicit eligible selections; review-origin left ID retained; both detail links open the exact selected run. |
| Current and superseded history, same recording set, current run still running | Eligibility depends only on known terminal state; no history replacement or invented supersession. |
| Fewer than two eligible candidates and paginated history | Explanation with no guessed pair; older pages remain reachable; no exhaustive insufficiency claim before exhaustion. |
| Unique capability/scope, changed node/version/schema/type/provenance | Unique rows align but every field difference remains explicit. |
| Left-only, right-only, repeated capability/scope | Every source stream appears exactly once per side; zero guessed pairings and no synthetic zero counterpart. |
| Different workflows, recording sets and reported node input coverage | Differences shown independently; equal counts never claim equal snapshots or better quality. |
| No accepted results; zero expected units; accepted complete zero facts | Distinct explanations; only accepted positive coverage with no incomplete units qualifies as complete zero-fact analysis. |
| Accepted partial zero/positive facts; all-skipped/failed/blocked/missing | Counts and incomplete/skipped explanations retained; no zero-as-success fallback. |
| No streams versus missing collection/required metadata; absent optional model digest | Explicit empty workflow, incompatible metadata, and model-not-reported remain different states. |
| Duplicate/cross-match/nonterminal/unknown-state IDs; unrelated member | No completed comparison or unauthorized details; preserve concealed not found. |
| One-side delayed/failed reads; pair changes with older responses last | Explicit loading/error; recoverable retry; no old data under new headings. |
| Manual refresh, navigation return, window return, newly superseded run | Both sides freshly authorized and current/superseded metadata updated without changing IDs. |
| Detected team access loss or ended session | Both sides and reusable comparison state removed; late responses cannot resurrect them. |
| Offline then reconnect with authorized reads delayed or refused | Zero result detail offline and while revalidation is pending/failed, even after a successful health probe. |

Assert SC-002 by counting rendered stream identities against both fixture
inputs, including ambiguous groups. Assert SC-004 and SC-006 with zero-detail
and zero-persisted-data expectations, not just checking a warning exists.
Exercise reordered stream arrays and collision-like capability/scope strings.

Run axe WCAG 2.2 AA and keyboard helpers on selection, ready/aligned,
unmatched, empty, incomplete, loading, offline, refused, incompatible and
recoverable-error states. Test narrow layouts and long inspectable identifiers.
Inspect local/session storage, IndexedDB and Cache Storage for fixture marker
values in responses, coverage, and provenance. Compare memory/cache cleanup
and late-response behavior in unit tests as well; storage inspection alone
does not prove access-loss cleanup.

## Real-Platform Connected Evidence

From `src\clients\web`:

```powershell
npm run test:e2e -- --project platform-supersession
```

Keep the configured project dependencies; do not bypass shared platform
setup. Extend the existing `platform/run-supersession.spec.ts` scenario:

1. Create its own season/team/match, upload and finalize, and wait for the
   first run. Cancel it through the existing UI so it is terminal.
2. Finalize the next recording-set version and wait for the second run;
   verify the first is superseded without changing its terminal state.
3. Cancel the second run if still active and wait for a known terminal state.
   Open comparison from the match, select both IDs, and verify recording
   sets/workflows, current/superseded attribution, and explicit result
   availability without requiring facts to exist.
4. Follow both detail links and return. Start again from one terminal
   review and verify that its ID remains the initial left selection.
5. Use the existing active-season cleanup. Keep all earlier cancellation
   and supersession assertions intact.

This is real connected metadata/navigation evidence, not a claim of rich
completed Analyst outputs. Rich coverage/contract/provenance and role/refusal
matrices are deterministic simulated evidence, supported by adapter tests and
the existing server authorization tests.

Run the whole existing browser chain before delivery:

```powershell
npm run test:e2e
```

Existing fact review, match cancellation and triage must retain their behavior.

## Documentation Gate

From the repository root after the implementation documentation task:

```powershell
node .github\scripts\check-markdown.mjs
```

Correct every architecture statement that delivery makes false, not just the
new comparison paragraph. Comparison is described as implemented only with
its actual development evidence; no visualization, evaluation, production
profile, or production-readiness deferral is removed.
