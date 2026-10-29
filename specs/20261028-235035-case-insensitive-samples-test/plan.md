# Implementation Plan: Case-Sensitivity-Independent Sample Tooling Test

**Branch**: `20261028-235035-case-insensitive-samples-test` | **Date**: 2026-10-28 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261028-235035-case-insensitive-samples-test/spec.md`

## Summary

The test 'refuses a missing folder, no videos, empty videos, and paths that differ only in case; never follows links' in `tools/samples.test.mjs` decides whether the file system keeps `a.mp4` and `A.mp4` apart by running `discoverVideos` on a probe folder. On a case-sensitive file system the discovery itself refuses that folder, and the refusal fails the test. The plan replaces the probe with a direct check that does not call the code under test: create the two probe files with `folder()` and count the directory entries with `readdirSync`. Two entries mean the names coexist, and the unchanged case-clash assertion runs; one entry means they cannot coexist, and the assertion is not demanded. Only `tools/samples.test.mjs` changes.

## Technical Context

**Language/Version**: JavaScript (ES modules), Node.js 24

**Primary Dependencies**: Node.js built-ins only (`node:test`, `node:assert/strict`, `node:fs`); `readdirSync` is added to the existing `node:fs` import

**Storage**: Temporary folder created by the test (`mkdtempSync` under the OS temporary folder, removed after the run)

**Testing**: `node --test tools/samples.test.mjs` (and `node --test tools/samples.test.mjs .github/scripts/speckit-*.test.mjs` for the wider tooling tests)

**Target Platform**: Linux (CI, case-sensitive), macOS and Windows (case-insensitive, case-preserving)

**Project Type**: Repository developer tooling test (`tools/`, not a source area under `src/`)

**Performance Goals**: N/A

**Constraints**: Change only `tools/samples.test.mjs`; keep the case-clash assertion `assert.throws(() => discoverVideos(clash), /differ only in case/)` textually unchanged; do not touch `tools/samples-lib.mjs` or any other test

**Scale/Scope**: One test, a few lines

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- **I. Architecture Is the Design Authority**: Pass. Test-only change with no architectural effect; the plan has an `## Architecture Updates` section stating `None` with a reason.
- **II. Source-Area Ownership**: Pass. Nothing is added under `src/`; `tools/` is existing developer tooling.
- **III. API-First Control Plane**: Not applicable (no capability, API, or contract).
- **IV. Evidence Over Claims**: Pass. The change is itself test code, evidenced by running the test; the evidence is development evidence only and claims no deployment support or production readiness.
- **V. Focused, Minimal Changes**: Pass. One file, no new dependency, no abstraction.
- **Tooling constraints**: Pass. Node.js is already provided by the environment actions (the tooling tests already run there), so no environment feature is needed.

Post-design re-check: unchanged, all gates pass. No Complexity Tracking entries.

## Project Structure

### Documentation (this feature)

```text
specs/20261028-235035-case-insensitive-samples-test/
├── plan.md              # This file (/speckit-plan command output)
├── research.md          # Phase 0 output (/speckit-plan command)
├── data-model.md        # Phase 1 output (/speckit-plan command)
├── quickstart.md        # Phase 1 output (/speckit-plan command)
└── tasks.md             # Phase 2 output (/speckit-tasks command - NOT created by /speckit-plan)
```

No `contracts/` folder: the feature exposes no interface.

### Source Code (repository root)

```text
tools/
├── samples-lib.mjs      # unchanged: discoverVideos and its refusal of case-only clashes
└── samples.test.mjs     # changed: the case probe of the refusal test
```

**Structure Decision**: Change the existing test file in place. No new file, helper module, or dependency.

## Architecture Updates

None. The change adjusts the probe of one repository tooling test; it implements or changes no narrative of `docs/architecture/` and makes no implementation-status statement there true or false.

## Complexity Tracking

No Constitution Check violations.
