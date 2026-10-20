# Quickstart: Web UI Connected Analysis Review

Validation guide for [spec.md](spec.md). Contracts:
[routes-and-roles](contracts/routes-and-roles.md),
[analysis adapters](contracts/analysis-adapters.md),
[review states](contracts/review-states.md).

## Prerequisites

- The batch sibling
  [Accepted Fact Indexing and Capability-Scoped Fact Queries](../20261017-175648-fact-indexing-queries/spec.md)
  is merged, so the committed OpenAPI snapshot contains
  `listAnalysisRunFactStreams` and `listAnalysisRunFacts`.
- Node.js 24, Kiota 1.36.0 on `PATH`, Playwright 1.63.0 Chromium; the .NET 10
  SDK and a running Docker-compatible runtime for the `platform` projects.

## Automated validation

From `src/clients/web`:

```powershell
npm ci
npm run build                               # regenerates the Kiota client from the snapshot
npm test                                    # adapters, state derivation, paging, clock, payload summary, components
npm run test:e2e -- --project simulated     # the two analysis-review specs among the simulated specs
npm run test:e2e -- --project platform      # whole chain, including platform-analysis
```

Expected:

- Unit tests cover every row of [review states](contracts/review-states.md),
  the adapter outcome table, payload normalization, `m:ss(.SSS)` formatting and
  parsing, and that the service worker leaves the analysis paths unhandled.
- The simulated specs (`analysis-review.spec.ts` and
  `analysis-review-access.spec.ts`) open a run from the match, show run and
  node status, page through at least three fact pages forward and back without
  repeats, apply and clear a range, show each explicit state, refresh a
  running run's status within 15 seconds, refuse a member without a team role,
  pass axe checks in every state, work by keyboard, show the
  connection-required state offline, and leave no `/api/` response in Cache
  Storage and no analysis data in browser storage.
- The `platform-analysis` spec opens the review of the run created by the
  upload flow and sees its run and node status, the stream list with zero
  facts, the in-progress or incomplete message, and the not-found view for an
  unknown run identifier.

## Manual check (optional)

1. `dotnet run --project src/platform/SocAlytics.Platform.AppHost` and sign in
   as a Coach of a team with a finalized recording set.
2. Open the match, choose "Open analysis review" on a run.
3. With a Manager running the simulated Analyst (see the batch sibling's
   quickstart), wait for accepted results: the run status refreshes, the stream
   list shows fact counts, and choosing a stream shows facts with coverage and
   provenance.
4. Enter a range such as `45:00` to `90:00`, page forward and back, reload the
   page (the stream and range stay selected), and copy an image digest.
