---

description: "Task list for Free Port for the PWA Tampering Proxy Test"
---

# Tasks: Free Port for the PWA Tampering Proxy Test

**Input**: Design documents from `specs/20261016-080200-fix-pwa-tamper-proxy-port/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md)

**Tests**: The task changes a test; its verification is running it.

## Format: `[ID] [P?] [Story] Description`

## Phase 1: User Story 1 - The tampered shell test runs on its own port (Priority: P1) 🎯 MVP

- [ ] T001 [US1] In `src/clients/web/tests/e2e/simulated/pwa-install.spec.ts`, change `const tamperedPort = 4176;` to `const tamperedPort = 4178;` after confirming that 4178 appears in no `playwright.config.ts`, `tests/e2e/support/`, or spec file. Keep every assertion and all other code unchanged. Verify: in `src/clients/web`, `npm run build` then `npm run test:e2e -- --project simulated` passes, including `a tampered shell file`.

## Dependencies & Execution Order

T001 is the only task.
