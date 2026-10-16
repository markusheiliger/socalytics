# Feature Specification: Free Port for the PWA Tampering Proxy Test

**Feature Branch**: `20261016-080200-fix-pwa-tamper-proxy-port`

**Created**: 2026-10-16

**Input**: User description: "In src/clients/web/tests/e2e/simulated/pwa-install.spec.ts the tampering proxy listens on tamperedPort = 4176, which since #127 is the fake object-store port from playwright.config.ts (EADDRINUSE in 'a tampered shell file'). Give the proxy a port no other Playwright server or spec uses (for example 4178 after checking 4173-4177) and keep every assertion unchanged; npm run test:e2e --project simulated must pass."

**Fix for**: #123 (implementation of `specs/20261015-195724-fix-telemetry-sink-race`)

## Problem

The Playwright `simulated` test `a tampered shell file` in `src/clients/web/tests/e2e/simulated/pwa-install.spec.ts` starts a tampering proxy in front of the preview server on `tamperedPort = 4176`. Since #127 (commit a688961, "Separate Mailpit and fake object-store ports"), `playwright.config.ts` starts the fake object store on `fakeStoragePort = 4176`, and `tests/e2e/support/simulatedApi.ts` and several simulated specs use that origin. The proxy's `listen(4176)` therefore fails with `EADDRINUSE` on every run.

Evidence: repair verification run 38867959633 of #123: Web UI browser tests `1 failed, 128 passed`, failing at `pwa-install.spec.ts:165 a tampered shell file` with `listen EADDRINUSE: address already in use 127.0.0.1:4176`. The stopped implementation changes only an Integration test, so the failure is deterministic and lies in Web UI test code on the base branch. The ports in use are 4173 (preview), 4174 (platform), 4176 (fake object store), and 4177 (release server); 4175 and 4178 are unused.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The tampered shell test runs on its own port (Priority: P1)

A maintainer runs the simulated browser tests. The tampering proxy listens on a port no other Playwright server or spec uses, so `a tampered shell file` starts its proxy and proves, as before, that a modified shell file is not served from the cache.

**Why this priority**: The deterministic failure blocks the verification and merge of every implementation that runs the Web UI browser tests.

**Independent Test**: Run `npm run test:e2e -- --project simulated` in `src/clients/web`; all tests pass, including `a tampered shell file`.

**Acceptance Scenarios**:

1. **Given** the fake object store listens on 4176, **When** the tampering proxy starts, **Then** it listens on a free port without `EADDRINUSE`.
2. **Given** the proxy on its new port, **When** the test runs, **Then** it makes the same requests and the same assertions as before.

### Edge Cases

- The new port must not equal any port in `playwright.config.ts`, `tests/e2e/support/`, the AppHost e2e mode, or another spec.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: `tamperedPort` in `pwa-install.spec.ts` MUST be `4178`, a port no Playwright web server, support helper, or spec uses.
- **FR-002**: Every assertion and request of `a tampered shell file` and of the other tests in the file MUST stay unchanged.
- **FR-003**: No other file, production code, option, or configuration changes; no test is skipped, loosened, or removed.

### Key Entities

- **Tampering proxy**: the test's local HTTP server that forwards to the preview server and alters one shell file.

## Success Criteria *(mandatory)*

- **SC-001**: `npm run test:e2e -- --project simulated` passes with no failures, including `a tampered shell file`.
- **SC-002**: The test passes in 5 consecutive runs without `EADDRINUSE`.
- **SC-003**: The diff changes only the `tamperedPort` value (FR-002, FR-003).

## Assumptions

- Port 4178 is chosen after checking 4173 to 4177 in `playwright.config.ts`, `tests/e2e/support/`, the simulated specs, and the AppHost e2e port (4174); none uses 4178.
- Dependencies: the feature owning the test is [Installable Web UI with Service Worker Updates](../archive/20261015-194113-web-ui-pwa-updates/spec.md); the implementation this fix unblocks is #123 (`specs/20261015-195724-fix-telemetry-sink-race`).
