# Implementation Plan: Repoint the archived Shot Boundary Analyst link in the deterministic agent workload test spec

**Branch**: `20261025-205528-repoint-archived-shot-boundary-link` | **Date**: 2026-10-25 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/archive/20261025-205528-repoint-archived-shot-boundary-link/spec.md`

## Summary

The Dependencies entry (line 83) of `specs/20261025-003459-deterministic-agent-workload-test/spec.md` links the Shot Boundary Analyst spec at its pre-archive location. The spec now lives in `specs/archive/20261023-022148-shot-boundary-analyst/`, so `node .github/scripts/check-markdown.mjs` reports one dead link and fails whole-repository verification of every implementation. The approach is a one-line edit that adds `archive/` to that link target, applied directly; the archive tooling's reference rewrite at the merge MAY also rewrite the plain-text mentions on lines 9 and 11. See [research.md](research.md).

## Technical Context

**Language/Version**: Markdown; Node.js 24 only to run the existing validator

**Primary Dependencies**: None added; the existing `.github/scripts/check-markdown.mjs`

**Storage**: N/A

**Testing**: `node .github/scripts/check-markdown.mjs` on the whole repository (SC-001) and a `git diff` scoped to the edited spec showing only reference rewrites (SC-002)

**Target Platform**: Repository documentation

**Project Type**: Documentation fix in a Spec Kit artifact

**Performance Goals**: N/A

**Constraints**: Outside this spec's folder only reference rewrites of the archived folder (line 83, optionally lines 9 and 11); no code, test, environment action, or `docs/architecture/` change (FR-002, FR-003)

**Scale/Scope**: One link target in one file

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle or constraint | Assessment |
| --- | --- |
| I. Architecture Is the Design Authority | Pass. No architecture statement is made true or false; the plan's Architecture Updates section is `None` with a reason. |
| II. Respect Source-Area Ownership | Pass. Nothing under `src/` changes. |
| III. API-First Control Plane | Not affected. No capability, API, or contract changes. |
| IV. Evidence Over Claims | Pass. The evidence is the repository Markdown validation reporting no finding, plus the scoped one-line diff. No platform behavior changes, so no new platform test is needed. |
| V. Focused, Minimal Changes | Pass. One link target changes; the archive tooling is not used because it would change more than the spec allows. |
| Environment tooling | Pass. Only Node.js and the existing validator are needed, which the environment actions already provide; no environment spec is required and no dependency is named. |

Post-design re-check: unchanged, no violations; Complexity Tracking stays empty.

## Project Structure

### Documentation (this feature)

```text
specs/archive/20261025-205528-repoint-archived-shot-boundary-link/
├── plan.md              # This file (/speckit-plan command output)
├── research.md          # Phase 0 output (/speckit-plan command)
├── data-model.md        # Phase 1 output (/speckit-plan command)
├── quickstart.md        # Phase 1 output (/speckit-plan command)
└── tasks.md             # Phase 2 output (/speckit-tasks command - NOT created by /speckit-plan)
```

No `contracts/` folder: the change exposes no interface.

### Source Code (repository root)

```text
specs/
└── 20261025-003459-deterministic-agent-workload-test/
    └── spec.md          # line 83: link target gains `archive/`
```

**Structure Decision**: No source, test, or documentation tree is added. The only edit outside this spec's folder is line 83 of the deterministic agent workload test spec, applied by hand; the archive tooling's reference rewrite at the merge MAY also rewrite its plain-text mentions (lines 9 and 11).

## Architecture Updates

None. The change repairs a relative link between two spec artifacts under `specs/`. It implements and changes no narrative in `docs/architecture/`, applies no design update, and makes no implementation-status statement there true or false, so no documentation task for `docs/architecture/` is required.

## Complexity Tracking

No Constitution Check violations; nothing to justify.
