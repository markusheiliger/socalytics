# Implementation Plan: Remove the Remaining Flakiness of the Club Structure Browser Test

**Branch**: `20261026-014846-fix-club-structure-flake` | **Date**: 2026-10-26 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261026-014846-fix-club-structure-flake/spec.md`

## Summary

Find, by reproduction, the cause that still makes the `platform-club-structure` browser test fail once and pass on its rerun after the two earlier hardenings, and remove it where it lives without retries, fixed sleeps, longer timeouts, or weakened assertions. The test already derives per-attempt names, archives active seasons before and after, and orders the absence assertion after the refreshed team list response, so the plan does not repeat those. The method and candidate list are in [research.md](research.md): the cause is a result of the implementation (FR-001) and is recorded in a code comment next to the fix. If the failure cannot be reproduced, only candidates supported by the traces and logs of the three recorded failures (#235, #284, #314) are hardened, and the comment states that the failure was not reproduced.

## Technical Context

**Language/Version**: TypeScript, React, Playwright 1.63.0 (Chromium), Node.js 24.

**Primary Dependencies**: existing `@playwright/test` and `@tanstack/react-query`; no new packages.

**Storage**: N/A. The AppHost in e2e mode holds the state (PostgreSQL); the test only creates and archives seasons and teams through the Web UI and the API.

**Testing**: in `src/clients/web`: `npm run build`, `npm test`, and `npm run test:e2e -- --project platform-club-structure` (the platform projects start the AppHost and need Docker; the project depends on `platform-navigation`, which chains back to `platform-session`).

**Target Platform**: Web UI browser tests against the platform host in end-to-end mode.

**Project Type**: web application test hardening (the test and its support code; the Web UI screens or platform code only if the evidence places the cause there).

**Performance Goals**: N/A.

**Constraints**: no retries, skipping, fixed sleeps, new or longer timeouts, or removed or loosened assertions; documented behavior of the season and team screens and the club structure operations unchanged; other platform specs that use shared support unchanged.

**Scale/Scope**: `tests/e2e/platform/club-structure.spec.ts`, its support (`tests/e2e/support/platformState.ts`, `fixtures.ts`), and, only if the evidence says so, `src/features/teams/`, `src/features/seasons/`, `src/serviceWorker/`, or platform code.

## Constitution Check

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | No design decision changes; behavior stays as documented (FR-004). | Pass |
| II. Source-area ownership | Only `src/clients/web` (and platform code only if the evidence requires it). | Pass |
| III. API-first control plane | The UI keeps using the `/api/v1` operations; no logic moves into the client. | Pass |
| IV. Evidence over claims | Pass/fail is proved by repeated runs (alone, under load, after the other platform projects) and three failure probes (SC-001, SC-002). | Pass |
| V. Focused, minimal changes | Touches only the files the evidenced cause lives in (SC-003). | Pass |
| Environment | `environment-setup` and `environment-verify` already provide Node.js 24, Playwright Chromium, and Docker for the platform projects; no new tool. | Pass |

Re-check after design: unchanged; the design adds no file, dependency, or behavior.

## Project Structure

### Documentation (this feature)

```text
specs/20261026-014846-fix-club-structure-flake/
├── spec.md
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/README.md
├── tasks.md              # /speckit-tasks
└── checklists/requirements.md
```

### Source Code (repository root)

```text
src/clients/web/
├── tests/e2e/platform/club-structure.spec.ts     # the test (assertions stay)
├── tests/e2e/support/platformState.ts            # shared state isolation, if the cause is shared state
├── tests/e2e/support/fixtures.ts                 # only if the cause is there
└── src/{features/teams,features/seasons,serviceWorker}/   # only if the evidence places the cause in the Web UI
```

**Structure Decision**: Test-first. A production file changes only when a reproduced trace shows that the screen or platform, not the test, produces the race, and then without changing its documented behavior.

## Design

1. **Reproduce first** (FR-001): run `platform-club-structure` alone (at least 20 repeats, on one AppHost started once and kept running across the dependency run and the repeats, with `CI` unset so Playwright reuses it; see [quickstart.md](quickstart.md)), under CPU load (one busy-loop process per core), and after the chained platform projects, with `--trace on`; keep the traces and platform logs of failing runs. Widen each candidate race with a temporary `page.route` delay or reorder in a scratch copy (removed afterward) to find which one fails the unchanged test.
2. **Explain why the earlier hardening was not enough** (FR-001): against the current test, show which step fails and why the existing waits, per-attempt names, and archiving did not prevent it.
3. **Fix the evidenced cause** by kind: wait on the awaited condition or control the racing input in the test; isolate shared state (the start state is established through the club structure operations before the test and cleaned up after it, also after a failure, FR-007); change a Web UI or platform file only if its behavior contradicts the documentation under concurrency.
4. **Not reproduced**: harden only candidates that the traces and logs of #235, #284, and #314 support (read with `gh run view --log` where still available), record that evidence and the missing reproduction in the code comment.
5. **Keep every assertion** and every explicit `timeout` as it is.
6. **Prove** with the SC-001 runs and the SC-002 probes, remove the probes, and run `npm run build` and `npm test`. The implementation is one task (T008) whose numbered steps do this; only the files the cause lives in are committed. Nothing is written under `specs/` by it.

## Architecture Updates

None. The change is limited to browser test code (and, only if the evidence requires, a Web UI or platform file without a behavior change), so no design decision or implementation-status statement of `docs/architecture/` becomes true or false.

## Complexity Tracking

No constitution violations.
