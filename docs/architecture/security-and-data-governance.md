# Security and Data Governance (Provisional)

This topic is the authoritative architecture policy for SocAlytics security
threat modeling and server-side data lifecycle. It consolidates existing point
controls without changing their decision status and defines the obligations
that must be resolved and evidenced before production acceptance.

The policy is provider-neutral and does not select a compliance regime,
production topology, region, retention duration, or implementation mechanism.
Those values require deployment-specific approval. Unassigned ownership and
unset policy values are production-blocking gaps, not implied defaults.

## Scope And Boundaries

This policy covers server-managed data, credentials, execution artifacts,
agent lineage, logs, telemetry, caches, indexes, replicas, and backups. The
Coach Client local-store and disconnected boundaries are accepted in
[Client Applications](client-applications.md). This topic
continues to govern their encryption, authorization, copy lifecycle, deletion,
hold, telemetry, and audit obligations; the ADRs do not resolve deployment-
specific durations or production-control evidence.

The following established invariants remain unchanged:

- exactly one club belongs to each deployment stamp
- the server remains the system of record
- the control plane never handles video bytes
- Analyst Containers never receive database credentials
- Analyst execution is at least once and attempt-fenced
- production provenance uses immutable digests

Production topology, backup and restore mechanisms, RPO/RTO, alerting,
operational escalation, disaster recovery, and runbooks remain part of future
operations architecture in
[Production Deployment and Operations](production-operations.md). That topic
realizes operational mechanisms for CTL-008 through CTL-012 and POL-001 through
POL-010 without changing this policy's authority or unset values. Agent
persistence and transport remain provisional in
[Intelligence and Agents](intelligence-and-agents.md). This policy defines how
those mechanisms must protect and govern data; it does not select them.

Normative requirements use **MUST** and **MUST NOT**. Every control, threat,
data class, and lifecycle policy has a stable identifier for review and future
evidence references.

## Actors And Assets

### Actors

| ID | Actor | Security relevance |
| --- | --- | --- |
| ACT-001 | Club Admin | Stamp-wide administration, access grants, Manager approval, policy approval when authorized |
| ACT-002 | Coach, Viewer, and Registrar | Team-scoped use and registration initiation under role authorization |
| ACT-003 | Analyst Manager | Device-bound work claimant and local OCI runtime coordinator |
| ACT-004 | Analyst Container | Untrusted-by-default job-scoped analysis executable |
| ACT-005 | Hermes, Coach Agents, and Specialist Agents | Authorized consumers of API/MCP evidence and producers of advice lineage |
| ACT-006 | Optional OIDC provider | External human identity assertion boundary |
| ACT-007 | OCI registry and artifact publishers | Sources of images, models, packages, signatures, and provenance |
| ACT-008 | Data-service and deployment operators | Privileged operators of database, object storage, messaging, replicas, and backups |
| ACT-009 | Security and data-governance approvers | Required accountable roles; assignment remains Open / Blocking for production |

### Data Classes

| ID | Data class | Classification | Purpose and authoritative owner | Permitted locations and copies | Lifecycle policy |
| --- | --- | --- | --- | --- | --- |
| DAT-001 | Identity, role, team, and registration records | Restricted | API identity and domain modules | Stamp database, authorized indexes, replicas, backups | POL-001 |
| DAT-002 | Device keys, tokens, grants, and broker credentials | Secret | Issuing identity or credential service; protected endpoint for device keys | Protected keystore or issuing service; transient authorized process memory; secrets MUST NOT enter logs, caches, or backups unless explicitly required and protected | POL-002 |
| DAT-003 | Source recordings and timeline mappings | Sensitive | Object storage for bytes; database for immutable references and mappings | Stamp object storage, metadata database, approved replicas and backups | POL-003 |
| DAT-004 | Materialized segments and media caches | Sensitive | Segment Service and object storage | Stamp object storage and bounded processing caches; approved replicas and backups only when policy requires | POL-004 |
| DAT-005 | Foundational facts, soccer concepts, results, and manifests | Sensitive | API-owned accepted indexes and immutable object results | Stamp database, object storage, authorized indexes, replicas, and backups | POL-005 |
| DAT-006 | Job, attempt, lease, outbox, and queue state | Internal | Job Registry and module-owned database state; NATS is transport only | Stamp database, stamp messaging, operational caches, replicas, and backups | POL-006 |
| DAT-007 | Analyst images, models, packages, signatures, and provenance | Restricted | Model/Capability Registry and approved artifact registry | Registry, stamp metadata, build evidence, approved mirrors and backups | POL-007 |
| DAT-008 | Agent conversations, invocations, evidence references, and advice lineage | Sensitive | Agent Orchestration module in stamp PostgreSQL; API/MCP authorization remains authoritative | Stamp PostgreSQL Primary, bounded JetStream Transport, active Hermes Cache, authorized indexes, telemetry, replicas, and backups | POL-008 |
| DAT-009 | Security, authorization, lifecycle, and administrative audit events | Restricted | Security audit authority; accountable owner unresolved | Append-protected audit store, approved indexes, replicas, and backups | POL-009 |
| DAT-010 | Application logs, traces, metrics, and diagnostic captures | Internal; Sensitive when correlated or payload-bearing | Emitting component and telemetry authority | Approved telemetry pipeline, bounded diagnostic stores, replicas, and backups | POL-010 |

`Sensitive` data includes recordings or information attributable to players,
matches, teams, users, or agent conversations. `Restricted` data can alter,
authorize, or materially explain system behavior. `Secret` data grants access
or proves identity. `Internal` data is not public and MUST be minimized when it
can be correlated with a person, team, match, stamp, or credential.

### Data Handling Requirements

| Applies to | Access scope | Encryption in transit and at rest | Credential or key responsibility | Residency | Required audit evidence |
| --- | --- | --- | --- | --- | --- |
| `DAT-001` | Stamp identity services and authorized administrators; team data remains team-scoped | Required for every persistent and transferred copy | Stamp-scoped identity and storage authorities; accountable owner unresolved | Approved stamp locations only | Authentication, authorization, role, registration, export, retention, and deletion actions |
| `DAT-002` | Issuer and intended workload only; no human-readable secret export | Required in transit, at rest, and in protected process or device storage | Issuer owns lifecycle; device key remains non-exportable; accountable owner unresolved | Approved stamp or protected endpoint locations only | Creation, rotation, expiry, revocation, recovery, and exceptional access without secret values |
| `DAT-003` | Team-authorized users and job-scoped services | Required for object bytes, metadata, replicas, and backups | Stamp storage authority; grant issuer owns short-lived access; accountable owner unresolved | Approved stamp locations and movements only | Upload, finalization, grant, access, export, hold, retention, and deletion actions |
| `DAT-004` | Team-authorized requests and job-scoped processing | Required for cache bytes and every persisted copy | Segment storage/cache authority; accountable owner unresolved | Same approved constraints as source recording | Materialization, cache publication, access, corruption, expiry, and purge outcomes |
| `DAT-005` | Team-authorized API, Analysts, and agents according to capability | Required for database, object, index, replica, and backup copies | Owning API module and storage authorities; accountable owner unresolved | Approved stamp locations and movements only | Acceptance, query authorization, export, supersession, hold, retention, and deletion actions |
| `DAT-006` | Scheduler, Job Registry, registered Managers, and scoped operators | Required for durable and transported state | Job Registry, messaging, and lease issuers; accountable owner unresolved | Approved stamp locations; transport remains stamp-local | State transitions, claims, leases, retries, fencing, publication, expiry, and purge outcomes |
| `DAT-007` | Approved build, registry, platform, and execution authorities | Required for registries, metadata, mirrors, and backups | Artifact publisher, registry, and authorization authority; accountable owner unresolved | Approved artifact and evidence locations | Publication, signature/provenance validation, authorization, vulnerability, exception, revocation, and purge actions |
| `DAT-008` | Initiating user plus current team/match scope through API/MCP | Required for every transport and persistent copy | Agent Orchestration owns Primary state; messaging, runtime, index, telemetry, replica, and backup authorities own governed copies; accountable owner unresolved | Approved stamp locations only unless separately approved | Invocation, delegation, claim, checkpoint, completion, query, tool call, evidence access, denial, export, hold, retention, deletion, purge, and restore-suppression actions |
| `DAT-009` | Authorized security reviewers and narrowly scoped operators | Required for audit transport, indexes, replicas, and backups | Independent audit authority; accountable owner unresolved | Approved evidence locations and movements only | Ingestion, access, export, integrity check, redaction, retention, deletion, and hold actions |
| `DAT-010` | Operational roles with purpose-limited diagnostic access | Required whenever persisted or transferred | Telemetry pipeline and diagnostic-store authorities; accountable owner unresolved | Approved telemetry locations and movements only | Schema/redaction decisions, exceptional capture/access, export, retention, and purge outcomes |

### Storage And Copy Classes

| Copy class | Examples | Governance requirement |
| --- | --- | --- |
| Primary | PostgreSQL module data, authoritative object artifacts | Authoritative state, access enforcement, retention, and deletion initiation |
| Transport | NATS messages, presigned requests, API responses | Bounded lifetime, minimum payload, no new authority, replay protection where required |
| Cache | Materialized media, process caches, explicitly cached data | Rebuildable or traceable to authority; bounded retention; purge on authorization loss or source deletion |
| Index | Accepted facts, search/read models, telemetry indexes | Derived from an authority; deletion and authorization changes propagate |
| Telemetry | Logs, traces, metrics, and diagnostic captures | Minimized at emission; bounded retention and access; payload deletion propagates to indexes, replicas, and backups |
| Replica | Database, object, or telemetry replica | Same classification, access, encryption, residency, and deletion obligations as its source |
| Backup | Immutable or mutable protected recovery copy | Approved retention and residency; access is exceptional and audited; deleted data is suppressed after restore |
| Audit | Append-protected security and lifecycle evidence | Minimized, integrity-protected, access-controlled, and independently retained under POL-009 |

## Trust Boundaries

| ID | Boundary | Required posture |
| --- | --- | --- |
| BND-001 | Human client to API/BFF | Authenticated session, CSRF defense, role and team authorization, no browser bearer-token exposure |
| BND-002 | Analyst Manager to deployment stamp | Approved device identity, proof-bound short-lived credentials, active registration, immediate server-side revocation |
| BND-003 | Analyst Container to Manager/runtime host | Non-root constrained execution, no runtime socket, bounded resources, scoped mounts and devices |
| BND-004 | Analyst Container to data services | Job-scoped API/object access, no database credentials, short-lived grants |
| BND-005 | Deployment stamp to deployment stamp | Dedicated logical data, messaging, credentials, and configuration; no cross-stamp authority |
| BND-006 | Agents to API/MCP | Initiating user context retained; team and match authorization enforced by API/MCP |
| BND-007 | Platform to external identity and artifact services | Explicit trust configuration, provenance validation, minimum scopes, failure closed |
| BND-008 | Active stores to caches, indexes, replicas, backups, and telemetry | Classification and lifecycle obligations propagate to every copy |
| BND-009 | Privileged operator to stamp resources | Least privilege, separation of duties where practical, auditable use, no implicit cross-stamp access |

## Control Catalog

The catalog records architecture obligations. A source marked Provisional
remains Provisional; inclusion here does not promote it to Accepted.

| ID | Control | Status | Source or requirement |
| --- | --- | --- | --- |
| CTL-001 | Dedicated logical database, object storage, messaging, credentials, and configuration per stamp | Provisional | [Deployment and Technology](tenancy-and-technology.md) |
| CTL-002 | BFF sessions use secure cookies, CSRF protection, and server-side authorization | Provisional | [Platform Implementation Profile](platform-implementation.md) |
| CTL-003 | Manager identity uses protected device keys, approval, proof-bound short-lived tokens, and server-side revocation | Provisional | [Analyst Manager](analyst-manager.md) |
| CTL-004 | Analyst execution is non-root and constrained by mounts, devices, capabilities, network, resources, and credentials | Provisional | [Analyst Runtime and Recovery](analyst-runtime-and-recovery.md) |
| CTL-005 | Attempts use leases, fencing, idempotent completion, and rejection of stale callbacks | Provisional | [Analyst Runtime and Recovery](analyst-runtime-and-recovery.md) |
| CTL-006 | Production images, models, preprocessing, packages, and results retain immutable provenance | Provisional | [Analysts, Models, and Hardware](analysts-models-and-hardware.md) |
| CTL-007 | API/MCP enforce initiating-user team and match authorization for agent evidence | Provisional | [Intelligence and Agents](intelligence-and-agents.md) |
| CTL-008 | Data is encrypted in transit across untrusted or shared boundaries and at rest in every persistent copy | Required | This policy; implementation evidence required before production |
| CTL-009 | Access, role, credential, retention, deletion, hold, export, and security actions emit minimized audit events | Required | This policy; event and evidence implementation deferred |
| CTL-010 | Lifecycle propagation discovers and verifies affected primary, cache, index, replica, telemetry, and backup copies | Required | This policy; implementation evidence required before production |
| CTL-011 | Security-relevant artifacts undergo provenance verification and vulnerability handling before authorization | Required | This policy; implementation evidence required before production |
| CTL-012 | Privileged operations are least-privileged, stamp-scoped, and attributable | Required | This policy; accountable owner and implementation evidence required |

## Threat Model

| ID | Threat and affected boundaries | Controls | Detection and audit evidence | Residual risk and disposition | Accountable owner |
| --- | --- | --- | --- | --- | --- |
| THR-001 | Cross-stamp data or credential access across BND-005/BND-009 | CTL-001, CTL-012 | Denied-access and privileged-access events correlated to both stamp and actor | Shared physical infrastructure can still fail isolation; production-blocking until isolation tests and owner acceptance exist | Open / Blocking |
| THR-002 | Privileged administrator abuses role, export, deletion, or policy authority | CTL-009, CTL-012 | Immutable administrative action, approval, export, hold, and deletion events | Authorized insiders retain exceptional power; require least privilege, independent review, and named owner | Open / Blocking |
| THR-003 | User session, Manager identity, token, credential, or presigned grant is compromised | CTL-002, CTL-003, CTL-009 | Authentication, token issuance, grant use, revocation, and anomalous-access events without secret values | Valid grants can be abused until expiry or revocation; lifetime and detection policy values are unresolved | Open / Blocking |
| THR-004 | Malicious or vulnerable image, model, package, or registry compromises analysis | CTL-004, CTL-006, CTL-011 | Digest, signature, provenance, vulnerability decision, and authorization evidence | Signed artifacts can remain vulnerable or malicious; approval and remediation ownership are unresolved | Open / Blocking |
| THR-005 | Container escape or excessive host/runtime access exposes data or credentials | CTL-004, CTL-009 | Runtime preflight, launch-policy denial, device/mount policy, and abnormal-exit evidence | Runtime or kernel vulnerabilities remain; supported-runtime and patch policy belongs to production readiness | Open / Blocking |
| THR-006 | Replay, tampering, duplicate completion, or stale attempt publishes unauthorized results | CTL-005, CTL-006, CTL-009 | Lease, digest, attempt, rejection, and accepted-completion evidence | Control defects may accept bad lineage; integration and failure tests are required | Open / Blocking |
| THR-007 | Agent accesses or discloses evidence outside initiating user, team, or match scope | CTL-007, CTL-009 | Invocation actor/context, claims, delegation, MCP calls, evidence references, query and denial events | Stored context is not a grant; authorization defects or stale policy evaluation can still disclose data, requiring boundary and revocation tests | Open / Blocking |
| THR-008 | Logs, traces, metrics, or diagnostics expose payloads, secrets, or personal data | CTL-008, CTL-009, CTL-010 | Telemetry schema review, redaction evidence, exceptional diagnostic access | Free-form diagnostics can bypass schema controls; production-blocking until minimization tests exist | Open / Blocking |
| THR-009 | Accidental or malicious deletion removes authoritative evidence or leaves unauthorized orphan copies | CTL-009, CTL-010 | Deletion request, authorization, copy inventory, purge result, tombstone, backup-expiry, and hold events | Asynchronous deletion and immutable backups delay physical removal; approved policy and restore suppression are required | Open / Blocking |
| THR-010 | Shared storage, replica, cache, index, backup, or telemetry infrastructure leaks data | CTL-001, CTL-008, CTL-010, CTL-012 | Configuration evidence, access denials, copy inventory, residency and encryption checks | Provider/operator defects remain; deployment evidence and residual-risk acceptance are required | Open / Blocking |
| THR-011 | Lifecycle policy is bypassed through an untracked copy or unsupported restore | CTL-009, CTL-010 | Periodic inventory reconciliation, orphan detection, restore suppression, and expiry evidence | Unknown copies cannot be governed; production deployment must enumerate every copy class | Open / Blocking |

## Encryption And Secret Handling

- CTL-008 requires authenticated encryption in transit across untrusted or
  shared boundaries. Plaintext transport MUST NOT be enabled in production.
- Every persistent Primary, Cache, Index, Replica, Backup, Audit, and Telemetry
  copy containing Secret, Sensitive, or Restricted data MUST be encrypted at
  rest. Provider-managed and application-managed mechanisms are both permitted
  when their ownership, access, rotation, recovery, and evidence are approved.
- Encryption keys and credential issuers MUST be scoped so compromise of one
  stamp does not grant authority to another stamp. A shared physical service
  MUST preserve logical key and authorization separation.
- Secrets MUST NOT appear in application logs, audit payloads, telemetry,
  result manifests, container arguments, durable queues, or error messages.
- Key and credential creation, rotation, expiry, revocation, recovery, and
  exceptional access MUST be attributable without recording secret values.

## Retention, Deletion, And Holds

### Lifecycle Policies

| ID | Scope | Required policy value before production | Deletion trigger and active-copy behavior | Backup and hold behavior | Approval owner |
| --- | --- | --- | --- | --- | --- |
| POL-001 | Identity, authorization, and registration records | Active and post-revocation retention periods | Revoke access first; purge or minimize primary, index, cache, telemetry, and replica copies when obligations permit | Expire from backup policy; suppress deleted authority after restore; audited hold allowed | Open / Blocking |
| POL-002 | Keys, tokens, grants, and credentials | Issuance lifetime, rotation, revocation, and destroyed-key evidence | Revoke immediately; destroy active and cached secret material; retain only non-secret audit evidence | Secrets MUST NOT be restored as active authority; hold applies only to non-secret evidence | Open / Blocking |
| POL-003 | Recordings and timeline mappings | Active, superseded, and requested-deletion periods | Deny new access at deletion start; purge bytes, metadata, caches, indexes, telemetry payloads, and replicas with verification | Immutable backups expire by policy; restored data remains suppressed; approved expiring hold allowed | Open / Blocking |
| POL-004 | Materialized segments and media caches | Maximum cache age, affected copy classes, and maximum source-deletion propagation delay | Purge on source deletion, authorization loss, corruption, or expiry; verify no visible cache/index association remains | Backup only when explicitly justified; otherwise rebuild from retained source | Open / Blocking |
| POL-005 | Facts, concepts, results, and manifests | Active, superseded, and source-deletion dependency periods | Remove access and purge active/indexed/replicated copies according to approved source and evidence rules | Expire by backup policy; restore suppression and approved expiring hold apply | Open / Blocking |
| POL-006 | Job, attempt, outbox, lease, and queue state | Terminal-state, failed-work, and publication-evidence periods | Stop authority first; purge transient transport and caches; retain only required bounded execution/audit evidence | Restore MUST NOT reactivate expired leases, grants, attempts, or queue authority | Open / Blocking |
| POL-007 | Images, models, packages, and provenance | Authorized, superseded, vulnerable, and evidence-retention periods | Remove execution authorization immediately when revoked; retain immutable evidence separately; purge unauthorized mirrors/caches | Backups and recovery MUST preserve revocation and authorization decisions | Open / Blocking |
| POL-008 | Agent conversations, invocations, and advice lineage | Conversation, evidence, and deletion periods | Deny query and execution authority first; commit an Agent Orchestration tombstone, then propagate purge to PostgreSQL state and outbox, JetStream transport, Hermes caches, indexes, telemetry, and replicas | Expire by backup policy; reapply current tombstones before restored data becomes visible or executable; approved expiring hold allowed | Open / Blocking |
| POL-009 | Security and lifecycle audit evidence | Event-class retention and integrity-verification periods | Minimize rather than silently erase evidence; deletion or redaction requires separate authorization and an audit event | Protected backup retention follows approved audit policy; narrowly scoped hold allowed | Open / Blocking |
| POL-010 | Logs, traces, metrics, and diagnostics | Per-signal retention and exceptional-capture period | Purge expired or request-scoped payloads from active telemetry, indexes, caches, and replicas; preserve only minimized aggregate data when approved | Diagnostic backups expire by policy; restored payloads remain subject to tombstones | Open / Blocking |

Retention values MUST be explicit, versioned, attributable, and set before a
production stamp accepts data. The absence of a value MUST fail production
readiness; it MUST NOT mean indefinite retention.

A deletion request MUST create a durable tombstone or equivalent suppression
record before active purge begins. Authorization is removed first. Purge then
propagates asynchronously to every known Primary, Cache, Index, Telemetry, and
Replica copy and records per-copy success or failure. Completion is reported
only after required active copies are verified absent or irreversibly
anonymized under an approved rule.

Immutable backups are not rewritten solely to remove one record. They MUST
expire under an approved, bounded backup-retention policy. Any restore MUST
reapply current deletion tombstones, authorization revocations, artifact
revocations, and expiry decisions before restored data becomes accessible or
can regain authority.

A hold MUST identify scope, reason, approving actor, creation time, expiry,
affected policies, and review history. Holds MUST be narrowly scoped, MUST NOT
silently restore access, and MUST expire unless explicitly renewed through a
new audited approval.

## Residency And Data Movement

Each production stamp MUST declare approved residency constraints for every
persistent copy class before accepting data. Movement to another location,
operator, or legal boundary requires authorization, encryption, an inventory
update, and an audit event. This policy does not select a geography or provider.
Unknown residency or an untracked replica or backup is production-blocking.

## Audit Events

Security and lifecycle events MUST record an event identifier, event type,
timestamp, stamp, actor or workload identity, authorization context, affected
resource and data-class identifiers, action and outcome, correlation identifier,
policy version, and reason or approval reference when applicable.

Audit events MUST be append-protected, access-controlled, integrity-verifiable,
and separated from ordinary application logs. They MUST contain no credential,
token, private key, unnecessary payload, or durable presigned URL. Access to
audit evidence and every export or redaction of it MUST itself be audited.

At minimum, authentication and authorization decisions, role changes, Manager
registration and revocation, credential lifecycle, artifact authorization,
attempt rejection and acceptance, policy changes, data export, deletion,
retention expiry, hold lifecycle, restore suppression, vulnerability decisions,
and declared security incidents require audit evidence.

## Supply Chain And Vulnerability Handling

Production Analyst profiles, images, models, packages, and generated artifacts
MUST be identified by immutable digest and retain source, license, build,
dependency, signature or attestation, validation, and authorization evidence
appropriate to their type. Container-owned preprocessing and postprocessing
implementation is covered by the immutable image provenance. Mutable tags MUST
NOT establish production identity.

A vulnerability process MUST define intake sources, triage criteria, affected-
artifact discovery, severity and exploitability assessment, authorization or
revocation decisions, remediation expectations, exception approval and expiry,
and evidence retention. A superseded or vulnerable artifact can remain stored
for evidence but MUST NOT remain executable unless an explicit, bounded,
audited exception has been approved.

## Security Incident Governance

A production readiness decision MUST assign accountable roles for declaring a
security incident, authorizing containment, preserving evidence, deciding
notification, approving exceptional access, and accepting residual risk. Role
assignments remain Open / Blocking in this provisional architecture.

Containment authority includes revoking sessions and credentials, fencing
attempts, disabling artifact execution, denying data access, and placing a
narrowly scoped evidence hold. Incident actions MUST preserve stamp isolation
and MUST be audited. Operational procedures, contacts, service objectives,
alerts, and recovery runbooks remain dependencies of future operations
architecture rather than being invented here.

## Security-Governance Decision Register

The decision register is the canonical resolution record for the security,
data-lifecycle, operational, and production-governance placeholders defined by
this topic and the linked architecture. Source sections remain authoritative
for policy and architecture obligations; register entries record the bounded
value, ownership, approval, and evidence state needed to satisfy those
obligations. A register entry MUST NOT silently replace or weaken its source.

### Identity, Versioning, And Atomicity

Every independently approvable decision has one stable identifier in the form
`GOV-<family>-<number>`, where `<family>` is `OWN`, `POL`, `THR`, `ENC`, `RES`,
`CRED`, `VUL`, `INC`, `AUD`, `TEL`, `OBJ`, `DEP`, or `SIGN`, and `<number>` is
a zero-padded sequence within that family. Versions use positive integers. A
change to scope, value, stage, dependencies, conditions, or required evidence
creates a new version rather than rewriting an approved historical version.
Related entry versions MAY be reviewed in one approval packet, but each entry
retains its identity, status, evidence, and outcome.

### Mandatory Entry Fields

Each register row or linked detail record MUST provide every field below. A
field appears once in the canonical record for an entry version; references
MUST link to that field rather than duplicate it with a conflicting value.

| Field | Required content |
| --- | --- |
| Identifier | Stable `GOV-<family>-<number>` identity |
| Version | Positive integer for the immutable decision revision |
| Title | Concise description of the atomic choice |
| Authoritative source | Source identifier and section that define the obligation |
| Scope | Data, control, boundary, role, objective, or operational concern governed |
| Affected stamps or profiles | Explicit profile scope, or `All profiles` for common policy |
| Decision stage | Exactly `Before Development` or `Before Production Promotion` |
| Accountable proposer role | Role responsible for preparing and maintaining the entry |
| Required approver roles | All roles whose attributable outcomes are required |
| Status | One allowed lifecycle status |
| Blocking gate | Implementation boundary or production-readiness gate that fails while unsatisfied |
| Candidate options or required inputs | Bounded alternatives and missing authoritative inputs; never an implicit default |
| Approved value or unresolved marker | Approved bounded value, or exactly `Unresolved / Blocking` |
| Rationale | Reasoning, consequences, and basis for the current outcome |
| Dependencies | Exact source, register, profile, control, runbook, or evidence versions relied upon |
| Evidence references | Reviewable evidence URIs or repository references; unresolved entries state required evidence |
| Decision timestamp | Attributable decision time for an approval or rejection, otherwise `Not yet decided` |
| Review timestamp | Last completed review time, otherwise `Not yet reviewed` |
| Expiry or review trigger | Bounded expiry and/or events requiring review |
| Supersession history | Prior and replacement entry versions, or `None` |

### Authoritative Source Inventory

The following inventory is bidirectional: the left side identifies every
authoritative source or explicit placeholder and the right side reserves the
register identities that resolve it. Ranges are inclusive. An entry is invalid
when its source is missing from this inventory, and a listed source is
uncovered when none of its reserved entries exists.

| Authoritative source | Required register entry or entries |
| --- | --- |
| Actor `ACT-009` | `GOV-OWN-001` |
| Data classes `DAT-001` through `DAT-010` | `GOV-OWN-002` through `GOV-OWN-011`, respectively |
| Lifecycle policies `POL-001` through `POL-010` | `GOV-POL-001` through `GOV-POL-010`, respectively |
| Threats `THR-001` through `THR-011` | `GOV-THR-001` through `GOV-THR-011`, respectively |
| Copy classes Primary, Transport, Cache, Index, Telemetry, Replica, Backup, and Audit | `GOV-ENC-001` through `GOV-ENC-008`, respectively; handling is also constrained by the applicable `GOV-POL-*` and `GOV-RES-*` entries |
| Data-class residency and movement for `DAT-001` through `DAT-010` across applicable copy classes | `GOV-RES-001` through `GOV-RES-010`, respectively |
| Human sessions, device keys, Manager/workload tokens, NATS credentials, object grants, external identity credentials, signing keys, encryption keys, and recovery credentials | `GOV-CRED-001` through `GOV-CRED-009`, respectively |
| Vulnerability intake, triage, affected-artifact discovery, severity/exploitability, remediation, authorization/revocation, exceptions, evidence retention, and approval | `GOV-VUL-001` through `GOV-VUL-009`, respectively |
| Incident declaration, containment, evidence preservation, notification, exceptional access, communications, recovery, residual-risk acceptance, contacts, objectives, escalation authority, and runbook authority | `GOV-INC-001` through `GOV-INC-012`, respectively |
| Audit independent authority, ordinary access, exceptional access, purpose/export, redaction, integrity verification, self-auditing, retention, holds/backup, and approval | `GOV-AUD-001` through `GOV-AUD-010`, respectively |
| Telemetry logs, metrics, traces, health signals, and exceptional diagnostics | `GOV-TEL-001` through `GOV-TEL-005`, respectively |
| Operational roles: incident commander, operations, PostgreSQL, JetStream, object storage, security/data governance, communications, architecture/change, audit, telemetry, vulnerability response, RPO, RTO, profile approval, and production sign-off | `GOV-OWN-012` through `GOV-OWN-026`, respectively |
| Production-profile values: host OS/architecture, engine/Compose versions, ports/exposure, DNS, certificate issuer, secret source, volume paths, replica counts, resource reservations/sizes, configuration versions, alert routes, and escalation routes | `GOV-DEP-001` through `GOV-DEP-012`, respectively |
| Objectives: stamp availability, API health, API latency, job acceptance, job claim, job completion, outbox publication lag, queue oldest-work age, queue drain time, backup freshness, restore RTO, recoverable-data RPO, capacity, and alert thresholds | `GOV-OBJ-001` through `GOV-OBJ-014`, respectively |
| Runbooks: production preflight, backup failure, isolated restore, whole-host/Compose loss, PostgreSQL outage/corruption, JetStream loss/backlog, object-storage outage/corruption/capacity, control-plane failure, Manager/runtime loss, credential compromise, telemetry loss, lifecycle-policy failure, and stamp-isolation failure | `GOV-INC-013` through `GOV-INC-025`, respectively |
| Escalation routes: security incident, capacity/backpressure, state-service/storage dependency, recovery, communications, and architecture/change or production authority | `GOV-INC-026` through `GOV-INC-031`, respectively |
| Final profile-scoped production promotion sign-off | `GOV-SIGN-001` |

The reverse inventory defines the only authoritative source for each initial
entry range:

| Register entries | Authoritative source |
| --- | --- |
| `GOV-OWN-001` | Actors and Assets / Actors (`ACT-009`) |
| `GOV-OWN-002` through `GOV-OWN-011` | Actors and Assets / Data Classes and Data Handling Requirements (`DAT-001` through `DAT-010`) |
| `GOV-OWN-012` through `GOV-OWN-026` | Production Deployment and Operations / Service Objectives and Ownership, Incident and Runbook Contract, and Validation and Promotion Evidence |
| `GOV-POL-001` through `GOV-POL-010` | Retention, Deletion, And Holds / Lifecycle Policies (`POL-001` through `POL-010`) |
| `GOV-THR-001` through `GOV-THR-011` | Threat Model (`THR-001` through `THR-011`) |
| `GOV-ENC-001` through `GOV-ENC-008` | Storage And Copy Classes, Encryption And Secret Handling, and `CTL-008` |
| `GOV-RES-001` through `GOV-RES-010` | Data Handling Requirements, Residency And Data Movement, and `DAT-001` through `DAT-010` |
| `GOV-CRED-001` through `GOV-CRED-009` | Encryption And Secret Handling, `DAT-002`, `POL-002`, and `THR-003` |
| `GOV-VUL-001` through `GOV-VUL-009` | Supply Chain And Vulnerability Handling, `CTL-011`, `THR-004`, and `THR-005` |
| `GOV-INC-001` through `GOV-INC-012` | Security Incident Governance and Production Deployment and Operations / Incident and Runbook Contract |
| `GOV-INC-013` through `GOV-INC-025` | Production Deployment and Operations / Incident and Runbook Contract required-runbook list |
| `GOV-INC-026` through `GOV-INC-031` | Production Deployment and Operations / Capacity and Backpressure, Incident and Runbook Contract, and Validation and Promotion Evidence |
| `GOV-AUD-001` through `GOV-AUD-010` | Audit Events, `DAT-009`, `POL-009`, and `CTL-009` |
| `GOV-TEL-001` through `GOV-TEL-005` | Production Deployment and Operations / Observability, Dashboards, and Alerts; `DAT-010`, `POL-010`, and `THR-008` |
| `GOV-OBJ-001` through `GOV-OBJ-014` | Production Deployment and Operations / Service Objectives and Ownership, Observability, Dashboards, and Alerts, and Capacity and Backpressure |
| `GOV-DEP-001` through `GOV-DEP-012` | Production Deployment and Operations / Versioned Production Profile |
| `GOV-SIGN-001` | Production Deployment and Operations / Environment and Preflight Contract and Validation and Promotion Evidence |

### Initial Register Seed

Every reserved entry is instantiated below at version `1`. These grouped seed
rows apply the same explicit unresolved state to each individual identifier in
the range; later approval packets add entry-specific inputs and evidence
without changing the initial state. No seed row supplies a policy, deployment,
owner, or approval value.

| Entry or inclusive range | Version | Stage | Accountable proposer role | Required approver roles | Status | Approved value or unresolved marker | Missing authoritative input | Evidence required to leave unresolved state |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `GOV-OWN-001` | 1 | Before Production Promotion | Unresolved / Blocking - authorized security/data-governance assignee required | Security/data-governance; architecture/change; production authority | Unresolved / Blocking | Unresolved / Blocking | Authorized assignee, delegation boundaries, separation of duties, review scope, and sign-off authority | Attributable assignment and approval outcomes for version 1 and profile scope |
| `GOV-OWN-002` through `GOV-OWN-011` | 1 | Before Production Promotion | Unresolved / Blocking - authorized data owner required per entry | Product/business; security/data-governance; accountable data-service owner | Unresolved / Blocking | Unresolved / Blocking | Accountable owner and approved handling authority for each data class | Attributable owner assignment, handling decision, and exact linked policy version |
| `GOV-OWN-012` through `GOV-OWN-026` | 1 | Before Production Promotion | Unresolved / Blocking - authorized operational assignee required per entry | Operations; security/data-governance; architecture/change; production authority where applicable | Unresolved / Blocking | Unresolved / Blocking | Accountable assignee, delegation, coverage, and production-profile scope | Attributable assignment plus role-specific operational and approval evidence |
| `GOV-POL-001` through `GOV-POL-010` | 1 | Before Production Promotion | Unresolved / Blocking - product/business policy owner required per entry | Product/business; security/data-governance; data-service owner; architecture/change | Unresolved / Blocking | Unresolved / Blocking | Bounded lifecycle, deletion, backup, restore-suppression, hold, owner, and review values | Joint attributable approval and lifecycle verification evidence for the exact policy version |
| `GOV-THR-001` through `GOV-THR-011` | 1 | Before Production Promotion | Unresolved / Blocking - accountable residual-risk owner required per entry | Security/data-governance; operations; architecture/change; production authority | Unresolved / Blocking | Unresolved / Blocking | Residual-risk owner, evidence set, disposition, conditions, and acceptance authority | Threat-specific control evidence and attributable acceptance or rejection |
| `GOV-ENC-001` through `GOV-ENC-008` | 1 | Before Production Promotion | Unresolved / Blocking - accountable technical/data-service owner required per entry | Security/data-governance; technical/data-service owner; operations | Unresolved / Blocking | Unresolved / Blocking | Deployment-applicable mechanism, custody, access, rotation, recovery, destruction, and verification | Configuration, test, access, rotation, recovery, and destruction evidence |
| `GOV-RES-001` through `GOV-RES-010` | 1 | Before Production Promotion | Unresolved / Blocking - authorized business/legal owner required per entry | Authorized business/legal; security/data-governance; operations | Unresolved / Blocking | Unresolved / Blocking | Permitted locations, operators, movements, legal constraints, and copy-class scope | Attributable legal/business input and deployment inventory evidence |
| `GOV-CRED-001` through `GOV-CRED-009` | 1 | Before Production Promotion | Unresolved / Blocking - credential authority owner required per entry | Security/data-governance; accountable technical/data-service owner | Unresolved / Blocking | Unresolved / Blocking | Issuance, bounded lifetime, renewal, rotation, revocation, recovery, and evidence rules | Issuer configuration, revocation, recovery, expiry, and non-secret audit evidence |
| `GOV-VUL-001` through `GOV-VUL-009` | 1 | Before Production Promotion | Unresolved / Blocking - vulnerability-response owner required per entry | Security/data-governance; technical/data-service owner; operations | Unresolved / Blocking | Unresolved / Blocking | Intake, triage, discovery, severity, remediation, authorization, exception, retention, and approval criteria | Process evidence, artifact discovery tests, bounded exception evidence, and attributable outcomes |
| `GOV-INC-001` through `GOV-INC-012` | 1 | Before Production Promotion | Unresolved / Blocking - incident-governance owner required per entry | Security/data-governance; operations; communications; production authority as applicable | Unresolved / Blocking | Unresolved / Blocking | Incident authorities, contacts, objectives, escalation, recovery, and acceptance assignments | Attributable assignments, approved procedures, and live incident-exercise evidence |
| `GOV-INC-013` through `GOV-INC-025` | 1 | Before Production Promotion | Unresolved / Blocking - runbook owner required per entry | Operations; affected data-service owner; security/data-governance | Unresolved / Blocking | Unresolved / Blocking | Versioned runbook content, owner, trigger, escalation, rollback/failback, and evidence location | Approved runbook plus live exercise evidence for the applicable profile |
| `GOV-INC-026` through `GOV-INC-031` | 1 | Before Production Promotion | Unresolved / Blocking - escalation owner required per entry | Operations; security/data-governance; communications or architecture/change as applicable | Unresolved / Blocking | Unresolved / Blocking | Accountable contacts, route, timing, fallback, scope, and review conditions | Tested route and attributable profile-scoped approval |
| `GOV-AUD-001` through `GOV-AUD-010` | 1 | Before Production Promotion | Unresolved / Blocking - independent audit owner required per entry | Independent security/data-governance approver; architecture/change; operations where applicable | Unresolved / Blocking | Unresolved / Blocking | Independent authority, access, purpose, export, redaction, integrity, retention, hold, backup, and review rules | Least-privilege tests, integrity evidence, self-audit evidence, and attributable approval |
| `GOV-TEL-001` through `GOV-TEL-005` | 1 | Before Production Promotion | Unresolved / Blocking - telemetry owner required per entry | Security/data-governance; telemetry owner; operations | Unresolved / Blocking | Unresolved / Blocking | Signal minimization, schema, access, bounded retention, diagnostics, export, purge, backup, and restore rules | Schema/redaction tests, access evidence, purge verification, and attributable approval |
| `GOV-OBJ-001` through `GOV-OBJ-014` | 1 | Before Production Promotion | Unresolved / Blocking - objective owner required per entry | Operations; affected data-service owner; security/data-governance; production authority | Unresolved / Blocking | Unresolved / Blocking | Numeric target, unit, window, signal, owner, alert, runbook, profile scope, and live measurement | Profile-scoped measurements, alert/runbook exercise, and attributable approval |
| `GOV-DEP-001` through `GOV-DEP-012` | 1 | Before Production Promotion | Unresolved / Blocking - deployment profile owner required per entry | Operations; affected data-service owner; security/data-governance; architecture/change | Unresolved / Blocking | Unresolved / Blocking | Authorized deployment and environment inputs for the exact profile version | Preflight, configuration, isolation, capacity, alert, escalation, and change-approval evidence |
| `GOV-SIGN-001` | 1 | Before Production Promotion | Unresolved / Blocking - authorized production-sign-off owner required | Security/data-governance; operations; architecture/change; authorized production authority | Unresolved / Blocking | Unresolved / Blocking | Exact approved register versions, profile version, copy inventory, controls, exercises, exceptions, residual risks, and outcomes | Complete current dependency set and attributable final sign-off for the exact profile scope |

### ACT-009 Authority Approval Packet

Packet `GOV-PKT-ACT-009-v1` requests resolution of `GOV-OWN-001` version 1
without assigning unauthorized people or treating architecture review as an
approval. Its requested decisions are:

| Decision concern | Required outcome | Current state |
| --- | --- | --- |
| Security and data-governance authority | Identify the authorized accountable assignee and the exact policy, data, risk, and exception decisions within scope | Unresolved / Blocking - authorized assignee required |
| Separation of duties | Define proposer, reviewer, approver, operator, and independent audit combinations that are prohibited or require additional review | Unresolved / Blocking - authorized security and architecture input required |
| Delegation | Define delegable actions, non-delegable approvals, delegate qualifications, scope, duration, revocation, and audit requirements | Unresolved / Blocking - delegation authority and evidence required |
| Review | Define ordinary review cadence and event-driven review triggers for authority, delegation, conflicts, and evidence | Unresolved / Blocking - bounded review decision required |
| Production sign-off scope | Define whether the role proposes, reviews, approves, rejects, or witnesses `GOV-SIGN`, and prohibit self-approval of its own exceptional access or risk proposal | Unresolved / Blocking - production-authority decision required |

The packet requires attributable outcomes from the authorized
security/data-governance authority, architecture/change approver, and production
authority for exact entry version `GOV-OWN-001` version 1 and the affected
profile scope. Approval evidence MUST identify each approver identity and role,
outcome, timestamp, rationale, evidence, conditions, and review or expiry
trigger. `GOV-OWN-001` remains `Unresolved / Blocking` and this packet remains
incomplete until authorized assignees and all required approval evidence are
recorded.

### Data-Class Ownership And Handling Entries

Each entry below is version 1, due `Before Production Promotion`, and remains
`Unresolved / Blocking` until its accountable owner and handling decisions are
approved for the exact linked policy version and profile scope.

| Entry | Source | Accountable owner | Access authority | Encryption and key responsibility | Residency and copy classes | Required audit evidence | Linked policy |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `GOV-OWN-002` | `DAT-001` | Unresolved / Blocking - authorized identity/domain owner required | Stamp identity services, authorized administrators, and team-scoped access require approval | Identity and storage authorities; mechanism, custody, and evidence unresolved | Approved stamp locations; Primary, Index, Replica, Backup, Audit, and applicable Telemetry copies require inventory approval | Authentication, authorization, role, registration, export, retention, and deletion | `POL-001` version unresolved |
| `GOV-OWN-003` | `DAT-002` | Unresolved / Blocking - credential authority owner required | Issuer and intended workload only; human-readable secret export prohibited | Issuer lifecycle and protected endpoint/device key authority; custody and mechanism unresolved | Protected endpoint or approved stamp locations; Primary, Transport, Audit, and narrowly justified Backup treatment | Creation, rotation, expiry, revocation, recovery, and exceptional access without secret values | `POL-002` version unresolved |
| `GOV-OWN-004` | `DAT-003` | Unresolved / Blocking - recording/data-service owner required | Team-authorized users and job-scoped services | Storage authority and short-lived grant issuer; mechanism and key custody unresolved | Approved stamp locations; Primary, Transport, Cache, Index, Telemetry, Replica, Backup, and Audit copies as applicable | Upload, finalization, grant, access, export, hold, retention, and deletion | `POL-003` version unresolved |
| `GOV-OWN-005` | `DAT-004` | Unresolved / Blocking - segment/cache owner required | Team-authorized requests and job-scoped processing | Segment storage/cache authority; mechanism and key custody unresolved | Source-approved locations; Primary, Cache, Index, Telemetry, Replica, Backup when justified, and Audit copies | Materialization, publication, access, corruption, expiry, and purge | `POL-004` version unresolved |
| `GOV-OWN-006` | `DAT-005` | Unresolved / Blocking - owning API/data-service owner required | Team-authorized API, Analysts, and agents by capability | Owning module and storage authorities; mechanism and key custody unresolved | Approved stamp locations; Primary, Transport, Index, Telemetry, Replica, Backup, and Audit copies | Acceptance, query authorization, export, supersession, hold, retention, and deletion | `POL-005` version unresolved |
| `GOV-OWN-007` | `DAT-006` | Unresolved / Blocking - Job Registry/messaging owner required | Scheduler, Job Registry, registered Managers, and scoped operators | Job Registry, messaging, and lease issuers; mechanism and key custody unresolved | Approved stamp-local locations; Primary, Transport, Cache, Telemetry, Replica, Backup, and Audit copies | State transition, claim, lease, retry, fencing, publication, expiry, and purge | `POL-006` version unresolved |
| `GOV-OWN-008` | `DAT-007` | Unresolved / Blocking - artifact authority owner required | Approved build, registry, platform, and execution authorities | Publisher, registry, signing, and authorization authorities; key custody unresolved | Approved artifact/evidence locations; Primary, Transport, Cache/mirror, Index, Telemetry, Replica, Backup, and Audit copies | Publication, provenance, vulnerability, exception, authorization, revocation, and purge | `POL-007` version unresolved |
| `GOV-OWN-009` | `DAT-008` | Unresolved / Blocking - Agent Orchestration owner required | Initiating user plus current team/match scope through API/MCP | Agent Orchestration Primary plus messaging, runtime, index, telemetry, replica, and backup authorities; mechanism and custody unresolved | Approved stamp locations; Primary, Transport, Cache, Index, Telemetry, Replica, Backup, and Audit copies | Invocation, delegation, claim, checkpoint, completion, query, tool call, evidence access, denial, export, hold, deletion, purge, and restore suppression | `POL-008` version unresolved |
| `GOV-OWN-010` | `DAT-009` | Unresolved / Blocking - independent audit owner required | Authorized security reviewers and narrowly scoped operators | Independent audit transport/store authority; mechanism and key custody unresolved | Approved evidence locations; Primary, Transport, Index, Replica, Backup, and Audit copies | Ingestion, access, export, integrity check, redaction, retention, deletion, and hold | `POL-009` version unresolved |
| `GOV-OWN-011` | `DAT-010` | Unresolved / Blocking - telemetry authority owner required | Purpose-limited operational and diagnostic roles | Telemetry pipeline and diagnostic-store authorities; mechanism and key custody unresolved | Approved telemetry locations; Transport, Cache, Index, Telemetry, Replica, Backup, and Audit copies as applicable | Schema/redaction, exceptional capture/access, export, retention, and purge | `POL-010` version unresolved |

No entry above can satisfy production readiness while its owner, handling
authority, copy treatment, evidence, or linked `GOV-POL-*` version remains
unresolved.

### Threat Residual-Risk Entries

These entries record ownership and disposition only. They do not alter the
threat statement, affected boundaries, required controls, detection evidence,
or `Open / Blocking` disposition in the Threat Model.

| Entry | Source | Accountable residual-risk owner | Required acceptance evidence | Acceptance authority | Current disposition |
| --- | --- | --- | --- | --- | --- |
| `GOV-THR-001` | `THR-001`; `CTL-001`, `CTL-012` | Unresolved / Blocking | Stamp-isolation configuration, denial, privileged-access, and isolation-test evidence | Security/data-governance; operations; architecture/change; production authority | Unresolved / Blocking |
| `GOV-THR-002` | `THR-002`; `CTL-009`, `CTL-012` | Unresolved / Blocking | Least-privilege, separation-of-duties, independent review, export, hold, deletion, and privileged-action audit evidence | Independent security/data-governance; architecture/change; production authority | Unresolved / Blocking |
| `GOV-THR-003` | `THR-003`; `CTL-002`, `CTL-003`, `CTL-009` | Unresolved / Blocking | Issuance, proof binding, lifetime, detection, revocation, recovery, and anomalous-access evidence without secret values | Security/data-governance; credential authority; operations | Unresolved / Blocking |
| `GOV-THR-004` | `THR-004`; `CTL-004`, `CTL-006`, `CTL-011` | Unresolved / Blocking | Digest, signature/provenance, vulnerability discovery, authorization, remediation, bounded exception, and revocation evidence | Security/data-governance; artifact authority; operations; production authority | Unresolved / Blocking |
| `GOV-THR-005` | `THR-005`; `CTL-004`, `CTL-009` | Unresolved / Blocking | Runtime isolation preflight, patch/support policy, launch denial, mount/device policy, and abnormal-exit evidence | Security/data-governance; runtime owner; operations; production authority | Unresolved / Blocking |
| `GOV-THR-006` | `THR-006`; `CTL-005`, `CTL-006`, `CTL-009` | Unresolved / Blocking | Lease, fencing, idempotency, stale-callback rejection, digest, lineage, integration, and failure-test evidence | Security/data-governance; Job Registry owner; architecture/change | Unresolved / Blocking |
| `GOV-THR-007` | `THR-007`; `CTL-007`, `CTL-009` | Unresolved / Blocking | Initiating-user context, team/match authorization, revocation, boundary, delegation, evidence-access, and denial-test evidence | Security/data-governance; API/MCP owner; architecture/change | Unresolved / Blocking |
| `GOV-THR-008` | `THR-008`; `CTL-008`, `CTL-009`, `CTL-010` | Unresolved / Blocking | Telemetry schema, minimization, secret scanning, redaction, exceptional-capture access, retention, and purge evidence | Security/data-governance; telemetry owner; operations | Unresolved / Blocking |
| `GOV-THR-009` | `THR-009`; `CTL-009`, `CTL-010` | Unresolved / Blocking | Deletion authorization, copy inventory, inaccessible-first handling, purge, tombstone, backup expiry, hold, and restore-suppression evidence | Security/data-governance; affected data owner; operations; production authority | Unresolved / Blocking |
| `GOV-THR-010` | `THR-010`; `CTL-001`, `CTL-008`, `CTL-010`, `CTL-012` | Unresolved / Blocking | Shared-service isolation, encryption, residency, access-denial, inventory, operator, backup, replica, cache, and telemetry evidence | Security/data-governance; operations; architecture/change; production authority | Unresolved / Blocking |
| `GOV-THR-011` | `THR-011`; `CTL-009`, `CTL-010` | Unresolved / Blocking | Complete copy reconciliation, orphan detection, expiry, purge verification, restore suppression, and unsupported-copy failure evidence | Security/data-governance; operations; affected data owners; production authority | Unresolved / Blocking |

Only attributable acceptance or rejection by every listed authority for the
exact entry version and profile scope can change an entry disposition. Missing
evidence or ownership leaves both the register entry and original threat row
blocking.

### Ownership Decision Outcome Log

The current review has no authorized assignee, owner, residual-risk acceptance,
or approval outcome for the entries below. No proposal has been submitted, so
there is no rejected value to adopt or preserve; any later rejection MUST
record the rejected proposal, rejecting identity and role, timestamp, reason,
evidence, entry version, and profile scope.

| Entry | Authoritative input availability | Proposal or rejection record | Current status |
| --- | --- | --- | --- |
| `GOV-OWN-001` | Unavailable - authorized ACT-009 authority assignments and scope required | No proposal submitted; no rejection recorded | Unresolved / Blocking |
| `GOV-OWN-002` | Unavailable - authorized `DAT-001` owner and handling decision required | No proposal submitted; no rejection recorded | Unresolved / Blocking |
| `GOV-OWN-003` | Unavailable - authorized `DAT-002` owner and handling decision required | No proposal submitted; no rejection recorded | Unresolved / Blocking |
| `GOV-OWN-004` | Unavailable - authorized `DAT-003` owner and handling decision required | No proposal submitted; no rejection recorded | Unresolved / Blocking |
| `GOV-OWN-005` | Unavailable - authorized `DAT-004` owner and handling decision required | No proposal submitted; no rejection recorded | Unresolved / Blocking |
| `GOV-OWN-006` | Unavailable - authorized `DAT-005` owner and handling decision required | No proposal submitted; no rejection recorded | Unresolved / Blocking |
| `GOV-OWN-007` | Unavailable - authorized `DAT-006` owner and handling decision required | No proposal submitted; no rejection recorded | Unresolved / Blocking |
| `GOV-OWN-008` | Unavailable - authorized `DAT-007` owner and handling decision required | No proposal submitted; no rejection recorded | Unresolved / Blocking |
| `GOV-OWN-009` | Unavailable - authorized `DAT-008` owner and handling decision required | No proposal submitted; no rejection recorded | Unresolved / Blocking |
| `GOV-OWN-010` | Unavailable - authorized `DAT-009` owner and handling decision required | No proposal submitted; no rejection recorded | Unresolved / Blocking |
| `GOV-OWN-011` | Unavailable - authorized `DAT-010` owner and handling decision required | No proposal submitted; no rejection recorded | Unresolved / Blocking |
| `GOV-THR-001` | Unavailable - accountable `THR-001` owner, evidence, and disposition required | No proposal submitted; no rejection recorded | Unresolved / Blocking |
| `GOV-THR-002` | Unavailable - accountable `THR-002` owner, evidence, and disposition required | No proposal submitted; no rejection recorded | Unresolved / Blocking |
| `GOV-THR-003` | Unavailable - accountable `THR-003` owner, evidence, and disposition required | No proposal submitted; no rejection recorded | Unresolved / Blocking |
| `GOV-THR-004` | Unavailable - accountable `THR-004` owner, evidence, and disposition required | No proposal submitted; no rejection recorded | Unresolved / Blocking |
| `GOV-THR-005` | Unavailable - accountable `THR-005` owner, evidence, and disposition required | No proposal submitted; no rejection recorded | Unresolved / Blocking |
| `GOV-THR-006` | Unavailable - accountable `THR-006` owner, evidence, and disposition required | No proposal submitted; no rejection recorded | Unresolved / Blocking |
| `GOV-THR-007` | Unavailable - accountable `THR-007` owner, evidence, and disposition required | No proposal submitted; no rejection recorded | Unresolved / Blocking |
| `GOV-THR-008` | Unavailable - accountable `THR-008` owner, evidence, and disposition required | No proposal submitted; no rejection recorded | Unresolved / Blocking |
| `GOV-THR-009` | Unavailable - accountable `THR-009` owner, evidence, and disposition required | No proposal submitted; no rejection recorded | Unresolved / Blocking |
| `GOV-THR-010` | Unavailable - accountable `THR-010` owner, evidence, and disposition required | No proposal submitted; no rejection recorded | Unresolved / Blocking |
| `GOV-THR-011` | Unavailable - accountable `THR-011` owner, evidence, and disposition required | No proposal submitted; no rejection recorded | Unresolved / Blocking |

Only complete attributable approval evidence for the exact entry version and
profile scope can change one of these entries from `Unresolved / Blocking`.
Architecture review, another entry's approval, a different version, or a
different profile scope cannot change status.

### Lifecycle Policy Approval Packets

Each packet is version 1, due `Before Production Promotion`, and remains
`Unresolved / Blocking`. Every duration or deadline below requires an approved
bounded value and unit; until then the explicit unresolved marker is the value.

#### POL-001 Identity, Authorization, And Registration

| Required decision | Version 1 value |
| --- | --- |
| Active retention | Unresolved / Blocking - bounded period and trigger required |
| Post-revocation retention | Unresolved / Blocking - bounded period, purpose, and review required |
| Access removal | Unresolved / Blocking - trigger and bounded completion deadline required |
| Active-copy purge | Unresolved / Blocking - Primary, Index, Cache, Telemetry, Replica, and Audit treatment plus bounded deadline required |
| Backup expiry | Unresolved / Blocking - bounded expiry and deletion verification required |
| Restore suppression | Unresolved / Blocking - current revocation/deletion authority and verification procedure required |
| Holds | Unresolved / Blocking - eligibility, maximum duration, renewal, approval, and no-authority-restoration rule required |
| Accountable owner | Unresolved / Blocking - authorized identity/domain owner required |
| Required approvals | Product/business; security/data-governance; identity/data-service owner; architecture/change |
| Approval evidence | Unresolved / Blocking - exact `GOV-POL-001` version, outcomes, timestamps, rationale, evidence, conditions, profile scope, and review/expiry required |

Every field in this packet is either an explicit unresolved blocker or, after
authorized review, an approved bounded value. A missing field cannot be
interpreted as inherited, indefinite, or not applicable.

#### POL-002 Keys, Tokens, Grants, And Credentials

| Required decision | Version 1 value |
| --- | --- |
| Issuance | Unresolved / Blocking - issuing authority, eligibility, scope, proof binding, and audit event required |
| Lifetime | Unresolved / Blocking - bounded lifetime and clock basis required for each credential class |
| Renewal | Unresolved / Blocking - eligibility, maximum extension, reauthentication, and evidence required |
| Rotation | Unresolved / Blocking - trigger, bounded completion period, overlap, and verification required |
| Immediate revocation | Unresolved / Blocking - revocation authority, propagation deadline, denial behavior, and evidence required |
| Destruction evidence | Unresolved / Blocking - active, cached, transported, and persisted secret destruction verification required |
| Non-secret audit retention | Unresolved / Blocking - bounded event retention and access policy required without secret values |
| Recovery | Unresolved / Blocking - recovery authority, identity proof, replacement, invalidation, and audit requirements |
| Backup exclusion | Unresolved / Blocking - proof that secret authority is excluded or irreversibly unusable after restore required |
| Holds | Not permitted for active secret authority; only separately governed non-secret audit evidence may be held |
| Accountable owner | Unresolved / Blocking - authorized credential/key authority required |
| Required approvals | Security/data-governance; credential/key authority; affected technical/data-service owner; architecture/change |
| Approval evidence | Unresolved / Blocking - exact `GOV-POL-002` version and credential-class scope required |

Secret authority MUST NOT be retained by a hold, restored from backup, or
reactivated through recovery. Recovery creates replacement authority and keeps
the prior credential revoked.

#### POL-003 Recordings And Timeline Mappings

`GOV-POL-003` version 1 is `Unresolved / Blocking` for active, superseded, and
requested-deletion retention; access-removal and purge-propagation deadlines;
Primary, Transport, Cache, Index, Telemetry, Replica, Backup, and Audit copy
treatment; backup expiry; restore suppression; expiring hold eligibility,
maximum duration, renewal, and approval; accountable recording/storage owner;
product/business, security/data-governance, data-service, and
architecture/change approval; and exact-version evidence. Every duration and
deadline requires an approved bounded value.

#### POL-004 Segments And Media Caches

`GOV-POL-004` version 1 is `Unresolved / Blocking` for maximum segment/cache
age; source-deletion, authorization-loss, corruption, and expiry triggers;
bounded propagation and purge verification across Primary, Cache, Index,
Telemetry, Replica, Backup, and Audit copies; backup justification and expiry;
owner; approvals; and evidence. Backup is permitted only with explicit approved
need, and an indefinite cache or unsupported backup remains blocking.

#### POL-005 Facts, Results, And Manifests

`GOV-POL-005` version 1 is `Unresolved / Blocking` for active, superseded,
source-dependent, and requested-deletion retention; access removal; active,
Index, Telemetry, Replica, and Backup purge; backup expiry; restore suppression;
expiring holds; owner; approvals; and verification. A derived index or replica
MUST NOT outlive the approved source rule unless an explicit bounded basis and
joint approval are recorded.

#### POL-006 Jobs, Attempts, Outbox, Leases, And Queues

`GOV-POL-006` version 1 is `Unresolved / Blocking` for terminal-job, failed-work,
and publication-evidence retention; lease, attempt, queue, Transport, and Cache
expiry/purge; bounded audit evidence; restore suppression; owner; approvals;
and verification. Recovery MUST NOT reactivate an expired lease, credential,
grant, attempt, queue item, or other authority.

#### POL-007 Artifacts And Provenance

`GOV-POL-007` version 1 is `Unresolved / Blocking` for authorized, superseded,
vulnerable, and evidence retention; immediate execution revocation; mirror and
Cache purge; bounded exception scope and expiry; recovery preservation of
authorization/revocation state; owner; approvals; and evidence. A vulnerable
artifact can remain executable only under an explicit bounded, audited,
approved exception.

#### POL-008 Agent Orchestration

`GOV-POL-008` version 1 is `Unresolved / Blocking` for conversation, invocation,
evidence, lineage, and requested-deletion retention; access removal; tombstone
creation and propagation through PostgreSQL Primary, outbox, JetStream
Transport, Hermes Cache, Index, Telemetry, Replica, Backup, and Audit copies;
backup expiry; restore suppression; expiring holds; owner; approvals; and
verification. Every Agent Orchestration copy is in scope.

#### POL-009 Audit Evidence

`GOV-POL-009` version 1 is `Unresolved / Blocking` for per-event-class
retention, integrity-verification cadence, access, export, minimization,
redaction, deletion, Backup treatment, narrowly scoped expiring holds, owner,
approvals, and evidence. Deletion or redaction requires separate authorization
and an audit event; the decision cannot approve itself.

#### POL-010 Telemetry And Diagnostics

`GOV-POL-010` version 1 is `Unresolved / Blocking` for per-signal log, metric,
and trace retention; exceptional diagnostic purpose, scope, access, and bounded
capture period; redaction; purge; approved aggregate preservation; Backup and
restore treatment; owner; approvals; and evidence. Secret-bearing capture and
unbounded diagnostic retention cannot be approved.

#### Joint Lifecycle Consistency Review

Packet `GOV-PKT-LIFECYCLE-v1` compares `GOV-POL-001` through `GOV-POL-010`
across Primary, Transport, Cache, Index, Telemetry, Replica, Backup, and Audit
copies. It requires consistent source-retention bounds, authorization-loss and
deletion triggers, propagation deadlines, backup expiry, restore suppression,
hold eligibility and maximum duration, and evidence ownership. The review is
`Unresolved / Blocking`: every policy value is unavailable, so no consistency
approval is recorded. Any missing or contradictory relationship remains
production-blocking and must identify the affected entry versions before joint
approval.

### Encryption, Key, Credential, And Residency Packets

#### Encryption Entries

All entries are version 1 and `Unresolved / Blocking`; mechanism, owner, and
deployment evidence are unavailable.

| Entry | Source copy or boundary | Required decision and evidence |
| --- | --- | --- |
| `GOV-ENC-001` | Primary | At-rest mechanism, owner, keys, access, rotation, recovery, destruction, configuration and test evidence |
| `GOV-ENC-002` | Transport and every untrusted/shared boundary | Authenticated in-transit mechanism, endpoint identity, owner, downgrade denial, configuration and test evidence |
| `GOV-ENC-003` | Cache | At-rest mechanism or approved non-persistence basis, owner, key lifecycle, purge and test evidence |
| `GOV-ENC-004` | Index | At-rest mechanism, owner, key lifecycle, source linkage, purge and test evidence |
| `GOV-ENC-005` | Telemetry | In-transit and at-rest mechanisms, owner, key lifecycle, redaction and test evidence |
| `GOV-ENC-006` | Replica | Source-equivalent mechanism, owner, independent access, key lifecycle and test evidence |
| `GOV-ENC-007` | Backup | At-rest and transfer mechanisms, owner, exceptional access, recovery, destruction and restore-test evidence |
| `GOV-ENC-008` | Audit | Transport/at-rest mechanisms, independent owner, integrity, access, key lifecycle and verification evidence |

#### Key Governance

`GOV-ENC-001` through `GOV-ENC-008` also require approved generation, custody,
per-stamp logical separation, least-privileged access, bounded rotation,
recovery, destruction, exceptional access, audit, and evidence decisions.
Cross-stamp key, recovery, or exceptional-access authority is prohibited and
must be rejected rather than recorded as a candidate approval.

#### Credential-Class Entries

| Entry | Credential class | Required unresolved decisions |
| --- | --- | --- |
| `GOV-CRED-001` | Human sessions | Issuance, bounded lifetime, renewal, revocation, recovery |
| `GOV-CRED-002` | Device keys | Protected issuance, non-exportability, rotation, revocation, replacement |
| `GOV-CRED-003` | Manager/workload tokens | Proof binding, bounded lifetime, renewal, immediate revocation, recovery |
| `GOV-CRED-004` | NATS credentials | Stamp/subject scope, bounded lifetime, rotation, revocation, recovery |
| `GOV-CRED-005` | Object grants | Object/action scope, bounded lifetime, one-time or replay rules, revocation |
| `GOV-CRED-006` | External identity credentials | Trust scope, lifetime, renewal, rotation, revocation, provider failure |
| `GOV-CRED-007` | Signing keys | Issuance, custody, rotation, revocation, destruction, verification continuity |
| `GOV-CRED-008` | Encryption keys | Generation, custody, rotation, recovery, revocation, destruction |
| `GOV-CRED-009` | Recovery credentials | Exceptional issuance, dual control, bounded lifetime, rotation, use and destruction |

Every row is version 1, `Before Production Promotion`, and `Unresolved /
Blocking`; no issued credential class has an approved lifetime or revocation
rule.

#### Residency And Movement Entries

| Entry | Source | Required unresolved decision |
| --- | --- | --- |
| `GOV-RES-001` | `DAT-001` | Permitted Primary, Index, Replica, Backup, Audit, operator and legal-boundary locations/movements |
| `GOV-RES-002` | `DAT-002` | Protected endpoint/stamp locations and movement prohibition for secret authority |
| `GOV-RES-003` | `DAT-003` | Recording Primary, Cache, Index, Telemetry, Replica, Backup and Audit locations/movements |
| `GOV-RES-004` | `DAT-004` | Segment/cache locations, source alignment, replicas and justified backups |
| `GOV-RES-005` | `DAT-005` | Fact/result Primary, Index, Telemetry, Replica, Backup and Audit locations/movements |
| `GOV-RES-006` | `DAT-006` | Stamp-local Primary/Transport plus Cache, Replica, Backup and Audit treatment |
| `GOV-RES-007` | `DAT-007` | Registry, mirror, evidence, Replica and Backup locations/movements |
| `GOV-RES-008` | `DAT-008` | PostgreSQL, JetStream, Hermes, Index, Telemetry, Replica, Backup and Audit locations |
| `GOV-RES-009` | `DAT-009` | Independent audit, Index, Replica and Backup evidence locations/movements |
| `GOV-RES-010` | `DAT-010` | Telemetry pipeline, diagnostic, Index, Replica, Backup and Audit locations/movements |

All residency entries are version 1 and `Unresolved / Blocking`. No provider,
geography, operator, or legal boundary is selected; authorized business/legal,
security/data-governance, and operations input is required.

#### Encryption And Residency Approval Outcome

Required security/data-governance, technical/data-service, operations, and
authorized business/legal decisions are unavailable. No approval or rejection
is recorded. Only attributable outcomes naming the exact entry version and
production-profile scope can change these entries from `Unresolved / Blocking`.

### Vulnerability, Incident, Audit, And Telemetry Packets

#### Vulnerability Packet

`GOV-VUL-001` through `GOV-VUL-009` version 1 cover intake sources, triage,
affected-artifact discovery, severity/exploitability, remediation expectations,
execution authorization/revocation, bounded exception scope/expiry, evidence
retention, owners and approvers. Every decision is `Unresolved / Blocking`; an
exception requires scope, rationale, compensating controls, expiry, approval,
and audit evidence.

#### Security-Incident Packet

`GOV-INC-001` through `GOV-INC-012` version 1 cover declaration, containment,
evidence preservation, notification, exceptional access, communications,
recovery, residual-risk acceptance, contacts, service objectives, escalation,
and runbook authority. Every assignment is `Unresolved / Blocking`, so missing
authority continues to fail production.

#### Audit-Governance Packet

`GOV-AUD-001` through `GOV-AUD-010` version 1 cover independent authority,
ordinary and exceptional access, purpose/export, redaction, integrity
verification, self-auditing, retention, holds/backup, owner, review/expiry and
approvals. Every decision is `Unresolved / Blocking`; no access decision may
approve itself or omit bounded review/expiry.

#### Telemetry-Governance Packet

`GOV-TEL-001` through `GOV-TEL-005` version 1 separately govern logs, metrics,
traces, health signals, and diagnostics. Each covers minimization,
schema/redaction, purpose, access, bounded retention, exceptional capture,
export, purge, backup, restore, owner and approvals. Every signal remains
`Unresolved / Blocking`.

#### Control-Governance Approval Outcome

Required vulnerability, incident, audit, and telemetry approvals are
unavailable; no rejected proposal is recorded. Architecture review and
tabletop fixtures are planning evidence only and cannot satisfy production
control or live-evidence gates.

### Operational Values, Objectives, And Sign-Off

#### Operational Owners

`GOV-OWN-012` through `GOV-OWN-026` map respectively to incident commander,
operations, PostgreSQL, JetStream, object storage, security/data governance,
communications, architecture/change, audit, telemetry, vulnerability response,
RPO, RTO, profile approval, and production sign-off. Each version 1 entry is
`Unresolved / Blocking` pending an authorized accountable assignee and
attributable profile-scoped approval.

#### Deployment Profile Values

`GOV-DEP-001` through `GOV-DEP-012` map respectively to host OS/architecture,
engine/Compose versions, ports/exposure, DNS, certificate issuer, secret source,
volume paths, replica counts, resource reservations/sizes, configuration
versions, alert routes, and escalation routes. Each is version 1,
profile-scoped, cloud-neutral, and `Unresolved / Blocking`; symbolic fixtures
cannot supply a value.

#### Operational Objectives

`GOV-OBJ-001` through `GOV-OBJ-014` map respectively to stamp availability, API
health, API latency, job acceptance, job claim, job completion, outbox
publication lag, queue oldest-work age, queue drain time, backup freshness,
restore RTO, recoverable-data RPO, capacity, and alert thresholds. Each entry
requires target, unit, window, signal, owner, alert, versioned runbook,
approval, profile scope, and live evidence. All are version 1 and `Unresolved /
Blocking`.

#### Runbooks And Escalations

`GOV-INC-013` through `GOV-INC-025` are the versioned runbooks, in source order,
for production preflight, backup failure, isolated restore, whole-host/Compose
loss, PostgreSQL outage/corruption, JetStream loss/backlog, object-storage
outage/corruption/capacity, control-plane failure, Manager/runtime loss,
credential compromise, telemetry loss, lifecycle-policy failure, and
stamp-isolation failure. `GOV-INC-026` through `GOV-INC-031` are the security,
capacity, dependency, recovery, communications, and change/production
escalation routes. Alerts and objectives MUST reference these exact versions.
Every owner, route, and live exercise remains `Unresolved / Blocking`; none is
presented as ready.

#### Deployment Evidence Outcome

Deployment-authority decisions and live evidence for profile values, RPO/RTO,
objectives, capacity, alerts, owners, runbooks and escalations are unavailable.
All corresponding entries remain visibly `Unresolved / Blocking`; symbolic
fixtures and tabletop exercises cannot satisfy them.

#### GOV-SIGN

`GOV-SIGN-001` version 1 is a profile-scoped derived decision referencing exact
approved register versions, the complete copy inventory, control evidence,
current exercises, accepted exceptions, residual-risk dispositions, and
attributable approver outcomes. It cannot approve, replace, waive, or override
an underlying blocker.

Final production sign-off may be requested only when every applicable
dependency is approved, current, consistent, evidenced, and adopted by the same
profile version. Any missing, stale, contradictory, expired, superseded, or
unsigned input leaves `GOV-SIGN-001` and production readiness `Unresolved /
Blocking`.

#### Architecture Decision Status

No register entry is `Approved`, and no entry has complete alternatives,
consequences, implementation or exercise evidence. All decisions remain in the
register at their actual `Unresolved / Blocking` status. A decision can become
current policy only through the approval and evidence rules in this document.

### Status And Disposition Rules

The allowed statuses are `Unresolved / Blocking`, `Proposed`, `In Review`,
`Approved`, `Rejected`, `Superseded`, and `Expired`. Only a current `Approved`
entry version can satisfy its blocking gate. `Proposed` and `In Review` remain
blocking. `Rejected` records an attributable rejected proposal and its reason;
it does not supply a value. `Superseded` and `Expired` versions remain
immutable evidence but cannot authorize implementation or production.

`Not Applicable` is a disposition recorded in the approved-value field, not a
status and never a blank cell. It requires an accountable owner, scope-specific
rationale, evidence that the obligation cannot apply, and attributable
security/data-governance and architecture/change approval for the exact entry
version and profile scope. An unsupported `Not Applicable` disposition remains
`Unresolved / Blocking`.

### Decision Stages And Escalation

`Before Development` means an affected implementation boundary MUST NOT proceed
until the entry is approved because the implementation cannot remain reversible
and policy-parameterized. `Before Production Promotion` means development MAY
continue while the implementation accepts an externally supplied policy or
profile value, but production data, ingress, work intake, and production-ready
claims remain blocked.

All currently unresolved architecture and deployment placeholders begin at
`Before Production Promotion`. Before implementation embeds a policy value,
selects a hard-to-reverse mechanism, or makes a data shape depend on an
unapproved choice, the accountable proposer MUST escalate the entry to
`Before Development`. Changing an entry from `Before Development` to
`Before Production Promotion` requires attributable security/data-governance and
architecture/change approval with evidence that the implementation remains
reversible and policy-parameterized.

For example, implementing a configurable retention scheduler whose duration is
supplied by an approved profile is permitted while the duration entry remains
`Before Production Promotion`. Encoding a fixed purge duration into persisted
data or selecting a non-replaceable key boundary is blocked until the relevant
entry is escalated to and approved at `Before Development`.

### Review, Expiry, Approval, And Supersession

An approval is attributable only when it identifies the entry identifier and
version, approver identity and role, outcome, timestamp, rationale, evidence,
conditions, expiry or review trigger, and affected profile scope. Approvals for
different versions or scopes do not combine. Missing, symbolic, fixture-derived,
or architecture-review-only evidence cannot approve a production value.

Every approved entry MUST define a bounded expiry or event-driven review
trigger. Review is required when evidence expires, profile scope changes, a
dependency is replaced, a control or exercise materially changes, an exception
expires, or contradictory information is discovered. Until reapproval, the
affected entry returns to a blocking review state and its consuming gate fails.

Supersession creates a new entry version that identifies the immutable prior
version. The prior version changes to `Superseded` only after the replacement
outcome is recorded; approval history is never rewritten. A replacement does
not inherit approval or evidence automatically, and dependent entries,
profiles, and `GOV-SIGN` MUST be reviewed against the new exact version.

## Validation And Promotion Evidence

Automated architecture checks MUST verify stable identifier uniqueness,
required policy fields, source-link resolution, storage-copy coverage, fixture
references, and the selected active-purge/backup-expiry semantics. Fixtures are
noncanonical policy examples and MUST NOT be treated as API or storage schemas.

Manual security and architecture review MUST confirm that:

- every actor, asset, trust boundary, storage class, and plausible threat is
  represented or explicitly excluded with rationale
- every threat maps to existing or required controls, detection evidence,
  residual risk, disposition, and accountable ownership
- every data class covers encryption, access, retention, deletion, residency,
  audit, replicas, backups, and holds
- existing point controls retain their documented decision status
- [Production Deployment and Operations](production-operations.md), agent
  persistence from [Intelligence and Agents](intelligence-and-agents.md),
  and the accepted Coach Client local-data decisions are referenced without
  resolving deployment-specific production values implicitly

Production acceptance requires named accountable owners, approved policy
values, serious evaluation of alternatives, threat and privacy review,
implementation and exercise evidence for required controls, verified deletion
and restore-suppression behavior across every deployed copy class, and explicit
acceptance of residual risks. Until then this topic remains Provisional and
production-blocking gaps remain visible.

The 2026-09-16 architecture review confirmed coverage of the documented actors,
assets, trust boundaries, threats, controls, data classes, and copy classes.
Automated structural and fixture validation passed. Named owners, approved
policy values, implementation evidence, operational exercises, and residual-
risk acceptance remain outstanding and continue to block production.

## Accepted Coach Client Local-Data Boundary

The accepted desktop boundary adds a user-scoped encrypted local replica; it
does not change the server-side authority or production readiness status above.
Electron main owns encrypted SQLite and the per-installation key protected by
the OS credential store. Renderer code uses narrow typed IPC and receives no
database path, SQL capability, or key.

REST delta synchronization and offline entitlement are bound to the current
user, one club stamp, authorized teams and matches, roles, policy version, and
compatible contract version. Reconciliation hides unauthorized or deleted data
before purging database, WAL/temp, media, thumbnail, index, and key copies.
Complete recordings are excluded; local media is limited to explicitly
downloaded clips.

Every local data class requires a versioned bounded retention value before it
is enabled. Sign-out policy, entitlement/policy expiry, authorization loss,
source deletion, quota, device reset, and explicit removal trigger
inaccessible-first lifecycle handling and verifiable purge. Holds are narrow,
approved, expiring, and never restore access or preserve secret keys.
Diagnostics exclude tokens, keys, entitlement payloads, database/media
payloads, and durable access URLs. Evidence and approvals are recorded in
[Client Architecture Decision Evidence](client-decision-evidence.md).
