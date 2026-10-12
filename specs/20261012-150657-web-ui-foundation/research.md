# Research: Web UI Foundation

## R1. OpenAPI source for client generation: committed snapshot

- **Decision**: Commit the platform's OpenAPI document as
  `src/platform/SocAlytics.Platform.Api/openapi/v1.json`. A new Host test
  (`OpenApiSnapshotTests`) starts the API as the existing Host tests do,
  fetches `/openapi/v1.json`, normalizes it (stable property order, no server
  URLs), and fails with a diff when the committed file differs. An opt-in
  environment variable rewrites the file, for developers who change the API.
  The Web UI `build` script runs `kiota generate --language typescript
  --openapi ../../platform/SocAlytics.Platform.Api/openapi/v1.json --output src/generated/platform-api --class-name PlatformApiClient --clean-output`.
- **Rationale**: The Web UI build needs no running API and no .NET build. The
  snapshot is reviewable, and the Web UI verification environment already runs
  the Web UI check when the API project changes, which catches a broken
  client. The architecture's "bundled OpenAPI document" is satisfied.
- **Alternatives considered**:
  - **Build-time document generation through `Microsoft.Extensions.ApiDescription.Server`**:
    it couples `npm run build` to a .NET build of the API.
  - **Fetching from a running API**: it needs the platform up for every build.

## R2. Session handling in the client

- **Decision**:
  - Sign-in (`POST /api/v1/session`) and session restore (`GET /api/v1/session`
    on start) return `SessionInfoDto` with the anti-forgery token, the
    password-change flag, and the member with club and team roles.
  - The session provider keeps that in memory only.
  - A Kiota middleware adds `X-CSRF-Token` to `POST`, `PUT`, `PATCH`, and
    `DELETE`.
  - A second middleware maps `401` and the session-ended problem types to a
    session-ended event. That clears TanStack Query's cache and routes to
    sign-in with a message (FR-009). A password-change-required `403` routes to
    the password-change view (FR-007).
  - Roles are re-read after each navigation through the cached session query
    with a short stale time, so role changes apply at the next action (FR-011).
- **Rationale**: This uses only existing operations, and the cookie stays
  HttpOnly. No secret is placed in storage (FR-004, SC-003).

## R3. One origin through the AppHost

- **Decision**:
  - The AppHost adds the Web UI as a Vite resource (Aspire JavaScript hosting)
    referencing the API.
  - `vite.config.ts` proxies `/api` and `/openapi` to the API endpoint that
    Aspire injects, in both `server` and `preview`.
  - The browser talks only to the Web UI origin, so the API's `__Host-` cookie
    and anti-forgery work as same-origin (FR-019).
  - An `e2e` mode of the AppHost, selected by an environment variable, serves
    the built Web UI with `vite preview`. It sets a known first Club Admin
    password and uses fresh, non-persistent database containers.
- **Alternatives considered**:
  - **Serving static files from the API**: it moves Web UI hosting into the
    platform deployment unit, against `client-applications.md`.
  - **CORS with credentials**: cross-origin cookies weaken the BFF design.

## R4. Content Security Policy, Trusted Types, and SRI

- **Decision**:
  - Preview mode and production-shaped builds send:
    `default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'; require-trusted-types-for 'script'; trusted-types default react-dom`.
  - Fonts and icons are bundled, so there are no third-party origins.
  - A build plugin adds `integrity` attributes to every emitted script and
    stylesheet.
  - The development server (hot reload) uses a relaxed policy and is never
    used for tests that measure CSP.
- **Rationale**: Scripts are fully locked down, which is the security goal of
  the baseline. Inline styles are allowed because MUI's Emotion injects
  `<style>` elements at run time. A nonce is impossible for static hosting
  without a server-rendered page.
- **Accepted trade-off**: `style-src 'unsafe-inline'` permits injected styles
  (CSS-based data exfiltration is mitigated by `connect-src 'self'` and
  `img-src 'self' data:`). This is recorded in
  [security-headers.md](contracts/security-headers.md).
- **Alternatives considered**: a build-time CSS extraction or Pigment CSS
  instead of Emotion. Rejected for this slice because the architecture names
  MUI and the risk is mitigated; it can be revisited later.

## R5. Browser test strategy

- **Decision**: Playwright with two projects:
  - **`simulated`**: runs against `vite preview` with `page.route`. A small
    simulated API answers `/api/v1/**` with fixtures typed from the generated
    models, so fixtures break when the API contract changes.
  - **`platform`**: runs against the AppHost in `e2e` mode. It covers
    sign-in, the required password change, session end (sign-out and a Club
    Admin ending the session), role-based navigation for Club Admin, Coach, and
    Viewer, and one create flow each for seasons, teams, and members
    (clarification).

  `@axe-core/playwright` runs WCAG 2.2 AA rules (`wcag2a`, `wcag2aa`,
  `wcag21a`, `wcag21aa`, `wcag22aa`) on every view in `simulated` (SC-007). A
  `securitypolicyviolation` listener fails any test with a CSP violation
  (SC-006). A storage check after each test asserts that `localStorage`,
  `sessionStorage`, IndexedDB, and `document.cookie` contain no secret
  (SC-003).
- **Rationale**: Fast, deterministic coverage of every scenario, plus proof
  against the real platform.
- **Alternatives considered**: Mock Service Worker in the browser. It installs
  a service worker, which conflicts with the later PWA service worker and with
  the CSP.

## R6. Unit tests

- **Decision**: Vitest with jsdom and Testing Library covers the middlewares,
  adapters (with a fake Kiota request adapter), the session provider, guards,
  error mapping, and view logic. `npm test` = `vitest run`.

## R7. Translation keys

- **Decision**: i18next with react-i18next, one namespace per feature, and
  English resources in `src/i18n/en/*.json`. A unit test fails when a view
  renders a literal string that is not a key (lint-free approach: a test scans
  `src/features/**` for JSX text nodes outside `t()`) (FR-021).

## R8. Conflict handling

- **Decision**: Every edit sends the loaded `version` with `If-Match`, as the
  existing API requires. A `412` or version conflict shows "changed by someone
  else" with a reload action. A `428` (version required) is a programming
  error, covered by unit tests (FR-017).

## R9. Connectivity state

- **Decision**: A connectivity provider combines `navigator.onLine` with
  fetch failures from the platform adapter, and probes `/alive` with
  exponential backoff while unreachable. State-changing buttons read it and are
  disabled while unreachable, and queries refetch on recovery (FR-013).

## R10. Dependency pins and Node.js

- **Decision**:
  - `package.json` declares `"engines": { "node": ">=24 <25" }`, exact
    versions for all direct dependencies, and `@playwright/test` `1.63.0`.
  - The lockfile is committed.
  - `npm ci` with `PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1` matches the
    environment check.
