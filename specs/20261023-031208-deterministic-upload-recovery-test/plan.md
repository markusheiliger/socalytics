# Implementation Plan: Deterministic Upload Recovery Browser Test

**Branch**: `20261023-031208-deterministic-upload-recovery-test` | **Date**: 2026-10-23 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261023-031208-deterministic-upload-recovery-test/spec.md`

## Summary

`refuses a different file and changes nothing (scenario 2)` asserts that the fake object store recorded no request, but its shared helper `startInterrupted` prepares the interrupted upload with a fixed 500 ms wait, a store `reset`, and a page `reload`. The upload worker of the first page keeps retrying part 3 (first retry delay about 1 s, `retryPolicy.ts`), so a retry or an in-flight request can reach the store after the reset and before the old page is gone, and be recorded as if the refusal had sent it. The plan first reproduces and evidences that cause, then removes it by controlling the racing input: wait on observable conditions (part 3 was requested and failed; the old page and its worker are gone; the store has no request in flight) and only then reset the store and open the match page again. The fake store gains one read-only counter, `inFlight`, to make the last condition observable. No assertion changes and no production code changes.

## Technical Context

**Language/Version**: TypeScript (Playwright 1.63.0 tests) and Node.js 24 ESM (`fakeObjectStorage.mjs`) in `src/clients/web`.

**Primary Dependencies**: Existing only (Playwright, Vitest). No new dependency.

**Storage**: None (the fake object store is in memory).

**Testing**: `npx playwright test --project simulated tests/e2e/simulated/recovery.spec.ts` repeated (`--repeat-each`), alone and under CPU load; `uploads.spec.ts` and `upload-shell.spec.ts` (other users of the store); Vitest `tests/unit/support/fakeObjectStorage.test.ts` for the new counter.

**Target Platform**: Linux CI runner and developer machines; Chromium.

**Project Type**: Test-only change to the Web UI client (a test, its shared helper, and the fake store).

**Performance Goals**: None; the preparation must not become slower than the upload's own retry timing allows (no wait of a retry delay).

**Constraints**: No assertion removed or loosened; no retry, fixed sleep, or longer timeout; no change of documented upload, resume, retry, or different-file refusal behavior; production retry delays unchanged.

**Scale/Scope**: `recovery.spec.ts`, `support/fakeObjectStorage.mjs` (and its `.d.mts` if the shape is typed), its unit test.

## Constitution Check

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | No design decision changes; see Architecture Updates. | Pass |
| II. Source-area ownership | Only `src/clients/web` (the owning area); no new `src` child. | Pass |
| III. API-first control plane | Not affected; the fake store only simulates object storage. | Pass |
| IV. Evidence over claims | Reproduction, repeated runs with and without load, two failing probes. | Pass |
| V. Focused, minimal changes | One helper, one test file, one counter; no abstraction or dependency. | Pass |
| Environment features | Needs only the existing Node.js, Playwright, and Chromium. No environment spec. | Pass |

Post-design re-check: Pass.

## Design

- **Evidence first** ([research.md](research.md) R1; the evidence is recorded in `recovery-evidence.md` next to the test, not under `specs/`): reproduce with repeated runs and CPU load, and log request timestamps (a temporary `ts` in the recorded request, always removed afterward) to confirm that the failing run records a part 3 request after the reset. If the evidence names another cause, the fix moves to that cause (the spec's other candidates, R3) and this design is amended through the implementation, not guessed.
- **Quiesce, then reset** (R2): `startInterrupted` waits for the two stored parts, then for the store to have recorded a part 3 request (so the failing retry cycle is under way), then for an observable sign that the upload journal (IndexedDB) already holds parts 1 and 2 (an `expect` on the upload row of the first page showing the stored-part progress or the retry status of the failing part 3; no sleep), navigates the page to `about:blank` and awaits it (this destroys the match page and terminates its dedicated upload worker, so no retry timer survives), polls until the old page reports no worker (`page.workers()` is empty), and polls the store until `inFlight` is 0. `inFlight` alone covers only requests the server has seen; a request sent just before the navigation and not yet arrived is excluded by the proof that the page and its worker are gone, and the fault stays in place until then. Only then it calls `reset` and `goto`s the match page again (the permitted equivalent of the reload, with the same resulting page state). `reset` therefore clears faults and requests that nothing of the old page can still touch, and the "no requests" assertion counts only what the refusal causes.
- **Fixed wait removed** (R2): the `waitForTimeout(500)` is deleted; every wait is `expect.poll` on a store condition or a Playwright navigation.
- **Store counter** (R4, [contracts/fake-store-control.md](contracts/fake-store-control.md)): `GET /__control/state` additionally returns `inFlight`, the number of part (PUT) requests whose handler started, counted before the body is read, and has not yet answered or closed (OPTIONS preflights are not counted). It is not cleared by `reset` (it is a live count), existing fields are unchanged, so other specs are unaffected.
- **Unchanged**: all assertions and steps of every test of `recovery.spec.ts`; the reloads inside individual tests stay (only the shared helper opens the match page with `goto` instead of `reload`, because its page is at `about:blank`); `serial` mode; the store's other behavior.

## Project Structure

### Documentation (this feature)

```text
specs/20261023-031208-deterministic-upload-recovery-test/
├── plan.md
├── research.md
├── quickstart.md
├── contracts/
│   └── fake-store-control.md
├── spec.md
├── checklists/
│   └── requirements.md
└── tasks.md             # /speckit-tasks, not created by /speckit-plan
```

### Source Code (repository root)

```text
src/clients/web/tests/
├── e2e/
│   ├── simulated/recovery-evidence.md       # the evidence of FR-001 (agents cannot change `specs/`)
│   ├── simulated/recovery.spec.ts           # startInterrupted: observable quiesce instead of the fixed wait
│   └── support/fakeObjectStorage.mjs        # inFlight counter in the state read (fakeObjectStorage.d.mts changes only if it types the state shape)
└── unit/support/fakeObjectStorage.test.ts   # covers the counter
```

**Structure Decision**: Edit the test helper and the fake store in place; no data model is needed (the only data shape is the store's state read, in the contract note).

## Architecture Updates

None. The change only makes a simulated browser test wait on observable conditions and adds a test-support counter to the fake object store. No architecture narrative, design decision, or implementation-status statement in `docs/architecture/` changes: the browser-test description in `platform-implementation.md` already names the `simulated` project and its fake object store and stays true.
