# Implementation Plan: Deterministic Web UI Session Restore Retry Test

**Branch**: `20261015-202520-fix-session-restore-test` | **Date**: 2026-10-15 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261015-202520-fix-session-restore-test/spec.md`

## Summary

In the test "retries restoring when connectivity returns", wait with `waitFor` for `api.restoreFailure?.kind` to be `unreachable` instead of asserting it right after the restore call count. No assertion is removed or loosened and no production code changes.

## Technical Context

**Language/Version**: TypeScript, Vitest with Testing Library, Node.js 24.

**Primary Dependencies**: existing `@testing-library/react`; no new packages.

**Testing**: in `src/clients/web`: `npm run build`, `npm test`.

**Constraints**: test-only change in `src/clients/web/tests/unit/session/session.test.tsx`.

## Constitution Check

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | No design decision changes. | Pass |
| II. Source-area ownership | Only `src/clients/web/tests`. | Pass |
| III. API-first control plane | Unaffected. | Pass |
| IV. Evidence over claims | Verified by repeated runs. | Pass |
| V. Focused, minimal changes | One test file. | Pass |

## Project Structure

```text
specs/20261015-202520-fix-session-restore-test/
├── spec.md
├── plan.md
├── tasks.md
└── checklists/requirements.md

src/clients/web/tests/unit/session/
└── session.test.tsx
```

## Architecture Updates

None. The change is limited to one unit test and alters no design decision or architecture statement.
