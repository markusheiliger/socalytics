# Implementation Plan: Finalize Verification Without Repeated Work

**Branch**: `20261024-210113-finalize-verification-reuse` | **Date**: 2026-10-24 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261024-210113-finalize-verification-reuse/spec.md`

## Summary

Environment-only change to the composite action `.github/actions/environment-verify`. In finalize mode the action
(1) records a passing result in a per-job file in `RUNNER_TEMP` keyed by a Git tree hash of the verified workspace, the
mode, the changed-files content, and the scope, and a later finalize run in the same job reuses it instead of running
every check again; (2) reduces the scope to the Environment action tests and the Markdown check when every changed
path lies in the two environment action folders. Anything uncertain, failed, missing, or mismatching runs every check
in full. The logic is inline bash with `node -e` snippets in `action.yml`; tests extend the existing Node test file
and the README documents the behavior. No workflow, input, or output changes (see [research.md](research.md)).

## Technical Context

**Language/Version**: Bash (the composite action step) with Node.js 24 snippets, Git; tests in Node.js `node:test`

**Primary Dependencies**: none new (`git`, `node`, `sha256` through Node `crypto`)

**Storage**: one JSON file per job under `$RUNNER_TEMP/socalytics-environment-verify/` ([data-model.md](data-model.md))

**Testing**: `node --test` over `analyst-projects.test.mjs` (existing `verification execution` fixtures with stub `uv`)
and `../environment-setup/toolchain.test.mjs`; `node .github/scripts/check-markdown.mjs`

**Target Platform**: GitHub Actions Linux runners (read-only verification jobs); tests also run locally

**Project Type**: build-environment tooling (composite GitHub Action)

**Performance Goals**: a reused finalize run adds under 2 minutes (seconds in practice); an environment-only finalize
run completes under 15 minutes; reuse plus first run under 80 minutes (SC-001, SC-002)

**Constraints**: change confined to `.github/actions/environment-verify/` (`action.yml`, `README.md`,
`analyst-projects.test.mjs`) and the spec's own task ticks; no workflow, other action, product code, or new input;
unchanged checks, order, covered paths, rerun and retry behavior

**Scale/Scope**: one action step; about 150 added script lines and about 15 new tests

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle or constraint | Assessment |
| --- | --- |
| I. Architecture is the design authority | Pass. `docs/architecture/` does not describe the verification action (no mention found); see Architecture Updates |
| II. Source-area ownership | Pass. No `src/` change; the action lives under `.github/actions/` |
| III. API-first control plane | Not applicable (no product capability) |
| IV. Evidence over claims | Pass. Behavior is proven by new Node tests next to the action; no production-readiness claim |
| V. Focused, minimal changes | Pass. Three existing files change; no new dependency, file, or abstraction |
| Environment constraint | Pass. This is itself a standalone environment spec changing only the action folder; it needs only Node, Python, and Git, which `environment-setup` and the runner already provide, and adds nothing to `environment-setup` |

Post-design re-check (after research, data model, and contract): unchanged, all gates pass. No Complexity Tracking
entry is needed.

## Project Structure

### Documentation (this feature)

```text
specs/20261024-210113-finalize-verification-reuse/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/
│   └── finalize-verification.md   # Phase 1 output
├── checklists/
│   └── requirements.md
└── tasks.md             # Phase 2 output (/speckit-tasks, not created here)
```

### Source Code (repository root)

```text
.github/actions/environment-verify/
├── action.yml                    # reuse record, workspace identity, scope reduction in "Run the checks"
├── README.md                     # documents reuse, reduced scope, and the unchanged rest
└── analyst-projects.test.mjs     # new "finalize reuse" and "finalize scope" tests in "verification execution"
```

**Structure Decision**: all work stays in the three existing files of the action folder, because FR-010 forbids any
other change and the callers are protected. No helper file is added.

## Architecture Updates

None. No narrative in `docs/architecture/` describes `environment-verify` or its finalize behavior (the build
environment tooling is documented only in the action's own README, in `README.md`, and in `AGENTS.md`, none of which
this change makes false). The action README is updated by the feature's documentation task.
