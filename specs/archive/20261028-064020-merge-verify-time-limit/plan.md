# Implementation Plan: Merge Verification of Environment Specs Within Its Time Limit

**Branch**: `20261028-064020-merge-verify-time-limit` | **Date**: 2026-10-28 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/archive/20261028-064020-merge-verify-time-limit/spec.md`

## Summary

The job `verify` of `.github/workflows/speckit-implement-merge.yml` runs the full finalize verification (about 70
minutes) and then, for environment specs, `Self-test the changed environment verification`, which runs the whole
finalize verification a second time with the workspace version of the action. The 90-minute limit cancels the job
before `Record the verdict`. The change has two parts:

1. The composite action `.github/actions/environment-verify` gets an optional input `environment-tests-only`
   (default `false`). When `true`, the action runs only the check "Environment action tests" (the Node tests and
   fixtures of both environment actions) and skips the verification plan, every component, the Python projects, and
   the Markdown check. The self-test step passes it, so the self-test still loads the changed action from
   `./workspace/.github/actions/environment-verify` and still feeds `SPECKIT_SELFTEST_OUTCOME`.
2. The `timeout-minutes` of the job `verify` rises from 90 to 150, which leaves a margin of more than one third over
   the roughly 80 minutes that the full verification, the setup, and the narrowed self-test need, and stays below the
   360-minute cap.

The full verification of the merged result is unchanged (trusted action, mode `finalize`, every check). It runs with
the trusted action, so the changed action's component, plan, and Markdown logic is covered by the action's own fixture
tests (stubbed tools), which must exercise the changed script for every component kind.

**Ordering**: the workflow edit (`timeout-minutes` 150 and `environment-tests-only` on the self-test step) is made by a
person outside the implementation and must be on the default branch before `/speckit implement` is requested. An older workspace action ignores the unknown input, so it is harmless there but not narrowed. The
spec's ordering gate states the precondition: do not request `/speckit implement` until that edit is on the default
branch. No task or check enforces it, because a task that targets `.github/workflows/` can never land; the person who
requests the implementation does, and the handoff comment repeats it (spec Clarifications, session 2026-10-28).

**Manual verification (not tasks)**: after the workflow edit, a person runs the greps of quickstart step 3
(`timeout-minutes: 150` and `environment-tests-only` in `.github/workflows/speckit-implement-merge.yml`) and checks the
workflows README statement of the limit and the self-test.

## Technical Context

**Language/Version**: GitHub Actions workflow YAML, a composite action with a bash script, Node.js 24 tests (`node --test`)

**Primary Dependencies**: none new; `node:test` and the existing test helpers in `analyst-projects.test.mjs`

**Storage**: N/A

**Testing**: `node --test` over the environment action tests (`analyst-projects.test.mjs`, `verification-scope.test.mjs`, `verification-plan.test.mjs`, `../environment-setup/toolchain.test.mjs`) and `node --test .github/scripts/speckit-*.test.mjs`

**Target Platform**: GitHub-hosted `ubuntu-latest` runner

**Project Type**: repository tooling (CI workflow and composite action); no product code

**Performance Goals**: the self-test adds less than 10% to the full verification (SC-002); the job records its verdict within its limit

**Constraints**: `timeout-minutes` at most 360 and at least one third above the time of both runs; no check weakened; the self-test loads the workspace action; implementation agents may not change `.github/workflows/`, so the workflow edit lands through this spec's own review

**Scale/Scope**: one workflow file, one action (`action.yml`, its tests, its README), the workflows README

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- **I. Architecture is the design authority**: pass. No `docs/architecture/` narrative describes the merge verification job's limit or the self-test; the Architecture Updates section states the outcome.
- **II. Source-area ownership**: pass. Nothing is added under `src/`.
- **III. API-first control plane**: not applicable (repository tooling).
- **IV. Evidence over claims**: pass. The narrowed self-test is covered by tests in `analyst-projects.test.mjs`, and the tooling tests keep passing.
- **V. Focused, minimal changes**: pass. One new action input, one timeout value, and the documentation they make stale.
- **Tooling constraints**: the change touches the environment action `environment-verify` and the merge workflow only, and needs no new framework, SDK, or tool, so no environment dependency applies. Changes to the environment actions are held for a person's review and the merge self-tests them.

Post-design re-check: pass; the design adds no dependency and removes no check.

## Project Structure

### Documentation (this feature)

```text
specs/archive/20261028-064020-merge-verify-time-limit/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── environment-verify-inputs.md
└── tasks.md             # /speckit-tasks, not created by /speckit-plan
```

### Source Code (repository root)

```text
.github/
├── workflows/
│   ├── speckit-implement-merge.yml     # verify job: timeout-minutes 150; self-test passes environment-tests-only
│   └── README.md                       # merge verification and self-test description
└── actions/
    └── environment-verify/
        ├── action.yml                  # input environment-tests-only; header comment
        ├── analyst-projects.test.mjs   # tests for the narrowed run
        └── README.md                   # input, self-test scope, "merge self-tests" coverage note
```

**Structure Decision**: Repository tooling only. The narrowing lives in the action (so it is tested by the action's own
tests and works for any caller), and the workflow only passes the input and raises the limit.

## Architecture Updates

None. `docs/architecture/` does not describe the merge verification job, its time limit, or the self-test of the
environment actions; those live in `.github/workflows/README.md` and the environment-verify README, which the
documentation task corrects. No implementation-status statement in `docs/architecture/` becomes true or false.

## Complexity Tracking

No constitution violations.
