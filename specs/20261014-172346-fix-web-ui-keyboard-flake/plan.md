# Implementation Plan: Deterministic Web UI Keyboard and Club Structure Browser Tests

**Branch**: `20261014-172346-fix-web-ui-keyboard-flake` | **Date**: 2026-10-14 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261014-172346-fix-web-ui-keyboard-flake/spec.md`

## Summary

Make `expectAllReachableByKeyboard` wait for focus to settle after each Tab press and name unreached elements in its failure message, and give the created-team link assertions in `club-structure.spec.ts` an explicit longer timeout. No assertion is removed or loosened.

## Technical Context

**Language/Version**: TypeScript, Playwright 1.63.0 (Chromium), Node.js 24.

**Primary Dependencies**: existing `@playwright/test`; no new packages.

**Testing**: in `src/clients/web`: `npm run build`, `npm test`, `npm run test:e2e -- --project simulated` (repeated, also under CPU load); the platform project needs Docker.

**Constraints**: test-only change. In the helper, record the active element before each Tab and wait (`page.waitForFunction`, bounded to about 1 s, tolerating a timeout at the end of the cycle) until it differs, then evaluate; report each unreached element by tag, id, and accessible text instead of its index.

## Constitution Check

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | No design decision changes. | Pass |
| II. Source-area ownership | Only `src/clients/web/tests`. | Pass |
| III. API-first control plane | Unaffected. | Pass |
| IV. Evidence over claims | Verified by repeated runs. | Pass |
| V. Focused, minimal changes | Two test files. | Pass |

## Project Structure

```text
specs/20261014-172346-fix-web-ui-keyboard-flake/
├── spec.md
├── plan.md
├── tasks.md
└── checklists/requirements.md

src/clients/web/tests/e2e/
├── support/keyboard.ts
└── platform/club-structure.spec.ts
```

## Architecture Updates

None. The change is limited to browser test support code and one spec and alters no design decision or architecture statement.
