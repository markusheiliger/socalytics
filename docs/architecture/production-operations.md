# Production Deployment and Operations (Provisional)

This topic is the authoritative operational architecture for SocAlytics
production deployment, recovery, observability, capacity, incidents, upgrades,
and rollback. It defines the initial production profile and the evidence gates
that a deployment stamp must satisfy before accepting production data.

The architecture remains **Provisional / Blocking production**. Docker Compose
is the selected initial profile, but no profile is production-ready until its
owners, values, approvals, and live exercise evidence are complete. This topic
does not provide deployment manifests or select a cloud provider.

## Scope and Invariants

The initial production profile operates the platform services and
Compose-managed PostgreSQL, NATS JetStream, and S3-compatible storage. .NET
Aspire remains the local-development composition and is not a production
orchestrator.

Every profile preserves these existing invariants:

- exactly one club belongs to each deployment stamp
- every stamp has dedicated logical data, messaging, credentials,
  configuration, networks, and volumes
- PostgreSQL, the Job Registry, and Agent Orchestration are authoritative for
   their domain and workflow state; NATS is transport only
- the control plane never proxies video bytes
- execution remains at least once, idempotent, and attempt-fenced
- production artifacts and results use immutable identities and digests
- all copies follow
  [Security and Data Governance](security-and-data-governance.md)

Agent Orchestration owns conversation, logical invocation, consultation,
attempt, accepted advice, lineage, tombstone, and outgoing-event state in stamp
PostgreSQL. Hermes is a separate recoverable OCI runtime with no direct database
access. Its JetStream notifications are bounded Transport copies reconstructed
from PostgreSQL and the outbox. A later production profile may use externally
operated state services or another orchestrator only after separate
architecture and evidence meet the same requirements.

## Versioned Production Profile

Each stamp references one immutable profile version. A profile records:

| Area | Required declaration |
| --- | --- |
| Identity | Profile ID and version, status, stamp ID, change history |
| Ownership | Operations, data-service, security, communications, and change-approval roles |
| Host | OS, architecture, container engine and Compose versions, placement and support evidence |
| Deployment units | Image digest, replicas, dependencies, startup order, health and readiness contract |
| Network | Ingress, internal networks, egress, DNS, TLS, exposed and denied endpoints |
| Configuration | Non-secret source, schema/version, validation, reload or restart behavior |
| Secrets | Source, scope, delivery, rotation, revocation, recovery, and audit behavior |
| Persistence | Volume, authority, durability, encryption, capacity, residency, retention, backup and recovery |
| Objectives | Numeric value, unit, window, signal, owner, approval, alert and evidence |
| Evidence | Preflight, restore, failure, capacity, upgrade, rollback and incident exercise references |

The initial profile has these fixed architecture choices:

- Docker Compose is the production orchestrator.
- PostgreSQL, JetStream, and S3-compatible storage are Compose-managed
  services with durable volumes.
- One Compose project namespace identifies one deployment stamp.
- Service-to-service traffic uses stamp-internal networks. Only declared
  ingress and required external endpoints may cross that boundary.
- Shared physical hosts are permitted only when project namespaces, networks,
  volumes, credentials, databases, streams, buckets, and resource reservations
  remain isolated and independently testable.

Host OS, engine versions, concrete ports, DNS names, certificate issuer,
secret source, volume paths, replica counts, and resource sizes remain profile
values. They are **Open / Blocking** until an accountable owner approves them
with evidence. Undocumented public exposure, plaintext transport over an
untrusted or shared boundary, a cross-stamp credential, or a missing health
contract fails preflight.

### Security-Governance Register Adoption

Each immutable production-profile version references one exact version set from
the [Security-Governance Decision Register](security-and-data-governance.md#security-governance-decision-register).
The profile records applicability, approved values or approved not-applicable
dispositions, owners, evidence, expiry/review state, and dependency consistency.
It MUST NOT copy unresolved values into profile defaults or treat architecture
review, symbolic fixtures, or tabletop exercises as production approval.

Production readiness consumes only current `Approved` entry versions adopted by
the same profile version. Missing, `Unresolved / Blocking`, rejected, expired,
superseded, contradictory, unevidenced, or scope-mismatched entries fail
preflight and keep ingress and work intake closed.

`GOV-SIGN-001` is the profile-scoped derived production-sign-off decision. It
references the adopted register versions, copy inventory, control evidence,
live exercises, exceptions, and residual-risk dispositions; it cannot waive or
approve an underlying blocker. Docker Compose remains the Provisional initial
profile, and this adoption gate does not change existing authority boundaries
or resolve any currently open value.

## Environment and Preflight Contract

The local Aspire and production Compose topologies must map every logical
component, dependency, configuration key, health signal, and telemetry source.
The mapping does not require identical orchestration or secret delivery.

Production preflight verifies, without accepting user traffic or work:

1. Profile identity, approvals, host support, image digests, and configuration
   versions match the intended release.
2. Every application, Analyst-profile, image, model, package, and generated
   artifact has current authorization, signature or attestation, provenance,
   vulnerability, revocation, and bounded-exception evidence as applicable.
3. Project namespace, networks, volumes, credentials, database, streams, and
   buckets are dedicated to the stamp.
4. Only declared ingress and egress paths exist; DNS and authenticated TLS
   terminate at documented boundaries.
5. Secret material is available to its minimum scope and is absent from image
   layers, command arguments, ordinary logs, and durable queue payloads.
6. Authenticated encryption is active on every required transport boundary and
   every persistent Primary, Cache, Index, Replica, Backup, Audit, and Telemetry
   copy; key scope, access, rotation, and recovery evidence is current.
7. PostgreSQL, JetStream, object storage, the control plane, Hermes/MCP, and
   telemetry dependencies pass their health and readiness checks.
8. Durable volume capacity, host CPU and memory, database connections, broker
   limits, object capacity, and configured concurrency satisfy approved
   reservations.
9. Sampled access, role, credential, artifact, attempt, policy, export,
   deletion, retention, hold, restore, and security actions emit minimized,
   append-protected audit events; audit access is itself attributable.
10. Backup freshness, restore evidence, lifecycle policy values, objectives,
   alert routes, runbooks, and accountable roles are current.

Any failed or unresolved production gate keeps ingress and work intake closed.

### Required Control Evidence

The following matrix makes production acceptance traceable for every control
whose status is `Required`. An unresolved owner, policy value, exception,
exercise, or evidence reference remains blocking and cannot be waived by
`GOV-SIGN-001`.

<!-- markdownlint-disable MD013 -->

| Control | Preflight checks | Required live exercise or inspection | Governance entries and accountable roles | Retained evidence | Blocking outcome |
| --- | --- | --- | --- | --- | --- |
| `CTL-008` | Steps 4-6 and 10 verify authenticated transport, encryption for every persistent copy class, key separation, and recovery | Inspect each copy and transport class; restore encrypted backups without bypassing key controls | `GOV-ENC-001` through `GOV-ENC-008`; approved copy owners, security/data-governance, operations, production authority | Configuration, key-scope, access, rotation, recovery, transport, copy-inventory, and restore evidence | Missing coverage, owner, value, or successful restore keeps ingress and work intake closed |
| `CTL-009` | Steps 5, 9, and 10 verify minimized audit emission, protected storage, attributable access, retention, and alerting | Exercise representative authorization, credential, artifact, attempt, export, deletion, hold, restore, and incident events | `GOV-AUD-001` through `GOV-AUD-010`; audit owner, security/data-governance, affected domain owners, production authority | Event-schema review, sampled events and denials, minimization checks, integrity proof, access history, retention and purge evidence | Missing event class, protected storage, owner, or evidence keeps ingress and work intake closed |
| `CTL-010` | Steps 3, 6, and 10 reconcile every Primary, Cache, Index, Replica, Telemetry, Backup, and Audit copy with lifecycle policy | Exercise deletion, expiry, hold, backup restoration, tombstone replay, orphan detection, and restore suppression | `GOV-POL-001` through `GOV-POL-010` and affected `GOV-THR-*`; data owners, security/data-governance, operations, production authority | Copy inventory, policy versions, propagation outcomes, tombstones, purge verification, backup disposition, and reconciliation evidence | Unknown copy, unset policy, failed propagation, or unresolved orphan keeps ingress and work intake closed |
| `CTL-011` | Steps 1-2 verify digest identity, provenance, signatures or attestations, vulnerability status, authorization, revocation, and exceptions | Attempt deployment of unauthorized, vulnerable, revoked, and expired-exception artifacts and verify denial | `GOV-VUL-001` through `GOV-VUL-009`; artifact authority, security/data-governance, operations, production authority | Artifact digest, source/build provenance, signature or attestation, scan and triage result, authorization, exception, remediation, and revocation evidence | Missing or stale evidence, unresolved vulnerability, or unauthorized exception keeps ingress and work intake closed |
| `CTL-012` | Steps 3-5 and 9 verify least privilege, stamp scope, separation of duties, and attributable privileged actions | Exercise cross-stamp denial, privileged access, approval, export, deletion, recovery, and emergency access | `GOV-OWN-012` through `GOV-OWN-026` and applicable `GOV-THR-*`; named service owners, independent reviewers, security/data-governance, production authority | Role and scope configuration, approvals, access denials, privileged-action events, review results, and exception evidence | Missing owner, excessive privilege, cross-stamp access, or unattributed action keeps ingress and work intake closed |

<!-- markdownlint-enable MD013 -->

## Persistent Authorities and Copy Inventory

<!-- markdownlint-disable MD013 -->

| Resource | Operational authority | Recovery rule |
| --- | --- | --- |
| PostgreSQL | Domain records, identity, Job Registry, Agent Orchestration, attempts, accepted state, outbox, indexes | Restore as the workflow authority and validate ordered migrations and recovery position |
| NATS JetStream | Durable lifecycle and ready-work transport | Restore or reconstruct from PostgreSQL and the platform outbox; broker contents never override the Job Registry or Agent Orchestration |
| S3-compatible storage | Source recordings, segments, immutable results and manifests | Restore inventory and bytes, verify immutable digests, and retain API/object authorization boundaries |
| OCI/artifact registry | Immutable application, Analyst, model and package artifacts | Recover by digest with provenance and current authorization or revocation state |
| Telemetry and audit stores | Operational signals and append-protected security/lifecycle evidence | Restore only when policy requires; preserve minimization, integrity and exceptional-access controls |

<!-- markdownlint-enable MD013 -->

The profile inventories every Primary, Transport, Cache, Index, Telemetry,
Replica, Backup, and Audit copy from Security and Data Governance. Each copy
declares classification, encryption, access, residency, retention, deletion,
hold, backup, restore, capacity, owner, and evidence treatment. An unknown copy
or unset required lifecycle value blocks production.

## Backup Set and Consistency

A recoverable backup is one application-coordinated set, not a collection of
unrelated successful snapshots. Its immutable record contains:

- backup-set ID, stamp, profile and policy versions, start/end timestamps, and
  accountable operator
- PostgreSQL backup and recovery position, schema/migration version, and
  verification result
- object-store inventory, versions, immutable digests, and verification result
- JetStream snapshot position or the declared PostgreSQL/outbox regeneration
  point
- protected configuration version without secret values
- deletion tombstones, expiries, holds, credential revocations, artifact
  revocations, and restore-suppression state
- application, Analyst, model, and package provenance required to interpret
  restored data
- encryption, residency, retention, expiry, access, and test-restore evidence

Backup capture coordinates writers or records a recoverable relationship among
their positions. Crash-consistent volume snapshots are acceptable only when
application-level checks prove that relationship can be reconciled. A missing
member, failed digest, unknown copy, absent lifecycle marker, or inconsistent
position marks the set failed. Failed or expired sets cannot be selected for
restore.

Backup frequency, retention, location, encryption mechanism, recovery window,
and acceptable lag consume approved POL-001 through POL-010 values. This topic
does not set those values.

## Restore and Disaster Recovery

Restore always starts in an isolated stamp target with ingress, user access,
Manager credentials, and work intake disabled:

1. Select an unexpired verified backup set and record the incident, recovery
   objective, target profile, operator, and approval.
2. Create isolated stamp networks, empty volumes, and protected configuration.
   Do not activate restored credentials.
3. Restore PostgreSQL and verify recovery position, schema, migrations,
   integrity, stamp identity, and authoritative workflow state.
4. Restore object storage and verify inventory, object versions, immutable
   digests, references, and stamp ownership.
5. Load current lifecycle authority and apply deletion tombstones, expiries,
   holds, credential and artifact revocations, and restore suppression before
   any data becomes accessible.
6. Restore JetStream only when its position is compatible; otherwise recreate
   streams and consumers and reconcile publication from the Job Registry,
   Agent Orchestration, and transactional outbox. Broker state never creates
   workflow authority.
7. Restore required telemetry/audit state under its policy, rotate operational
   credentials, and validate TLS and least-privilege access.
8. Start the control plane with scheduling and intake disabled. Reconcile
   outbox records, logical jobs and agent invocations, active leases, and
   registered Managers; mark pre-recovery attempts stale and fence their
   tokens.
9. Reconnect Managers and Hermes, run runtime preflight, and verify late
   heartbeats, checkpoints, and completions cannot alter accepted state.
10. Verify health, stamp isolation, lineage, lifecycle suppression, audit
    evidence, capacity, and declared recovery objectives. Open work intake and
    ingress only after every required gate passes.

An integrity, authority, policy, isolation, or reconciliation failure aborts
reopening. The target remains isolated while the incident owner selects a new
backup set, repairs the target, or performs documented failback. Recovery
evidence records each checkpoint, elapsed time, data-loss boundary, rejected
attempt, exception, approval, and final disposition.

The initial single-host profile can recover onto a prepared replacement host,
but it does not claim automatic failover or continuous availability. Warm
standby, multi-host, and multi-location variants require separate profiles and
evidence.

## Service Objectives and Ownership

Required objective classes are stamp availability, API health and latency,
job acceptance, claim and completion, outbox publication lag, queue oldest-work
age and drain time, backup freshness, restore RTO, and recoverable-data RPO.
Each objective record contains:

- stable ID and profile version
- numeric target and unit
- measurement and compliance window
- OpenTelemetry or recovery-evidence source
- accountable owner and approval
- alert threshold and evaluation window
- runbook, exception and evidence references

All numeric objectives and thresholds are **Open / Blocking**. Architecture
validation permits that status so proposals remain reviewable; production-
readiness validation rejects a missing or unapproved value, owner, signal,
runbook, or required live measurement. No value is inherited by default.

Required accountable roles are incident commander, operations owner,
PostgreSQL owner, JetStream owner, object-storage owner, security and data-
governance approver, communications owner, and architecture/change approver.
Roles are assigned in the profile; this document does not name people.

## Observability, Dashboards, and Alerts

OpenTelemetry remains the vendor-neutral signal contract. Signals correlate by
stamp and, where applicable, analysis run, logical job, execution attempt,
Manager, capability, artifact version, and profile version. They omit secrets,
media payloads, unrestricted prompts, presigned URLs, and unnecessary personal
or team data under POL-010.

Minimum dashboard and alert coverage includes:

<!-- markdownlint-disable MD013 -->

| Area | Required signals |
| --- | --- |
| Platform | Request rate, errors, latency, health, restart, dependency failure |
| PostgreSQL | Reachability, connections, storage, transaction failure, migration state, backup freshness |
| JetStream | Reachability, storage, consumer lag, oldest work, redelivery, publish/ack failure |
| S3-compatible storage | Reachability, capacity, request errors, failed digest/inventory checks |
| Workflow | Outbox age/retries, Scheduler backlog, blocked runs, lease expiry, stale completion rejection |
| Execution | Connected Managers, effective slots, runtime/profile failures, resource saturation, drain state |
| Recovery | Backup-set status, restore checkpoints, achieved RPO/RTO, reconciliation and suppression failures |
| Governance | Isolation denial, credential/artifact revocation, lifecycle propagation and audit-integrity failures |

<!-- markdownlint-enable MD013 -->

Every alert declares a stable ID, signal, threshold and approval status,
evaluation window, severity, affected stamp context, owner role, and versioned
runbook. An orphan alert or an alert that cannot identify the affected stamp
fails readiness. Paging products and dashboard implementations remain profile
details.

## Capacity and Backpressure

Profiles record measured capacity and approved limits for host CPU, memory and
disk; PostgreSQL connections and storage; JetStream storage and consumers;
object capacity and throughput; control-plane concurrency; and effective
Analyst Manager slots. Queue depth and oldest-work age measure demand but do not
become workflow state.

For every warning and critical threshold, the profile defines admission
throttling, pause/drain, bounded retry, degraded-mode, escalation, and recovery
conditions. Responses preserve durable logical jobs, current attempts,
idempotent publication, and fenced completion. They never drop work silently,
publish blocked jobs, accept stale results, or treat NATS as authoritative.

Scaling actions follow measured thresholds and a runbook. This architecture
does not select an autoscaler or imply that adding a Compose replica is safe for
every service.

## Incident and Runbook Contract

Required runbooks cover production preflight, backup failure, isolated restore,
whole-host or Compose loss, PostgreSQL outage/corruption, JetStream loss or
backlog, object-storage outage/corruption/capacity, control-plane failure,
Manager/runtime loss, credential compromise, telemetry loss, lifecycle-policy
failure, and stamp-isolation failure.

Each versioned runbook records its trigger, severity, owner, prerequisites,
containment, diagnostic evidence, recovery steps, checkpoint validation,
escalation, communication, rollback or failback, post-incident actions, and
evidence location. Security incidents additionally follow the containment,
audit, hold, and approval authority in Security and Data Governance.

An unassigned required role, unresolved escalation route, or missing runbook is
**Open / Blocking production**.

## Upgrade, Compatibility, and Rollback

Every release identifies application and Analyst images by immutable digest and
declares compatibility for the host engine/Compose versions, PostgreSQL schema,
OpenAPI and JSON Schema contracts, NATS subjects/consumers, object manifests,
Analyst Manager versions, and lifecycle-policy versions.

Before upgrade, the change record requires successful profile preflight,
current backup and isolated-restore proof, capacity headroom, pause/drain or
admission control, old/new compatibility windows, ordered migrations, an
approved rollback deadline, and health and objective gates. Expand/contract
evolution is preferred when versions overlap.

Rollback is permitted only while authoritative data and contracts remain
backward-compatible. An irreversible schema or data transition must identify
the approved forward-fix or isolated backup-restore path and cannot claim
binary rollback. Ingress and work intake reopen only after state, isolation,
health, lineage, lifecycle, and objective checks pass.

## Validation and Promotion Evidence

Architecture validation checks profile completeness, stable identifiers,
stamp isolation, store/copy coverage, data-governance copy classes and lifecycle
policies from [Security and Data Governance](security-and-data-governance.md),
objective status, alert-to-runbook links, backup and restore order, queue
authority, scenario coverage, and upgrade/rollback compatibility.
Production-readiness validation additionally requires approved owners, values,
policy inputs, and live evidence.

Required exercises cover Aspire-to-Compose mapping and preflight, backup and
isolated restore, host loss, each state-service and telemetry failure, queue
backlog and backpressure, upgrade, rollback or forward-fix, stamp isolation,
and incident declaration/escalation. Each record states assumptions, injected
fault, expected signals, checkpoints, measured or not-yet-measurable objective
result, gaps, owner role, and follow-up evidence.

Tabletop fixtures are explicitly noncanonical and non-production evidence.
They cannot satisfy live recovery, capacity, objective, production-readiness,
or production-acceptance gates.

Production acceptance requires serious evaluation of deployment and recovery
alternatives, a real Compose deployment, approved
data-governance policy values (`DAT-008`, `POL-008`, and related authorities in
[Security and Data Governance](security-and-data-governance.md)) and operational
owners, measured objectives and capacity, and successful live preflight,
backup/restore, dependency-failure, backlog, upgrade, rollback, isolation, and
incident exercises.

---

Related architecture: [Index](README.md) | [Overview](overview.md) |
[Platform Implementation Profile](platform-implementation.md) |
[Deployment and Technology](tenancy-and-technology.md) |
[Security and Data Governance](security-and-data-governance.md) |
[Job Processing](job-processing.md) | [Analyst Manager](analyst-manager.md) |
[Analyst Runtime and Recovery](analyst-runtime-and-recovery.md)
