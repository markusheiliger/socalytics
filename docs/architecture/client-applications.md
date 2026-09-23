# 2. Client Applications

SocAlytics has two client applications with deliberately different scopes. The
clients follow the shared [API-first principle](terminology-and-principles.md#1-api-first).

## Source Ownership And Delivery

The future React and TypeScript sources for both client applications are owned
under `src/clients`. This shared source area supports a future `pnpm` workspace
without making runtime composition a source-ownership boundary. Only the
first-level source-area README exists today; there are no client projects,
packages, manifests, containers, or builds yet.

The Web UI release is coordinated with the platform API and will be built and
deployed as a separate OCI container from `src/clients`. Future Docker Compose
deployment and Aspire local composition may compose that container with the
platform API without moving Web UI source under `src/platform`. The Electron
Coach Client retains an independent build and release lifecycle.

Only environment-neutral generated API clients, validation, design assets and
tokens, selected MUI components, and agent-access transport or domain-neutral
logic may be shared. Browser authentication, Electron and Node dependencies,
preload, main-process and IPC code, offline storage, shell routing,
workflow-specific UI, and privileged integrations remain owned by the
application that uses them.

## Web UI

The React web UI supports connected platform and club workflows, including:

- club, season, and team management
- club-user and team-role management
- platform configuration and administration
- match creation, recording uploads, and upload finalization

The provisional implementation is an independent React and TypeScript SPA built
with Vite. React Router handles navigation, TanStack Query manages remote server
state, and MUI provides accessible controls under a SocAlytics theme. It uses
Kiota-generated clients behind application-owned adapters.

Local accounts are always available, and configured external OpenID Connect
providers appear as additional login options. Both paths establish the same
ASP.NET Core backend-for-frontend session. Authentication tokens remain on the
server; the browser receives a Secure, HttpOnly, SameSite cookie and uses CSRF
protection for state-changing requests.

## Coach Client

The coach client is planned as a cross-platform desktop application built with
Electron and React. It is scoped to the signed-in user and only exposes teams
and matches that user may access. It does not provide club administration or
match recording uploads. The
[client decision evidence](client-decision-evidence.md) records the review that
established its application and sharing boundaries.

The first delivery may require continuous API access. The architecture must
nevertheless preserve offline operation as the target state for unreliable
environments such as post-match team meetings.

Offline readiness requires:

- stable resource identifiers and versioned API contracts
- a local persistence boundary for authorized team, match, analytics, and media
  metadata
- incremental synchronization rather than full-state replacement
- an outbox for changes made while disconnected
- explicit conflict detection and resolution rules
- clear online, synchronizing, stale, and offline client states
- encrypted local data with sign-out, revocation, and retention controls

The server remains the system of record. The local store is a user-scoped
replica, and the coach client must not depend on direct database or object
storage access. Features introduced before offline synchronization is delivered
must use boundaries that can later support local reads and queued writes.

**Decision status: Accepted.** See the
[client decision evidence](client-decision-evidence.md).

### Connected Agent Conversations

The Web UI and Coach Client use the same API/BFF surface for connected agent
conversations. A valid direct Coach Agent or Specialist Agent submission
includes an idempotency key and receives `202 Accepted`, durable conversation
and invocation identifiers, a status location, and retry guidance. Both clients
use conditional REST polling for status, accepted advice, and lineage. Neither
client calls Hermes, publishes agent transport messages, or receives a separate
completion semantic.

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
decisions do not themselves scaffold a client or add a client-facing API.

### Delivery And Workspace

The accepted delivery and workspace design establishes:

- an online-only first Coach Client release using application-owned repository
  interfaces and explicit connectivity state
- Web UI release coordination with the platform API and separate
  containerization, while the Coach Client retains an independent release
  lifecycle and only selected environment-neutral packages are shared
- Windows 11 x64 as the initial Coach Client platform, with signed packaging,
  controlled updates, forward-fix by default, explicit uninstall/local-data
  behavior, and evidence required before adding another platform

### Offline Data And Synchronization

The accepted offline data and synchronization design establishes:

- encrypted SQLite owned by Electron main behind narrow typed IPC
- REST delta synchronization with opaque cursors, tombstones, idempotency, and
  bounded authorized snapshot recovery
- authorized metadata and analytics plus explicitly downloaded clips; complete
  recordings are excluded by default
- read-only first offline delivery; later queued writes require versions/ETags,
  scoped idempotency, safe domain merges, and explicit conflict resolution

### Disconnected Trust And Lifecycle

The accepted disconnected trust and lifecycle design establishes:

- short-lived signed device-bound offline entitlements; indefinite
  disconnected authority is prohibited
- an app-encrypted local store with a per-installation key protected by the OS
  credential store
- bounded per-class retention, inaccessible-first deletion, verifiable purge,
  narrow expiring holds, explicit removal, and minimized diagnostics

The evidence and approval record is maintained in
[Client Architecture Decision Evidence](client-decision-evidence.md).

---

Related architecture: [Index](README.md) | [Overview](overview.md) |
[Security and Data Governance](security-and-data-governance.md) |
[Platform Implementation Profile](platform-implementation.md) |
[Match Data Pipeline](match-data-pipeline.md) | [Tenancy and Technology](tenancy-and-technology.md)
