# Client Architecture Decision Evidence

This dossier records the evidence and approvals for the client architecture
summarized by [Client Applications](client-applications.md).
The evidence is architecture-only: it does not scaffold either client or add a
client-facing API.

## Decision Owners And Approvers

| Role | Named assignee | Approval responsibility | Approval reference |
| --- | --- | --- | --- |
| Architecture Owner | Markus Heiliger | Architectural consistency, established invariants, and final gate release | `APR-ARCH-2026-09-21` |
| Product Owner | Markus Heiliger | First-release workflows, offline/media scope, and write behavior | `APR-PROD-2026-09-21` |
| Security and Data-Governance Owner | Markus Heiliger | Persistence, disconnected authorization, encryption, retention, deletion, privacy, and residual risk | `APR-SECGOV-2026-09-21` |
| Desktop Release Owner | Markus Heiliger | Initial operating systems, signing, packaging, updates, recovery, and support | `APR-DESKTOP-2026-09-21` |

One named assignee fills all four roles for this architecture decision. The
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

### D-01 First Coach Client Release Scope

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
  Architecture review; offline capability remains a separately gated delivery.

### D-02 Client Code Sharing

- **Alternatives:** independent applications; separate shells with selected
  shared packages; one React application hosted in web and Electron.
- **Owner approvals:** Architecture Owner, Product Owner, Security and
  Data-Governance Owner, and Desktop Release Owner.
- **Evaluation method:** disposable workspace/build and dependency-boundary
  analysis without production client scaffolding.
- **Required evidence:** separate builds and releases, generated-client reuse,
  environment-neutral package boundaries, and Electron privilege isolation.
- **Result:** separate application shells with selected shared packages is
  supported.
- **Risks:** shared UI or utilities can accidentally import Electron or
  workflow-specific dependencies.
- **Migration effects:** version shared packages independently enough to
  preserve shell release autonomy and generated-client compatibility.
- **Residual-risk disposition:** accepted with automated dependency-boundary
  tests required before shared production packages are introduced.

### D-03 Local Persistence Technology

- **Alternatives:** SQLite in Electron main; renderer IndexedDB;
  application-managed encrypted files.
- **Owner approvals:** Architecture Owner and Security and Data-Governance
  Owner.
- **Evaluation method:** process-boundary, threat, lifecycle, migration, and
  recovery comparison.
- **Required evidence:** renderer isolation, encryption/key custody, schema
  migration, corruption recovery, and verifiable purge.
- **Result:** SQLite owned by Electron main behind narrow IPC and repository
  interfaces is supported.
- **Risks:** IPC overexposure or key mishandling could reveal the user-scoped
  replica.
- **Migration effects:** local schemas need versioned forward migrations,
  recovery or rebuild, and purge receipts.
- **Residual-risk disposition:** accepted subject to implementation threat
  tests and OS-keystore evidence before offline release.

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
- **Result:** a short-lived signed, device-bound offline entitlement is
  supported; indefinite disconnected authority is rejected.
- **Risks:** revocation cannot be observed until reconnect or local expiry.
- **Migration effects:** entitlement signing keys, policy versions, clock
  tolerance, rollover, and reconnect reconciliation require contracts.
- **Residual-risk disposition:** accepted with short expiry, failure-closed
  verification, and no offline-session renewal.

### D-08 Local Data Protection And Retention

- **Alternatives:** OS full-disk encryption only; app-encrypted store with
  OS-protected per-installation key; user-managed application password.
- **Owner approvals:** Architecture Owner and Security and Data-Governance
  Owner.
- **Evaluation method:** key custody and lifecycle review covering sign-out,
  expiry, authorization loss, deletion, holds, removal, diagnostics, and device
  loss.
- **Required evidence:** per-class retention, inaccessible-first purge,
  minimized logs, key destruction, and purge verification.
- **Result:** an app-encrypted store with a per-installation key protected by
  the OS credential store is supported.
- **Risks:** a compromised unlocked device can access locally authorized data.
- **Migration effects:** key rotation/recovery, policy-versioned retention,
  deletion tombstones, hold handling, and explicit cache removal are required.
- **Residual-risk disposition:** accepted with bounded offline entitlement,
  OS-keystore protection, and implementation evidence before offline release.

### D-09 Desktop Platforms And Distribution

- **Alternatives:** Windows only; Windows and macOS; Windows, macOS, and Linux.
- **Owner approvals:** Architecture Owner, Product Owner, and Desktop Release
  Owner.
- **Evaluation method:** validated initial coach-demand review plus per-platform
  install, signature, update, recovery, uninstall, local-data, and support
  obligations.
- **Required evidence:** demand traceability and a supportable signed release
  lifecycle for every selected OS.
- **Result:** Windows 11 on x64 is the initial supported platform; macOS and
  Linux are deferred pending validated demand and release-operation evidence.
- **Risks:** coaches using deferred platforms cannot run the packaged client.
- **Migration effects:** keep shared/application code platform-neutral and
  require a packaging evidence matrix before adding an OS.
- **Residual-risk disposition:** accepted by Product and Desktop Release review
  with forward-fix as the default recovery policy and controlled downgrade only
  for explicitly compatible data schemas.

## Delivery And Workspace Evidence

### EV-D01 Coach Workflow Evaluation

| Workflow condition | Online-only | Read-only offline | Offline reads and writes |
| --- | --- | --- | --- |
| Connected pre-match review | Supported through authoritative API/BFF reads; current authorization is enforced | Supported, but local replication adds no first-release benefit while connected | Supported, but queueing and conflicts add avoidable first-release complexity |
| Degraded post-match meeting | Explicit reconnect/retry and stale-connectivity UI; unavailable requests fail visibly | Previously synchronized data remains useful, subject to entitlement and stale-state controls | Useful but requires the same read controls plus safe command queueing |
| Fully disconnected meeting | Deferred; no local data is presented as available | Supported after persistence, sync, entitlement, retention, and purge controls exist | Supported only after read controls plus conflict-safe write evidence exist |
| Agent submission and refresh | Connected-only, preserving current API/BFF authority | Connected-only | Connected-only unless a later accepted contract changes it |

**Supported candidate:** online-only for the first Coach Client release.

**Deferred behavior:** offline reads, local media, disconnected sessions, and
queued writes are not first-release behavior. The UI must expose connectivity
and retry state rather than presenting stale or fabricated success.

**Migration consequences:** coach screens obtain data through
application-owned repositories; transport adapters remain replaceable; server
contracts retain stable IDs and versions; connectivity state is explicit; no
component may assume that a remote API call is the only possible future read
source.

**Evidence result:** `EV-D01` supports D-01. The Product and Architecture
Owners accepted the usability limitation because it avoids introducing
unproven local authority while retaining a reversible path to offline reads.

### EV-D02 Disposable Workspace And Dependency Proof

The proof is an architecture-level workspace graph, not a production scaffold.
It models two independently buildable shell roots and environment-neutral
package edges:

```text
apps/web -----------------> packages/api-client
    |---------------------> packages/validation
    |---------------------> packages/design-tokens
    `---------------------> packages/ui-components

apps/coach-renderer ------> packages/api-client
    |---------------------> packages/validation
    |---------------------> packages/design-tokens
    `---------------------> packages/ui-components

apps/coach-main ----------> coach-owned privileged adapters
apps/coach-preload -------> narrow typed IPC contract
```

| Alternative | Separate builds and releases | Generated-client reuse | Environment-neutral package boundary | Electron privilege isolation | Result |
| --- | --- | --- | --- | --- | --- |
| Independent applications | Strong | Duplicated or externally published | Strong through duplication | Strong | Rejected because stable contract, validation, and design logic would drift |
| Separate shells with selected shared packages | Strong; each shell owns entry points, packaging, version, and release | Direct workspace consumption behind application adapters | Shared packages may depend on browser-neutral TypeScript/React only and must not import Electron, Node, shell routes, storage, IPC, or workflow modules | Main/preload remain Coach-owned; renderer receives only a typed allowlisted IPC facade | Supported |
| One React application in both environments | Coupled build and release pressure | Direct | Weak because environment branches enter shared workflows | Weak; privileged concerns are more likely to leak into common code | Rejected |

**Dependency assertions required before production packages exist:**

- `packages/*` cannot depend on either `apps/*`.
- shared packages cannot import `electron`, Node built-ins, preload/main modules,
  local-store adapters, shell routing, or privileged IPC implementations.
- the Web UI and Coach Client compile, test, package, and release independently.
- generated API clients remain behind shell-owned adapters so generator changes
  do not define domain or presentation models.
- selected UI sharing is optional; a component stays shell-owned when its
  workflow, authorization context, or platform behavior differs.

**Evidence result:** `EV-D02` supports D-02 without creating directories,
manifests, or source files for either production client.

### EV-D09 Platform Demand And Distribution Review

The 2026-09-21 owner review recorded one validated initial demand signal:
Windows 11 x64 for the project sponsor's current coach deployment environment.
No named first-release coach or support owner confirmed macOS or Linux demand.
Absence of evidence is not evidence against those platforms; it means they
remain deferred rather than silently accepted.

| Candidate set | Demand evidence | Release-operation burden | Result |
| --- | --- | --- | --- |
| Windows 11 x64 | Confirmed current environment | One signed packaging/update path | Supported initial set |
| Windows 11 x64 and current macOS | Windows confirmed; macOS unconfirmed | Adds Apple signing, notarization, hardware, update, and support obligations | Deferred |
| Windows 11 x64, current macOS, and supported Linux distributions | Windows confirmed; macOS/Linux unconfirmed | Adds divergent package formats, signatures, desktop integration, update, and support matrices | Deferred |

#### Windows 11 x64 Release Evidence Contract

| Obligation | Accepted evidence before release |
| --- | --- |
| Install and launch | Clean-user and upgrade installation launches the signed application without administrator rights unless a documented packaging constraint requires elevation |
| Signature | Installer and application artifacts validate against the approved publisher identity; invalid or revoked signatures fail closed |
| Update | Controlled channel verifies signed update metadata and payloads before applying an update |
| Recovery | Forward-fix is the default; controlled downgrade is allowed only when the installed local schema and entitlement policy remain compatible |
| Uninstall | Removes application binaries and offers an explicit, separately confirmed removal of user-scoped local data |
| Local data | Upgrade preserves compatible encrypted data; uninstall, sign-out, expiry, revocation, and explicit removal follow the accepted lifecycle policy |
| Support | Desktop Release Owner owns the minimum supported Windows 11 release, packaging channel, signing identity, update response, and end-of-support notice |

Adding macOS or Linux requires a new evidence row with validated coach demand,
supported OS versions, signing/notarization or package-signature proof, update
authority, recovery, uninstall/local-data semantics, and an assigned support
owner. Platform-neutral application and shared-package boundaries must prevent
Windows-only dependencies from becoming domain dependencies.

**Evidence result:** `EV-D09` supports D-09 and the Windows 11 x64 initial
candidate.

## Offline Data And Synchronization Evidence

### EV-D03 Local Persistence Comparison

| Criterion | SQLite in Electron main | Renderer IndexedDB | Application-managed encrypted files |
| --- | --- | --- | --- |
| Process isolation | Database connection and encryption key stay in main; renderer uses allowlisted typed IPC | Renderer owns direct data access; a renderer compromise reaches the store | Main can own files, but every query/update path becomes custom privileged code |
| Renderer access | No path, SQL, key, or generic database execution crosses IPC | Direct browser API access | Must expose custom commands; generic file access is forbidden |
| Encryption and key custody | SQLCipher-equivalent encrypted database; per-installation key retrieved only in main from OS credential store | Browser storage encryption and key isolation are inconsistent across Electron/platform versions | Application must correctly design record/file encryption, nonces, indexes, and atomic replacement |
| Schema migration | Transactional numbered migrations with recorded schema version | Version-change callbacks, but recovery and large migrations are renderer-lifecycle-sensitive | Entire custom format and migration engine required |
| Corruption recovery | Integrity check; restore last safe local snapshot or purge/rebuild from server; never treat replica as authority | Browser quota/eviction and partial upgrade recovery are less controllable | Custom journal, integrity, and repair behavior required |
| Purge behavior | Close handles, destroy key material, remove database/WAL/temp copies, verify absence, retain minimized receipt | Delete database and verify object stores, subject to browser/runtime behavior | Enumerate, delete, and verify every data/index/temp file |
| Result | Supported | Rejected | Rejected |

The IPC contract is entity- and use-case-specific. It validates the current
user/team/match scope in main, rejects arbitrary SQL and paths, returns
minimized typed results, and records no sensitive payload in diagnostics.

**Evidence result:** `EV-D03` supports D-03. SQLite is a replaceable local
replica implementation behind application repositories, not a source of
authority.

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
| Entitlement tampering | No offline artifact | Signature, audience, device binding, policy version, and claims validation fail closed | Cached flags/state are easier to alter and lack bounded issuer proof |
| Device loss | Server revocation prevents next online login | Exposure lasts no longer than entitlement/local-key protections; reconnect reconciles revocation and purges | Lost device can retain continuing authority |
| Mid-outage revocation | Access ends at the next online check | Cannot be learned while offline; bounded expiry limits exposure and reconnect purges | Cannot be learned and has no expiry bound |
| Signing-key rotation | Not applicable | Entitlement carries key ID; client accepts only approved current/grace verification keys and policy versions | No reliable issuer/key lifecycle |
| Result | Secure but no disconnected use | Supported | Rejected |

The entitlement identifies issuer, audience, subject user, club stamp, permitted
teams and matches, roles, issue time, not-before time, expiry, device public-key
binding, policy version, and unique identifier. It authorizes only reading data
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
| OS full-disk encryption only | Protects a powered-off disk but not application-scoped access on an unlocked device | No application key boundary for prompt inaccessibility or crypto-erasure | File deletion only; hold behavior is application-defined anyway | Does not prevent payload logging | Rejected as insufficient alone |
| App-encrypted store with OS-protected per-installation key | Non-exportable where the platform permits; main process retrieves it; device loss exposure is bounded by OS login, entitlement expiry, and revocation reconciliation | Content becomes inaccessible first; key/store/media copies are then purged according to trigger and policy | Holds preserve only specifically approved content while removing ordinary access; explicit removal covers database, media, indexes, temp files, and key material | Structured allowlist excludes secrets and payloads; purge evidence is minimized | Supported |
| User-managed application password | User supplies/recovers key material; weak/reused/lost passwords add support and recovery risk | Can support encryption but complicates unattended expiry/purge and recovery | Explicit removal possible; holds still require policy machinery | Does not inherently prevent payload logging | Rejected |

#### Accepted Lifecycle By Data Class

| Local data class | Maximum policy shape | Inaccessibility and purge triggers | Hold behavior |
| --- | --- | --- | --- |
| Authorization/team/match metadata | Bounded by entitlement and configured metadata retention | sign-out policy, entitlement/policy expiry, authorization loss, source deletion, explicit removal | A hold never grants UI access; retain only approved evidence scope |
| Analytics/result summaries | No longer than related authorized match metadata | same triggers plus result supersession when policy requires | Same |
| Explicit clips/thumbnails | Shorter media-specific period and quota | same triggers plus quota eviction and user clip removal | Same |
| Sync cursors/tombstones | Minimum period needed for safe reconciliation and purge verification | replace after recovery; retain deletion suppression as policy requires | Hold cannot reactivate deleted authority |
| Entitlements/keys | Entitlement expires cryptographically; old keys have a bounded rotation grace only for verification/decryption migration | expiry, sign-out removal policy, device reset, revocation reconciliation, explicit removal, completed key rotation | Secret key material is not retained under content holds |
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
| Human/renderer to API and privileged desktop process (`BND-001`) | BFF/API remains authoritative; context isolation, sandboxed renderer, narrow typed preload IPC, no direct database/key access | A renderer or dependency vulnerability can invoke allowed operations as the active user | Accept for architecture; implementation requires IPC authorization, dependency, and adversarial tests |
| Stamp and team/match isolation (`BND-005`, `CTL-001`, `CTL-007`) | Cursor, entitlement, snapshot, and delta are bound to one club stamp and current user/team/match scope | Server authorization defect could disclose data before local controls act | Accept only with server boundary tests; local state never broadens scope |
| Active store to cache/index/telemetry (`BND-008`, `CTL-010`) | Copy inventory includes SQLite, WAL/temp, media, thumbnails, indexes, diagnostics, and purge receipts | Crash/interruption can delay physical removal | Accept with inaccessible-first behavior, restartable purge, retries, and verification |
| Encryption and secrets (`CTL-008`) | App-encrypted store, OS-protected per-installation key, signed entitlement, no secret/payload logging | Unlocked compromised device can access active authorized content | Accept with OS account protection, bounded entitlement, rapid expiry, explicit removal, and device-loss response |
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

The Architecture, Product, Security/Data-Governance, and Desktop Release roles
reviewed the client choices together against the architecture decision
scenarios.

| Decision | Realistic alternatives | Supporting evidence | Consequences and migration understood | Accountable owner/required approval | Review result |
| --- | --- | --- | --- | --- | --- |
| D-01 First release scope | Yes | `EV-D01` | Yes | Architecture + Product | Pass |
| D-02 Code sharing | Yes | `EV-D02` | Yes | Architecture + Product + Security/Data-Governance + Desktop Release | Pass |
| D-03 Local persistence | Yes | `EV-D03`, `EV-SPR-01` | Yes | Architecture + Security/Data-Governance | Pass |
| D-04 Synchronization | Yes | `EV-D04`, `EV-SPR-01` | Yes | Architecture + Security/Data-Governance | Pass |
| D-05 Offline data/media | Yes | `EV-D05`, `EV-SPR-01` | Yes | Architecture + Product + Security/Data-Governance | Pass |
| D-06 Offline writes | Yes | `EV-D06` | Yes | Architecture + Product | Pass |
| D-07 Disconnected authorization | Yes | `EV-D07`, `EV-SPR-01` | Yes | Architecture + Product + Security/Data-Governance | Pass |
| D-08 Protection/retention | Yes | `EV-D08`, `EV-SPR-01` | Yes | Architecture + Security/Data-Governance | Pass |
| D-09 Platforms/distribution | Yes | `EV-D09` | Yes | Architecture + Product + Desktop Release | Pass |

Promotion-policy review confirms serious alternatives, explicit tradeoffs,
architecture-level evidence, reversible boundaries, and maturity sufficient to
govern later implementation proposals. It does not claim implementation,
packaging, security-control, or operational exercise evidence already exists.

#### Final Approval Outcomes

| Approval reference | Assignee/role | Outcome |
| --- | --- | --- |
| `APR-ARCH-2026-09-21` | Markus Heiliger, Architecture Owner | Approved all nine outcomes and atomic gate release |
| `APR-PROD-2026-09-21` | Markus Heiliger, Product Owner | Approved first-release, offline/media, write, entitlement-usability, and platform outcomes |
| `APR-SECGOV-2026-09-21` | Markus Heiliger, Security and Data-Governance Owner | Approved persistence, synchronization, data scope, entitlement, lifecycle, privacy, and residual-risk outcomes |
| `APR-DESKTOP-2026-09-21` | Markus Heiliger, Desktop Release Owner | Approved Windows 11 x64 initial support and release-operation obligations |

Every required approval is Approved. Architecture synchronization must still
occur atomically; this review alone does not release the gate.

### Unsupported Candidate Disposition

| Candidate | Disposition and reason |
| --- | --- |
| Read-only or read/write offline in the first release | Deferred because persistence, synchronization, entitlement, and lifecycle implementation evidence does not exist |
| Fully independent duplicated client logic | Rejected because generated contracts, validation, and design primitives would drift |
| One shared React application for web and Electron | Rejected because it couples releases and weakens product and Electron privilege boundaries |
| Renderer-owned IndexedDB | Rejected because privileged local data and key boundaries are weaker |
| Application-managed encrypted files | Rejected because it creates a custom database, indexing, journaling, migration, and recovery burden |
| Full-snapshot-only synchronization | Rejected as the ordinary contract; retained only as bounded cursor-expiry recovery |
| Server-event replay to the desktop | Rejected because it exposes retention, ordering, projection, and compatibility complexity to the client |
| Complete recording replication | Rejected by default because exposure, storage, deletion, and support cost exceed the validated workflow |
| Server-wins or blind last-write-wins | Rejected because user intent can be silently discarded |
| Indefinite cached authority | Rejected because revocation and expiry would be unbounded |
| OS full-disk encryption alone | Rejected because an unlocked device lacks an application-scoped protection and purge boundary |
| User-managed encryption passwords | Rejected because weak/lost/reused credentials add risk and lifecycle complexity |
| Unvalidated macOS or Linux first-release support | Deferred until named coach demand and complete release-operation evidence exist |

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
| Product | Product Owner, Markus Heiliger | Online-only first release, explicit-clip scope, read-only first offline delivery, bounded-outage behavior, and Windows 11 x64 scope match the validated workflows and deferrals |
| Security and privacy | Security and Data-Governance Owner, Markus Heiliger | Local isolation, entitlement, encryption/key custody, retention, deletion, holds, diagnostics, copy inventory, and residual risks satisfy the architecture trust/copy obligations |
| Desktop release | Desktop Release Owner, Markus Heiliger | Initial platform, signing, install/update/recovery, uninstall/local-data behavior, support ownership, and evidence required for added platforms are explicit |

#### Completeness Confirmation

- D-01 through D-09 have alternatives, evaluation methods, required evidence,
  results, risks, migration effects, residual-risk dispositions, and passing
  combined-review rows.
- `APR-ARCH-2026-09-21`, `APR-PROD-2026-09-21`,
  `APR-SECGOV-2026-09-21`, and `APR-DESKTOP-2026-09-21` are Approved.
- The owning architecture documents link this evidence and state the accepted decisions.
- The four affected architecture sources state the same accepted outcomes.
- Later implementation must still provide workspace/dependency tests, local
  schema/recovery tests, sync simulations, IPC and entitlement threat tests,
  keystore/purge evidence, and Windows packaging/update evidence.

#### Non-Goals Confirmed

- No client, shared package, installer, local database, sync engine,
  entitlement issuer, or client-facing API was scaffolded or implemented.
- No server retention duration, residency, production topology, backup value,
  or unrelated production-governance gap was resolved.
- The Coach Client did not gain club administration, recording upload, or
  offline agent submission/refresh.

#### Preserved Invariants Confirmed

- Two separately packaged clients retain their accepted scopes.
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
