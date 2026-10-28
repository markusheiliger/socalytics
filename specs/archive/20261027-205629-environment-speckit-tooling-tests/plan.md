# Implementation Plan: Spec Kit Tooling Tests in Environment Verification

**Branch**: `20261027-205629-environment-speckit-tooling-tests` | **Date**: 2026-10-27 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/archive/20261027-205629-environment-speckit-tooling-tests/spec.md`

## Summary

`environment-verify` gets a check "Spec Kit tooling tests" that runs `node --test` over every `.github/scripts/speckit-*.test.mjs` of the verified workspace, and a tooling scope that adds `.github/scripts/speckit-*.mjs` (which includes `speckit-*.test.mjs`) to the covered paths. The check follows "Environment action tests": same gating by an environment variable (so existing fixtures are unaffected), the same nested-run marker, the same failure message shape, and a place in the check order between "Environment action tests" and "Markdown check". The scope rule names the tooling files instead of "is not Markdown". Only the two environment action folders change; see [research.md](research.md) for the decisions.

## Technical Context

**Language/Version**: Bash (the composite action step in `action.yml`) and Node.js ESM (`verification-plan.mjs`, `verification-scope.mjs`, and the `node:test` tests), with the caller-provided Node.js

**Primary Dependencies**: None added; `node:test` and `node:assert` only; installs or downloads nothing

**Storage**: N/A (the action's log, outputs, and plan JSON are unchanged in shape)

**Testing**: `node --test .github/actions/environment-verify/*.test.mjs .github/actions/environment-setup/toolchain.test.mjs` (the files the "Environment action tests" check runs); fixtures in `analyst-projects.test.mjs` extract and run the action script against stub repositories

**Target Platform**: GitHub Actions Linux runners and local Bash on Linux or macOS

**Project Type**: Repository tooling (composite GitHub Actions); no product code

**Performance Goals**: N/A; the tooling tests are local and need no network, credentials, or services

**Constraints**: Change only `.github/actions/environment-setup`, `.github/actions/environment-verify`, and the spec's own `tasks.md` ticks; preserve every existing check, input, output, mode rule, cache boundary, and result field; no other `.github/` path becomes covered; protected paths stay protected; existing action tests pass unchanged

**Scale/Scope**: One new check, one covered-path pattern, one scope message, new self-tests, README section

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle or constraint | Status | Evidence |
| --- | --- | --- |
| I. Architecture is the design authority | Pass | No architecture narrative describes the verification check list; see Architecture Updates below |
| II. Source-area ownership | Pass | Nothing under `src/` changes |
| III. API-first control plane | Pass (not applicable) | No platform capability or contract |
| IV. Evidence over claims | Pass | New cases in the action's own tests prove check, trigger, coverage, and skip; the tests fail when a rule is removed (SC-005) |
| V. Focused, minimal changes | Pass | Limited to the two environment action folders (FR-013); no new dependency |
| Environment constraint | Pass | This is the standalone environment feature: it changes only the two actions and is reviewed and merged first; the dependent spec `specs/20261026-100223-environment-guard-archive-rewrites` names it. It needs no framework, SDK, or tool the actions lack (Node.js is already provided) |

Post-design re-check: unchanged, all gates pass; no Complexity Tracking entries.

## Project Structure

### Documentation (this feature)

```text
specs/archive/20261027-205629-environment-speckit-tooling-tests/
├── plan.md              # This file
├── research.md          # Phase 0 decisions
├── data-model.md        # Phase 1: check, scope, and plan entities
├── quickstart.md        # Phase 1: validation scenarios
├── checklists/
└── tasks.md             # Phase 2 output (/speckit-tasks)
```

No `contracts/` folder: the change adds no external interface. The check's name, trigger, command, and failure message are the action's documented behavior and belong in the action README ([data-model.md](data-model.md) lists them), and the existing `checks`, `uncovered`, and `plan` outputs keep their shape.

### Source Code (repository root)

```text
.github/actions/environment-verify/
├── action.yml                    # new check step, SPECKIT_TOOLING_TESTS env, trigger, description text
├── verification-scope.mjs        # exported tooling-file pattern; classify() reason for tooling files
├── verification-plan.mjs         # tooling pattern added to the covered paths
├── analyst-projects.test.mjs     # action fixtures: check success, failure, skip, nested guard, order
├── verification-scope.test.mjs   # reason text for tooling and other files
├── verification-plan.test.mjs    # coverage and uncovered outcomes
└── README.md                     # documents the check, trigger, command, failure message, scope
.github/actions/environment-setup/
└── (no change expected; the caller already provides Node.js)
```

**Structure Decision**: Extend the existing files in place, as every previous check did; add no new file and no folder.

## Design

- **Scope pattern**: `verification-scope.mjs` exports `TOOLING_FILE = /^\.github\/scripts\/speckit-[^/]+\.mjs$/`. It matches `speckit-*.mjs` and `speckit-*.test.mjs` directly under `.github/scripts/` only (no subfolders, no non-`speckit-` file, no non-`.mjs` file). Markdown under `.github/scripts/` keeps its existing coverage by the Markdown check.
- **Coverage**: `verification-plan.mjs` imports the pattern and appends it to the covered list next to `STATIC_COVERED`; nothing else under `.github/` changes status, and protected-path handling lives with the callers and is untouched.
- **Scope reason**: in `classify`, the first non-Markdown file that matches the tooling pattern reports "`<file>` is a Spec Kit tooling file covered by the Spec Kit tooling tests" (still `documentationOnly: false`); every other file keeps "is not Markdown". The message names the first non-Markdown file, so a change list that mixes a tooling file and another file reports whichever comes first, as today.
- **Check step** in `action.yml`, after "Environment action tests" and before "Markdown check": runs when `status` is 0, `MAIN` is 1, the new `SPECKIT_TOOLING_TESTS` variable (the tooling folder, `.github/scripts` relative to the workspace) is set, `SOCALYTICS_ENVIRONMENT_TESTS_RUNNING` is empty, and `applies '^\.github/scripts/speckit-[^/]+\.mjs$'` holds (finalize mode or a matching changed path). It appends "Spec Kit tooling tests" to `checks`, fails with the named message when `node` is missing or no `speckit-*.test.mjs` file exists, and otherwise runs `node --test .github/scripts/speckit-*.test.mjs` through `step` with the nested-run marker set. A failure prints one line: `Spec Kit tooling tests failed (command: node --test <files>). Recommended action: reproduce this command, correct the failing Spec Kit tooling script or its test without weakening the test, and retry verification`.
- **Task-named files**: the unchanged-branch verification passes the task-named files to the action as its change list, so a task naming `.github/scripts/speckit-implement-core.mjs` or `speckit-implement-work.mjs` triggers the check and is covered through the same pattern, with no extra input and no change to `verifiedUnchangedCheck` (FR-009; changing it is out of scope). A non-`speckit-` file named by a task does not start the check, which matches FR-003 and the edge cases.
- **Why an opt-in variable**: like `ENVIRONMENT_TESTS`, the step sets `SPECKIT_TOOLING_TESTS` and fixtures leave it unset, so every existing fixture keeps its checks and its finalize check list (SC-005). New fixtures create stub tests in a workspace-relative stub folder and set the variable to that folder: the variable only gates the step, and its value is the tooling folder used for the file glob, the command, and the reproduce command in the failure message.
- **Order and text**: the order sentence in the README and the action description gain the new check; the `checks` output lists it when it ran.

## Architecture Updates

None. The change affects only the verification coverage of repository tooling (the two environment actions) and adds no product behavior, contract, or design decision. `docs/architecture/` describes the platform, clients, analysts, and agents design, not the Spec Kit verification checks, so no narrative or implementation-status statement there becomes true or false. The action README, which is not part of `docs/architecture/`, is updated by the implementation tasks.

## Complexity Tracking

No constitution violations.
