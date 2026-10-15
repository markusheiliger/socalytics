---

description: "Task list for Installable Web UI with Service Worker Updates"
---

# Tasks: Installable Web UI with Service Worker Updates

**Input**: Design documents from `specs/20261015-194113-web-ui-pwa-updates/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/web-ui-pwa.md](contracts/web-ui-pwa.md), [quickstart.md](quickstart.md)

**Tests**: Required by the constitution. Every task adds the tests for the code it creates in the same task, so the Web UI and the platform build and their test suites stay green after each task.

**Organization**: Tasks are grouped by user story. Merged before this feature starts (do not re-create): Web UI Foundation (`src/clients/web/vite.config.ts` with the security headers and `vite-plugin-sri3`, `src/trustedTypes.ts`, `src/platform/client.ts` and its Kiota middleware chain under `src/platform/middleware/`, `ConnectivityProvider`, `AppLayout`, `ConnectivityIndicator`, `tests/e2e/simulated/headers.spec.ts`, `tests/unit/build/sri.test.ts`, `tests/e2e/support/simulatedApi.ts`, the `simulated` Playwright project) and Web UI Match Workflow and Recording Upload (the upload worker and its resume journal under `src/uploads/`). No migration.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies on incomplete tasks); consecutive `[P]` tasks under one heading run in parallel, each from the same commit
- **[Story]**: Which user story this task belongs to (US1–US3)
- Every task names its exact file paths

---

## Phase 1: Foundational (Blocking Prerequisites)

**Purpose**: A sortable release version in every build, and the platform's minimum-version signal.

- [x] T001 [P] Release version and shell manifest (research.md § R1, § R2; data-model.md § Release Version, § Shell Manifest): add `src/clients/web/src/serviceWorker/version.ts` (parse `YYYY.MMDD.HHMMSS`, compare as three integers, reject other shapes); add the local Vite plugin `src/clients/web/build/pwaShell.ts` (computes the release version from `SOCALYTICS_WEB_VERSION` or the UTC build time, exposes it as `import.meta.env.VITE_SOCALYTICS_WEB_VERSION` through `define`, and after the bundle is written emits `dist/shell-manifest.json` with `version` and `files` [{ `url`, `integrity` `sha384-…` }] for every file of `dist/` except `sw.js` and `shell-manifest.json`, and replaces the version placeholder in `dist/sw.js` when that file exists); register it in `src/clients/web/vite.config.ts` and include `build/` in `src/clients/web/tsconfig.node.json`. Tests: `src/clients/web/tests/unit/serviceWorker/version.test.ts` and `src/clients/web/tests/unit/build/pwaShell.test.ts` (file list, integrity values, version from the variable and from the clock, rejected shapes).
- [x] T002 [P] Platform minimum Web UI version (contracts/web-ui-pwa.md § 1; data-model.md § Minimum Web UI Version; research.md § R4; FR-010): add `src/platform/SocAlytics.Platform.Api/WebUi/MinimumWebVersion.cs` (options `WebUi:MinimumVersion`, empty by default, validated at start-up as `YYYY.MMDD.HHMMSS` when set; middleware that adds `SocAlytics-Minimum-Web-Version` to every `/api/**` response when set, never refusing a request); bind, validate, and add the middleware in `src/platform/SocAlytics.Platform.Api/Program.cs`. Tests in `src/platform/Tests/SocAlytics.Platform.Host.Tests/MinimumWebVersionTests.cs`: header present on API responses including errors when set, absent when empty, absent on `/alive` and `/health`, invalid values refused at start-up, requests never refused; the OpenAPI snapshot test stays green (update `src/platform/SocAlytics.Platform.Api/openapi/v1.json` only if the document lists shared response headers).

**Checkpoint**: Builds carry a version and a shell manifest; the platform can name its minimum.

---

## Phase 2: User Story 1 - A coach installs the Web UI as an app (Priority: P1) 🎯 MVP

**Goal**: The Web UI is installable and starts from an integrity-verified shell cache that holds no user data.

**Independent Test**: In Chromium the Web UI has no installability errors; a repeat start loads no shell file from the network; `caches` hold only shell URLs.

- [x] T003 [US1] Manifest, service worker, and registration (contracts/web-ui-pwa.md § 2–§ 4; research.md § R1, § R3, § R5, § R6; FR-001–FR-006): add `src/clients/web/public/manifest.webmanifest` (`name` and `short_name` "SocAlytics", `start_url` "/", `scope` "/", `display` "standalone", `theme_color` and `background_color` from `src/clients/web/src/theme.ts`) and `src/clients/web/public/icons/` (192 and 512 PNG and a 512 maskable PNG from the existing SocAlytics mark); link the manifest and `theme-color` in `src/clients/web/index.html`; add `src/clients/web/src/serviceWorker/sw.ts`, built as a separate entry to `dist/sw.js` with the version placeholder (install: `cache.addAll` of every `shell-manifest.json` file as `new Request(url, { integrity })` into `socalytics-shell-<version>`; activate: delete other `socalytics-shell-*` caches; fetch: navigations → cached `/index.html`, listed shell URLs → cache first, everything else not handled; message `SKIP_WAITING` → `skipWaiting()`); add `src/clients/web/src/serviceWorker/registration.ts` (register `/sw.js` with `{ scope: '/', updateViaCache: 'none' }` through a `service-worker` Trusted Types policy whose `createScriptURL` accepts exactly `/sw.js`; log failures without user data; no-op without service worker support) called from `src/clients/web/src/main.tsx`; add the policy to `src/clients/web/src/trustedTypes.ts`; add `manifest-src 'self'` and `service-worker` to the `trusted-types` list of both policies in `src/clients/web/vite.config.ts` and update `src/clients/web/tests/e2e/simulated/headers.spec.ts`. Tests: unit tests for the worker's routing decisions and the registration module under `src/clients/web/tests/unit/serviceWorker/`; `src/clients/web/tests/e2e/simulated/pwa-install.spec.ts` (Chromium DevTools `Page.getInstallabilityErrors` empty; manifest fields; a repeat start with an unchanged release loads no shell file from the network and shows the start page within 2 seconds (SC-002); after the browser's caches are cleared, the next start downloads the shell again and works; after signing in, browsing, and uploading in the simulated API, `caches` contain only URLs listed in `shell-manifest.json`; a tampered shell file fails the install and the page still works from the network); the existing SRI and third-party-origin tests pass.

**Checkpoint**: Installable, fast-starting, and free of user data.

---

## Phase 3: User Story 2 - Users receive new releases safely (Priority: P2)

**Goal**: Update notice, forward-only activation in every window, and enforced minimum versions.

**Independent Test**: Switching the release server from A to B shows the notice and B runs after "Update"; a minimum above A blocks A's requests and B runs within 30 seconds.

- [ ] T004 [US2] Update checks and notice (contracts/web-ui-pwa.md § 5; data-model.md § Update State; research.md § R3; FR-007–FR-009): in `src/clients/web/src/serviceWorker/registration.ts`, call `registration.update()` at start, when the page becomes visible, and every 15 minutes, expose the `current` and `available` states, post `SKIP_WAITING` on update, and reload on `controllerchange`; add `src/clients/web/src/shell/UpdateNotice.tsx` (non-blocking notice "A new version of SocAlytics is available." with "Update") rendered by `src/clients/web/src/shell/AppLayout.tsx`, with its strings in `src/clients/web/src/i18n/en/shell.json`; add `src/clients/web/tests/e2e/support/releaseServer.ts` (serves two prebuilt releases with the preview security headers and switches between them) and build release A and B with distinct `SOCALYTICS_WEB_VERSION` values in the Playwright global setup in `src/clients/web/playwright.config.ts`. Tests: unit tests for the update states; `src/clients/web/tests/e2e/simulated/pwa-update.spec.ts` (notice within the check period plus 10 seconds after switching to B, "Update" runs B in two open windows, A's cache removed, dismissing keeps the notice available, no activation to an older release).
- [ ] T005 [US2] Version skew (contracts/web-ui-pwa.md § 1, § 5; research.md § R4; FR-011, FR-012): add `src/clients/web/src/platform/middleware/minimumVersion.ts` (reads `SocAlytics-Minimum-Web-Version`; when it is higher than the running version, enters `required`, makes every later request fail fast without sending, and triggers the update) and add it to the chain in `src/clients/web/src/platform/client.ts`; in `registration.ts` and `src/clients/web/src/shell/UpdateNotice.tsx`, show the blocking `required` text, activate the newer worker and reload, or after 30 seconds without one show `reload-required` with "Reload" while keeping requests blocked; make the upload worker under `src/clients/web/src/uploads/` stop issuing part requests while blocked, keeping its resume journal; extend `src/clients/web/tests/e2e/support/simulatedApi.ts` to send the header. Tests: unit tests for the middleware (blocking, comparison, header absent) and the upload pause; `src/clients/web/tests/e2e/simulated/pwa-version-skew.spec.ts` (A receives a minimum of B: no further request leaves A, the notice appears, B runs within 30 seconds; with B unavailable `reload-required` appears and requests stay blocked; an upload in progress resumes after the reload).

**Checkpoint**: No outdated client keeps talking to a newer platform.

---

## Phase 4: User Story 3 - The installed app opens without a network (Priority: P3)

**Goal**: An offline start shows the shell with the connection-required state and no data.

**Independent Test**: Install, go offline, open: shell with connection required on every route; reconnect: normal operation under the session rules.

- [ ] T006 [US3] Offline shell (research.md § R7; FR-013, FR-014): make every route render the existing connection-required state when `ConnectivityProvider` reports offline at start, adjusting `src/clients/web/src/shell/AppLayout.tsx` or `src/clients/web/src/connectivity/ConnectivityProvider.tsx` only where the current start-up path assumes a network (for example a session check that renders an error instead of the offline state). Tests: `src/clients/web/tests/e2e/simulated/pwa-offline.spec.ts` (offline start after a previous session shows the shell and connection required, no member, team, match, or recording data on any route, nothing queued; on reconnect the session rules apply: still signed in with a valid session, otherwise sign-in).

**Checkpoint**: All user stories work.

---

## Phase 5: Polish & Cross-Cutting Concerns

- [ ] T007 Documentation (plan.md § Architecture Updates): make every narrative listed in plan.md § Architecture Updates and every status statement this feature affects anywhere in `docs/architecture/` true for the merged state, re-reading the documents now rather than only applying the listed rewrites (they are examples, not the whole job): turn the decided service-worker paragraph in `docs/architecture/client-applications.md` into present tense and correct its "not implemented yet" sentence; update the opening paragraph, Client Technology, and Architecture Reassessment of `docs/architecture/platform-implementation.md` (install and update evidenced on Chromium, offline mode still open); add `manifest-src 'self'` and the `service-worker` policy to the policy listing in `docs/architecture/security-and-data-governance.md`; record the install, launch, and update evidence for `EV-D09` in `docs/architecture/client-decision-evidence.md` without changing its dated review records; update `src/clients/README.md` (installation, update notice, `WebUi:MinimumVersion`, and the explicit statement that offline mode with local data does not exist yet, FR-015), `src/platform/README.md` (the `WebUi:MinimumVersion` option and header), `README.md` if it describes the Web UI's capabilities, and the "Current State" of `AGENTS.md` (the PWA shell exists; offline mode does not).
- [ ] T008 Run the validation in [quickstart.md](quickstart.md) § Automated validation and `node .github/scripts/check-markdown.mjs`; fix what fails.

---

## Dependencies & Execution Order

- **Phase 1**: T001 and T002 in parallel (Web UI build vs. platform API).
- **US1 (Phase 2)**: after Phase 1; T003 needs the version and shell manifest of T001.
- **US2 (Phase 3)**: after US1; T004, then T005 (needs the update states and the release server of T004, and the header of T002).
- **US3 (Phase 4)**: after US1; scheduled after US2.
- **Polish**: after all stories.

### Parallel opportunities

- Phase 1: T001 (`src/clients/web/build/`, `src/clients/web/src/serviceWorker/version.ts`, `vite.config.ts`, `tsconfig.node.json`, unit tests) with T002 (`src/platform/SocAlytics.Platform.Api/WebUi/`, `Program.cs`, Host tests).

## Implementation Strategy

1. Phase 1, then User Story 1 as the MVP: the installable PWA with an integrity-verified shell.
2. User Story 2 (updates and version skew), then User Story 3 (offline shell).
3. Documentation and validation; installation on Edge and Chrome on Windows is a manual check (quickstart.md § Manual validation).
