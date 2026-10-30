# Implementation Plan: Sample Tooling Tests in Environment Verification

**Branch**: `20261030-102557-environment-sample-tooling-tests` | **Date**: 2026-10-30 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261030-102557-environment-sample-tooling-tests/spec.md`

## Summary

Add the verification check "Sample tooling tests" to `environment-verify`. It runs exactly
`node --test tools/samples.test.mjs` when one of exactly four `tools/` files changed (and always in finalize mode), and
it makes exactly those four files covered, so a change to them no longer ends as "files not covered by verification".
The check follows the pattern of the existing "Spec Kit tooling tests" check: a bash block in the action, gated by a
workspace-relative environment variable that fixtures leave unset, the nested-run marker, the first shard only, and a
failure message with the command and one recommended action. Coverage is a path pattern in the plan and scope
modules. The action's own tests and README change with it. Nothing outside `.github/actions/environment-setup` and
`.github/actions/environment-verify` changes (FR-013), and `environment-setup` needs no change because the check uses
the caller's Node.js.

## Technical Context

**Language/Version**: Node.js ES modules (`verification-plan.mjs`, `verification-scope.mjs`), Bash inside the composite
action `action.yml`

**Primary Dependencies**: None added; the Node.js the caller provides (`node --test`); no installation or download

**Storage**: N/A

**Testing**: `node --test` of the environment action tests (`verification-plan.test.mjs`, `verification-scope.test.mjs`,
`analyst-projects.test.mjs` for the shell-level fixtures), run by the "Environment action tests" check

**Target Platform**: GitHub Actions runners and local Linux/macOS shells (Bash)

**Project Type**: Repository tooling (composite action); an environment spec

**Performance Goals**: The check adds seconds, accounted as one minute of the first shard's shared-check budget

**Constraints**: Only the two environment action folders and the spec's own `tasks.md` ticks change; no change to
`.github/scripts/`, workflows, the required-sync barrier or its uncovered-path filter, pending repair scope, or write-job
isolation; every existing check, input, output, mode rule, cache boundary, and result field stays

**Scale/Scope**: Four covered files; one new check; one new coverage pattern; one new constant

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle or constraint | Result |
| --- | --- |
| I. Architecture is the design authority | Pass: no architecture narrative describes the verification coverage rules (checked in `docs/architecture/`); see Architecture Updates |
| II. Source-area ownership | Pass: no `src/` change |
| III. API-first control plane | Not affected |
| IV. Evidence over claims | Pass: no path becomes covered without a check that runs its test; new tests fail when the check, triggers, scope, or sharding are removed |
| V. Focused, minimal changes | Pass: one check and one pattern, no `tools/` glob, no change to the sample tooling |
| Environment actions constraint | Pass: this is the environment feature; it changes only the two action folders, and the dependent spec `20261028-235035-case-insensitive-samples-test` names it as a dependency |

Post-design re-check: unchanged; the design adds no dependency, tool, or path outside the two action folders.

## Design Decisions

Details and rejected alternatives are in [research.md](research.md); entities in [data-model.md](data-model.md); the
check's behavior as a contract in [contracts/sample-tooling-check.md](contracts/sample-tooling-check.md).

1. **Check in `action.yml`.** A block after "Spec Kit tooling tests" and before "Markdown check" in the first shard:
   `MAIN=1`, `SAMPLE_TOOLING_TESTS` set, nested-run marker unset, and
   `applies '^tools/(samples\.test|samples-lib|upload-samples|download-samples)\.mjs$'` (finalize always applies; a
   documentation-only change never does). `SAMPLE_TOOLING_TESTS` is the workspace-relative test file
   `tools/samples.test.mjs`, set in the action's `env` and unset in fixtures, exactly like `SPECKIT_TOOLING_TESTS`.
   Missing `node` or a missing test file are named failed checks with one recommended action; a failing test prints the
   reproduce command. The check is appended to `checks` before it runs, so it appears whenever it ran or failed.
2. **Coverage in the plan and scope modules.** `verification-scope.mjs` exports `SAMPLE_TOOLING_FILE`
   (`/^tools\/(?:samples\.test|samples-lib|upload-samples|download-samples)\.mjs$/`, anchored, case-sensitive);
   `verification-plan.mjs` adds it to the covered patterns next to `TOOLING_FILE`. The scope reason for such a path names
   the "Sample tooling tests" instead of "is not Markdown". Every other `tools/` path (including `.bak` suffixes, nested
   paths, and case variants) stays uncovered; `tools/*.md` stays covered by the Markdown rule; protected paths are decided
   outside this module and stay protected.
3. **Planning and sharding.** The check is a shared check of the first shard, like the Spec Kit tooling tests, so it
   runs in exactly one shard in sharded runs and never twice. Its budget is part of `MAIN_MINUTES` (raised from 5 to 6
   with a comment) so `packShards` accounts for it; the shard log line names it. The check is not a component and needs no
   `verification.json`.
4. **Aggregation.** A failing check sets `status=1` like the others, so the shard fails and the overall verification fails.
5. **Order.** Environment action tests, Spec Kit tooling tests, Sample tooling tests, Markdown check; the README order
   sentence and the `checks` description are updated.

## Architecture Updates

None. The change affects only how the repository's own verification selects and covers the sample tooling tests;
`docs/architecture/` does not describe the environment actions, `environment-verify`, or the coverage rules of
`tools/` (searched for `environment-verify`, `verification`, and `tools/`). The documentation of the check lives in
the action's README, which the implementation updates (FR-011). The documentation task must nevertheless re-read
`docs/architecture/` at implementation time and confirm that no statement became false.

## Project Structure

### Documentation (this feature)

```text
specs/20261030-102557-environment-sample-tooling-tests/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── sample-tooling-check.md
├── checklists/
│   └── requirements.md
└── tasks.md             # created by /speckit-tasks
```

### Source Code (repository root)

```text
.github/actions/environment-verify/
├── action.yml                    # new check block, SAMPLE_TOOLING_TESTS env, header and shard log text
├── verification-scope.mjs        # SAMPLE_TOOLING_FILE export and scope reason
├── verification-plan.mjs         # covered pattern, MAIN_MINUTES
├── verification-scope.test.mjs   # scope reason and exact-path cases
├── verification-plan.test.mjs    # coverage, selection, sharding, budget cases
├── analyst-projects.test.mjs     # shell-level fixtures: run, fail, missing file, nested guard, finalize, skip
└── README.md                     # section "Sample tooling tests", order, covered paths
.github/actions/environment-setup/   # unchanged (the caller's Node.js is used)
```

**Structure Decision**: Extend the existing action files only; the shell-level fixtures live beside the existing
"Spec Kit tooling tests" fixtures in `analyst-projects.test.mjs` because that file owns the action's shell behavior
(the spec names the plan and scope tests for rules, and the fixtures need the shell runner that already exists there).
No new files in the action folders.

## Complexity Tracking

No constitution violations.
