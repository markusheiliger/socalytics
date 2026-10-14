---

description: "Task list for Deterministic Web UI Keyboard and Club Structure Browser Tests"
---

# Tasks: Deterministic Web UI Keyboard and Club Structure Browser Tests

**Input**: Design documents from `specs/archive/20261014-172346-fix-web-ui-keyboard-flake/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md)

**Tests**: The tasks change tests; their verification is running them.

## Format: `[ID] [P?] [Story] Description`

## Phase 1: User Story 1 - The Web UI browser tests pass reliably (Priority: P1) 🎯 MVP

- [X] T001 [US1] In `src/clients/web/tests/e2e/support/keyboard.ts`, make `expectAllReachableByKeyboard` wait after each Tab press until the active element differs from the one before the press (bounded to about 1 s; a timeout is tolerated because the last element or the end of the cycle does not change focus), then evaluate focus; collect description (tag, id, accessible text) of every target element in the first pass, and make the "not reachable" assertion list the unreached elements by that description instead of indexes. Keep the visible focus indicator check, the target set, and the Tab limit. Verify: in `src/clients/web`, `npm run build` and `npm run test:e2e -- --project simulated` pass at least 5 times in a row, also with CPU load, and a temporary (uncommitted) interactive element that Tab cannot reach makes the failure name it (SC-004).
- [X] T002 [US1] In `src/clients/web/tests/e2e/platform/club-structure.spec.ts`, give the created-team link visibility assertions (`getByRole('link', { name })` after creation and after the rename) an explicit timeout of 15 s; change no other assertion. Verify: `npm run build` in `src/clients/web` passes and, where Docker is available, `npm run test:e2e -- --project platform` runs the spec repeatedly without failing.

## Dependencies & Execution Order

T001 and T002 change different files and are independent; run in the listed order.
