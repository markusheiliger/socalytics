# Feature Specification: Installable Web UI with Service Worker Updates

**Feature Branch**: `20261015-194113-web-ui-pwa-updates`

**Created**: 2026-10-15

**Status**: Draft

**Input**: User description: "Installable PWA with Service Worker Updates: coaches and Club Admins install the Web UI as an app on Chromium-based browsers on Windows, it starts quickly from a cached application shell, and it updates itself safely with every release, as the accepted delivery decision requires; this is the prerequisite for the later offline mode. Scope: a web app manifest (name, icons, theme, start URL, standalone display) that makes the Web UI installable; one service worker, versioned with each release, that precaches the application shell (HTML, scripts, styles, fonts, icons with their integrity hashes) and never caches API responses, uploads, or recording data, within the existing Content Security Policy and Trusted Types, adding only what the worker and manifest need; update flow: when a new release is available the user sees an update prompt and the new version activates on reload, forward-fix only; version skew: the API tells the client the minimum Web UI version it requires, and an older client stops issuing requests, updates through its service worker, and reloads before continuing; offline shell: without a network the installed app still opens and shows the existing offline state with 'connection required' for every screen, no data cached and nothing queued; sign-out clears no shell cache (it holds no user data) and the worker caches no user data. Out of scope: offline data, replica, entitlements, key custody, and synchronization (offline mode, after its prototype gate); push notifications; background sync; app stores; browsers other than the matrix's first entry (Chromium-based on Windows) for installation evidence. Depends on Web UI Foundation and Web UI Verification Environment. Implements docs/architecture/platform-implementation.md (Client Technology: a service worker caches the application shell and is versioned with each release), docs/architecture/client-applications.md (Delivery And Workspace: installable PWA, service-worker updates, forward-fix; the Version skew rule in Offline Mode In A PWA), and EV-D09 in docs/architecture/client-decision-evidence.md; corrects 'service worker … not implemented' statements and provides the 'PWA installs, updates through its service worker' evidence of Architecture Reassessment, while offline mode stays unimplemented."

## Clarifications

### Session 2026-10-15

- Q: When the platform needs a newer Web UI than the one open, who enforces it? → A: The Web UI: the platform advertises the minimum Web UI version with its responses, and an older Web UI stops sending requests, updates, and reloads; the platform does not refuse requests by client version.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - A coach installs the Web UI as an app (Priority: P1)

A coach uses the Web UI in a Chromium-based browser on Windows. The browser offers to install it; once installed, it opens in its own window from the Start menu or taskbar like any app, with the SocAlytics name and icon, and starts quickly because its application shell is already on the device. Sign-in, navigation, and every workflow work exactly as in the browser tab.

**Why this priority**: Installation and a fast-starting shell are the visible value of the PWA and the foundation every later offline capability builds on.

**Independent Test**: Can be fully tested in a Chromium-based browser by confirming the Web UI is reported as installable, installing it, opening it in its own window, and comparing a repeat start with and without the cached shell.

**Acceptance Scenarios**:

1. **Given** a Chromium-based browser on Windows, **When** a user opens the Web UI, **Then** the browser reports it as installable with the SocAlytics name and icon.
2. **Given** an installed Web UI, **When** the user opens it, **Then** it opens in its own window at the start page and every connected workflow behaves as in the browser tab.
3. **Given** a repeat visit, **When** the application starts, **Then** the application shell is served from the device and no shell file is downloaded again unless a new release exists.
4. **Given** any page, **When** its network traffic is inspected, **Then** API responses, upload data, and recording data are never stored by the application's offline cache.

---

### User Story 2 - Users receive new releases safely (Priority: P2)

When a new Web UI release is deployed, an open or installed Web UI notices it, tells the user that an update is available, and switches to the new version when the user reloads or chooses to update. Updates only move forward. When the platform requires a newer Web UI than the one running, the old Web UI does not keep talking to it: it stops, updates, and reloads before the user continues.

**Why this priority**: Without safe updates an installed app could run an outdated shell against a newer API indefinitely; the accepted delivery decision makes service-worker updates the release mechanism.

**Independent Test**: Can be fully tested by serving release A, opening and installing it, deploying release B, and confirming the update prompt and the switch on reload; then making the platform require release B while release A is open and confirming that release A stops sending requests, updates, and reloads.

**Acceptance Scenarios**:

1. **Given** an open Web UI of release A, **When** release B is deployed, **Then** within the update check period the Web UI shows that an update is available without interrupting the current task.
2. **Given** the update notice, **When** the user chooses to update or reloads, **Then** release B is active and release A's shell files are no longer used.
3. **Given** an open Web UI of release A, **When** the platform reports that it requires at least release B, **Then** release A sends no further requests, tells the user it is updating, updates to release B, and reloads; unsaved form input on the current screen is the only thing lost, and the user is told so.
4. **Given** an update fails to download, **When** the platform requires the newer release, **Then** the Web UI shows that it must be reloaded and keeps blocking requests until the newer release runs.
5. **Given** two open windows of the Web UI, **When** an update activates, **Then** both end up on the new release.

---

### User Story 3 - The installed app opens without a network (Priority: P3)

A coach opens the installed Web UI in a room without connectivity. The app still opens, shows the SocAlytics shell, and clearly states that a connection is required for every screen; nothing is shown from earlier sessions and nothing is queued. When the connection returns, the app continues normally.

**Why this priority**: It avoids a browser error page and sets the expectation for the later offline mode, but delivers no data offline.

**Independent Test**: Can be fully tested by installing the Web UI, disconnecting the network, opening it, and confirming the shell with the connection-required state, then reconnecting and confirming normal operation.

**Acceptance Scenarios**:

1. **Given** an installed Web UI and no network, **When** the user opens it, **Then** the shell appears with the existing offline state and a "connection required" message instead of a browser error page.
2. **Given** the offline state, **When** the user navigates, **Then** every screen shows that a connection is required and no member, team, match, or recording data appears.
3. **Given** the connection returns, **When** the existing connectivity check succeeds, **Then** the Web UI continues with the session rules that already apply (signed in if the session is still valid, otherwise the sign-in page).

---

### Edge Cases

- The user signs out: the shell cache remains (it holds no user data); nothing personal remains in the application's offline cache.
- The browser evicts the cache: the next start downloads the shell again; nothing breaks.
- A release changes only the API, not the Web UI: no update notice appears unless the platform raises the minimum Web UI version.
- The platform requires a newer version while the user is uploading recordings: the upload's existing resume journal keeps its progress; the update proceeds after the user is told that the upload resumes after the reload.
- The user dismisses the update notice: it stays available in the shell until the update is applied; a required minimum version still forces the update.
- A browser that does not support installation or service workers: the Web UI works as today in the tab, without installation or offline shell.
- The service worker registration fails: the Web UI works as today; the failure is logged in the browser console without user data.

## Requirements *(mandatory)*

### Functional Requirements

#### Installation

- **FR-001**: The Web UI MUST be installable in Chromium-based browsers, with the name SocAlytics, a short name, icons in the sizes the browsers require (including a maskable icon), a theme color, its start page, and a standalone window.
- **FR-002**: The installed Web UI MUST offer exactly the same workflows, authorization, and session behavior as the Web UI in a browser tab.

#### Application shell cache

- **FR-003**: The Web UI MUST register one application worker that stores the application shell of the current release (the start page, scripts, styles, fonts, icons, and translations) on the device and serves it on later starts.
- **FR-004**: The application worker MUST NOT store API responses, sign-in or session data, upload parts, recordings, clips, or any other user data, and MUST pass every request outside the application shell to the network unchanged.
- **FR-005**: Every stored shell file MUST be verified against the integrity hash the release recorded for it; a mismatch MUST discard the release's cache and fall back to the network.
- **FR-006**: The Web UI MUST keep the existing Content Security Policy, Trusted Types enforcement, Subresource Integrity, and the rule that no third-party origin is contacted, adding only the permissions the application worker and the installation information require.

#### Updates

- **FR-007**: Each release MUST carry a version; the application worker of a release MUST store only that release's shell and remove older releases' shells when it activates.
- **FR-008**: An open Web UI MUST check for a new release when it starts, when it regains focus, and at a fixed period while open, and MUST show a non-blocking "update available" notice when one exists.
- **FR-009**: Choosing to update, or reloading, MUST activate the new release in every open window of the Web UI; releases MUST never activate an older version (forward-fix only).

#### Version skew

- **FR-010**: The platform MUST tell the Web UI, with its responses, the minimum Web UI version it requires; the value is part of the platform's deployment configuration. The platform MUST NOT refuse requests based on the client's version; enforcement is the Web UI's (FR-011).
- **FR-011**: When the running Web UI's version is lower than the required minimum, it MUST stop sending further requests, tell the user it is updating (naming the possible loss of unsaved input on the current screen), update, and reload; if the update cannot be obtained, it MUST keep blocking requests and ask the user to reload.
- **FR-012**: A running upload MUST keep its progress across a forced update through the existing resume journal, and the user MUST be told that it resumes after the reload.

#### Offline shell

- **FR-013**: Without a network, the installed Web UI MUST open from the stored shell and show the existing offline state with a "connection required" message on every screen, showing no data from earlier sessions and queuing no actions.
- **FR-014**: When the connection returns, the Web UI MUST continue under the existing session rules without further user action.

#### Documentation

- **FR-015**: The Web UI documentation MUST describe installation, the update notice, the minimum version setting of the platform, and that offline mode with local data does not exist yet.

### Key Entities

- **Release Version**: The version of a Web UI release, carried by its shell and its application worker; compared with the platform's minimum.
- **Minimum Web UI Version**: The platform's deployment setting that names the oldest Web UI release it accepts; sent with its responses.
- **Application Shell Cache**: The device-local copy of one release's shell files with their integrity hashes; holds no user data; replaced on each release.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In automated browser tests on Chromium, the Web UI is reported as installable and its installed window opens the start page in 100% of runs.
- **SC-002**: A repeat start downloads no application shell file when the release is unchanged, and the start page is visible in under 2 seconds on the reference test machine.
- **SC-003**: After a new release is deployed, an open Web UI shows the update notice within one update check period plus 10 seconds, and runs the new release after one reload, in 100% of automated tests.
- **SC-004**: When the platform's minimum version exceeds the running release, the old release sends no further request after receiving that signal and runs the new release within 30 seconds, in 100% of automated tests.
- **SC-005**: In automated tests, 0 API responses, session data, upload parts, or recordings are found in the application's offline cache.
- **SC-006**: The security header, Trusted Types, Subresource Integrity, and no-third-party-origin tests keep passing unchanged except for the permissions FR-006 adds.

## Assumptions

- **Dependencies**: Web UI Foundation (shell, sign-in, connectivity state, security headers, Trusted Types, Subresource Integrity) and Web UI Verification Environment (Chromium for browser tests), both implemented; the upload resume journal of Web UI Match Workflow and Recording Upload is reused for FR-012.
- **Browsers**: Installation evidence covers Chromium-based browsers (Chromium in automated tests; Edge and Chrome on Windows in manual checks), the first entry of the supported-browser matrix; other browsers keep working in the tab.
- **Update period**: The fixed update check period is a non-production value chosen in the plan (minutes, not seconds); the minimum Web UI version is deployment configuration with a default that requires no particular version.
- **Releases**: The Web UI and the platform API are released together, as the accepted delivery decision states; the minimum version lets the platform refuse an older client that is still open.
- **Offline mode**: Local data, the replica, entitlements, key custody, synchronization, and their prototype gate remain future work; this feature caches no user data.
- **Out of scope**: push notifications, background synchronization, app store packaging, and per-user opt-in (installation needs no opt-in because it stores no user data).
- **Architecture**: This feature implements the service worker of `docs/architecture/platform-implementation.md` (Client Technology), the installable PWA and service-worker updates of `docs/architecture/client-applications.md` (Delivery And Workspace) and its Version skew rule, and `EV-D09` in `docs/architecture/client-decision-evidence.md`, and corrects every statement that no service worker exists, while offline mode stays unimplemented.
