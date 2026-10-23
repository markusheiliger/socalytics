# Upload Recovery Test Evidence

This document is the evidence of the deterministic upload recovery test (`recovery.spec.ts`). The per-task sections below (T001 to T007, and the sections that T009 to T012 append) hold the detail; this overview merges the findings in the order the feature needs them. All commands run in `src/clients/web`.

## Overview

### Before the fix (T002)

| Command | Condition | Result |
| --- | --- | --- |
| `npx playwright test --project=simulated tests/e2e/simulated/recovery.spec.ts --workers=1 --repeat-each=30` | no load, first run | 1 helper start of the logged starts had a part 3 request after the `reset` (11 ms later); no assertion failure recorded in the filtered log |
| same command | no load, second run (stopped by the 600 s timeout at test 143 of 180) | 142 helper starts logged, none with a request after the `reset`, no failure |
| same command with `--repeat-each=10` | four busy-loop processes (one per core), stopped afterward; cut by the 400 s timeout after 49 helper starts | 3 of 49 helper starts had a part 3 request after the `reset` (24, 32, and 37 ms later); 2 failures: `lists a pending upload ... (scenario 1, 2; SC-004)` (extra sent part) and `refuses a different file and changes nothing (scenario 2)` (`Expected length: 0, Received length: 1`) |

The flakiness reproduced naturally, so no stress reproduction was needed; with load it is frequent, without load rare.

### Request log of a failing run (T002)

Times are milliseconds relative to the helper's `reset`, taken from the temporary `ts` arrival timestamp of the fake object store (removed by T003 and T005).

| Request | Arrival | Store result |
| --- | --- | --- |
| first page, part 3 | before the `reset` | injected 500 |
| first page, parts 1 and 2 | before the `reset` | stored |
| first page, part 3 retry (about 0.8 to 1.2 s after the first part 3 failure, `retryPolicy.ts`) | 11 ms after the `reset` in the unloaded run; 24, 32, and 37 ms after it in the loaded runs | recorded in the fresh store, so `requests` counts it |

### Candidates of research R3 and the evidenced cause

| Candidate | Result |
| --- | --- |
| Still-running upload retry racing the fixed `waitForTimeout(500)` and the `reset` | **Cause** (evidenced above) |
| Shared store state between serial tests | Ruled out |
| Leftover journal or sessions | Ruled out |
| Order of journal state, pending list, and refusal message after the reload | Ruled out |

The first page's upload worker is still running when the helper resets the store, and its retry of part 3 arrives after the `reset`, so the second page's start state differs. The fix (T003 counter, T005 helper) waits for observable conditions instead.

### Isolation of serial tests

The existing close order is sufficient: Playwright closes a test's context, with its pages and dedicated workers, before the next test's `beforeEach` `reset`; no log of a test started with a request of the previous test (T002), and all 180 serial runs after the fix passed (T006). The race was inside one test, so no fix of the close order was needed.

### After the fix (T006, T007)

| Command | Condition | Result |
| --- | --- | --- |
| `npx playwright test --project=simulated tests/e2e/simulated/recovery.spec.ts --workers=1 --repeat-each=30` | no load | 180 passed, no retries |
| same command with `--repeat-each=10` | four busy-loop processes (one per core), stopped afterward | 60 passed, no retries |

## T001

Tooling confirmation in `src/clients/web` for the commands of the deterministic upload recovery test quickstart.

| Item | Version or outcome |
| --- | --- |
| Node.js (`node --version`) | v24.21.0 (`engines.node` is `>=24 <25`) |
| Playwright (`npx playwright --version`) | 1.63.0 |
| Chromium (`npx playwright install chromium`) | installed, no errors |

| Command | Outcome |
| --- | --- |
| `npm ci` | succeeded (only an `esbuild` install-script advisory and an `npm audit` hint) |
| `npx playwright install chromium` | succeeded |
| `npm run build` | succeeded (only the chunk-size warning) |
| `npx playwright test --project=simulated tests/e2e/simulated/recovery.spec.ts --workers=1` | 6 passed (36.9s), one run of each test, no retries |

## T002

Reproduction of the flakiness with temporary instrumentation (the `ts` arrival timestamp in `tests/e2e/support/fakeObjectStorage.mjs` and the state logging around the helper's `reset` in `tests/e2e/simulated/recovery.spec.ts`; both are removed by T003 and T005).

| Command | Condition | Outcome |
| --- | --- | --- |
| `npx playwright test --project=simulated tests/e2e/simulated/recovery.spec.ts --workers=1 --repeat-each=30` | no load, first run | filtered log of the helper starts: in the last logged start of the helper a part 3 request arrived 11 ms after the `reset` (`T002 after-reload requests=[[3,11]]`), the other runs logged none after the `reset` |
| same command | no load, second run | stopped by the 600 s timeout at test 143 of 180; 142 helper starts logged, none with a request after the `reset`, no failure seen |
| same command with `--repeat-each=10` | four busy-loop processes (one per core), stopped afterward; run cut by the 400 s timeout after 49 helper starts | 3 of 49 helper starts logged a part 3 request after the `reset` (24, 32, and 37 ms later); `lists a pending upload ... (scenario 1, 2; SC-004)` failed on its `toEqual` of the sent parts (an unexpected extra request, `+ Received + 1`), and `refuses a different file and changes nothing (scenario 2)` failed with `Expected length: 0, Received length: 1` (a part 3 request of 8388608 bytes) |

Request log of a failing run (milliseconds relative to the helper's `reset`): the first page sent part 3 (fails with the injected 500), parts 1 and 2 (stored), and then, after its retry delay (about 0.8 to 1.2 s after the first part 3 failure, `retryPolicy.ts`), part 3 again. In the logs of the other runs the retried part 3 already appears before the `reset` (for example `[3,...],[1,...],[2,...],[3,...]` with the second part 3 about 1.2 s after the first), so the retry moment is spread over the time the helper's fixed 500 ms wait and the reload span. A retry that falls after the `reset` is recorded in the fresh store and is counted by `requests`.

Evidenced cause: the first page's upload worker is still running and its retry of part 3 races the fixed `waitForTimeout(500)` and the `reset`; a retry that arrives after the `reset` makes the second page's start state differ (a recorded request where none is expected, or `sent` unequal to `[3]`). The plan's cause is confirmed, so T003 (the `inFlight` counter) and T005 apply as planned.

Other candidates of research R3:

| Candidate | Result |
| --- | --- |
| Shared store state between serial tests | Ruled out: every helper start logged `parts=2` and a request list that begins with the test's own part 3, 1, 2 requests, so the `beforeEach` reset left no state of the previous test |
| Leftover journal or sessions | Ruled out: each test has a fresh browser context (fresh IndexedDB), and the upload sessions come from the per-test `api` fixture |
| Order of journal state, pending list, and refusal message after the reload | Ruled out as a cause: the assertions use auto-waiting `expect`; the only failing assertions were the request counts |

Isolation of serial tests (spec scenario 3): Playwright closes a test's context, with its pages and dedicated workers, before the next test's `beforeEach` runs `reset`; in all runs the logs of the next test never started with a request of the previous test (every list begins with part 3 of its own upload), so the existing close order is sufficient between tests. The race is inside one test, between the helper's `reset` and its own first page. T008 keeps this note.

## T006

Repeated runs after the fix (the `startInterrupted` helper waits for observable conditions: a part 3 request, the journal entry with parts 1 and 2, the first page and its worker gone, and `inFlight` 0 before the `reset`).

| Command | Outcome |
| --- | --- |
| `npx playwright test --project=simulated tests/e2e/simulated/recovery.spec.ts --workers=1 --repeat-each=30` | 180 passed (10.2m): all 30 runs of each of the 6 tests, no failures, no retries |

Spec scenario 3 (no fault or activity of one serial test affects the next): all 180 runs passed in serial order with the unchanged `beforeEach` reset, so no request or fault of a previous test changed the next test's start state. This confirms the T002 note: Playwright closes a test's context, with its page and dedicated worker, before the next `beforeEach` runs, so the existing close order is sufficient between tests, and the race fixed by T005 was inside one test.

## T007

Repeated runs after the fix under CPU load (four busy-loop processes, one per core, started before the run and stopped afterward).

| Command | Condition | Outcome |
| --- | --- | --- |
| `npx playwright test --project=simulated tests/e2e/simulated/recovery.spec.ts --workers=1 --repeat-each=10` | four busy-loop processes (one per core) for the duration | 60 passed (8.2m): all 10 runs of each of the 6 tests, no failures, no retries |

The same load made 3 of 49 helper starts of the old helper record a part 3 request after the `reset` (T002); with the fixed helper no run failed (SC-001).

## T009

Other users of the unchanged Web UI upload code and helpers still pass unchanged (FR-006, SC-002); neither spec file nor any production file was modified.

| Command | Outcome |
| --- | --- |
| `npx playwright test --project=simulated tests/e2e/simulated/uploads.spec.ts tests/e2e/simulated/upload-shell.spec.ts` | 11 passed (1.2m), no failures, no retries |
| `npm test` | 125 test files and 1267 tests passed (110.8s) |
