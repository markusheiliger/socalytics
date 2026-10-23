# Quickstart: Web UI Analysis Fact Timeline

Validation guide for the timeline in the connected analysis review. Run from `src/clients/web` unless stated otherwise (Node.js 24, Kiota 1.36.0 on `PATH`, Playwright 1.63.0 Chromium; see the repository `README.md`).

## Prerequisites

```bash
npm ci
```

The `simulated` browser project needs no Docker. The `platform` project starts the AppHost in e2e mode and needs a Docker-compatible container runtime.

## 1. Unit tests: derivation, states, bounds, components

```bash
npm test -- tests/unit/features/analysis/timeline tests/unit/features/analysis/analysisKeys.test.ts
```

Expected: density bins count overlapping facts once per bin, partial coverage and unknown values are never zero, the bound marks lanes partial, zoom and extent movement, selection range, the text alternative equals the graphic's data, and all lane states render their own labels.

## 2. Simulated browser project

```bash
npm run test:e2e -- --project simulated analysis-timeline
```

Expected scenarios (see [spec.md](spec.md) user stories): lanes per stream over a shared axis with extra-time labels, zoom and extent, the four distinct coverage states and an in-progress lane, the jump to the fact list with exactly the selected range in the address and the "Back to timeline" return with zoom and extent kept, keyboard-only operation, axe checks in every state, role-based access, offline connection-required state, and no timeline data in storage or service-worker caches after leaving or access loss.

## 3. Platform browser project

```bash
npm run test:e2e -- --project platform analysis-timeline
```

Expected: a Coach opens the review of a run with accepted facts against the running platform, sees the timeline, jumps from a bin to the fact list, and sees exactly the facts overlapping the bin; the lane's totals agree with the stream's reported fact count.

## 4. Build and repository checks

```bash
npm run build
```

```bash
node .github/scripts/check-markdown.mjs
```

The second command runs from the repository root. The API, the OpenAPI snapshot, and the contracts do not change, so the platform and contract test suites are not part of this validation.

## Interfaces

No new platform operation or contract exists: the timeline consumes `listAnalysisRunFactStreams` and `listAnalysisRunFacts` through the existing adapters; the data shapes are in [data-model.md](data-model.md).
