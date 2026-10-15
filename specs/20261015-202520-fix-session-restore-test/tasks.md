---

description: "Task list for Deterministic Web UI Session Restore Retry Test"
---

# Tasks: Deterministic Web UI Session Restore Retry Test

**Input**: Design documents from `specs/20261015-202520-fix-session-restore-test/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md)

**Tests**: The task changes a test; its verification is running it.

## Format: `[ID] [P?] [Story] Description`

## Phase 1: User Story 1 - The session restore retry test passes reliably (Priority: P1) 🎯 MVP

- [ ] T001 [US1] In `src/clients/web/tests/unit/session/session.test.tsx`, in the test 'retries restoring when connectivity returns', keep the wait for one `restore` call and the status assertion, and replace the synchronous `expect(api.restoreFailure?.kind).toBe('unreachable')` with `await waitFor(() => expect(api.restoreFailure?.kind).toBe('unreachable'))`; change no other assertion and no production code. Verify: in `src/clients/web`, `npm run build` and `npm test -- tests/unit/session/session.test.tsx` pass at least 20 times in a row, and `npm test` passes.

## Dependencies & Execution Order

T001 is the only task.
