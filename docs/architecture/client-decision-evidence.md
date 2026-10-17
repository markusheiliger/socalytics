# Client Architecture Decision Evidence

This dossier records the evidence and approvals for the client architecture
summarized by [Client Applications](client-applications.md): one React Web UI,
delivered as a Progressive Web App (PWA) with an offline mode for coaches.
The evidence is architecture-only: it does not scaffold the client or add a
client-facing API.

The 2026-10-12 revision replaces the earlier plan for a separate Electron Coach
Client with the offline-capable PWA. No client code existed, so the decisions
are revised in place: D-01 and D-04 to D-06 keep their outcomes, and D-02,
D-03, D-07, D-08, and D-09 record the PWA outcomes with their evidence and
residual risks.

## Decision Owners And Approvers

| Role | Named assignee | Approval responsibility | Approval reference |
| --- | --- | --- | --- |
| Architecture Owner | Markus Heiliger | Architectural consistency, established invariants, and final gate release | `APR-ARCH-2026-09-21` |
| Product Owner | Markus Heiliger | First-release workflows, offline/media scope, and write behavior | `APR-PROD-2026-09-21` |
| Security and Data-Governance Owner | Markus Heiliger | Persistence, disconnected authorization, encryption, retention, deletion, privacy, and residual risk | `APR-SECGOV-2026-09-21` |
| Web Release Owner | Markus Heiliger | Supported browsers, installation, service-worker updates, recovery, and support | `APR-WEB-2026-10-12` |

One named assignee fills all four roles for these architecture decisions. The
review found no separation-of-duties requirement for approving a documentation
boundary that creates no runtime authority, credential, production release, or
data-processing mechanism. Later implementation and production-readiness
reviews may require independent reviewers.

No client implementation may treat these decisions as approved unless all four
approval references above have an explicit outcome in the final review record.

## Evidence Record Format

Each decision record contains the required alternatives, accountable approvals,
evaluation method, required evidence, observed result, material risks, migration
effects, and residual-risk disposition. Evidence identifiers remain stable so
architecture documents and future validation can refer to them.

## Decision Evidence Index

### D-01 First Release Scope

- **Alternatives:** online-only; read-only offline; offline reads and queued
  writes.
- **Owner approvals:** Architecture Owner and Product Owner.
- **Evaluation method:** representative coach journey review across connected,
  degraded, and disconnected conditions.
- **Required evidence:** workflow coverage, explicit deferred behavior, and a
  replaceable repository/connectivity boundary.
- **Result:** online-only is supported for the first release; connected API/BFF
  access remains required for reads, agent submission, and refresh.
- **Risks:** unreliable venues cannot use the first release while disconnected.
- **Migration effects:** application-owned repositories and explicit
  connectivity state must allow later local reads without rewriting screens.
- **Residual-risk disposition:** accepted for the first release by Product and
  Architecture review; offline mode remains a separately gated delivery.

### D-02 Client Application Shape

- **Alternatives:** a Web UI plus a separate Electron Coach Client with selected
  shared packages; one Web UI delivered as a PWA with an offline mode; two PWA
  builds from one codebase on separate origins.
- **Owner approvals:** Architecture Owner, Product Owner, Security and
  Data-Governance Owner, and Web Release Owner.
- **Evaluation method:** delivery-cost, workflow-duplication, release-coupling,
  and trust-boundary analysis without production client scaffolding.
- **Required evidence:** every workflow is built once, releases stay
  coordinated with the API, and the offline replica has a defined boundary on
  the shared origin.
- **Result:** one React Web UI on a single origin, delivered as a PWA, is
  supported. Coach workflows are part of the general Web UI; offline mode is a
  capability of it, not a separate module, build, or origin.
- **Risks:** administration screens share the origin with the offline replica,
  so a script compromise on the origin reaches both.
- **Migration effects:** the Web UI owns all client code; repositories and
  connectivity state stay application-owned; the origin is hardened before
  offline mode ships.
- **Residual-risk disposition:** accepted with the origin hardening in
  `EV-SPR-01` and the prototype gate before offline release.

### D-03 Local Persistence Technology

- **Alternatives:** an encrypted replica owned by a dedicated worker (for
  example SQLite compiled to WebAssembly on the Origin Private File System);
  IndexedDB accessed from the UI thread; application-managed encrypted records
  in browser storage.
- **Owner approvals:** Architecture Owner and Security and Data-Governance
  Owner.
- **Evaluation method:** isolation, threat, lifecycle, migration, eviction, and
  recovery comparison.
- **Required evidence:** no storage access from screens, encryption and key
  custody, schema migration, corruption and eviction recovery, and verifiable
  purge.
- **Result:** an encrypted replica owned by a dedicated worker behind
  application-owned repositories is supported.
- **Risks:** the browser may evict the replica, and a script compromise on the
  origin can message the worker while an offline session is unlocked.
- **Migration effects:** local schemas need versioned forward migrations,
  rebuild after eviction or corruption, and purge receipts.
- **Residual-risk disposition:** accepted subject to the prototype gate and
  implementation threat tests before offline release.

### D-04 Synchronization Contract

- **Alternatives:** full user-scoped snapshots; REST deltas; server-event
  replay.
- **Owner approvals:** Architecture Owner and Security and Data-Governance
  Owner.
- **Evaluation method:** compatibility simulation for duplication, reordering,
  interruption, cursor expiry, deletion, revocation, and client versions.
- **Required evidence:** bounded recovery with no silent loss, duplicate
  authority, or cross-team disclosure.
- **Result:** REST deltas with opaque server cursors, tombstones, idempotent
  commands, and bounded snapshot recovery is supported.
- **Risks:** expired cursors and old clients can increase recovery time.
- **Migration effects:** APIs require stable IDs, compatibility windows,
  cursor-version handling, and snapshot fallback.
- **Residual-risk disposition:** accepted with failure-closed authorization on
  every delta and recovery response.

### D-05 Offline Data And Media Scope

- **Alternatives:** metadata and analytics only; metadata/analytics plus
  explicit clips; complete recordings.
- **Owner approvals:** Architecture Owner, Product Owner, and Security and
  Data-Governance Owner.
- **Evaluation method:** workflow, quota, stale-state, authorization, deletion,
  retention, and purge review by local copy class.
- **Required evidence:** explicit acquisition, bounded storage, visible stale
  state, access-loss handling, deletion propagation, and purge verification.
- **Result:** authorized metadata and analytics plus explicitly downloaded
  clips is supported; complete recordings are excluded by default.
- **Risks:** clips can remain sensitive after device loss or delayed
  reconciliation.
- **Migration effects:** add quota/eviction, copy inventory, tombstones, and
  per-class retention before offline storage ships.
- **Residual-risk disposition:** accepted with bounded entitlement, encryption,
  and purge controls; complete-recording support requires a new decision.

### D-06 Offline Writes And Conflict Resolution

- **Alternatives:** read-only offline; server-wins queued writes; optimistic,
  domain-aware queued writes.
- **Owner approvals:** Architecture Owner and Product Owner.
- **Evaluation method:** conflict matrix covering versions/ETags, idempotency,
  safe merges, unsafe conflicts, and reconnect failures.
- **Required evidence:** no blind last-write-wins and no silent discard.
- **Result:** the first offline-capable release is read-only; any later writes
  must use optimistic concurrency, scoped idempotency, domain merge rules, and
  explicit user resolution.
- **Risks:** read-only mode limits field corrections while disconnected.
- **Migration effects:** reserve entity versions and command idempotency in
  contracts before queued writes are proposed.
- **Residual-risk disposition:** accepted; write support remains blocked until
  its own prototype and product acceptance.

### D-07 Disconnected Authentication And Authorization

- **Alternatives:** online reauthentication; bounded signed offline
  entitlement; indefinite cached authority.
- **Owner approvals:** Architecture Owner, Product Owner, and Security and
  Data-Governance Owner.
- **Evaluation method:** expiry, clock skew, tampering, device loss, revocation
  reconciliation, and key-rotation threat scenarios.
- **Required evidence:** bounded authority identifying user, teams, matches,
  roles, issue/expiry, device binding, and policy version.
- **Result:** a short-lived signed entitlement bound to a non-exportable
  WebCrypto key generated on the device is supported; the session cookie never
  unlocks offline data, and indefinite disconnected authority is rejected. The
  binding never uses a passkey, because synced passkeys exist on all of a
  user's devices.
- **Risks:** revocation cannot be observed until reconnect or local expiry; the
  device key lives in the browser profile rather than an OS credential store.
- **Migration effects:** entitlement signing keys, policy versions, clock
  tolerance, rollover, and reconnect reconciliation require contracts.
- **Residual-risk disposition:** accepted with short expiry, failure-closed
  verification, and no offline-session renewal.

### D-08 Local Data Protection And Retention

- **Alternatives:** browser and OS disk encryption only; a store key wrapped by
  a key derived through the WebAuthn PRF extension from a platform passkey; a
  store key wrapped by a non-exportable WebCrypto key in the browser profile;
  a user-managed application password.
- **Owner approvals:** Architecture Owner and Security and Data-Governance
  Owner.
- **Evaluation method:** key custody and lifecycle review covering sign-out,
  expiry, authorization loss, deletion, holds, removal, diagnostics, eviction,
  and device loss.
- **Required evidence:** per-class retention, inaccessible-first purge,
  crypto-erasure, minimized logs, and purge verification through browser APIs.
- **Result:** a store key wrapped by a PRF-derived key (one platform
  authenticator gesture per offline session) is preferred; where PRF is
  unavailable, a non-exportable WebCrypto key in the browser profile wraps it,
  or offline mode is not offered on that device.
- **Risks:** a compromised unlocked session can access locally authorized data;
  PRF availability depends on operating-system and browser versions.
- **Migration effects:** key rotation and recovery, policy-versioned retention,
  deletion tombstones, hold handling, and explicit cache removal are required.
- **Residual-risk disposition:** accepted with bounded offline entitlement,
  crypto-erasure, and the prototype gate before offline release.

### D-09 Platforms And Distribution

- **Alternatives:** Electron packages for Windows only, or for Windows, macOS,
  and Linux; an installable PWA with a supported-browser matrix.
- **Owner approvals:** Architecture Owner, Product Owner, and Web Release
  Owner.
- **Evaluation method:** coach-demand review plus install, update, recovery,
  local-data, and support obligations per platform.
- **Required evidence:** demand traceability and a supportable release and
  support path for every supported browser.
- **Result:** an installable PWA with a supported-browser matrix is supported.
  Offline mode starts with Chromium-based browsers on Windows; other browsers
  and operating systems follow once their storage, eviction, and PRF evidence
  exists. Connected use needs only a supported current browser.
- **Risks:** offline mode is unavailable on browsers outside the matrix.
- **Migration effects:** keep application code browser-neutral and require an
  evidence row before adding a browser to the offline matrix.
- **Residual-risk disposition:** accepted by Product and Web Release review
  with forward-fix through service-worker updates as the default recovery.

## Delivery And Workspace Evidence

### EV-D01 Coach Workflow Evaluation

| Workflow condition | Online-only | Read-only offline | Offline reads and writes |
| --- | --- | --- | --- |
| Connected pre-match review | Supported through authoritative API/BFF reads; current authorization is enforced | Supported, but local replication adds no first-release benefit while connected | Supported, but queueing and conflicts add avoidable first-release complexity |
| Degraded post-match meeting | Explicit reconnect/retry and stale-connectivity UI; unavailable requests fail visibly | Previously synchronized data remains useful, subject to entitlement and stale-state controls | Useful but requires the same read controls plus safe command queueing |
| Fully disconnected meeting | Deferred; no local data is presented as available | Supported after persistence, sync, entitlement, retention, and purge controls exist | Supported only after read controls plus conflict-safe write evidence exist |
| Agent submission and refresh | Connected-only, preserving current API/BFF authority | Connected-only | Connected-only unless a later accepted contract changes it |

**Supported candidate:** online-only for the first release.

**Deferred behavior:** offline reads, local media, disconnected sessions, and
queued writes are not first-release behavior. The UI must expose connectivity
and retry state rather than presenting stale or fabricated success.

**Migration consequences:** screens obtain data through application-owned
repositories; transport adapters remain replaceable; server contracts retain
stable IDs and versions; connectivity state is explicit; no component may
assume that a remote API call is the only possible future read source.

**Evidence result:** `EV-D01` supports D-01. The Product and Architecture
Owners accepted the usability limitation because it avoids introducing
unproven local authority while retaining a reversible path to offline reads.

### EV-D02 Application Shape Comparison

| Criterion | Web UI plus Electron Coach Client | One Web UI as a PWA with offline mode | Two PWA builds on separate origins |
| --- | --- | --- | --- |
| Workflow duplication | Coach workflows exist in both applications or in shared packages with strict boundaries | Every workflow is built once | Built once, but routing, authentication, and release are split |
| Release and installation | Independent signed installer, update channel, and support matrix per operating system | Released with the API; the service worker updates installed PWAs | Two coordinated web releases |
| Privilege isolation of the replica | Main process owns database and key behind typed IPC | A dedicated worker owns the replica on the shared origin | Separate origin isolates the replica from administration screens |
| Key custody | OS credential store | PRF-derived key, else a non-exportable WebCrypto key | Same as the single PWA |
| Offline-capable platforms | Windows first; each added OS needs packaging evidence | Supported-browser matrix; Chromium-based browsers on Windows first | Same as the single PWA |
| Result | Superseded | Supported | Rejected because the isolation gain does not justify a second build and origin |

The single PWA accepts that administration screens and the offline replica share
one origin. The origin hardening in `EV-SPR-01` limits that exposure; a script
compromise could already read every piece of data the user sees online.

**Evidence result:** `EV-D02` supports D-02.

### EV-D09 Platform Demand And Distribution Review

The 2026-09-21 owner review recorded one validated initial demand signal:
Windows 11 x64 for the project sponsor's current coach deployment environment.
An installable PWA serves that environment through Chromium-based browsers and
removes operating-system packaging from the release path; other platforms
depend on browser evidence instead of installers.

| Candidate set | Demand evidence | Release-operation burden | Result |
| --- | --- | --- | --- |
| Electron package for Windows 11 x64 | Confirmed current environment | Signed installer, update channel, uninstall, and support for one OS | Superseded |
| Electron packages for Windows, macOS, and Linux | Windows confirmed; others unconfirmed | Adds Apple signing and notarization, Linux package formats, and per-OS update and support | Superseded |
| Installable PWA; offline mode for Chromium-based browsers on Windows first | Confirmed environment covered | Service-worker updates with the API release; a supported-browser matrix | Supported |

#### Supported-Browser Evidence Contract

| Obligation | Accepted evidence before offline mode is enabled on a browser |
| --- | --- |
| Install and launch | The PWA installs from the browser and starts offline from the cached application shell |
| Persistent storage | The browser grants persistent storage to the installed PWA; eviction is detected and shown as missing or stale data |
| Key custody | PRF-derived key wrapping works with the platform authenticator, or the non-exportable WebCrypto fallback is accepted for that browser |
| Device-bound entitlement | A non-exportable WebCrypto key is generated, used for binding, and cannot be exported |
| Update | The service worker updates before synchronization when the API requires a newer client |
| Recovery | Forward-fix is the default; a corrupt or incompatible replica is rebuilt from the server |
| Removal | Explicit removal and sign-out destroy the key and delete IndexedDB, Origin Private File System, and Cache Storage copies, confirmed through browser APIs |
| Quota | Explicitly downloaded clips fit the granted quota, with eviction of clips before metadata |
| Support | Web Release Owner owns the supported-browser matrix, minimum versions, and end-of-support notice |

Adding a browser or operating system to the offline matrix requires an evidence
row covering these obligations and validated coach demand.

#### Implementation Evidence

The Web UI implements the installable PWA and service-worker updates as
development evidence, verified in Chromium through the `pwa-install`,
`pwa-update`, `pwa-version-skew`, and `pwa-offline` browser specs and unit and
Host tests. Installation on Edge and Chrome on Windows is a manual check, not a
production-readiness claim.

| Obligation | Status |
| --- | --- |
| Install and launch | Evidenced on Chromium: the manifest and icons are installable, and the shell starts offline from the cached release with the connection-required state and no data |
| Update | Evidenced on Chromium: an open Web UI offers a new release, activates it in every window, and enforces the platform's minimum version (`SocAlytics-Minimum-Web-Version`) before further requests |
| Persistent storage, key custody, device-bound entitlement, recovery, removal, quota | Open: offline mode with local data does not exist yet |

**Evidence result:** `EV-D09` supports D-09.

## Offline Data And Synchronization Evidence

### EV-D03 Local Persistence Comparison

| Criterion | Worker-owned encrypted replica | IndexedDB from the UI thread | Application-managed encrypted records |
| --- | --- | --- | --- |
| Isolation | Only the worker opens the store; screens use repositories backed by worker messages | Every screen and script on the origin reaches the store directly | Possible, but every query and index is custom code |
| Screen access | No storage handle, SQL, or key crosses the worker boundary | Direct browser API access | Must expose custom commands |
| Encryption and key custody | Store encrypted with a key wrapped by a PRF-derived or non-exportable WebCrypto key; the unwrapped key stays in the worker | Encryption must be added per record in the UI thread | Application designs record encryption, nonces, indexes, and atomic replacement |
| Schema migration | Transactional numbered migrations with recorded schema version | Version-change callbacks tied to the page lifecycle | Entire custom format and migration engine |
| Eviction and corruption recovery | Integrity check; rebuild from the server after eviction or corruption; never treat the replica as authority | Same rebuild, but partial upgrades are harder to detect | Custom journal, integrity, and repair behavior |
| Purge behavior | Destroy the key (crypto-erasure), then delete the store, temporary files, and cached media; confirm through browser APIs | Delete databases and verify object stores | Enumerate and delete every record and index |
| Result | Supported | Rejected | Rejected |

The worker's message contract is entity- and use-case-specific. It validates
the current user, team, and match scope, rejects arbitrary queries, returns
minimized typed results, and records no sensitive payload in diagnostics.

**Evidence result:** `EV-D03` supports D-03. The replica is a replaceable
local cache behind application repositories, not a source of authority.

### EV-D04 Synchronization Failure Simulation

| Scenario | Full snapshots | REST deltas with opaque cursor | Server-event replay |
| --- | --- | --- | --- |
| Duplicate response/delivery | Replace is naturally repeatable but expensive | Stable record IDs, versions, tombstones, and idempotent application make replay safe | Requires durable consumer offsets and idempotent projection |
| Reordered delivery | Snapshot order is irrelevant | Cursor pages are applied in server order; mismatched cursor/version is rejected | Requires event ordering/partition rules exposed to the client |
| Interrupted transfer | Restart full download | Resume from last committed opaque cursor; page commit is atomic | Resume from durable offset with compatibility guarantees |
| Expired progress position | Always fetch a new snapshot | Server returns explicit expiry; client performs bounded authorized snapshot recovery | Requires retained event history or snapshot bootstrap |
| Source deletion | Omission can be ambiguous without generation boundary | Tombstone removes every affected local class and advances cursor atomically | Deletion event requires retained history and projection semantics |
| Authorization revocation | New snapshot excludes data but must still purge old copies | Every request re-evaluates current user/team/match authorization and emits purge tombstones | Subscription and replay must both re-evaluate authority |
| Client/server version skew | Snapshot schema must be backward compatible | Cursor and payload carry contract version; unsupported clients fail visibly and recover only through a compatible snapshot | Event schemas and projection code require long compatibility windows |
| Cross-team disclosure defense | Server filters the complete snapshot | Server binds cursor to user, club stamp, authorized team/match scope, and policy version; scope changes invalidate or narrow it | Event subscription, history, and projection must all enforce the same scope |
| Result | Recovery fallback only | Supported | Rejected for client synchronization |

The supported delta contract uses opaque server-issued cursors, stable resource
identifiers, record versions, tombstones, page-level transactional apply, and
idempotent commands. A cursor conveys progress, not authority. The server
re-evaluates authorization on every request. Scope or policy changes can expire
the cursor and return a bounded user-scoped snapshot recovery instruction.

The local transaction commits the page and next cursor together. On failure it
commits neither, so retry cannot silently skip changes. Purge tombstones are
processed before the affected data becomes queryable after reconciliation.

**Evidence result:** `EV-D04` supports D-04 and demonstrates bounded recovery
without silent loss, duplicate authority, or cross-team disclosure.

### EV-D05 Offline Data And Media Scope

| Scope alternative | Acquisition | Quota and eviction | Stale presentation | Authorization loss/source deletion | Retention and verifiable purge | Result |
| --- | --- | --- | --- | --- | --- | --- |
| Metadata and analytics only | Automatic authorized synchronization | Per-user bounded records; oldest non-required analytics evicted first | Shows last-sync time and stale/offline state | Deny immediately after reconciliation; tombstones purge affected rows and indexes | Policy-versioned expiry; database/key/temp-copy verification | Viable but rejects the explicit-clip meeting workflow |
| Metadata/analytics plus explicit clips | Metadata sync plus a user-confirmed clip download with size and expiry shown | Separate encrypted media quota; LRU eviction excludes active download and honors holds | Clip and related metadata show last-sync, source status, entitlement expiry, and offline state | Reconciliation denies access first, then removes clip bytes, metadata, thumbnails, temp files, and indexes | Per-class expiry; copy inventory and purge receipt verify every local path | Supported |
| Complete recordings | Explicit selection is insufficient because multi-gigabyte acquisition changes the product and support model | Large quotas, long downloads, and eviction contention | Stale/full-recording status is hard to communicate safely | Revocation/deletion propagation has much larger exposure and delay | Deletion verification across partial files and derived media is costly | Rejected by default |

Permitted local copy classes are:

| Local class | Acquisition rule | Retention/purge rule |
| --- | --- | --- |
| Team, match, and media metadata | Current authorized delta synchronization | Purge on authorization loss, source deletion, policy expiry, sign-out removal policy, or explicit cache removal |
| Analytics and accepted result summaries | Current authorized delta synchronization | Same triggers; derived indexes are included |
| Explicitly downloaded clip bytes and thumbnails | User action while authorized, within entitlement and quota | Shorter media retention; purge bytes, thumbnails, manifests, temp parts, and lookup indexes |
| Complete source recordings | Not permitted | No local copy may be created |

Purge records contain only copy-class/resource identifiers, policy version,
trigger, timestamp, and outcome. They contain no media, analytics payload, key,
token, path that exposes a username, or durable access URL.

**Evidence result:** `EV-D05` supports D-05. Local availability never creates
authority; reconciliation removes visibility before asynchronous purge.

### EV-D06 Offline Write And Conflict Matrix

| Alternative | Versions/ETags | Scoped idempotency | Conflict detection | Safe merge rules | User resolution | Result |
| --- | --- | --- | --- | --- | --- | --- |
| Read-only offline | Stored versions support freshness and future migration; no offline mutation is accepted | Not needed for offline writes | Server changes appear during reconciliation | Not needed | Not needed | Supported for first offline-capable release |
| Server-wins queued writes | Can detect conflict but then discards local intent | Can prevent duplicate application | Conflict is observed | None | None; user work can disappear | Rejected |
| Optimistic/domain-aware queued writes | Required precondition on every command | Required per user, device, command kind, and target | Server compares authoritative version and authorization | Only explicitly approved commutative/domain merges | Required when no safe rule applies; both versions remain visible until resolved | Supported only for a later write-capable release |

Blind last-write-wins is rejected. A later queued-write contract must:

1. capture the base entity version or ETag with the local command;
2. assign a scoped idempotency key stable across retry;
3. re-evaluate user, team, match, role, and policy authorization on the server;
4. reject stale commands unless a named domain merge rule proves the fields
   independent and preserves invariants;
5. return authoritative and local intent for explicit user resolution when a
   safe merge is unavailable;
6. never silently discard either accepted server state or unresolved local
   intent; and
7. expire or reject commands whose target was deleted, revoked, or moved
   outside the user's authority.

**Evidence result:** `EV-D06` supports D-06. The accepted first offline release
is read-only; optimistic queued writes remain a future separately evidenced
capability.

## Disconnected Trust And Lifecycle Evidence

### EV-D07 Disconnected Authorization Threat Scenarios

| Scenario | Online reauthentication | Bounded signed offline entitlement | Indefinite cached authority |
| --- | --- | --- | --- |
| Access-token/authority expiry | Denies disconnected session immediately | Denies a new offline session after entitlement expiry; never self-extends | Continues authority without a bound |
| Clock skew | Server time controls access | Signed issue/expiry plus narrow configured skew; rollback beyond tolerance fails closed | Local clock manipulation can extend access indefinitely |
| Entitlement tampering | No offline artifact | Signature, audience, device-key binding, policy version, and claims validation fail closed | Cached flags/state are easier to alter and lack bounded issuer proof |
| Device loss | Server revocation prevents next online login | Exposure lasts no longer than entitlement and key protections (PRF requires the platform authenticator); reconnect reconciles revocation and purges | Lost device can retain continuing authority |
| Mid-outage revocation | Access ends at the next online check | Cannot be learned while offline; bounded expiry limits exposure and reconnect purges | Cannot be learned and has no expiry bound |
| Signing-key rotation | Not applicable | Entitlement carries key ID; client accepts only approved current/grace verification keys and policy versions | No reliable issuer/key lifecycle |
| Result | Secure but no disconnected use | Supported | Rejected |

The entitlement identifies issuer, audience, subject user, club stamp, permitted
teams and matches, roles, issue time, not-before time, expiry, the public key of
the non-exportable device key, policy version, and unique identifier. The
binding never uses a passkey: synced passkeys exist on all of a user's devices. It authorizes only reading data
already present and permitted by D-05/D-06; it cannot authorize administration,
recording upload, new agent submission, refresh, sync, or queued writes.

Verification is local and failure-closed. Expiry prevents creation of a new
offline session and does not silently delete data needed for an approved hold;
it makes ordinary content inaccessible and schedules policy-directed purge.
Reconnect obtains current server authorization before showing reconciled data.

**Evidence result:** `EV-D07` supports D-07 and explicitly rejects indefinite
disconnected authority.

### EV-D08 Local Protection And Lifecycle Review

| Alternative | Key custody and device loss | Sign-out/expiry/access loss/deletion | Holds and explicit removal | Diagnostics | Result |
| --- | --- | --- | --- | --- | --- |
| Browser and OS disk encryption only | Protects a powered-off disk but not data in an unlocked browser profile | No application key boundary for prompt inaccessibility or crypto-erasure | Deletion only; hold behavior is application-defined anyway | Does not prevent payload logging | Rejected as insufficient alone |
| Store key wrapped by a PRF-derived key | Unwrapping needs the platform authenticator (one gesture per offline session); a copied profile alone cannot open the store | Destroying the wrapped key makes content unreadable at once; storage deletion follows | Holds preserve only approved content while removing ordinary access; explicit removal covers store, media, caches, and key material | Structured allowlist excludes secrets and payloads; purge evidence is minimized | Supported (preferred) |
| Store key wrapped by a non-exportable WebCrypto key | The key cannot be exported, but anyone using the unlocked browser profile can use it | Same crypto-erasure and deletion | Same | Same | Supported where PRF is unavailable |
| User-managed application password | User supplies and recovers key material; weak, reused, or lost passwords add support and recovery risk | Can support encryption but complicates expiry, purge, and recovery | Explicit removal possible; holds still require policy machinery | Does not inherently prevent payload logging | Rejected |

#### Accepted Lifecycle By Data Class

| Local data class | Maximum policy shape | Inaccessibility and purge triggers | Hold behavior |
| --- | --- | --- | --- |
| Authorization/team/match metadata | Bounded by entitlement and configured metadata retention | sign-out policy, entitlement/policy expiry, authorization loss, source deletion, explicit removal | A hold never grants UI access; retain only approved evidence scope |
| Analytics/result summaries | No longer than related authorized match metadata | same triggers plus result supersession when policy requires | Same |
| Explicit clips/thumbnails | Shorter media-specific period and quota | same triggers plus quota or browser eviction and user clip removal | Same |
| Sync cursors/tombstones | Minimum period needed for safe reconciliation and purge verification | replace after recovery; retain deletion suppression as policy requires | Hold cannot reactivate deleted authority |
| Entitlements/keys | Entitlement expires cryptographically; old keys have a bounded rotation grace only for verification/decryption migration | expiry, sign-out removal policy, browser-data reset, revocation reconciliation, explicit removal, completed key rotation | Secret key material is not retained under content holds |
| Minimized purge/audit receipts | Separately bounded non-payload evidence period | policy expiry or approved evidence deletion | May be held when specifically authorized |

Source deletion and authorization loss remove visibility before asynchronous
copy purge. A hold records scope, reason, approver, start, expiry, and review;
it neither extends entitlement nor restores ordinary access. Logs, crash
reports, traces, metrics, and support bundles exclude tokens, keys, entitlement
payloads, database content, media bytes, and durable URLs.

**Evidence result:** `EV-D08` supports D-08. Exact durations remain
deployment-policy values, but indefinite retention is prohibited and every
class has an attributable bounded policy before storage is enabled.

### EV-SPR-01 Security And Privacy Review

Review scope: D-03 through D-08 against the trust and copy-lifecycle rules in
[Security and Data Governance](security-and-data-governance.md).

| Boundary or obligation | Selected control | Residual risk | Disposition |
| --- | --- | --- | --- |
| Human client to API/BFF and the offline replica (`BND-001`) | BFF/API remains authoritative; one hardened origin with a strict Content Security Policy, Trusted Types, Subresource Integrity, and no third-party scripts; only the worker opens the replica, and screens use typed repositories | A script or dependency compromise on the origin can invoke allowed operations and read the unlocked replica as the active user, as it can already read everything the user sees online | Accept for architecture; implementation requires worker-message authorization, dependency, and adversarial tests |
| Stamp and team/match isolation (`BND-005`, `CTL-001`, `CTL-007`) | Cursor, entitlement, snapshot, and delta are bound to one club stamp and current user/team/match scope | Server authorization defect could disclose data before local controls act | Accept only with server boundary tests; local state never broadens scope |
| Active store to cache/index/telemetry (`BND-008`, `CTL-010`) | Copy inventory includes the replica and its temporary files, IndexedDB, Origin Private File System and Cache Storage entries, media, thumbnails, diagnostics, and purge receipts | Crash/interruption can delay physical removal; physical removal is confirmed only through browser APIs | Accept with crypto-erasure first, inaccessible-first behavior, restartable purge, retries, and verification |
| Encryption and secrets (`CTL-008`) | Encrypted store with a key wrapped by a PRF-derived or non-exportable WebCrypto key, device-bound signed entitlement, no secret/payload logging | A compromised unlocked session can access active authorized content; without PRF the wrapping key lives in the browser profile | Accept with OS account protection, bounded entitlement, rapid expiry, explicit removal, and device-loss response |
| Authorization loss and source deletion | Reconcile current authority, hide first, persist tombstone, purge all local classes, retain minimized outcome evidence | Offline device cannot learn revocation until expiry/reconnect | Accept because authority is time-bounded; indefinite authority is prohibited |
| Retention and holds | Versioned per-class bounded policy; narrow expiring holds do not restore access; secrets excluded from holds | Incorrect policy configuration can over-retain content | Accept only with readiness validation that rejects absent/unbounded values |
| Diagnostics and support | Allowlisted operational fields; no tokens, keys, entitlement payloads, database/media payloads, or durable URLs | Free-form support capture could bypass normal schemas | Accept only with support-bundle review/redaction tests and explicit exceptional capture controls |

Privacy review confirms data minimization: the client stores only current
authorized metadata/analytics and user-selected clips; complete recordings are
excluded; receipts contain no payload; connected-only operations do not create
offline conversation or agent authority.

**Security and Data-Governance Owner outcome:** `APR-SECGOV-2026-09-21`
approves the selected architecture and the residual-risk dispositions above.
This approval does not approve production deployment or implementation
evidence; those remain later gates.

**Architecture Owner outcome:** `APR-ARCH-2026-09-21` confirms the controls
preserve API authority, team/match authorization, and one-club-per-stamp
isolation.

## Atomic Promotion Review

### REV-CLIENT-2026-09-21

The Architecture, Product, Security/Data-Governance, and Web Release roles
reviewed the client choices together against the architecture decision
scenarios. The 2026-10-12 revision repeated the review for D-02, D-03, D-07,
D-08, and D-09.

| Decision | Realistic alternatives | Supporting evidence | Consequences and migration understood | Accountable owner/required approval | Review result |
| --- | --- | --- | --- | --- | --- |
| D-01 First release scope | Yes | `EV-D01` | Yes | Architecture + Product | Pass |
| D-02 Application shape | Yes | `EV-D02` | Yes | Architecture + Product + Security/Data-Governance + Web Release | Pass |
| D-03 Local persistence | Yes | `EV-D03`, `EV-SPR-01` | Yes | Architecture + Security/Data-Governance | Pass |
| D-04 Synchronization | Yes | `EV-D04`, `EV-SPR-01` | Yes | Architecture + Security/Data-Governance | Pass |
| D-05 Offline data/media | Yes | `EV-D05`, `EV-SPR-01` | Yes | Architecture + Product + Security/Data-Governance | Pass |
| D-06 Offline writes | Yes | `EV-D06` | Yes | Architecture + Product | Pass |
| D-07 Disconnected authorization | Yes | `EV-D07`, `EV-SPR-01` | Yes | Architecture + Product + Security/Data-Governance | Pass |
| D-08 Protection/retention | Yes | `EV-D08`, `EV-SPR-01` | Yes | Architecture + Security/Data-Governance | Pass |
| D-09 Platforms/distribution | Yes | `EV-D09` | Yes | Architecture + Product + Web Release | Pass |

Promotion-policy review confirms serious alternatives, explicit tradeoffs,
architecture-level evidence, reversible boundaries, and maturity sufficient to
govern later implementation proposals. It does not claim implementation,
browser, security-control, or operational exercise evidence already exists.

#### Final Approval Outcomes

| Approval reference | Assignee/role | Outcome |
| --- | --- | --- |
| `APR-ARCH-2026-09-21` | Markus Heiliger, Architecture Owner | Approved all nine outcomes and atomic gate release; confirmed the 2026-10-12 PWA revision |
| `APR-PROD-2026-09-21` | Markus Heiliger, Product Owner | Approved first-release, offline/media, write, entitlement-usability, and platform outcomes; confirmed the single PWA |
| `APR-SECGOV-2026-09-21` | Markus Heiliger, Security and Data-Governance Owner | Approved persistence, synchronization, data scope, entitlement, lifecycle, privacy, and residual-risk outcomes; accepted the PWA residual risks in `EV-SPR-01` |
| `APR-WEB-2026-10-12` | Markus Heiliger, Web Release Owner | Approved the supported-browser matrix, service-worker update, and offline evidence obligations |

Every required approval is Approved. Architecture synchronization must still
occur atomically; this review alone does not release the gate.

### Unsupported Candidate Disposition

| Candidate | Disposition and reason |
| --- | --- |
| Read-only or read/write offline in the first release | Deferred because persistence, synchronization, entitlement, and lifecycle implementation evidence does not exist |
| Separate Electron Coach Client next to the Web UI | Superseded because every coach workflow would be built and maintained twice and a cross-platform installer adds release cost |
| Two PWA builds on separate origins | Rejected because the isolation gain does not justify a second build, release, and origin |
| IndexedDB accessed from the UI thread | Rejected because every script on the origin reaches the store directly |
| Application-managed encrypted records | Rejected because it creates a custom database, indexing, journaling, migration, and recovery burden |
| Full-snapshot-only synchronization | Rejected as the ordinary contract; retained only as bounded cursor-expiry recovery |
| Server-event replay to the client | Rejected because it exposes retention, ordering, projection, and compatibility complexity to the client |
| Complete recording replication | Rejected by default because exposure, storage, deletion, and support cost exceed the validated workflow |
| Server-wins or blind last-write-wins | Rejected because user intent can be silently discarded |
| Indefinite cached authority | Rejected because revocation and expiry would be unbounded |
| Browser and OS disk encryption alone | Rejected because an unlocked profile lacks an application-scoped protection and purge boundary |
| User-managed encryption passwords | Rejected because weak/lost/reused credentials add risk and lifecycle complexity |
| Passkey-bound entitlement | Rejected because synced passkeys exist on all of a user's devices and cannot bind to one |
| Offline mode on browsers outside the matrix | Deferred until storage, eviction, and PRF evidence and validated coach demand exist |

The selected candidates require no revision after evidence review. The current
decisions are recorded directly in the owning architecture documents; later
implementation evidence must validate them.

## Final Semantic Review

### REV-CLIENT-FINAL-2026-09-21

This review records architecture acceptance and synchronization. Automated
implementation validation remains future work.

| Review lens | Reviewer role/assignee | Confirmation |
| --- | --- | --- |
| Architecture | Architecture Owner, Markus Heiliger | All nine decisions are explicit and mutually coherent; architecture sources state the accepted choices without relying on separate decision records |
| Product | Product Owner, Markus Heiliger | Online-only first release, explicit-clip scope, read-only first offline delivery, bounded-outage behavior, one Web UI for every workflow, and the supported-browser matrix match the validated workflows and deferrals |
| Security and privacy | Security and Data-Governance Owner, Markus Heiliger | Worker isolation, origin hardening, entitlement, encryption/key custody, retention, deletion, holds, diagnostics, copy inventory, and residual risks satisfy the architecture trust/copy obligations |
| Web release | Web Release Owner, Markus Heiliger | Supported browsers, installation, service-worker update and recovery, local-data removal, support ownership, and evidence required for added browsers are explicit |

#### Completeness Confirmation

- D-01 through D-09 have alternatives, evaluation methods, required evidence,
  results, risks, migration effects, residual-risk dispositions, and passing
  combined-review rows.
- `APR-ARCH-2026-09-21`, `APR-PROD-2026-09-21`,
  `APR-SECGOV-2026-09-21`, and `APR-WEB-2026-10-12` are Approved.
- The owning architecture documents link this evidence and state the accepted decisions.
- The four affected architecture sources state the same accepted outcomes.
- Later implementation must still provide local schema/recovery tests, sync
  simulations, worker-message and entitlement threat tests, key custody and
  purge evidence, and the supported-browser evidence of `EV-D09`.

#### Non-Goals Confirmed

- No client, service worker, local database, sync engine, entitlement issuer, or
  client-facing API was scaffolded or implemented.
- No server retention duration, residency, production topology, backup value,
  or unrelated production-governance gap was resolved.
- Offline mode does not offer club administration, recording upload, or
  agent submission/refresh.

#### Preserved Invariants Confirmed

- One Web UI serves every workflow; roles and team authorization decide what
  each user sees, online and offline.
- Server APIs remain authoritative; local state is a user-scoped replica.
- Current user, team, and match authorization remains mandatory.
- Every cursor, entitlement, and local copy is scoped to one club deployment
  stamp and grants no cross-club authority.
- Connected Web UI administration/upload and connected agent submission/refresh
  remain unchanged.

**Final disposition:** all nine client architecture decisions are accepted as
current architecture, have the required approvals, and are synchronized across
the owning documents. Automated and implementation evidence remain required by
later scoped implementation proposals.
