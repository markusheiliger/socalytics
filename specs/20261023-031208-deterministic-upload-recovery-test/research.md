# Research: Deterministic Upload Recovery Browser Test

## Decisions

### R1 - Establish the cause before fixing it

- **Decision**: First run `recovery.spec.ts` repeatedly (`--repeat-each=30`, `--workers=1`) alone and under CPU load (one busy-loop process per core), keeping traces on failure, and read the store's recorded requests with a temporary arrival timestamp from a failing run. The cause is confirmed when a failing run records a part 3 request that arrives after the helper's `reset`.
- **Fallback**: If the failure does not reproduce naturally, widen the race on purpose, test-side only and removed afterward (the store's existing `delayMs` fault or a temporarily lowered retry delay), and show that the old helper then fails and the new helper passes; record both in the Evidence section.
- **Rationale**: FR-001 requires evidence. The retry delays (`retryBaseDelaysMs` 1000 ms first, jitter 20 %, `src/clients/web/src/uploads/retryPolicy.ts`) mean the first page's worker sends the next part 3 request about 0.8 to 1.2 s after the previous failure; the helper's fixed 500 ms plus a slow reload under load can span that moment.
- **Alternatives considered**: Fixing from the hypothesis alone (rejected: not evidence); using production-code instrumentation (rejected: out of scope).

### R2 - Control the racing input; no sleep, retry, or timeout

- **Decision**: Quiesce the first page, then reset: wait for a part 3 request to be recorded, wait on an observable sign that the upload journal (IndexedDB) holds parts 1 and 2 (an `expect` on the first page's upload row, with role and text taken from its actual markup, showing the retry text for the failing part 3, which is only visible after the journal write; if the markup has no such state, reading the journal entry from the page's IndexedDB with `page.evaluate` before navigating; no sleep), navigate to `about:blank` and await it (kills the page and its dedicated worker, so no retry timer survives), poll until the old page reports no worker, wait for the store's `inFlight` to be 0, then `reset` and open the match page.
- **Rationale**: Navigating away removes the source of later requests; the journal condition replaces the persistence time the fixed wait gave; `inFlight` covers a request that left before the navigation and is still being handled. `inFlight` alone covers only requests the server has seen, so a request sent just before the navigation and not yet arrived is excluded by the proof that the page and its worker are gone, with the fault in place until then. Each wait is an observable condition (FR-002, FR-003). The fault stays in place until the page is gone, so a late request cannot even be stored.
- **Alternatives considered**: Waiting out the retry delay (a timing guess); a longer wait (forbidden); filtering requests by timestamp in the assertion (weakens the assertion); making the store drop requests after reset (changes store semantics for other specs).

### R3 - Other candidates the evidence must rule in or out

- **Decision**: Check shared store state between serial tests (the `beforeEach` reset runs before the page of the test exists, and Playwright closes the previous page first), leftover journal or sessions (each test has a fresh browser context, hence fresh IndexedDB), and the order of journal state, pending list, and refusal message after reload (the assertions use auto-waiting `expect`).
- **Rationale**: The spec requires them to be ruled in or out explicitly; the evidence records the result of each.
- **Alternatives considered**: Ignoring them (rejected by the spec).

### R4 - `inFlight` counter in the fake store

- **Decision**: Add `inFlight` to the state read of `fakeObjectStorage.mjs`, incremented at the start of the handler of every PUT part request before the body is read (OPTIONS preflights are not counted) and decremented in a `finally` or on both response `finish` and connection `close`.
- **Rationale**: A request already sent by the old page is invisible to the test otherwise; the counter is a minimal, additive, observable condition. Existing fields stay, so `uploads.spec.ts`, `upload-shell.spec.ts`, and the Vitest tests stay unchanged in behavior (FR-006).
- **Alternatives considered**: Inspecting browser network events (worker requests are not reliably routed or reported, as the store's header comment notes); polling `requests.length` for stability (a timing guess).

## Evidence

To be written by the implementation (FR-001): the tasks that only run commands (T001, T006, T007, T009, T010, T011, T012) each record their outcome here under a heading of their own, T008 merges the evidence of T001-T007 into a coherent section while keeping the per-task headings, and T009-T012 append their own headings afterwards. The merged section holds: commands, run counts with and without load, the failing run's request log with timestamps, the result for each candidate of R3, and the repeated runs after the fix. Until then this section is empty.
