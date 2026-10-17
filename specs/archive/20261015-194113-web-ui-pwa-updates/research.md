# Research: Installable Web UI with Service Worker Updates

## R1. A repository-owned service worker instead of a PWA plugin

- **Decision**: Write the service worker by hand (`src/clients/web/src/serviceWorker/sw.ts`, built as its own entry to `/sw.js`) and add a small local Vite plugin (`src/clients/web/build/pwaShell.ts`) that, after the bundle is written, emits `/shell-manifest.json` (release version plus every shell file of `dist/` with its SHA-384 integrity) and stamps the version into `sw.js`. The worker precaches exactly those files with `cache.addAll` on `Request` objects that carry the `integrity`, so a mismatch fails the install (FR-005). It answers navigations with the cached `index.html`, shell assets cache-first from the current release's cache, and passes everything else, including `/api/**`, `/alive`, and object-storage requests, to the network untouched (FR-004).
- **Rationale**: The shell is a small static set; a hand-written worker of about a hundred lines plus a build step avoids a Workbox dependency tree on an origin whose security review requires minimal dependencies (`EV-SPR-01`), and keeps the cache rules auditable. `vite-plugin-sri3` already computes integrity for HTML; the plugin computes it for the whole shell.
- **Alternatives considered**: `vite-plugin-pwa` with Workbox `injectManifest` (rejected: large dependency tree for a few rules); runtime caching of API responses (rejected: FR-004).

## R2. Release version

- **Decision**: The release version is the build's UTC timestamp `YYYY.MMDD.HHMMSS` (for example `2026.1015.194113`), or `SOCALYTICS_WEB_VERSION` when set (a CI or release value with the same shape). It is compiled into the app (`import.meta.env.VITE_SOCALYTICS_WEB_VERSION` through `define`), written into `sw.js` and `/shell-manifest.json`, and compared as three integers. The cache is named `socalytics-shell-<version>`; activation deletes every other `socalytics-shell-*` cache (FR-007).
- **Rationale**: A monotonic, sortable version without a release process the repository does not have yet; forward-fix means a later build always sorts higher.
- **Alternatives considered**: `package.json` version (stays `0.0.0`); a content hash (not ordered).

## R3. Registration, updates, and the notice

- **Decision**: `src/clients/web/src/serviceWorker/registration.ts` registers `/sw.js` with `{ scope: '/', updateViaCache: 'none' }` through a Trusted Types policy `service-worker` whose `createScriptURL` accepts exactly `/sw.js`; it calls `registration.update()` at start, on `visibilitychange` to visible, and every 15 minutes. A waiting worker shows the non-blocking "Update available" notice in the shell (`src/clients/web/src/shell/UpdateNotice.tsx`); "Update" posts `SKIP_WAITING`, and every window reloads on `controllerchange` (FR-008, FR-009). A new worker installs but never takes over a page by itself. Registration failures are logged without user data; browsers without service workers keep working as today.
- **Rationale**: The standard waiting-worker update pattern; `updateViaCache: 'none'` makes update checks reach the network; the check period is a non-production value.

## R4. Minimum Web UI version (version skew)

- **Decision**: Platform: `WebUi:MinimumVersion` (default empty = no minimum; validated as `YYYY.MMDD.HHMMSS` when set) in `src/platform/SocAlytics.Platform.Api`, sent by a small middleware as the response header `SocAlytics-Minimum-Web-Version` on every `/api/**` response, and listed in `Access-Control-Expose-Headers` only if CORS ever applies (it does not on one origin). The platform never refuses requests by client version (clarification). Web UI: a Kiota middleware `src/clients/web/src/platform/middleware/minimumVersion.ts` compares the header with the running version; when it is higher, it sets a `blocked` state that makes every later request fail fast without sending (FR-011), and the shell shows "Updating SocAlytics" (mentioning possible loss of unsaved input and that uploads resume), calls `registration.update()`, activates the waiting worker, and reloads; if no newer worker arrives within 30 seconds it shows "Reload required" and keeps blocking. The upload worker stops issuing part requests while blocked and its existing resume journal continues after the reload (FR-012).
- **Rationale**: The architecture's rule ("the service worker updates the PWA … whenever the API requires a newer client") enforced by the client; one header costs nothing on one origin.
- **Alternatives considered**: A separate version endpoint polled by the client (rejected: a second request path; the header arrives with every response the client makes anyway).

## R5. Manifest and icons

- **Decision**: `src/clients/web/public/manifest.webmanifest` (`name` "SocAlytics", `short_name` "SocAlytics", `start_url` "/", `scope` "/", `display` "standalone", `theme_color` and `background_color` from the theme, icons 192 and 512 PNG plus a 512 maskable PNG under `public/icons/`), linked from `index.html` with `<link rel="manifest">` and `<meta name="theme-color">`. The icons are generated once from the existing SocAlytics mark and committed.
- **Rationale**: Chromium's installability criteria: manifest with name, icons 192 and 512, start URL, display, and a registered service worker with a fetch handler.

## R6. Content Security Policy changes

- **Decision**: Add `service-worker` to `trusted-types` (FR-006) and `manifest-src 'self'` to the preview and development policies in `src/clients/web/vite.config.ts` and the header test; `worker-src 'self'`, `img-src 'self'`, and Subresource Integrity already cover the worker and icons. Nothing else changes.

## R7. Offline shell

- **Decision**: Navigations without a network get the cached `index.html`; the app starts, the existing `ConnectivityProvider` reports offline, and every route renders the existing connection-required state (`src/clients/web/src/shell/`), with no data because no API response is cached (FR-013). When connectivity returns, the existing session check decides between the current page and sign-in (FR-014).

## R8. Tests

- **Decision**: Vitest unit tests for the plugin (manifest of files and integrity, version stamping), the version comparison, the minimum-version middleware (blocking), and the registration module (with a fake `navigator.serviceWorker`). Playwright `simulated` project: installability through the Chromium DevTools protocol (`Page.getInstallabilityErrors` empty), repeat start served from the worker, no API, upload, or session entry in `caches`, offline start with the connection-required state, the update notice and reload, and version skew; a test support server (`src/clients/web/tests/e2e/support/releaseServer.ts`) serves two builds with different `SOCALYTICS_WEB_VERSION` values (built in global setup) and switches between them. Platform Host tests for the header, its default, and the options validation. The existing header, Trusted Types, SRI, and third-party-origin tests keep passing (SC-006).
- **Rationale**: Installation, caching, and updates are only observable in a real browser; the two-release server makes the update path deterministic.
- **Tooling**: Node, Vite, Playwright Chromium, and .NET are already provided by the environment actions; no new npm package and no environment feature are needed.
