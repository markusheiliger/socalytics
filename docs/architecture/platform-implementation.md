# Platform Implementation Profile

This document defines the implementation baseline for SocAlytics.
It refines the logical components in the [architecture overview](overview.md)
without changing their domain responsibilities. The source and runtime choices
in this profile are adopted architecture; production readiness and operational
values remain **Provisional / Blocking production** as governed by
[Production Deployment and Operations](production-operations.md). Only the
platform host scaffold is executable: it provides the .NET 10 solution, local
Aspire composition, operational API surface, the layered project structure, and
host and architecture tests described below. It provides no domain behavior,
external infrastructure integration, security implementation, client runtime,
deployment configuration, or production-readiness evidence.

## Source And Runtime Baseline

SocAlytics uses one `src` root with these first-level ownership areas:

- `src/platform` owns the platform API, control plane, and supporting platform
  services; its initial executable host scaffold is implemented
- `src/clients` owns the future Web UI and Electron Coach Client source
- `src/agents` owns future intelligence-agent runtimes and agent-specific
  integration code
- `src/analysts` owns the future Analyst Manager, a per-user desktop
  application whose first slice is planned under `src/analysts/manager`, and
  the future Analyst SDK and Analyst capability implementations across their
  required runtimes

The executable platform projects are peers under `src/platform`, except for the
three test projects grouped under `src/platform/Tests`:

- `SocAlytics.Platform.Api`
- `SocAlytics.Platform.AppHost`
- `SocAlytics.Platform.Migrator`
- `SocAlytics.Platform.ServiceDefaults`
- `SocAlytics.Platform.Domain`
- `SocAlytics.Platform.Application`
- `SocAlytics.Platform.Infrastructure`
- `Tests/SocAlytics.Platform.Host.Tests`
- `Tests/SocAlytics.Platform.Architecture.Tests`
- `Tests/SocAlytics.Platform.Integration.Tests`

`src/platform/SocAlytics.Platform.slnx` is the .NET 10 solution. The platform is
one application structured in layers, one project per layer:

| Layer | Project | Responsibility | May depend on |
| --- | --- | --- | --- |
| Domain | `SocAlytics.Platform.Domain` | entities, value objects, domain rules; no framework or infrastructure dependencies | nothing |
| Application | `SocAlytics.Platform.Application` | command and query handlers, authorization, validation, transaction boundaries, interfaces for infrastructure | Domain |
| Infrastructure | `SocAlytics.Platform.Infrastructure` | PostgreSQL access, SQL and migrations, messaging, object storage, external services | Application, Domain |
| Presentation | `SocAlytics.Platform.Api` | HTTP endpoints, OpenAPI, backend-for-frontend, composition root | Application, Infrastructure |

`SocAlytics.Platform.AppHost` composes the local development environment and
references the API and the Migrator; `SocAlytics.Platform.ServiceDefaults` provides shared
hosting defaults. The functional areas of the platform (Club, Identity Access,
Recordings, Registry, Analysis, and Agent Orchestration) are folders and
namespaces inside the layers, for example
`SocAlytics.Platform.Application.Recordings`; they are not separate projects.
Application and Infrastructure each expose one public dependency-injection
composition method and keep their implementation types internal.

The persistence foundation adds a second host next to the API:
`SocAlytics.Platform.Migrator`, a console application that depends on
Infrastructure, applies pending migrations once, and exits with a success or
failure outcome. It will ship as its own OCI image and will be the only
component that ever receives database access able to create or alter data
structures; the API image will contain no migration execution path.

Executable components use or will use the native workspace and dependency tools
of each ecosystem:

- .NET solution and projects for C# components
- `pnpm` workspace for the separately packaged TypeScript client applications
  and selected environment-neutral shared packages, as described by
  [Client Applications](client-applications.md)
- `uv`, `pyproject.toml`, and committed lockfiles for Python projects
- future versioned OpenAPI and JSON Schema documents for cross-language contracts

The platform API and control-plane baseline is ASP.NET Core on .NET 10 LTS; the
initial host implements that runtime choice. The remaining ecosystem tooling is
future until its owning source area gains executable projects.
Future deployables will use cloud-neutral OCI images. .NET Aspire owns local
development composition, service discovery, health checks, OpenTelemetry
defaults, and developer dependencies. Aspire is not the production
orchestrator.

Docker Compose is the initial deployment mechanism. It will compose the
platform API and Web UI as separate OCI containers: the API container will be
built from the platform source area, while the Web UI container will be built
from the client source area and release-coupled to the platform API. Future
Compose deployments will also include PostgreSQL, NATS JetStream, and
S3-compatible storage on durable stamp-dedicated logical resources, plus the
Migrator as a one-shot service that every API service depends on with
`condition: service_completed_successfully`, so it runs once per deployment
regardless of the API replica count. Locally, the Aspire AppHost will mirror
that order: PostgreSQL, then the Migrator, then the API, which waits for the
Migrator to complete and for the S3-compatible RustFS container to report
healthy. The local PostgreSQL container provisions both database roles from
a committed initialization shell script that reads generated development-only
passwords, which the AppHost persists in its user secrets, and keeps its data in
a named development volume; these local values are not production values, and
production credentials, role-to-identity mapping, and the scheduling of the
Migrator remain unresolved.

Azure Container Apps is the Provisional target cloud profile, and Compose
remains supported as the self-hostable profile. Both run the same cloud-neutral
images, and application code takes no dependency that only one profile can
satisfy. Production topology, configuration, secrets, persistence, recovery,
objectives, telemetry, capacity, and promotion evidence remain unresolved for
every profile and are governed by
[Production Deployment and Operations](production-operations.md); this profile
does not make Aspire a production dependency.

## Control Plane

The current control-plane evidence is an ASP.NET Core host that
registers the Application and Infrastructure layers through their public
composition methods. It exposes `/alive`, `/health`, and the built-in `v1`
OpenAPI document at `/openapi/v1.json`; its domain paths are limited to the
club hierarchy, accounts, sessions, membership, and roles under `/api/v1`. The Aspire
AppHost composes PostgreSQL, the one-off Migrator, and the API in that order;
the API's `/health` readiness includes a `database` check that requires a
reachable database with a current migration state.
Architecture tests enforce the layer dependency direction and the intended
public surface. This is development-host evidence, not an infrastructure topology, deployment mechanism, security posture, or
production ingress contract.

The target control plane is one ASP.NET Core application deployed as a single
unit and structured in the layers described under
[Source And Runtime Baseline](#source-and-runtime-baseline). It is a
well-structured monolith, not a modular monolith: its functional areas share one
domain model, one application layer, and one database, and they are separated
by folders and namespaces rather than by isolated modules. The functional areas
include identity and authorization; club, season, team, and match management;
recordings and uploads; segments; analysis scheduling and durable job state;
capability and model registration; result ingestion and indexing; Analyst
Manager registration; MCP-facing application tools; and Agent Orchestration,
which covers conversations, logical invocations, delegated consultations,
attempts, accepted advice, lineage projections, lifecycle tombstones, and their
outgoing events.

Layer boundaries are enforced in code and tests: the Domain layer depends on
nothing, Application only on Domain, Infrastructure on Application and Domain,
and the API composes them. State changes go through Application command
handlers rather than direct table writes from the API or Infrastructure code.
Splitting the application into separate deployments would require a future
architecture change backed by measured scaling, fault-isolation, or operational
evidence. The one-off Migrator is not such a split: it shares the
Infrastructure layer and runs only before API instances start.

### Planned Data Organization

Each stamp uses one PostgreSQL database with one application schema,
`socalytics`, for all functional areas. Table names are prefixed or grouped by
functional area where that improves readability, but areas do not have separate
schemas, roles, or access separation. Migration history is kept apart from
domain data in `socalytics_migrations.history`, which records each applied
migration's sequence (the number in its script name), identity, SHA-256
checksum, and application time but contains no domain state. Two database
roles separate access: `socalytics_migrator` owns both schemas and is the only
role that creates or alters data structures, and `socalytics_app` may read and
write tables in `socalytics` and read the migration history. The environment
provisions both roles and their login identities before the Migrator first
runs; migrations grant privileges but never create roles or credentials. The
exceptions to uniform runtime access are the security audit table and the
break-glass recovery ledger: the runtime role may only insert and read them. A
trigger also rejects updates, deletes, and truncation of the audit table, so
audit evidence is append-protected and kept apart from application logs, and
because ledger entries cannot be changed or removed, a used recovery
identifier can never be made reusable. The audit table holds development
audit evidence; the production audit store, integrity verification, and
retention remain governed by POL-009 in
[Security and Data Governance](security-and-data-governance.md#audit-events).

Table definitions and SQL live in the Infrastructure layer. The API references
Application contracts rather than persistence types. Registry data includes the
queryable Analyst manifest cache keyed by immutable image digest. Analysis data
covers durable runs, DAG state, logical jobs, attempts, and accepted-result
references. Segment materialization runs on demand within the control-plane
deployment unless measured scaling later justifies extraction.

### Persistence And CQRS

The persistence foundation implements PostgreSQL access with Npgsql and Dapper,
DbUp migrations applied by the Migrator, explicit units of work, and
trigger-managed versions; NATS JetStream, S3-compatible storage, the
transactional outbox, and all domain tables remain unimplemented.

Each future deployment stamp uses one logical PostgreSQL database. Npgsql and Dapper
provide database access; Entity Framework Core is not part of the baseline.
DbUp applies one ordered sequence of numbered, versioned PostgreSQL SQL scripts
for the whole platform; the scripts live in the Infrastructure layer, and a
custom DbUp journal records them in `socalytics_migrations.history`.

Migrations run only in the one-off Migrator, never inside the API:

- the Migrator uses a migration role that may create and alter data structures;
  the API uses a runtime role that may read and write application data and read
  the migration history, but cannot change structures;
- the API reports ready only while the database is reachable and its migration
  state is current, meaning every registered migration is recorded as applied
  with a matching checksum; otherwise it stays not ready and reports why;
- a Migrator run that starts while another is in progress waits, up to a bounded
  time, for that run to finish and then applies only what is still pending; if
  the wait expires it fails without applying anything. PostgreSQL realizes this
  with a session-level advisory lock held for the whole run, so repeated runs,
  pipeline retries, and per-instance runs stay safe;
- before applying anything, the Migrator verifies that every applied migration
  it knows still has a matching checksum (computed over the script text with
  line endings normalized) and that no pending migration is numbered at or
  below the highest applied one; it applies each pending migration together
  with its history record in one transaction and exits with `0` on success or
  a distinct non-zero code per failure category (configuration, database
  unavailable, invalid migration set, checksum mismatch, sequence conflict,
  migration failure, lock-wait timeout, cancellation); diagnostics name the
  failing migration but never contain credentials, connection secrets, or
  migration content;
- history records for migrations the Migrator does not know are left untouched
  and reported, so a backward-compatible older release can still start.

CQRS is logical rather than physical:

- commands use plain typed C# handlers in the Application layer, resolved
  through .NET dependency injection, and own authorization, validation,
  transactions, and state changes
- queries use separate typed handlers and purpose-built SQL projections
  implemented in the Infrastructure layer
- command and query paths share the stamp database
- event sourcing and separate read and write databases are not implied

Optimistic concurrency protects contested writes. Every mutable aggregate root
(a record together with the child rows that always change with it, such as a
team or an analysis run) carries a `version` column. The platform guarantees,
independently of the database product, that:

- every individual change to an aggregate, including changes to its child rows
  and data migrations, advances the root's `version` by exactly one, atomically
  with the change, so one transaction that changes several rows of an
  aggregate may advance it more than once;
- a write that changes nothing does not advance it;
- consumers compare versions only for equality and never derive meaning from
  the size of a step;
- an edit handler names the version it read in its change
  (`WHERE id = @Id AND version = @ExpectedVersion`), and zero affected rows is
  reported as a concurrency conflict instead of overwriting another change;
- a lifecycle state-transition handler (approve, revoke, complete, finalize,
  and similar) names the state it requires instead
  (`WHERE id = @Id AND state = @ExpectedState`), so a transition is never
  refused merely because an unrelated edit advanced the version.

Immutable records (recording versions, timeline mappings, finalized recording
sets, accepted results, lineage) have no `version`; their identity or digest
identifies them. Views and projections are read-only and never carry their own
`version`; see
[Contracts and Compatibility](contracts-and-compatibility.md#representation-conventions)
for how versions appear as HTTP ETags.

PostgreSQL realizes the guarantee with triggers that the Infrastructure layer's
migrations create, so no handler can forget an increment:

- one shared `BEFORE UPDATE` trigger function, `socalytics.advance_version()`,
  attached to every table with a `version bigint not null default 1` column,
  sets `version = OLD.version + 1` and fires only when the row actually changes
  (`WHEN (OLD.* IS DISTINCT FROM NEW.*)`); it does not check versions, which
  stays the handler's visible responsibility;
- `AFTER INSERT`, `UPDATE`, and `DELETE` triggers on an aggregate's child
  tables, using the shared function `socalytics.touch_aggregate_root()`, touch
  the aggregate root, which advances its `version` through the same increment
  trigger;
- a migration may suppress the increment for its own transaction with the
  transaction-local setting `socalytics.suppress_version = 'on'`; the trigger
  functions honor it only when the current role is a member of
  `socalytics_migrator`, so runtime access cannot suppress it; by default,
  migrations and backfills advance versions like any other change.

If the platform moved to another database, the guarantee would stay and only
this mechanism would change.

Database changes and outgoing events will commit atomically through a
PostgreSQL transactional outbox (`socalytics.outbox_messages`). The API process
will host the background workers: an outbox publisher that delivers pending
records to NATS JetStream at least once with bounded retries and records each
outcome, a retention worker that removes only records whose publication
outcome is known, the Job Monitor that detects expired attempt leases from
durable state, the consumer of `matches.recordings-finalized`, and publication
reconciliation at startup and after transport reconnection. Workers claim rows
with `FOR UPDATE SKIP LOCKED`, so several API replicas can run them safely
without leader election, and they add no deployable. Consumers and completion
handlers remain idempotent.

Encryption, retention, deletion, copy propagation, residency, and audit
requirements for the database, outbox, generated indexes, telemetry, replicas,
and backups are governed by
[Security and Data Governance](security-and-data-governance.md).

### Object Storage

The Recordings area will adopt S3-compatible object storage through the S3
protocol only. Infrastructure will use `AWSSDK.S3` with path-style addressing
against a configured endpoint; Application and Domain depend only on a storage
abstraction. Every upload will be a multipart upload with composite SHA-256
checksums: the API issues presigned per-part grants that sign each part's
length and checksum, assembles the declared parts itself, and verifies the
storage-reported composite digest and size; it never receives media bytes. A
hosted worker in the API host will expire abandoned upload sessions and abort
their multipart uploads. Locally the Aspire AppHost will run a pinned RustFS
container as a plain container resource, the API will create the development
bucket, and integration tests will use the same image through Testcontainers
together with a store conformance probe that any production store must pass.
Bucket provisioning, production storage selection, encryption, credentials,
browser CORS, public endpoints, grant and session lifetimes, and lifecycle
policy remain governed by
[Production Deployment and Operations](production-operations.md) and
[Security and Data Governance](security-and-data-governance.md).

## API And Identity

The current API implements local accounts, the server-validated BFF session,
anti-forgery protection, membership, club and team roles, team-scoped
authorization, security audit events, and the `Club > Season > Team > Match`
hierarchy, as specified in
`specs/20261005-130701-club-identity-foundation`. It implements no external
OpenID Connect sign-in, MFA enforcement, generated client, or Analyst Manager
identity. Exposure and access policy for operational endpoints in a production
ingress remain unresolved.

REST with JSON will be the primary platform protocol. ASP.NET Core will publish
a versioned OpenAPI description, and Kiota will generate TypeScript, C#, and Python
clients. Applications keep generated clients behind local adapter boundaries so
transport generation does not define their domain models.

The future canonical artifacts, owner map, and compatibility policy are
governed by [Contracts and Compatibility](contracts-and-compatibility.md).

Platform-managed local accounts are always available. ASP.NET Core Identity
provides password hashing, security stamps, recovery tokens, lockout, and MFA;
its stores use Dapper and PostgreSQL. A stamp may additionally configure one or
more external OpenID Connect providers. The login UI displays external options
only when configured.

Local and external login paths establish the same ASP.NET Core backend-for-
frontend session. The browser receives a Secure, HttpOnly, SameSite cookie and
does not store identity-provider bearer tokens. State-changing browser requests
use CSRF protection. Club membership and team authorization remain platform
data enforced by the API.

The BFF session is server-validated. The cookie carries only an opaque
random session token, and the database stores its hash together with idle and
absolute expiry and the account's security stamp at issue. Clients send a
per-session anti-forgery token, derived from the session token with HMAC-SHA256
and recomputed on every request, in a request header on every state-changing
request. Every request revalidates the session, the account's
active membership, and its security stamp, and reads current roles, so
sign-out, session termination, password changes, deactivation, and role
revocation take effect on the affected member's next request. No session or
anti-forgery secret is stored in recoverable form, and the API needs no shared
key ring across instances. Single-use, time-limited set-password and reset
credentials are issued by a Club Admin through an ASP.NET Core Identity token
provider and stored only as hashes; unused credentials stop working when
their issuer loses the Club Admin role or is deactivated, or their target is
deactivated. Every refused sign-in performs the same password-hash work, so
refusal reasons cannot be told apart by timing. The club and its first Club Admin are
established from protected deployment configuration by the API, not the
Migrator. The API serializes this one-time bootstrap with a database
transaction lock and uniqueness constraints, so concurrently starting API
instances create exactly one club and administrator, and an instance reports
not ready while no club is established or the configuration conflicts with it.
The first Club Admin must change the configured initial password at the first
sign-in, and bootstrap no longer needs that value once the club exists.
A club whose only Club Admin can no longer sign in recovers through a
break-glass directive in protected deployment configuration. The directive
names an existing active account, a single-use recovery identifier, and an
operator-supplied temporary credential. The API applies it at start under the
same lock, at most once per identifier, which it records in the recovery
ledger. Applying it sets the credential, ends the account's sessions, and
requires a password change at the next sign-in. It grants no roles and is
audited. The platform never writes the credential to logs or diagnostics, and
no API operation, authenticated or not, performs recovery.

Analyst Manager registration uses browser device-code pairing initiated by a
club `Registrar` and explicitly approved by a `Club Admin`. Club Admin inherits
the Registrar capability and may self-approve through a separate audited
operation. The AM activates only after proving possession of the protected
device key bound during pairing.

An active AM uses OAuth 2.0 client credentials with `private_key_jwt` and
short-lived DPoP-bound access tokens. API authorization also checks the
authoritative active registration and stamp binding. The durable registration
survives restart, while tokens and scoped NATS credentials expire and rotate.
Administrative revocation blocks API access and credential renewal immediately.
The complete workflow is defined in the
[Analyst Manager architecture](analyst-manager.md#registration-and-stamp-binding).

The Analyst Manager token endpoint will be a minimal endpoint in the API's
Registry functional area that supports only this grant; the platform does not
run a general-purpose authorization server for machine clients.

Until a dedicated stamp-operator role is defined, operational workflow
inspection and reconciliation operations under `/api/v1/operations` will be
restricted to Club Admins and audited when they change state.

### Agent Runtime

Agent Orchestration will use the same Dapper, PostgreSQL, DbUp, logical CQRS,
optimistic-concurrency, and transactional-outbox baseline as the rest of the
platform. Its state remains in the stamp database; this profile introduces
neither a second agent database nor a special extraction rule. NATS JetStream
carries only minimal, at-least-once work notifications. Consumers claim durable
work through the Agent Orchestration application handlers and remain idempotent
under duplicate delivery.

The planned control-plane surface includes canonical asynchronous submission,
private conditional status/result/lineage reads, workload claim, lease,
checkpoint, consultation and completion operations, lifecycle suppression,
migration readiness, and bounded recovery/reconciliation.

Hermes is a planned separate Python OCI deployment with no direct database
access. It uses Microsoft Agent Framework behind local adapters, authenticates
as a stamp-local workload, and uses versioned REST
operations to claim, renew, checkpoint, delegate, and complete work. Connected
Web UI and Coach Client applications use the API/BFF for asynchronous
submission and conditional status, result, and lineage polling; they never call
Hermes directly. The complete persistence, transport, authorization, fencing,
and recovery contract is defined by
[Intelligence and Agents](intelligence-and-agents.md#agent-orchestration-authority).
MCP tools, prompt/profile resolution, model execution, and Web UI/Coach Client
agent workflows remain future implementation work.

## Client Technology

The connected Web UI is an independent React and TypeScript application built
with Vite. React Router handles navigation, TanStack Query manages remote server
state, and MUI supplies accessible components under a SocAlytics theme.

The Web UI and Electron Coach Client keep separate application shells,
packages, and builds. The Web UI release is coordinated with the platform API
and remains a separately containerized deployment unit built from
`src/clients`; the Coach Client retains an independent release lifecycle. A
future `pnpm` workspace may allow generated API clients, domain-neutral
validation, design tokens, and selected MUI components to be shared while
workflow-specific screens and security-sensitive shell integrations remain
application-owned. Shared packages cannot import Electron, Node built-ins,
preload/main modules, local-store adapters, shell routing, or privileged IPC
implementations. See
[Client Applications](client-applications.md).

Future offline Coach Client implementation follows the decisions in
[Client Applications](client-applications.md):
main-process encrypted SQLite, opaque-cursor REST deltas, explicit clips,
read-only first offline delivery, bounded device-bound entitlement, and
OS-protected per-installation encryption keys.

## Analyst Technology

The Analyst Manager will be a per-user desktop application in a separate .NET
solution under `src/analysts/manager` with its own tests. A .NET 10 Generic
Host worker in a library without UI dependencies owns lifecycle,
registration, key providers, signed local state, queue, hardware, autostart,
and OCI-runtime responsibilities; Avalonia supplies the cross-platform tray
and status UI in the same process. Device keys use Windows CNG, a small Swift
bridge over CryptoKit and the Keychain on macOS, and PKCS#11 through
Pkcs11Interop on Linux. The worker and UI are tested without a display using
Avalonia headless tests; Linux CI uses a SoftHSM2 token, while the Windows and
macOS key stores are verified on those systems. Shared golden fixtures under
`contracts/analyst-manager/` are verified by both the platform and the Manager
test suites so the two implementations of the registration exchanges cannot
drift.

Analyst Containers use Python because PyTorch, ONNX Runtime, OpenCV, NumPy,
SciPy, scikit-learn, SAHI, and accelerator-vendor tooling provide the paved
model and computer-vision path. Each runtime image may pin a different Python
minor version when native or vendor compatibility requires it.

Future versioned JSON Schema documents will be canonical for Analyst job and result
contracts. The internal Analyst SDK exposes Pydantic models and validates input
and output at the container boundary. The platform independently validates the
same schemas. Runtime-specific OCI images use locked dependencies and record
their immutable image, model, and package provenance. Preprocessing and
postprocessing implementation is traced through the immutable OCI image rather
than selected as a separate job artifact.

## Test And Observability Baseline

The executable platform scaffold uses xUnit v3 and Shouldly for the Aspire host
smoke test and NetArchTest.Rules plus project-reference and reflection assertions
for architecture tests. ServiceDefaults provides the standard Aspire
service-discovery, resilience, OpenTelemetry, liveness, and readiness wiring;
this is local host instrumentation evidence, not production telemetry evidence.

The broader default test tools are:

- xUnit v3, Shouldly, and NSubstitute for .NET
- Vitest and Testing Library for React and TypeScript
- pytest for Python
- Playwright for browser workflows
- Testcontainers for PostgreSQL, NATS, S3, and other real infrastructure
  boundaries

OpenTelemetry is the vendor-neutral telemetry contract. Aspire ServiceDefaults
wires health checks, structured logs, metrics, and traces for .NET services;
other runtimes emit compatible telemetry with the same correlation context.
Telemetry payload minimization and lifecycle follow
[Security and Data Governance](security-and-data-governance.md).
Testcontainers runs disposable PostgreSQL instances for
`SocAlytics.Platform.Integration.Tests`.

## Planned Acceptance Evidence

The initial platform-host change provides this executable evidence:

- `src/platform/SocAlytics.Platform.slnx` restores, builds, and tests on .NET 10;
- Aspire starts PostgreSQL, the one-off Migrator, and the API in that order and
  reports `/health` readiness, including the `database` check;
- host tests exercise `/alive`, `/health`, and `/openapi/v1.json` and verify the
  Application and Infrastructure registrations; and
- architecture tests enforce the layer dependency direction, API use of the
public composition methods, and internal implementation visibility; and
- PostgreSQL integration tests cover ordered checksum-aware migrations, repeat
and concurrent Migrator runs with the bounded lock wait, rollback of failing
migrations, unknown applied migrations, distinguishable Migrator exit
outcomes, explicit units of work, optimistic concurrency, the version
guarantee including child-root and migration behavior, trigger presence,
absence of `club_id`, runtime-role access limits, and API readiness against
current and non-current migration states.

The following remain validation targets rather than claims of current evidence:

- PostgreSQL integration tests cover Dapper mappings of domain records,
idempotency, authorization, immutable lineage, registry versions, analysis
recovery, and state-plus-outbox atomicity;- NATS recovery tests cover broker outage, retry, publisher restart,
  expired-lease recovery, duplicate notification handling, and PostgreSQL
  revalidation;
- canonical contract validation, registry transport mapping, runtime
  conformance, and generated TypeScript, C#, and Python client smoke checks
  pass without introducing duplicate wire contracts; and
- telemetry tests cover correlation and operational signals while excluding
  recording, result, and outbox payload bodies.

### Architecture Reassessment

The architecture must be revisited when implementation evidence contradicts a
choice or reveals a material operational tradeoff:

- **Realistic alternatives seriously evaluated — partially met.** The change
  design compares monolith structure, persistence, migration, CQRS, concurrency,
  outbox, lineage, workflow-record, and transport-adapter alternatives. It does
  not complete the existing comparative evidence for production orchestration,
  identity, client sharing, Analyst Manager UI/runtime, or Analyst
  image/runtime choices.
- **Consequences and operational tradeoffs understood — partially met.**
  Layer dependency rules, host composition with PostgreSQL and the Migrator,
  local transaction boundaries, and local database unavailability are
  exercised. Duplicate delivery, restart recovery, other local infrastructure
  failure, production database access control, backup/restore, lifecycle controls, service objectives, capacity, and complete
  workflow failure behavior remain unevidenced.
- **Prototype, measurement, or implementation evidence supports the choice —
  partially met.** The .NET 10 host, Aspire local composition, operational
  API surface, layer composition methods, structural tests, and the persistence
  foundation (migrations, Migrator, units of work, version triggers) are
  implemented. Domain persistence, publication, secure BFF identity, both client
  shells, cross-runtime schema agreement, reproducible Analyst images, and the
  Avalonia/OCI platform matrix remain unsupported by implementation evidence.
- **Mature enough to govern subsequent implementation — not met for
  promotion.** The foundation can guide further reversible implementation, but
  the incomplete identity, client, Scheduler, Analyst runtime, production
  operations, and security/lifecycle evidence prevents the production profile
  from becoming production-ready or implementation-proven.

The host scaffold has demonstrated that Aspire starts the local API with health
and telemetry defaults and that architecture tests enforce the layer
dependency and visibility boundaries. Before treating the remaining
choices as implementation-proven, evidence must demonstrate:

- PostgreSQL integration tests cover Dapper mappings, migrations, optimistic
  concurrency, outbox recovery, and duplicate delivery
- local and optional OIDC login share a secure BFF session, including CSRF,
  reset, lockout, MFA, logout, and authorization tests
- Kiota-generated TypeScript, C#, and Python clients compile and pass contract
  compatibility tests
- shared React packages operate correctly in the Vite and Electron shells
- JSON Schema validation agrees at .NET and Python boundaries
- locked Analyst images rebuild reproducibly and emit valid manifests
- Avalonia and OCI-runtime spikes satisfy the platform matrix in the
  [Analyst Manager architecture](analyst-manager.md)

---

Related architecture: [Index](README.md) | [Overview](overview.md) |
[Deployment and Technology](tenancy-and-technology.md) |
[Client Applications](client-applications.md) |
[Job Processing](job-processing.md) | [Analyst Manager](analyst-manager.md) |
[Analysts, Models, and Hardware](analysts-models-and-hardware.md) |
[Production Deployment and Operations](production-operations.md)
