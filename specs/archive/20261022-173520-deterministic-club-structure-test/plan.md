# Implementation Plan: Deterministic Club Structure Browser Test

**Branch**: `20261022-173520-deterministic-club-structure-test` | **Date**: 2026-10-22 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/archive/20261022-173520-deterministic-club-structure-test/spec.md`

## Summary

Find, by reproduction, why the `platform-club-structure` browser test passes only on its rerun, and remove that cause where it lives without retries, longer timeouts, or weakened assertions. The plan fixes the method and the search space; the concrete cause is a result of the implementation (FR-001), recorded in a code comment next to the fix (the commit message, written by the person or automation that commits, repeats it; task agents cannot write under `specs/`). The ranked candidates, from the code path the test exercises, are in [research.md](research.md): the cached, stale team list that the Teams screen shows on remount before its invalidation refetch resolves, the season option and the `created` status that precede the awaited refetch, shared Club Admin and active-season state across the chained platform projects, and the time-derived names.

## Technical Context

**Language/Version**: TypeScript, React, Playwright 1.63.0 (Chromium), Node.js 24.

**Primary Dependencies**: existing `@playwright/test` and `@tanstack/react-query`; no new packages.

**Storage**: N/A. The platform instance of the `platform` projects holds the state (PostgreSQL started by the AppHost in e2e mode); the test only creates and archives seasons and teams through the Web UI and the API.

**Testing**: in `src/clients/web`: `npm run build`, `npm test`, and `npm run test:e2e -- --project platform-club-structure` (the platform projects start the AppHost and need Docker; the project depends on `platform-navigation`, which chains back to `platform-session`).

**Target Platform**: Web UI browser tests against the platform host in end-to-end mode.

**Project Type**: web application test hardening (test, its support code, and only if the evidence says so the Web UI screens).

**Performance Goals**: N/A.

**Constraints**: no retries, no skipping, no new or longer timeouts, no removed or loosened assertion; documented behavior of the season and team screens and the club structure operations unchanged; other platform specs that use shared support behave unchanged.

**Scale/Scope**: one spec file (`tests/e2e/platform/club-structure.spec.ts`), its support (`tests/e2e/support/platformState.ts`, `fixtures.ts`), and, only if the evidence places the cause there, `src/features/teams/` or `src/features/seasons/`.

## Constitution Check

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | No design decision changes; production behavior stays as documented (FR-004). | Pass |
| II. Source-area ownership | Only `src/clients/web` (tests, and screens if the evidence requires). | Pass |
| III. API-first control plane | The UI keeps using the `/api/v1` operations; no logic moves into the client. | Pass |
| IV. Evidence over claims | Pass/fail is proved by repeated runs (alone, under load, after the other platform projects) and by three failure probes (SC-001, SC-002). | Pass |
| V. Focused, minimal changes | Touches only the files the evidenced cause lives in (SC-003). | Pass |
| Environment | `environment-setup` and `environment-verify` already provide Node.js 24, Playwright Chromium, and Docker for the platform projects; no new tool. | Pass |

Re-check after design: unchanged, the design adds no file, dependency, or behavior.

## Project Structure

### Documentation (this feature)

```text
specs/archive/20261022-173520-deterministic-club-structure-test/
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
└── src/features/{teams,seasons}/                 # only if the evidence places the cause in a screen
```

**Structure Decision**: Test-first. A production file changes only when a reproduced trace shows that the screen, not the test, produces the race, and then only without changing its documented behavior.

## Design

1. **Reproduce first** (FR-001): run `platform-club-structure` alone (at least 20 times), under CPU load, and after the chained platform projects, with `--trace on`; keep the traces of failing runs. Use a temporary probe that delays the team list and season list responses (a Playwright `page.route` handler in a scratch copy, removed afterward) to widen each candidate race and confirm which one fails the test deterministically.
2. **Fix the evidenced cause**, by the kind of cause:
   - *Stale cached list or refetch order*: make the test wait on the awaited condition, such as the refetched list after the save, before the absence assertion, using the existing assertions in a provable order; change a screen only if the screen shows a state that contradicts its documented behavior.
   - *Shared state*: have the test establish its own start state (no active season through `archiveActiveSeasons` before it, and not only after), and derive names that cannot collide between a rerun or a worker.
   - *External readiness*: wait on the platform's readiness before the first sign-in in the shared support.
3. **Keep every assertion** and every explicit `timeout` as it is (none lengthened).
4. **Prove** with the SC-001 runs and the SC-002 probes, remove the probes, and write the cause and its evidence into the code comment next to the fix and the final summary (the commit message, written by the person or automation that commits, repeats it) (SC-003). Nothing is written under `specs/` by the task.

## Architecture Updates

None. The change is limited to browser test code (and, only if the evidence requires, a screen without a behavior change), and no design decision or implementation-status statement of `docs/architecture/` becomes true or false.

## Complexity Tracking

No constitution violations.
