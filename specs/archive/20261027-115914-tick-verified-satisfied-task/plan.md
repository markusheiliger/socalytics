# Implementation Plan: Tick a Verified, Already-Satisfied Task

**Branch**: `20261027-115914-tick-verified-satisfied-task` | **Date**: 2026-10-27 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/archive/20261027-115914-tick-verified-satisfied-task/spec.md`

## Summary

When the agent of a task changes nothing, the land job today fails the attempt unless `doneByPush` finds a non-workflow commit at the head or after the task's first attempt. This feature adds a second, independent way for an empty change to land: the verification of the unchanged branch, selected for the files the task names in `tasks.md`, passed with at least one check, covered every named file, and ran on the very head the land job lands on. The work job records that verification in a separate `unchanged` object (never in `reasons`, so existing outcomes do not change), and the land job recomputes the head and decides. A skipped task whose work is missing still fails, a head mismatch fails the attempt and counts toward `MAX_TASK_ATTEMPTS`, and the comment, commit message, and check run say the task was already satisfied. Details: [research.md](research.md), [data-model.md](data-model.md), [contracts/task-result.md](contracts/task-result.md).

## Technical Context

**Language/Version**: Node.js ES modules (`.mjs`), the version the Spec Kit workflows use

**Primary Dependencies**: none new; the repository's `speckit-implement-*.mjs` modules and the composite action `.github/actions/environment-verify` (unchanged)

**Storage**: N/A (fields of the work job's `result.json` artifact)

**Testing**: `node --test .github/scripts/speckit-*.test.mjs`, mainly `speckit-implement.test.mjs` (real git repositories under `withRepos`, a fake GitHub client)

**Target Platform**: GitHub Actions runners and local Spec Kit runs

**Project Type**: repository tooling (CLI scripts and workflows), not product code

**Performance Goals**: N/A; the empty-change verification runs only the checks selected for the task's named files

**Constraints**: the land job never runs agent-written code and trusts only what it recomputes; `MAX_TASK_ATTEMPTS`, attempt counting, protected paths, environment exclusivity, covered-file checks, and the existing `doneByPush` results stay unchanged

**Implementation mode**: this plan changes protected tooling paths (`.github/scripts/`), so it is implemented locally (`SPECKIT_IMPLEMENT_MODE=local`) or by a person's or Copilot's push, not by the unattended agent; its pull request is held for review because changes under `.github/scripts/` are covered by no check.

**Check coverage**: `environment-verify` selects no check for `.github/scripts/`, so tasks that name only such files (for example T004 and T005 of `specs/archive/20261026-100223-environment-guard-archive-rewrites`) are not covered by the verified-unchanged path and still need a push by a person or Copilot (`doneByPush`).

**Scale/Scope**: three touched scripts (`speckit-implement-work.mjs`, `speckit-implement-core.mjs`, tests), one README, and one sentence of `AGENTS.md`

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-checked after Phase 1 design.*

| Principle | Result |
| --- | --- |
| I. Architecture is the design authority | Pass. No `docs/architecture/` narrative describes this tooling; see Architecture Updates. |
| II. Source-area ownership | Pass. Only `.github/scripts/`, `.github/workflows/README.md`, and `AGENTS.md` change; nothing is added under `src/`. |
| III. API-first control plane | Not applicable (no product API). |
| IV. Evidence over claims | Pass. Automated tests cover every scenario in FR-008; nothing claims production readiness. |
| V. Focused, minimal changes | Pass. No new dependency, no change to `environment-verify`, the workflows, or attempt counting. |
| Environment tooling constraint | Pass. No new framework, SDK, or tool; the existing `environment-verify` task mode selects the checks, so no environment spec is needed; the path is narrowed to tasks with a covered check, and tooling-only tasks under `.github/scripts/` (uncovered by any check) still need `doneByPush`, so no environment spec for a `.github/scripts` check is added. |

No violations; Complexity Tracking stays empty. The post-design check gives the same result.

## Design

1. **Task files** (`speckit-implement-core.mjs`): a pure function returns the distinct, safe, repository-relative paths a task's text names (backticked tokens and path-like tokens), filtered to files that exist in the checkout and capped at `MAX_CHANGED_FILES` (research R2a).
2. **Package** (`runPackage`, task kind): when `patchBytes === 0`, write `result.taskFiles` and use them as the content of `changed-files.txt`, so `environment-verify` in `task` mode selects the checks for them. `changedFiles` stays empty (R2).
3. **Verdict** (`runVerdict`): for `patchBytes === 0` with configured verification, record `result.unchanged` (`status`, `head`, `checks`, `uncovered`, `files`) and do not add the verification failure to `reasons` or change `verified`, so the `doneByPush` path behaves as today (R3).
4. **Land** (`runLand`, `kind === 'task'`): `allowEmpty` becomes `doneByPush(...) || isVerifiedUnchanged(result, headNow)`, and the boolean flag `verifiedUnchanged` records the path for reporting. `isVerifiedUnchanged` requires `status === 'passed'`, at least one test or build check (the "Markdown check" does not qualify), no uncovered named file, non-empty `files`, evidence that the work exists on the branch (at least one named file that no other task in `tasks.md` names differs from the base branch through the `branchPaths` diff against `origin/<base>`), and `unchanged.head` equal to the pull request's current head (the tip fetched before the land checkout, not the detached checkout of the work head, which always equals `unchanged.head`). When an empty change is refused, the reason names the cause (head mismatch, verification failed, not run or not configured, task names no verifiable file, no qualifying check covered the files, no named file is specific to this task, no specific named file differs from the base branch); it counts toward `MAX_TASK_ATTEMPTS` like any failure (R4).
5. **Push** (`pushTask`): skip `git apply` for an empty patch so a tick-only landing survives a moved branch (R5).
6. **Reporting** (`runLand` task comment, commit message, check-run title): when the landing used the verified-unchanged path, say the task was already satisfied and name the passed checks; the task is ticked and the implementation continues without a review hold (R6, FR-007).
7. **Documentation**: describe both exemptions and the refusal in `.github/workflows/README.md` and the matching rule in `AGENTS.md` (R7, FR-009).

## Project Structure

### Documentation (this feature)

```text
specs/archive/20261027-115914-tick-verified-satisfied-task/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── task-result.md
└── tasks.md             # created by /speckit-tasks
```

### Source Code (repository root)

```text
.github/
├── scripts/
│   ├── speckit-implement-core.mjs      # task-file extraction
│   ├── speckit-implement-work.mjs      # package, verdict, runLand, pushTask, reporting
│   └── speckit-implement.test.mjs      # tests for FR-008 and unchanged existing cases
└── workflows/
    └── README.md                       # rule description
AGENTS.md                               # matching sentence in Spec Twins
```

**Structure Decision**: all changes stay inside the existing Spec Kit tooling modules and their test file; no new files in `.github/scripts/`, no workflow YAML change (the verification step already consumes `changed-files.txt` and the verdict reads its outputs).

## Architecture Updates

None. This feature changes only repository tooling under `.github/scripts/`, its README, and `AGENTS.md`; `docs/architecture/` describes the product system and contains no statement about the Spec Kit implementation automation, so no narrative, design update, or implementation-status statement is affected.

## Complexity Tracking

No Constitution Check violations.
