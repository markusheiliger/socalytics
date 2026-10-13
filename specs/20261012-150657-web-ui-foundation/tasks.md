---

description: "Task list for Web UI Foundation"
---

# Tasks: Web UI Foundation

**Input**: Design documents from `specs/20261012-150657-web-ui-foundation/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/routes-and-roles.md](contracts/routes-and-roles.md), [contracts/platform-adapter.md](contracts/platform-adapter.md), [contracts/openapi-snapshot.md](contracts/openapi-snapshot.md), [contracts/security-headers.md](contracts/security-headers.md), [quickstart.md](quickstart.md)

**Tests**: Required. The spec requires automated coverage of every acceptance scenario (SC-002) and the constitution requires focused tests. Every task adds the tests for the code it creates in the same task, so `npm run build`, `npm test`, `npm run test:e2e`, and the platform test suites stay green after each task. Browser tests in the `simulated` Playwright project run axe WCAG 2.2 AA checks, fail on any CSP violation, and check that no secret is in browser storage (support from T006).

**Organization**: Tasks are grouped by user story. Merged before this feature starts (do not re-create): the Web UI verification environment `specs/archive/20261012-145505-web-ui-environment` (Kiota 1.36.0 on `PATH`, the Playwright 1.63.0 Chromium browser, the Web UI check running `npm ci`, the Playwright pin check, `npm run build`, `npm test`, `npm run test:e2e` in `src/clients/web/`) and the Club and Identity Foundation `specs/archive/20261005-130701-club-identity-foundation` (session, anti-forgery, members, roles, club hierarchy API; the Host tests in `src/platform/Tests/SocAlytics.Platform.Host.Tests`). Every dependency in `src/clients/web/package.json` is pinned to an exact version; `@playwright/test` is exactly `1.63.0`.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies on incomplete tasks); consecutive `[P]` tasks under one heading run in parallel, each from the same commit
- **[Story]**: Which user story this task belongs to (US1–US4)
- Every task names its exact file paths

## Path Conventions

- Web UI project: `src/clients/web/` with `src/` (application), `tests/unit/` (Vitest), `tests/e2e/simulated/`, `tests/e2e/platform/`, `tests/e2e/support/` (Playwright); generated client in the ignored `src/clients/web/src/generated/platform-api/`.
- Platform: `src/platform/SocAlytics.Platform.Api/openapi/v1.json`, `src/platform/SocAlytics.Platform.AppHost/`, `src/platform/Tests/SocAlytics.Platform.Host.Tests/`.

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: The OpenAPI snapshot the client is generated from, and the Web UI project skeleton that the verification environment builds and tests.

- [X] T001 OpenAPI snapshot (contracts/openapi-snapshot.md; research.md § R1; FR-002): add `src/platform/Tests/SocAlytics.Platform.Host.Tests/OpenApiSnapshotTests.cs` that starts the API as the existing Host tests do, fetches `/openapi/v1.json`, normalizes it (keys sorted, `servers` removed, LF, trailing newline), and compares it with `src/platform/SocAlytics.Platform.Api/openapi/v1.json`, failing with a short diff and the regeneration instruction; with `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1` it rewrites the file instead. Generate and commit `src/platform/SocAlytics.Platform.Api/openapi/v1.json`.
- [X] T002 Web UI project skeleton (plan.md § Technical Context, § Project Structure; research.md § R5, § R6, § R7, § R10; FR-001, FR-002, FR-021): create `src/clients/web/` with `package.json` (`"engines": { "node": ">=24 <25" }`, exact versions of React 19, React Router 7, TanStack Query 5, MUI 7 with Emotion, i18next, react-i18next, `@microsoft/kiota-bundle`, Vite 7, `@vitejs/plugin-react`, TypeScript 5, Vitest, jsdom, Testing Library, `@playwright/test` `1.63.0`, `@axe-core/playwright`; scripts `generate` = `kiota generate --language typescript --openapi ../../platform/SocAlytics.Platform.Api/openapi/v1.json --output src/generated/platform-api --class-name PlatformApiClient --clean-output`, `build` = `npm run generate && tsc -b && vite build`, `test` = `vitest run`, `test:e2e` = `playwright test`, `preview` = `vite preview`), a committed `package-lock.json`, `.gitignore` (`node_modules/`, `dist/`, `src/generated/`, `test-results/`, `playwright-report/`), `tsconfig.json` and `tsconfig.node.json` (strict), `vite.config.ts` (React plugin), `vitest.config.ts` (jsdom), `playwright.config.ts` (projects `simulated`, Chromium, against `vite preview`; `platform` added later), `index.html`, `src/main.tsx`, `src/App.tsx`, `src/theme.ts` (SocAlytics MUI theme), `src/i18n/index.ts` and `src/i18n/en/common.json`; a unit test `tests/unit/app.test.tsx` that renders the app, and a browser test `tests/e2e/simulated/smoke.spec.ts` that loads the app.

**Checkpoint**: The Web UI verification environment's check passes: install, Playwright pin, build with client generation, unit tests, browser tests.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Platform access through the generated client, browser-test support, one-origin composition, and the security baseline. No user story starts before this phase is complete.

### Access layer and test support

- [X] T003 [P] Platform access layer (contracts/platform-adapter.md; research.md § R2; FR-002, FR-003, FR-005): add `src/clients/web/src/platform/client.ts` (Kiota client factory with base URL `/api/v1` of the current origin, `credentials: 'same-origin'`), `src/platform/middleware/antiforgery.ts` (`X-CSRF-Token` from the in-memory session on `POST`, `PUT`, `PATCH`, `DELETE`), `src/platform/middleware/sessionOutcome.ts` (session-ended and password-change-required events), `src/platform/middleware/connectivity.ts` (reports network failures), and `src/platform/failure.ts` (`PlatformFailure` kinds and mapping per the contract table). Only `src/platform/` imports `src/generated/`. Unit tests in `src/clients/web/tests/unit/platform/` with a fake request adapter for every middleware and every mapping row.
- [x] T004 [P] Browser-test support (research.md § R5; SC-003, SC-006, SC-007): add `src/clients/web/tests/e2e/support/simulatedApi.ts` (routes `/api/v1/**` with `page.route`, fixtures typed from the generated models, helpers to sign in as Club Admin, Coach, or Viewer and to end the session), `tests/e2e/support/fixtures.ts` (a Playwright fixture that fails a test on any `securitypolicyviolation` event and, after each test, asserts that `localStorage`, `sessionStorage`, IndexedDB, and `document.cookie` hold no password, credential code, or anti-forgery token), and `tests/e2e/support/axe.ts` (WCAG 2.2 AA rule tags `wcag2a`, `wcag2aa`, `wcag21a`, `wcag21aa`, `wcag22aa`, asserting 0 violations), and `tests/e2e/support/keyboard.ts` (reaches every interactive element of a view with Tab, checks visible focus, and triggers the view's main action with the keyboard), used by the simulated browser tests of T009–T017 (SC-007); switch `tests/e2e/simulated/smoke.spec.ts` to these fixtures.

### Connectivity, composition, and security baseline

- [ ] T005 Connectivity state (research.md § R9; FR-013): add `src/clients/web/src/connectivity/ConnectivityProvider.tsx` and `src/connectivity/useConnectivity.ts` (combines `navigator.onLine` and the failures reported by the connectivity middleware from T003, probes `/alive` with exponential backoff while unreachable, refetches queries on recovery), and mount the provider in `src/clients/web/src/App.tsx`. Unit tests in `tests/unit/connectivity/`.
- [ ] T006 One-origin composition and the platform test project (research.md § R3; FR-019): add the Aspire JavaScript hosting package matching Aspire 13.4.6 to `src/platform/Directory.Packages.props` and `src/platform/SocAlytics.Platform.AppHost/SocAlytics.Platform.AppHost.csproj`; in `src/platform/SocAlytics.Platform.AppHost/Program.cs` add the Web UI from `../../clients/web` as a Vite resource referencing the API, and an `e2e` mode (environment variable `SOCALYTICS_APPHOST_MODE=e2e`) that serves the built Web UI with `vite preview`, sets a known first Club Admin password from `SOCALYTICS_E2E_ADMIN_PASSWORD`, and uses non-persistent containers; in `src/clients/web/vite.config.ts` proxy `/api` and `/alive` to the API endpoint Aspire injects, in `server` and `preview`; in `src/clients/web/playwright.config.ts` add the `platform` project with a `webServer` that runs the AppHost in `e2e` mode and waits for the Web UI origin; add `tests/e2e/platform/origin.spec.ts` (the app loads and `GET /api/v1/session` through the Web UI origin answers `401` when signed out). Extend `src/platform/Tests/SocAlytics.Platform.Host.Tests/PlatformHostTests.cs` to assert the Web UI resource references the API.
- [ ] T007 Security headers and Subresource Integrity (contracts/security-headers.md; research.md § R4; FR-020, SC-006): in `src/clients/web/vite.config.ts` set the preview server headers (the Content Security Policy of the contract, `X-Content-Type-Options`, `Referrer-Policy`, `Permissions-Policy`) and a relaxed policy for the development server only; add a Subresource Integrity build plugin (exact version) to `package.json` and `vite.config.ts`; add `src/clients/web/src/trustedTypes.ts` (the `default` policy that rejects script creation) loaded first from `src/main.tsx`; bundle fonts and icons locally. Tests: `tests/unit/build/sri.test.ts` checks `dist/index.html` for `integrity` and `crossorigin` on every script and stylesheet; `tests/e2e/simulated/headers.spec.ts` asserts the response headers.

**Checkpoint**: The generated client is wrapped, browser tests have their support, the AppHost serves the Web UI on one origin, and the security baseline is enforced.

---

## Phase 3: User Story 1 - Sign in, stay signed in safely, and sign out (Priority: P1) 🎯 MVP

**Goal**: Members sign in, change a required password, set a password with a code, change their own password, sign out, and return to sign-in when the session ends.

**Independent Test**: Sign in as the first Club Admin, change the required initial password, sign out, sign in again, and observe that an ended session returns to sign-in.

- [ ] T008 [US1] Session adapter, provider, and guards (data-model.md § Session; contracts/routes-and-roles.md § Rules; FR-004, FR-007, FR-009, FR-010): add `src/clients/web/src/platform/adapters/sessionAdapter.ts` (sign in, restore, sign out, change own password, redeem credential), `src/session/SessionProvider.tsx` (states `unknown`, `signedOut`, `passwordChangeRequired`, `signedIn`; token in memory only; clears the query cache on sign-out and session end), and `src/session/guards.tsx` (signed-in guard, password-change guard, role guard); add `src/router.tsx` with the routes of contracts/routes-and-roles.md as placeholders. Unit tests in `tests/unit/session/`.
- [ ] T009 [US1] Sign-in, required password change, and sign-out views (spec US1 scenarios 1–3, 6–8; FR-006, FR-007, FR-009, FR-010): add `src/clients/web/src/features/sign-in/SignInPage.tsx`, `src/features/account/PasswordChangePage.tsx`, the sign-out action in `src/features/account/`, and the session-ended and session-problem messages, with keys in `src/i18n/en/session.json`. Unit tests in `tests/unit/features/sign-in/`; browser tests in `tests/e2e/simulated/session.spec.ts` for scenarios 1–3 and 6–8 and the cookies-blocked edge case.
- [ ] T010 [US1] Set-password and account views (spec US1 scenarios 4–5; clarification on credential codes; FR-008): add `src/clients/web/src/features/set-password/SetPasswordPage.tsx` (sign-in name, code, new password, confirmation; asks to sign out first when signed in; the code never enters an address) and `src/features/account/AccountPage.tsx` (change own password), with keys in `src/i18n/en/account.json`. Unit tests in `tests/unit/features/account/`; browser tests in `tests/e2e/simulated/account.spec.ts` for scenarios 4–5 and the set-password-while-signed-in edge case.
- [ ] T011 [US1] Platform end-to-end set for sessions (clarification on browser tests; SC-002, SC-004): add `src/clients/web/tests/e2e/platform/session.spec.ts`: sign in as the first Club Admin with the e2e password, complete the required password change, sign out, sign in again, and observe that an ended session returns to sign-in without member data.

**Checkpoint**: User Story 1 works against the simulated API and the real platform.

---

## Phase 4: User Story 2 - Find my way by my roles (Priority: P2)

**Goal**: A role-aware shell with navigation, not-permitted handling, and connectivity state.

**Independent Test**: Sign in as Club Admin, Coach, and Viewer and compare navigation; stop the platform and observe the connectivity state.

- [ ] T012 [US2] Shell and role-based navigation (contracts/routes-and-roles.md; FR-011, FR-012, FR-013; SC-005): add `src/clients/web/src/shell/AppLayout.tsx`, `src/shell/Navigation.tsx` (exactly the routes the member's club and team roles allow), `src/shell/NotPermittedPage.tsx`, `src/shell/NotFoundPage.tsx`, `src/features/home/HomePage.tsx`, and the connectivity indicator that disables state-changing actions while unreachable, with keys in `src/i18n/en/shell.json`; wire them into `src/router.tsx`. Add the translation-key coverage test `tests/unit/i18n/keys.test.ts` (fails on literal JSX text in `src/features/**` and `src/shell/**`). Unit tests in `tests/unit/shell/`; browser tests in `tests/e2e/simulated/navigation.spec.ts` for US2 scenarios 1–5 with axe checks on every view.
- [ ] T013 [US2] Platform end-to-end role navigation (SC-005): add `src/clients/web/tests/e2e/platform/navigation.spec.ts`, which creates a Coach and a Viewer through the API with the Club Admin's session in test setup and compares each member's navigation with contracts/routes-and-roles.md.

**Checkpoint**: User Stories 1 and 2 work independently.

---

## Phase 5: User Story 3 - Club Admins manage the club, seasons, and teams (Priority: P3)

**Goal**: Club details, seasons, and teams with conflict handling.

**Independent Test**: Edit the club, create and activate a season, create and edit teams, archive a season, and provoke a concurrent edit.

- [ ] T014 [US3] Club and seasons (contracts/platform-adapter.md; research.md § R8; FR-014, FR-015, FR-017): add `src/clients/web/src/platform/adapters/clubAdapter.ts`, `src/platform/adapters/seasonAdapter.ts`, `src/features/club/ClubPage.tsx`, `src/features/seasons/SeasonsPage.tsx`, and `src/features/seasons/SeasonPage.tsx` (create, activate, archive; field errors from the platform; conflict message with reload), with keys in `src/i18n/en/club.json` and `src/i18n/en/seasons.json`; register the routes in `src/router.tsx`. Unit tests in `tests/unit/features/club/` and `tests/unit/features/seasons/`; browser tests in `tests/e2e/simulated/club-seasons.spec.ts` for US3 scenarios 1, 2, 4, 5.
- [ ] T015 [US3] Teams (FR-016, FR-017): add `src/clients/web/src/platform/adapters/teamAdapter.ts`, `src/features/teams/TeamsPage.tsx`, and `src/features/teams/TeamPage.tsx` (Club Admin creates teams in a season and edits them; Coach and Viewer see their teams read-only), with keys in `src/i18n/en/teams.json`; register the routes in `src/router.tsx`. Unit tests in `tests/unit/features/teams/`; browser tests in `tests/e2e/simulated/teams.spec.ts` for US3 scenario 3 and read-only views.
- [ ] T016 [US3] Platform end-to-end create flows for seasons and teams (SC-002): add `src/clients/web/tests/e2e/platform/club-structure.spec.ts`: create a season, activate it, create two teams, and edit one through the Web UI against the real platform.

**Checkpoint**: User Stories 1–3 work independently.

---

## Phase 6: User Story 4 - Club Admins manage members and roles (Priority: P4)

**Goal**: Members, credential codes, roles, deactivation, unlock, and ending sessions.

**Independent Test**: Create a member, issue a code, set the password, assign the Coach role, check navigation, then deactivate the member.

- [ ] T017 [US4] Members and roles (contracts/platform-adapter.md; FR-018; US4 scenarios 1–6): add `src/clients/web/src/platform/adapters/memberAdapter.ts`, `src/features/members/MembersPage.tsx`, and `src/features/members/MemberPage.tsx` (create; issue a credential code shown once in a dialog and never again; assign and remove club roles and team roles; deactivate and reactivate; unlock; end sessions; the platform's refusals, for example removing the last Club Admin, shown clearly), with keys in `src/i18n/en/members.json`; register the routes in `src/router.tsx`. Unit tests in `tests/unit/features/members/`; browser tests in `tests/e2e/simulated/members.spec.ts` for US4 scenarios 1–6.
- [ ] T018 [US4] Platform end-to-end member flow (SC-001, SC-002, SC-004): add `src/clients/web/tests/e2e/platform/members.spec.ts`: create a member, issue a code, set the member's password on `/set-password` in a second browser context, assign the Coach role, check the member's navigation, deactivate the member, and observe that their next action returns to sign-in.

**Checkpoint**: All user stories work independently.

---

## Phase 7: Polish & Cross-Cutting Concerns

- [ ] T019 Documentation: record in `docs/architecture/platform-implementation.md` § Client Technology the committed OpenAPI snapshot location, the AppHost composition on one origin, and the accepted `style-src 'unsafe-inline'` trade-off, and record the concrete Content Security Policy, Trusted Types, SRI, and the accepted `style-src 'unsafe-inline'` trade-off in the Web UI security baseline of `docs/architecture/security-and-data-governance.md`; update `src/clients/README.md` (now executable: purpose, commands, Kiota and Playwright prerequisites, the `simulated` and `platform` browser-test projects), the root `README.md` (Web UI commands and the OpenAPI snapshot update command), and the "Current State" in `AGENTS.md`.
- [ ] T020 Run the validation in [quickstart.md](quickstart.md): platform build and Host tests, then in `src/clients/web/` `npm ci`, `npm run build`, `npm test`, and `npm run test:e2e` (both projects), and `node .github/scripts/check-markdown.mjs`; fix what fails.

---

## Dependencies & Execution Order

- **Setup (Phase 1)**: T001 → T002 (the build generates the client from the snapshot).
- **Foundational (Phase 2)**: after Setup; blocks all stories.
- **US1 (Phase 3)**: after Foundational. **US2 (Phase 4)**: after US1 (guards and session). **US3 (Phase 5)** and **US4 (Phase 6)**: after US2 (shell and navigation).
- **Polish (Phase 7)**: after all stories.
- Within each story, the platform end-to-end task comes last.

### Parallel opportunities

- Phase 2: T003 (`src/platform/`) and T004 (`tests/e2e/support/`) in parallel. T005 follows because it uses T003's connectivity middleware. T006 and T007 both change `vite.config.ts`.
- Phase 5: none. T014 and T015 both register routes in `src/router.tsx`, and their browser tests need those routes.
- No other phase has tasks with disjoint footprints.

## Implementation Strategy

1. Setup and Foundational, then User Story 1 as the MVP (sign-in works end to end).
2. Add User Story 2 (shell), then 3 (club structure), then 4 (members).
3. Documentation and full validation.
