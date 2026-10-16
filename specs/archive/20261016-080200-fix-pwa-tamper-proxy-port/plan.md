# Implementation Plan: Free Port for the PWA Tampering Proxy Test

**Branch**: `20261016-080200-fix-pwa-tamper-proxy-port` | **Date**: 2026-10-16 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/archive/20261016-080200-fix-pwa-tamper-proxy-port/spec.md`

## Summary

Change `tamperedPort` in `pwa-install.spec.ts` from 4176 (the fake object-store port) to the unused 4178. All assertions stay.

## Technical Context

**Language/Version**: TypeScript, Node.js 24, Playwright 1.63.0.

**Primary Dependencies**: existing Playwright `simulated` project; no new packages.

**Storage**: None.

**Testing**: `npm run test:e2e -- --project simulated` in `src/clients/web`.

**Constraints**: test-only change; one constant.

## Constitution Check

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | No design decision changes. | Pass |
| II. Source-area ownership | Only `src/clients/web`. | Pass |
| III. API-first control plane | Not affected. | Pass |
| IV. Evidence over claims | The clash is removed and verified by the simulated browser tests. | Pass |
| V. Focused, minimal changes | One constant in one file. | Pass |

## Project Structure

```text
specs/archive/20261016-080200-fix-pwa-tamper-proxy-port/
├── spec.md
├── plan.md
├── tasks.md
└── checklists/requirements.md

src/clients/web/tests/e2e/simulated/
└── pwa-install.spec.ts
```

## Architecture Updates

None. The change is limited to a test port constant and alters no design decision or architecture statement.
