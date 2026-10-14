---

description: "Task list for Sequential Platform Test Projects in Environment Verification"
---

# Tasks: Sequential Platform Test Projects in Environment Verification

**Input**: Design documents from `specs/20261014-180324-fix-sequential-platform-tests/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md)

**Tests**: The task changes the verification action; its verification is a run of the action.

## Format: `[ID] [P?] [Story] Description`

## Phase 1: User Story 1 - Docker-backed test projects never run at the same time (Priority: P1) 🎯 MVP

- [x] T001 [US1] In `.github/actions/environment-verify/action.yml`, change `run_tests` so that in phase and finalize mode it no longer calls `dotnet test "$solution" --no-build` but discovers the test projects like task mode (`find "$(dirname "$solution")" -name '*Tests.csproj'` excluding `bin` and `obj`, sorted) and runs `step dotnet test "$project" --no-build` for each one after another, setting `result=1` on a failure without stopping the loop and returning `result` at the end; keep task mode selection unchanged and update the comment above `run_tests`. Change no other file than the two environment action folders. Verify: a finalize run of the action on the repository builds and runs every platform test project one after another (log shows one `dotnet test <project>` per project, with no overlap), and a temporarily failing test project (uncommitted) does not hide later projects and fails the check.

## Dependencies & Execution Order

T001 is the only task.
