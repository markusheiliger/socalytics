# Data Model: Repoint the archived Shot Boundary Analyst link

The change introduces no persisted data, entity, state, or schema. The only artifact involved is one documentation link.

## Link under repair

| Attribute | Before | After |
| --- | --- | --- |
| Location | `specs/archive/20261025-003459-deterministic-agent-workload-test/spec.md`, line 83 (Dependencies entry) | unchanged |
| Link text | `Shot Boundary Analyst` | unchanged |
| Link target (relative) | `../20261023-022148-shot-boundary-analyst/spec.md` (does not resolve) | `../archive/20261023-022148-shot-boundary-analyst/spec.md` (resolves) |

## Validation rule

The target MUST resolve to the existing file `specs/archive/20261023-022148-shot-boundary-analyst/spec.md`; the repository Markdown validation enforces this (FR-004).
