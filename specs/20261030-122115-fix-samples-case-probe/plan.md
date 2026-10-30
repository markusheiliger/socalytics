# Implementation Plan: Fix the Case-Sensitivity Probe of the Sample Tooling Test

**Branch**: `20261030-122115-fix-samples-case-probe` | **Date**: 2026-10-30 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261030-122115-fix-samples-case-probe/spec.md`

## Summary

`node --test tools/samples.test.mjs` fails on case-sensitive file systems because the test's probe runs `discoverVideos` on a folder holding `probe/a.mp4` and `probe/A.mp4`, and the discovery's correct refusal ("Paths differ only in case") escapes from the probe. The fix replaces the probe with a direct file-system check: create only `probe/a.mp4` in the test's temporary location with the existing `folder` helper and test whether `probe/A.mp4` exists with `existsSync`. The case-clash assertion runs only where it does not exist and stays textually unchanged. Only `tools/samples.test.mjs` changes.

## Technical Context

**Language/Version**: Node.js 24 (ES modules, `node:test`, `node:assert/strict`)

**Primary Dependencies**: None added; `existsSync` is already imported by the test file

**Storage**: N/A (temporary folder under `os.tmpdir()`, removed by the test's `after` hook)

**Testing**: `node --test tools/samples.test.mjs` (13 tests; see [quickstart.md](quickstart.md))

**Target Platform**: Developer machines and the merge verification: Linux (case-sensitive, exercised here), macOS and Windows (case-insensitive, manual)

**Project Type**: Developer tooling under `tools/` (not a first-level source area)

**Performance Goals**: N/A

**Constraints**: Change only `tools/samples.test.mjs`; no assertion removed, loosened, or skipped on case-sensitive file systems; the probe must not call `discoverVideos`

**Scale/Scope**: One probe expression in one test

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle or constraint | Assessment |
| ----------------------- | ---------- |
| I. Architecture is the design authority | Pass. Test-only change with no design effect; see Architecture Updates. |
| II. Source-area ownership | Pass. `tools/` is developer tooling; no new immediate child of `src/`. |
| III. API-first control plane | Not applicable. |
| IV. Evidence over claims | Pass. The existing test file is the evidence; success is claimed only for the tooling test, not for deployment or production readiness. |
| V. Focused, minimal changes | Pass. One file, no new dependency or abstraction. |
| Environment actions | Pass. Needs only Node.js, which the actions already provide (the baseline command is the same one the sibling spec #418 runs). No environment spec is required. |

Post-design re-check: unchanged, all gates still pass. No complexity violations.

## Architecture Updates

None. The change fixes the probe of a developer-tooling unit test under `tools/`; no architecture narrative in `docs/architecture/` describes or depends on it, and it makes no implementation-status statement true or false.

## Project Structure

### Documentation (this feature)

```text
specs/20261030-122115-fix-samples-case-probe/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── checklists/
│   └── requirements.md
└── tasks.md             # created by /speckit-tasks
```

No `contracts/` folder: the feature exposes no interface.

### Source Code (repository root)

```text
tools/
└── samples.test.mjs     # the only changed file: the probe in the "refuses a missing folder ..." test
```

**Structure Decision**: Edit the existing test in place; nothing else under `tools/`, `src/`, `.github/`, or `docs/` changes.

## Complexity Tracking

No Constitution Check violations.
