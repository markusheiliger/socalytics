# Implementation Plan: Deterministic Keyboard Paging Test of the Connected Analysis Review

**Branch**: `20261028-235115-deterministic-analysis-paging-test` | **Date**: 2026-10-28 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/archive/20261028-235115-deterministic-analysis-paging-test/spec.md`

## Summary

The Playwright test "has no violations with facts, and every control is reachable by keyboard with visible focus" in `src/clients/web/tests/e2e/simulated/analysis-review-access.spec.ts` activates "Next page" before the range-filtered page has replaced the unfiltered one. Code analysis (see [research.md](research.md)) puts the cause in the test: after "Apply range" the test's only waits are the address and `toHaveCount(2)`, and the unfiltered first page already holds two facts, so the count is satisfied by the previous selection. The review then swaps the facts list and the page navigation for a loading state and mounts new ones, so the control the test focused is detached or disabled. The fix lives in the test: wait for the filtered first page (its first fact is `pass-1`, the unfiltered one is `pass-0`) and for "Next page" to be enabled before acting, keeping every assertion and accessibility check unchanged. The page is changed only if the repeated-run evidence the implementation gathers contradicts this.

## Technical Context

**Language/Version**: TypeScript 5 on Node.js 24 (Web UI in `src/clients/web`)

**Primary Dependencies**: Playwright 1.63.0 Chromium (existing); no new dependency

**Storage**: N/A

**Testing**: Playwright `simulated` project (`npm run test:e2e -- --project simulated`, no Docker); Vitest unit tests for `useFactPages` stay unchanged; the rest of the simulated suite and Vitest are verified by the Phase 2 verification, not by a task

**Target Platform**: Browser tests of the Web UI on the Linux verification runner

**Project Type**: Web client test change (no product behavior change expected)

**Performance Goals**: N/A

**Constraints**: No retries, fixed sleeps, longer timeouts, or forced actions; no assertion or accessibility check removed, loosened, skipped, or moved; no platform, contract, or OpenAPI change

**Scale/Scope**: One test of one spec file; shared support under `tests/e2e/support/` only if the evidence requires it

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- I. Architecture Is the Design Authority: PASS. No design decision changes; `## Architecture Updates` below states `None` with a reason.
- II. Source-Area Ownership: PASS. Everything stays in `src/clients/web` and this spec folder.
- III. API-First Control Plane: PASS. No API, contract, or OpenAPI change.
- IV. Evidence Over Claims: PASS. The cause is established with evidence first, and the quickstart's load, delay, and probe runs prove determinism and that the assertions still bite. Results are development evidence only.
- V. Focused, Minimal Changes: PASS. One test is changed; no abstraction or dependency is added.
- Environment: PASS. The `simulated` Playwright project and Node.js 24 are already provided by `environment-setup` and `environment-verify`; no environment spec is needed. The spec's dependent `specs/20261023-085421-cv-inference-environment` waits for this one.

Post-design re-check: PASS, unchanged.

## Project Structure

### Documentation (this feature)

```text
specs/archive/20261028-235115-deterministic-analysis-paging-test/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
└── tasks.md             # /speckit-tasks, not created by /speckit-plan
```

No `contracts/` folder: the feature exposes no interface.

### Source Code (repository root)

```text
src/clients/web/
├── tests/e2e/simulated/analysis-review-access.spec.ts   # changed: wait for the filtered page before paging
├── tests/e2e/support/                                   # unchanged unless the evidence requires it
└── src/features/analysis/{FactBrowser.tsx,useFactPages.ts}   # unchanged unless the evidence puts the cause here
```

**Structure Decision**: The fix is confined to the one spec file in the existing Web UI browser-test tree.

## Complexity Tracking

No constitution violations.

## Architecture Updates

None. The feature changes only a browser test of the connected analysis review (or, if the evidence contradicts the expected cause, a defect of the page that keeps the documented contract); it adds no capability, changes no design decision, and makes no statement in `docs/architecture/` true or false.
