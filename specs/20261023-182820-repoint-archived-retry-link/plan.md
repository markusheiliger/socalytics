# Implementation Plan: Repoint the archived workload retry link in the stall expiry spec

**Branch**: `20261023-182820-repoint-archived-retry-link` | **Date**: 2026-10-23 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261023-182820-repoint-archived-retry-link/spec.md`

## Summary

The Dependencies bullet (line 80) of `specs/20261023-150804-deterministic-stall-expiry-test/spec.md` links the workload retry spec at its pre-archive location. The spec now lives in `specs/archive/20261020-224223-deterministic-simulated-workload-retry/`, so `node .github/scripts/check-markdown.mjs` reports one dead link and fails whole-repository verification of every implementation. The approach is the archive tooling's reference rewrite, which changes the link target to `../archive/20261020-224223-deterministic-simulated-workload-retry/spec.md` (the required fix) and, as accepted side effects, two plain-text mentions of the spec path, verified by the Markdown check. See [research.md](research.md) for the decisions.

## Technical Context

**Language/Version**: Markdown only; the validator is Node.js 24 (`.github/scripts/check-markdown.mjs`)

**Primary Dependencies**: None added

**Storage**: N/A

**Testing**: `node .github/scripts/check-markdown.mjs` on the whole repository (zero findings), plus a diff check, scoped to the stall expiry spec folder against the base branch, that exactly three lines in two files changed (line 80 and line 19 of `spec.md`, line 102 of `tasks.md`)

**Target Platform**: Repository documentation

**Project Type**: Documentation fix to a spec artifact

**Performance Goals**: N/A

**Constraints**: Only the archive rewrite's output changes: the link target on line 80 of the stall expiry `spec.md` (the required fix) and the plain-text mentions of the retry spec path on line 19 of that `spec.md` and line 102 of its `tasks.md` (accepted side effects); the link text, the rest of those lines, other artifacts, code, tests, environment actions, and `docs/architecture/` stay unchanged; because `specs/` is a protected path for task agents (`isProtectedPath`, `validateTaskChange`) and only archive reference rewrites (`referenceOnlySpecChanges`) are accepted outside the own spec folder, the edit is made by the archive tooling's reference rewrite (`node .github/scripts/speckit-archive.mjs 20261020-224223-deterministic-simulated-workload-retry`), not by a hand edit

**Scale/Scope**: Three lines in two files

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle or constraint | Result |
| --- | --- |
| I. Architecture Is the Design Authority | Pass: no architecture narrative or status statement is affected; Architecture Updates is `None` with a reason |
| II. Respect Source-Area Ownership | Pass: no source area or `src/` child is touched |
| III. API-First Control Plane | Pass: not applicable, no capability or contract changes |
| IV. Evidence Over Claims | Pass: the Markdown validation on the whole repository is the executable evidence; no behavior is added, so no new test is needed |
| V. Focused, Minimal Changes | Pass: only the three lines of the archive rewrite change, no new files outside this spec folder, no dependencies |
| Environment actions | Pass: the task needs only Node.js, which `environment-setup` already provides for the Markdown check; no environment spec is needed |
| Documentation changes pass the Markdown check | Pass: it is the verification of this feature |

Post-design re-check: unchanged, all gates pass; no Complexity Tracking entries.

## Project Structure

### Documentation (this feature)

```text
specs/20261023-182820-repoint-archived-retry-link/
├── plan.md              # This file (/speckit-plan command output)
├── research.md          # Phase 0 output (/speckit-plan command)
├── data-model.md        # Phase 1 output (/speckit-plan command)
├── quickstart.md        # Phase 1 output (/speckit-plan command)
└── tasks.md             # Phase 2 output (/speckit-tasks command - NOT created by /speckit-plan)
```

No `contracts/` folder: the feature exposes no interface.

### Source Code (repository root)

```text
specs/20261023-150804-deterministic-stall-expiry-test/
├── spec.md              # line 80: link target; line 19: plain-text mention
└── tasks.md             # line 102: plain-text mention
```

**Structure Decision**: No source, test, or environment file changes. The edited files are the stall expiry spec's `spec.md` and `tasks.md`, which this spec's own folder does not contain; the edit is the whole implementation. It lands as a reference-only rewrite of an already archived spec, produced by `node .github/scripts/speckit-archive.mjs 20261020-224223-deterministic-simulated-workload-retry`, the only kind of change outside the own spec folder that the automation accepts.

## Architecture Updates

None. The feature repairs one Markdown link inside a spec artifact and changes no design, behavior, or implementation status, so no `docs/architecture/` narrative or status statement becomes true or false and no documentation task is needed.

## Complexity Tracking

No Constitution Check violations.
