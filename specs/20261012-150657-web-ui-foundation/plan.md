# Implementation Plan: Web UI Foundation

**Branch**: `20261012-150657-web-ui-foundation` | **Date**: 2026-10-12 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261012-150657-web-ui-foundation/spec.md`

## Summary

Create the Web UI as one npm project at `src/clients/web/`, a React and
TypeScript single-page application built with Vite, using React Router,
TanStack Query, MUI under a SocAlytics theme, and i18next for translation keys.

- **Platform access**: it talks to the platform only through a Kiota-generated
  TypeScript client. The client is generated at build time from a committed
  OpenAPI snapshot of the platform API (`src/platform/SocAlytics.Platform.Api/openapi/v1.json`,
  kept current by a platform test) into the ignored folder `src/generated/`,
  and wrapped by application-owned adapters and repositories.
- **Session**: the existing backend-for-frontend session: sign-in returns the
  anti-forgery token and the member's roles, held only in memory. A Kiota
  middleware adds `X-CSRF-Token` to state-changing requests and turns session
  refusals into a return to sign-in.
- **Features**: Club Admin views for club, seasons, teams, and members; team
  views for Coaches and Viewers; the account view with password change; and the
  set-password page with sign-in name and credential code.
- **Composition**: the Aspire AppHost composes the Web UI with the API on one
  origin. The Vite server proxies `/api` to the API in development and in
  preview mode.
- **Security**: production builds and preview mode send a strict Content
  Security Policy with Trusted Types and emit Subresource Integrity attributes.
- **Tests**:
  - Vitest and Testing Library unit tests;
  - Playwright browser tests in two projects: `simulated` (API simulated with
    request routing, typed from the generated models) and `platform` (an
    end-to-end set against the AppHost with a disposable database);
  - axe WCAG 2.2 AA checks on every view.

Design rationale: [research.md](research.md). Client model and routes:
[data-model.md](data-model.md). Contracts: [contracts/](contracts/).
Validation: [quickstart.md](quickstart.md).

## Technical Context

**Language/Version**: TypeScript 5.x (strict) on Node.js 24 (provided by the
workflows); the platform side C# on .NET 10 (`src/platform/global.json`).

**Primary Dependencies** (pinned in `src/clients/web/package.json` with a
committed `package-lock.json`):

- **Application**: React 19, React Router 7, TanStack Query 5, MUI 7 with
  Emotion, i18next with react-i18next, and the Kiota TypeScript runtime
  bundle `@microsoft/kiota-bundle`.
- **Build**: Vite 7, `@vitejs/plugin-react`, TypeScript, and a Vite plugin
  that adds Subresource Integrity attributes to emitted assets.
- **Tests**: Vitest, Testing Library (`@testing-library/react`,
  `@testing-library/user-event`, `@testing-library/jest-dom`), jsdom,
  `@playwright/test` **1.63.0**, and `@axe-core/playwright`.
- **Tools from the environment**: Kiota 1.36.0 (`kiota` on `PATH`) and the
  Playwright 1.63.0 Chromium browser.
- **Platform**: the Aspire JavaScript hosting integration (`Aspire.Hosting.JavaScript`,
  the version that matches the AppHost's Aspire 13.4.6 packages) for the
  AppHost composition.

**Storage**: none in the browser beyond in-memory state; no local or session
storage of member data or secrets (FR-004, SC-003).

**Testing**:

- `npm test`: Vitest for adapters, middleware, hooks, and views.
- `npm run test:e2e`: Playwright projects `simulated` and `platform`.
  `platform` starts the AppHost in its `e2e` mode through Playwright's
  `webServer` and waits for the Web UI origin.
  The `platform` specs share one platform instance, so they run one after
  another in the fixed order `session`, `navigation`, `club-structure`,
  `members` (for example chained Playwright projects, the last one named
  `platform` so `--project platform` runs the whole chain), and a spec that
  activates the club's single active season archives it again before it ends.
- The platform side gets a Host test that keeps the OpenAPI snapshot current.

**Target Platform**: Chromium-based browsers (automated: Chromium on Linux);
local development on Windows, macOS, and Linux.

**Project Type**: Web application (single-page application) consuming the
existing platform API.

**Performance Goals**: SC-001 (a first Club Admin builds a club structure in
under 10 minutes); no load targets.

**Constraints**:

- No business rules in the client (FR-003).
- No secrets in browser storage (FR-004).
- Credentials never in addresses (FR-008).
- CSP with `require-trusted-types-for 'script'`, `script-src 'self'`, and no
  third-party origins (FR-020).
- One origin with the API (FR-019).
- English through translation keys (FR-021).

**Scale/Scope**: About 15 views, 4 user stories, one npm project.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | Follows `client-applications.md` (one React SPA in `src/clients`, Vite, React Router, TanStack Query, MUI, Kiota behind adapters, BFF cookie and CSRF, online-only first release, Chromium first) and `contracts-and-compatibility.md` (generated client into ignored output, compile-tested). The committed OpenAPI snapshot is the "bundled OpenAPI document" that `platform-implementation.md` § API And Identity anticipates; `platform-implementation.md` § Client Technology records the snapshot location in this change. | Pass |
| II. Source-area ownership | Web UI sources only under `src/clients/web/`; the AppHost composes the Web UI without moving source into `src/platform`, as `client-applications.md` § Source Ownership And Delivery allows; the OpenAPI snapshot belongs to the API project that implements it. | Pass |
| III. API-first control plane | The Web UI is an API consumer with no business logic and no database access; it uses only existing operations; the OpenAPI document stays authoritative over the generated code. | Pass |
| IV. Evidence over claims | Unit tests, simulated browser tests, an end-to-end set against the real platform, axe and CSP checks, and the platform snapshot test; no production readiness claimed. | Pass |
| V. Focused, minimal changes | Only the dependencies the architecture names plus the test and accessibility tools the spec requires; no service worker, no offline code, no container image. | Pass |
| Tooling constraints | Depends on the environment feature `specs/archive/20261012-145505-web-ui-environment` (Kiota 1.36.0, Playwright 1.63.0 Chromium, the Web UI check with scripts `build`, `test`, `test:e2e`); the .NET SDK and Docker for the `platform` browser tests are already provided. | Pass |

Post-design re-check: passes; no Complexity Tracking entries.

## Project Structure

### Documentation (this feature)

```text
specs/20261012-150657-web-ui-foundation/
├── plan.md
├── research.md          # Decisions R1–R10
├── data-model.md        # Client session model, view models, routes
├── quickstart.md
├── contracts/
│   ├── routes-and-roles.md       # Routes, required roles, views
│   ├── platform-adapter.md       # Adapter boundary over the generated client, error mapping
│   ├── openapi-snapshot.md       # Committed OpenAPI document and its platform test
│   └── security-headers.md       # CSP, Trusted Types, SRI, and the dev-server exception
├── checklists/requirements.md
└── tasks.md
```

### Source Code (repository root)

```text
src/clients/web/
├── package.json, package-lock.json, tsconfig*.json, vite.config.ts, vitest.config.ts, playwright.config.ts
├── index.html
├── .gitignore                      # node_modules/, dist/, src/generated/, test-results/, playwright-report/
├── src/
│   ├── main.tsx, App.tsx, router.tsx, theme.ts, trustedTypes.ts
│   ├── i18n/                       # i18next setup, English translation keys
│   ├── platform/                   # Kiota client factory, CSRF and session middleware, adapters (session, club, seasons, teams, members), error mapping
│   ├── session/                    # Session provider, guards (signed in, password change, roles)
│   ├── connectivity/               # Reachability state and provider
│   ├── shell/                      # Layout, navigation, not-permitted and not-found views
│   └── features/
│       ├── home/ sign-in/ set-password/ account/
│       ├── club/ seasons/ teams/ members/
├── tests/unit/                     # Vitest
└── tests/e2e/
    ├── simulated/                  # Playwright with routed API
    ├── platform/                   # Playwright against the AppHost
    └── support/                    # Fixtures, simulated API, axe helper

src/platform/
├── SocAlytics.Platform.Api/openapi/v1.json            # Committed OpenAPI snapshot
├── SocAlytics.Platform.AppHost/Program.cs             # + Web UI resource (Vite), one origin, e2e mode
└── Tests/SocAlytics.Platform.Host.Tests/OpenApiSnapshotTests.cs

docs/architecture/platform-implementation.md          # Client Technology: snapshot location and composition
src/clients/README.md, README.md, AGENTS.md            # Current state and commands
```

**Structure Decision**: One npm project `src/clients/web/` (the path the Web UI
verification environment covers). There are three platform touch points: the
OpenAPI snapshot with its test, and the AppHost composition.

## Complexity Tracking

No constitution violations.
