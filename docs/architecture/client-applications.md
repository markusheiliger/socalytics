# 2. Client Applications

SocAlytics has one client application: the React Web UI, delivered as a
Progressive Web App (PWA). Coaches use the same Web UI as every other user; an
offline mode lets them keep using previously synchronized team and match data
where connectivity is unreliable, such as post-match team meetings. The client
follows the shared [API-first principle](terminology-and-principles.md#1-api-first).

## Source Ownership And Delivery

The React and TypeScript sources of the Web UI are owned under `src/clients`.
The online-only Web UI foundation is implemented as development evidence in the
npm project `src/clients/web`: a Vite build, a Kiota client generated from the
committed OpenAPI snapshot, and Vitest and Playwright tests. The AppHost
composes it with the platform API on one origin as a Vite resource, without
moving Web UI source under `src/platform`.

The Web UI release is coordinated with the platform API and is
deployed as a separate OCI container whose image is built from `src/clients`.
The service worker and installable PWA are implemented as development
evidence; offline mode is not implemented. The self-hosted Docker Compose
evaluation composition serves the Web UI image next to the platform API as
development evidence; production deployment stays unresolved.

The Web UI image is implemented (see
[Platform Implementation](platform-implementation.md#source-and-runtime-baseline)).
It serves the built shell byte for byte, so every shell file keeps passing its
integrity check, with the production security headers from the same policy
module as preview mode; the object storage origin allowed in `connect-src` is
its only runtime-configurable part. `index.html`, `sw.js`,
`shell-manifest.json`, and the web app manifest are revalidated on every
request, and only fingerprinted assets may be cached long-term. Client routes
return the HTML shell, and missing files return not found. The image forwards
`/api` and `/alive` to the API container named by its configuration, streams
requests and responses without a smaller size or time limit than the API's,
and answers those paths with a gateway error while the API is unreachable, so
the connection-required state appears.

The PWA is served from a single origin. Its service worker caches the
application shell and is versioned with each release, so an installed PWA
always runs the current release after it reconnects. There is no separate
client build, release lifecycle, installer, or origin for coaches.

The service worker is written in the Web UI
project without a PWA framework. It precaches exactly the release's shell
files, each verified against its SHA-384 integrity, keeps one cache per
release, and passes every other request, including all API, session, upload,
and media traffic, to the network, so it holds no user data. Releases carry a
sortable build version; an open Web UI checks for a new release at start, on
focus, and periodically, shows a non-blocking update notice, and reloads every
window when the user updates. For version skew, the platform sends the minimum
Web UI version it requires, from deployment configuration, with its API
responses; an older Web UI stops sending requests, updates through its worker,
and reloads. The platform never refuses requests by client version. Without a
network, the installed PWA opens the shell with the connection-required state
and no data. Offline mode with local data does not exist yet; the offline shell
shows the connection-required state only, and nothing is queued or stored.

## Web UI

The React Web UI supports connected platform and club workflows, including:

- club, season, and team management
- club-user and team-role management, including invitations by email that
  Club Admins create, resend, and revoke in the members area and that invitees
  accept on a public invitation page (decided, not yet implemented; see
  [Email Notifications](email-notifications.md#member-invitations))
- platform configuration and administration (the Analyst Registry
  administration, the workflow operations area, and the agent catalog
  administration for Club Admins are decided, not yet implemented; see below)
- Analyst Manager pairing for Registrars and the fleet view for Club Admins
  (implemented): registration approval, rejection, and revocation, and each
  Manager's connectivity, operating state, and activity (see
  [Analyst Manager](analyst-manager.md#fleet-visibility-in-the-web-ui))
- match creation, recording uploads, and upload finalization (implemented as
  connected workflows); the match page's analysis run list will offer Club
  Admins and Coaches of the owning team a confirmed cancel action with an
  optional reason for requested and running runs, and shows cancelled runs
  with who cancelled them, when, and why (decided, not yet implemented); the
  run list will also mark the match's current run and each superseded run
  with the run that superseded it, and shows a run cancelled because it was
  superseded without a member (decided, not yet implemented)
- team and match review, analytics, and agent conversations for coaches
  (the match triage conversations with Specialist Agents are implemented as
  connected workflows, see below; the analysis review is decided and not yet
  implemented, and so are the Coach Agent conversations and consultations)

What a user sees is decided by their roles and team authorization, online and
offline alike.

The connected analysis review is decided as follows and not yet implemented.
From a match, members who may read it (Club Admins, and Coaches and Viewers of
the owning team) open the review of one analysis run: its state, timestamps,
failure reason, workflow, and each capability node's tier, scope, state,
blocking node, and job counts, refreshed while the run is in progress. For
each capability node, members browse the run's indexed facts in match-time
order through the member fact reads of
[Capability-Scoped Result Access](analysts-models-and-hardware.md#capability-scoped-result-access),
filter them by a match-time range, page forward and back, and see coverage
windows and run-pinned provenance, with explicit messages for missing,
incomplete, empty, and unavailable analysis instead of empty lists. The review
is online only and keeps analysis data in memory only; it offers no run
controls, visualizations, cross-run comparison, or export, and plays no video
other than the evidence playback below.

Evidence playback in the analysis review is decided as follows and not yet
implemented (see
[Evidence Clip Access](../../specs/20261017-224326-evidence-clip-access/spec.md)).
Each fact offers "Play evidence": the Web UI resolves the fact with five
seconds of context on each side through the member evidence-media operation of
[Capability-Scoped Result Access](analysts-models-and-hardware.md#capability-scoped-result-access)
and plays the returned short-lived links directly from object storage, in
match-time order and across segment boundaries, marking the fact's own window.
It shows explicit resolving, preparing (with automatic, bounded re-resolution),
unavailable or not-covered, shortened, link-renewal, not-found, offline, and
error states, and resolves again when a link expires. Links and media stay in
memory only, the service worker never handles them, and playback is online
only; downloads, sharing, and whole-recording playback are not offered.

The connected match triage conversations are implemented as development
evidence as follows. From a match with a `Completed` or `PartiallyCompleted`
analysis run, Club Admins and Coaches of the owning team start a triage conversation
for a run they choose, add Specialist Agents the stamp enables, and address a
question to one of them. Members list and reopen their own conversations from
the match view, see the bound run with a link to its review and whether it is
still the match's current run, and read the history, each advice with its
agent attribution and AI disclosure, and each question's lineage. Viewers read
their own conversations without start, participant, or question controls. The
views are online only and keep conversation content, drafts, and idempotency
keys in memory only; Coach Agents, consultations, streaming, editing,
deletion, sharing, and evidence clip playback are not part of them (see
[Connected Agent Conversations](#connected-agent-conversations)). Direct Coach
Agent conversations in these views are decided and not yet implemented: Coach
Agents are offered as a separate participant group with their simulation
disclosure, every Coach Agent advice is shown with that disclosure, and on
request the advice's applied philosophy emphasis (base, modifier, and applied
weight per dimension, modifier reasons, and evidence references) is shown with
the statement that weights change priority, not facts; the Agent Catalog area
enables Coach Agent versions like Specialist Agent versions. Delegated
consultations in these views are decided and not yet implemented: a Coach
Agent turn shows while it consults which Specialists it asked and when it
combines their answers, and below its advice each consultation is attributed
to its Specialist with the question, the response and its evidence references
or the reason it was unavailable, and whether the Coach Agent adopted or
disagreed with it, a conflict marked as a disagreement; the Agent Catalog area
shows and registers consultation policies and lets Coach Agent versions
reference one.

The Analyst Registry administration is decided as follows and not yet
implemented. Club Admins, the only role that operates the Registry, see the
stamp's default workflow and browse the registered Analyst images with their
imported declarations, the Analyst profile versions with their states, and the
workflow versions with their nodes and dependency edges as readable lists.
They register images by digest (see
[Image Metadata And Packaging](analysts-models-and-hardware.md#image-metadata-and-packaging)),
create profile versions from a registered declaration, approve and retire
profile versions, define workflow versions as structured node and edge lists,
and change the default workflow. Approvals, retirements, and default changes
are confirmed and conditional on the version last seen; a default change
applies only to analysis runs created afterwards. The platform decides every
rule and the Web UI explains its refusals; the area is online only and offers
no deletion, image building, signing, or graphical workflow editing.

The workflow operations area is decided as follows and not yet implemented.
Club Admins, who use the operational workflow operations until a stamp-operator
role exists (see
[API And Identity](platform-implementation.md#api-and-identity)), open an
Operations area with the analysis operational summary as the platform reports
it, with its observation time and a manual refresh; the outgoing messages of
the transactional outbox filtered by state and message type, paged, with a
detail view of everything the platform returns about a message but never its
payload; and the finalized-recordings notification receipts filtered by
outcome, paged. From the summary they start an analysis reconciliation after a
confirmation that states that it only queues eligible work and unpublished
notifications again from durable state and never authorizes work or overrides
accepted state (see
[Production Queue Recovery and Backpressure](job-processing.md#production-queue-recovery-and-backpressure)),
and they see its outcome. The area offers no per-message retry, edit, or
deletion, no automatic refresh, charts, or alerts, and is online only.

The agent catalog administration is decided as follows and not yet
implemented. Club Admins, who operate the agent catalog until a stamp-operator
role exists (see
[Intelligence Agent Catalog → Prompt And Advice Governance](intelligence-agent-catalog.md#prompt-and-advice-governance)),
open an Agent Catalog area that lists the catalog's agents with their kind,
enabled version, version states, and what triage conversations offer as the
platform's available-agents read reports it. They read agent versions with
every artifact reference and digest and each artifact read-only: prompt text
exactly as stored, model selections without secrets, tool and safety
policies, and for Coach Agents the disclosure, dimension set, base philosophy
profile as a table of dimensions and integer weights totalling 100 with its
distances, and the modifier policy. They register artifacts and agent versions
from structured forms whose hints never replace the platform's validation,
which the forms show next to each field; approve and retire versions; and
enable, change, or disable an agent's version. Approvals, retirements, and
enablement changes are confirmed and conditional on the state last seen; an
enablement change applies only to questions submitted afterwards, while
existing invocations keep their pinned versions. The area is online only and
offers no deletion, editing of registered records, model-rendered previews, or
evaluation results.

Recording uploads run in a dedicated upload worker that hashes the parts
and transfers them directly to object storage with the platform's per-part
grants; screens reach it only through an application-owned upload coordinator
and repository. The worker keeps a resume journal in worker-owned
IndexedDB so an interrupted upload can continue after a reload. The journal is
member-scoped, holds identifiers and progress only, never grants or file
contents, and is purged on explicit sign-out. It is not the offline replica:
uploads stay connected-only.

The provisional implementation is an independent React and TypeScript SPA built
with Vite. React Router handles navigation, TanStack Query manages remote server
state, and MUI provides accessible controls under a SocAlytics theme. It uses
Kiota-generated clients behind application-owned adapters and repositories.

Local accounts are always available, and configured external OpenID Connect
providers appear as additional login options. Both paths establish the same
ASP.NET Core backend-for-frontend session. A member's provider identity is
linked when the member redeems a Club Admin's link credential at the provider
on the link-account page, which signs the member in at the provider and
links that identity; unlinking is a Club Admin action on the member screen.
SocAlytics offers no MFA for local accounts, so
MFA comes from the external provider. The sign-in page offers members with
a local password a "Forgot your password?" entry that leads to a public
recovery request page and, from the emailed link, to a public page for setting
a new password (implemented as development evidence; see
[Email Notifications](email-notifications.md#password-recovery)). Authentication tokens remain on the
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
transport messages, or receive a separate completion semantic. The Web UI's
retry and polling behavior is implemented for Specialist Agent submissions:
when the answer to a submission is lost, sending the unchanged question to the same
agent again reuses its idempotency key, and the Web UI checks status
conditionally, starting at the platform's retry guidance and slowing down to
a cap while nothing changes, pausing while the view is hidden or offline.

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
