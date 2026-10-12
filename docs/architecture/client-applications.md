# 2. Client Applications

SocAlytics has one client application: the React Web UI, delivered as a
Progressive Web App (PWA). Coaches use the same Web UI as every other user; an
offline mode lets them keep using previously synchronized team and match data
where connectivity is unreliable, such as post-match team meetings. The client
follows the shared [API-first principle](terminology-and-principles.md#1-api-first).

## Source Ownership And Delivery

The future React and TypeScript sources of the Web UI are owned under
`src/clients`. Only the first-level source-area README exists today; there are
no client projects, packages, manifests, containers, or builds yet.

The Web UI release is coordinated with the platform API and will be built and
deployed as a separate OCI container from `src/clients`. Future Docker Compose
deployment and Aspire local composition may compose that container with the
platform API without moving Web UI source under `src/platform`.

The PWA is served from a single origin. Its service worker caches the
application shell and is versioned with each release, so an installed PWA
always runs the current release after it reconnects. There is no separate
client build, release lifecycle, installer, or origin for coaches.

## Web UI

The React Web UI supports connected platform and club workflows, including:

- club, season, and team management
- club-user and team-role management
- platform configuration and administration
- the Analyst Manager fleet view for Club Admins: registration approval,
  rejection, and revocation, and each Manager's connectivity, operating state,
  and activity (see
  [Analyst Manager](analyst-manager.md#fleet-visibility-in-the-web-ui))
- match creation, recording uploads, and upload finalization
- team and match review, analytics, and agent conversations for coaches

What a user sees is decided by their roles and team authorization, online and
offline alike.

The provisional implementation is an independent React and TypeScript SPA built
with Vite. React Router handles navigation, TanStack Query manages remote server
state, and MUI provides accessible controls under a SocAlytics theme. It uses
Kiota-generated clients behind application-owned adapters and repositories.

Local accounts are always available, and configured external OpenID Connect
providers appear as additional login options. Both paths establish the same
ASP.NET Core backend-for-frontend session. Authentication tokens remain on the
server; the browser receives a Secure, HttpOnly, SameSite cookie and uses CSRF
protection for state-changing requests.

## Offline Mode

The first release is online-only. The architecture nevertheless preserves
offline operation as the target state for unreliable environments such as
post-match team meetings, delivered later as a separately gated capability of
the Web UI.

Offline readiness requires:

- stable resource identifiers and versioned API contracts
- a local persistence boundary for authorized team, match, analytics, and media
  metadata
- incremental synchronization rather than full-state replacement
- an outbox for changes made while disconnected (later; the first offline
  delivery is read-only)
- explicit conflict detection and resolution rules
- clear online, synchronizing, stale, and offline client states
- encrypted local data with sign-out, revocation, and retention controls

The server remains the system of record. The local store is a user-scoped
replica, and the Web UI must not depend on direct database or object storage
access. Features introduced before offline synchronization is delivered must
use application-owned repositories and explicit connectivity state, so they
can later support local reads and queued writes without rewriting screens.

### Offline Mode In A PWA

Offline mode is a capability of the one Web UI, not a separate module or
application. It applies these PWA-specific rules:

- **Opt-in per user and device.** A user enables offline mode explicitly in the
  installed PWA, and only once the browser grants persistent storage. Club
  administration, recording upload, agent submission, and refresh stay
  connected-only.
- **A worker owns the replica.** An encrypted local replica (for example SQLite
  compiled to WebAssembly on the Origin Private File System) is owned by a
  dedicated worker. Screens reach it only through application-owned
  repositories backed by worker messages; no screen uses storage APIs directly.
- **Key custody.** A key-encryption key protects the store key. It is
  preferably derived through the WebAuthn PRF extension from a platform passkey
  (Windows Hello, Touch ID or Face ID, Android), with one user gesture per
  offline session. Where PRF is unavailable, a non-exportable WebCrypto key in
  the browser profile protects it, or offline mode is not offered on that
  device.
- **Offline authority.** A short-lived signed entitlement, never the session
  cookie, unlocks an offline session. It is bound to a separate non-exportable
  WebCrypto key generated on the device, never to the passkey, because synced
  passkeys exist on all of a user's devices.
- **The replica is a cache.** The browser may evict local data. Eviction is a
  fail-safe loss that the UI shows as missing or stale data; it never grants
  access to anything.
- **Purge by crypto-erasure.** Destroying the key makes local content
  unreadable at once; deleting the browser storage follows, and the browser
  APIs confirm it.
- **One origin, hardened.** Administration and offline data share the origin,
  so the PWA uses a strict Content Security Policy with Trusted Types, no
  third-party scripts, and Subresource Integrity.
- **Version skew.** After reconnecting, the service worker updates the PWA
  before synchronization runs whenever the API requires a newer client.
- **Supported browsers.** Offline mode is supported per browser in a supported
  matrix, starting with Chromium-based browsers on Windows. Others follow once
  their storage, eviction, and PRF behavior is proven.

A prototype gate precedes the offline release. It provides, per browser in the
matrix, evidence for PRF availability and its fallback, device-bound entitlement
keys, eviction and persistence, purge, the Content Security Policy, and clip
quota.

**Decision status: Accepted.** See the
[client decision evidence](client-decision-evidence.md).

### Connected Agent Conversations

The Web UI uses the API/BFF surface for connected agent conversations. A valid
direct Coach Agent or Specialist Agent submission includes an idempotency key
and receives `202 Accepted`, durable conversation and invocation identifiers, a
status location, and retry guidance. The Web UI uses conditional REST polling
for status, accepted advice, and lineage. It does not call Hermes, publish agent
transport messages, or receive a separate completion semantic.

Ordinary conversation and lineage reads remain private to the initiating user
and require current team and match authorization on every poll. Connected agent
submission and refresh require network access. They do not add offline agent
invocation, local agent-state persistence, synchronization, or conflict rules;
those remain subject to the existing gates below and a future accepted contract.
The durable server-side behavior is defined by
[Intelligence and Agents](intelligence-and-agents.md#agent-orchestration-authority).

## Client Architecture Decisions (Accepted)

The nine detailed client decisions are accepted. The client implementation
gate is released for later scoped implementation proposals; these architecture
decisions do not themselves scaffold the client or add a client-facing API.

### Delivery And Workspace

The accepted delivery and workspace design establishes:

- an online-only first release using application-owned repository interfaces
  and explicit connectivity state
- one React PWA, released together with the platform API and containerized
  separately, so every workflow is built and maintained once
- a supported-browser matrix instead of operating-system packaging, starting
  with Chromium-based browsers on Windows; updates are delivered by the service
  worker, forward-fix by default

### Offline Data And Synchronization

The accepted offline data and synchronization design establishes:

- an encrypted local replica owned by a dedicated worker behind
  application-owned repositories
- REST delta synchronization with opaque cursors, tombstones, idempotency, and
  bounded authorized snapshot recovery
- authorized metadata and analytics plus explicitly downloaded clips; complete
  recordings are excluded by default
- read-only first offline delivery; later queued writes require versions/ETags,
  scoped idempotency, safe domain merges, and explicit conflict resolution

### Disconnected Trust And Lifecycle

The accepted disconnected trust and lifecycle design establishes:

- short-lived signed entitlements bound to a non-exportable key on the device;
  indefinite disconnected authority is prohibited
- a store key wrapped by a key derived through WebAuthn PRF, or else by a
  non-exportable WebCrypto key in the browser profile
- bounded per-class retention, inaccessible-first deletion, crypto-erasure,
  narrow expiring holds, explicit removal, and minimized diagnostics

The evidence and approval record is maintained in
[Client Architecture Decision Evidence](client-decision-evidence.md).

---

Related architecture: [Index](README.md) | [Overview](overview.md) |
[Security and Data Governance](security-and-data-governance.md) |
[Platform Implementation Profile](platform-implementation.md) |
[Match Data Pipeline](match-data-pipeline.md) | [Tenancy and Technology](tenancy-and-technology.md)
