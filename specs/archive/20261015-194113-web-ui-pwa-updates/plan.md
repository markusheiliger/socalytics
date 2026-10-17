# Implementation Plan: Installable Web UI with Service Worker Updates

**Branch**: `20261015-194113-web-ui-pwa-updates` | **Date**: 2026-10-15 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/archive/20261015-194113-web-ui-pwa-updates/spec.md`

**Note**: This template is filled in by the `/speckit-plan` command; its definition describes the execution workflow.

## Summary

The Web UI becomes an installable PWA: a web app manifest with icons, and a
hand-written service worker that precaches exactly the release's application
shell, each file verified against its SHA-384 integrity, and passes every
other request, including all API traffic, to the network. A small local Vite
plugin emits the shell manifest and stamps a sortable release version
(`YYYY.MMDD.HHMMSS`). The shell checks for updates at start, on focus, and
every 15 minutes, shows a non-blocking update notice, and reloads every window
on activation. For version skew, the platform sends
`SocAlytics-Minimum-Web-Version` on API responses from deployment
configuration; an older Web UI blocks further requests, updates through its
worker, and reloads. Offline, the installed app opens the shell with the
existing connection-required state and no data. The CSP gains only
`manifest-src 'self'` and the `service-worker` Trusted Types policy.

## Technical Context

**Language/Version**: TypeScript with React 19 and Vite 7 (`src/clients/web`);
C# on .NET 10 for the platform header.

**Primary Dependencies**: Existing Vite build with `vite-plugin-sri3`, Kiota
fetch middleware chain, `ConnectivityProvider`, i18next, MUI; ASP.NET Core
middleware and options validation. No new npm or NuGet package.

**Storage**: Browser Cache Storage for the shell only; no platform storage.

**Testing**: Vitest (plugin, version comparison, middleware, registration);
Playwright `simulated` project with a two-release server (installability,
caching, offline start, update, version skew, headers); platform Host tests
(header and options).

**Target Platform**: Chromium-based browsers on Windows first; other browsers
keep the tab behavior.

**Project Type**: Web application plus web service (existing).

**Performance Goals**: SC-002 repeat start under 2 seconds without shell
downloads; SC-004 skew resolved within 30 seconds.

**Constraints**: No user data in caches; CSP, Trusted Types, SRI, and
no-third-party-origin rules kept; forward-fix only; no platform refusal by
version.

**Scale/Scope**: One shell of a few dozen files per release.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Evidence |
| --- | --- | --- |
| I. Architecture is the design authority | Pass | Implements the service worker of `platform-implementation.md` (Client Technology), the installable PWA and service-worker updates of `client-applications.md` (Delivery And Workspace) and its Version skew rule, and `EV-D09`; the minimum-version header and the hand-written worker reach the architecture through `## Architecture Updates`. |
| II. Source-area ownership | Pass | Web UI code in `src/clients/web`; the header middleware and option in `src/platform/SocAlytics.Platform.Api`. |
| III. API-first control plane | Pass | The client learns the minimum version from API responses; the worker never proxies or stores API data or media. |
| IV. Evidence over claims | Pass | Unit, browser, and Host tests (quickstart.md); installation on Edge and Chrome is a manual check, not a production-readiness claim. |
| V. Focused, minimal changes | Pass | No new dependency (research.md § R1); offline data stays out. |
| Tooling | Pass | Node, Vite, Playwright Chromium, and .NET are provided by the environment actions; no environment feature is needed (research.md § R8). |

Re-check after Phase 1 design: still passes; no Complexity Tracking entries.

## Architecture Updates

### Narratives

- [Client Applications](../../../docs/architecture/client-applications.md):
  Source Ownership And Delivery (service worker, installable PWA), Offline
  Mode In A PWA (the Version skew rule; offline data stays future), and
  Delivery And Workspace (installable PWA, service-worker updates,
  forward-fix) — implemented by this feature except offline mode.
- [Platform Implementation](../../../docs/architecture/platform-implementation.md):
  the opening status paragraph, Client Technology, and Architecture
  Reassessment ("the Web UI PWA installs, updates through its service worker
  …").
- [Security and Data Governance](../../../docs/architecture/security-and-data-governance.md):
  the Web UI security baseline (the policy listing gains `manifest-src 'self'`
  and the `service-worker` Trusted Types policy).
- [Client Decision Evidence](../../../docs/architecture/client-decision-evidence.md):
  `EV-D09` (installable PWA, service-worker updates) — evidence for the
  install, launch, and update rows; the offline rows stay open. Its dated
  review records stay unchanged.

### Design updates

Applied now, worded "decided, not yet implemented":

1. **Service worker, updates, and version skew** (`client-applications.md`
   Source Ownership And Delivery): a framework-free worker that precaches only
   the integrity-verified shell of one release and holds no user data; a
   sortable release version; update checks with a non-blocking notice;
   the platform's minimum Web UI version sent with API responses and enforced
   by the client only; the offline shell with the connection-required state.

The implementation turns update 1 into present tense and drops its "decided,
not yet implemented" marker.

### Status statements at merge

- `platform-implementation.md`, opening paragraph: "the Web UI's offline mode
  and service worker … do not exist yet" → the service worker exists; offline
  mode does not.
- `platform-implementation.md`, Client Technology: "The service worker,
  offline mode, container image, and production server configuration are not
  implemented." → the service worker is implemented.
- `client-applications.md`, Source Ownership And Delivery: "The service
  worker, offline mode, OCI container image, and Docker Compose deployment are
  not implemented yet." → only the service worker is implemented.
- `platform-implementation.md`, Architecture Reassessment: "the Web UI PWA
  installs, updates through its service worker, and meets the supported-browser
  evidence for offline mode" → install and update have evidence on Chromium;
  the offline-mode evidence stays open.
- `security-and-data-governance.md`, the Web UI baseline's policy listing →
  the two added directives.
- Outside `docs/architecture/` (kept current by the documentation task):
  `src/clients/README.md` ("No service worker, offline mode, container image,
  or deployment configuration exists yet"; installation, updates, and the
  `WebUi:MinimumVersion` setting), `src/platform/README.md` (the
  `WebUi:MinimumVersion` option and header), `README.md` if it describes the
  Web UI's capabilities, and `AGENTS.md` Current State (the PWA shell exists;
  offline mode does not).

The documentation task in `tasks.md` must make every narrative above and every
status statement the feature affects anywhere in `docs/architecture/` true for
the merged state, re-reading the documents at implementation time; the
rewrites listed here are examples, not the whole job.

## Project Structure

### Documentation (this feature)

```text
specs/archive/20261015-194113-web-ui-pwa-updates/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── web-ui-pwa.md
├── checklists/
│   └── requirements.md
└── tasks.md             # /speckit-tasks
```

### Source Code (repository root)

```text
src/clients/web/
├── index.html                                   # manifest link, theme-color
├── public/manifest.webmanifest, public/icons/   # 192, 512, 512 maskable
├── build/pwaShell.ts                            # Vite plugin: shell-manifest.json, version stamp
├── vite.config.ts                               # plugin, define, CSP additions, sw entry
├── src/serviceWorker/sw.ts                      # install, activate, fetch, SKIP_WAITING
├── src/serviceWorker/registration.ts            # Trusted Types policy, register, update checks
├── src/serviceWorker/version.ts                 # parse and compare versions
├── src/platform/middleware/minimumVersion.ts    # header check, blocking
├── src/platform/client.ts                       # middleware chain
├── src/shell/UpdateNotice.tsx, AppLayout.tsx    # notices
├── src/trustedTypes.ts                          # service-worker policy
├── src/uploads/…                                # pause part requests while blocked
├── src/i18n/en/shell.json
├── README.md (src/clients/README.md)
└── tests/unit/…, tests/e2e/simulated/pwa.spec.ts, tests/e2e/simulated/headers.spec.ts,
    tests/e2e/support/releaseServer.ts, playwright.config.ts (global setup builds two releases)

src/platform/SocAlytics.Platform.Api/
├── WebUi/MinimumWebVersion.cs                   # options and middleware
├── Program.cs                                   # binding, validation, middleware
└── Tests: src/platform/Tests/SocAlytics.Platform.Host.Tests/MinimumWebVersionTests.cs
```

**Structure Decision**: The PWA lives entirely in the Web UI project with one
local build plugin; the platform contributes one option and one response
header.

## Complexity Tracking

No constitution violations.
