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
- `src/analysts` owns the future Analyst Manager, Analyst SDK, and Analyst
  capability implementations across their required runtimes

The executable platform projects are peers under `src/platform`, except for the
two test projects grouped under `src/platform/Tests`:

- `SocAlytics.Platform.Api`
- `SocAlytics.Platform.AppHost`
- `SocAlytics.Platform.ServiceDefaults`
- `SocAlytics.Platform.Domain`
- `SocAlytics.Platform.Application`
- `SocAlytics.Platform.Infrastructure`
- `Tests/SocAlytics.Platform.Host.Tests`
- `Tests/SocAlytics.Platform.Architecture.Tests`

`src/platform/SocAlytics.Platform.slnx` is the .NET 10 solution. The platform is
one application structured in layers, one project per layer:

| Layer | Project | Responsibility | May depend on |
| --- | --- | --- | --- |
| Domain | `SocAlytics.Platform.Domain` | entities, value objects, domain rules; no framework or infrastructure dependencies | nothing |
| Application | `SocAlytics.Platform.Application` | command and query handlers, authorization, validation, transaction boundaries, interfaces for infrastructure | Domain |
| Infrastructure | `SocAlytics.Platform.Infrastructure` | PostgreSQL access, SQL and migrations, messaging, object storage, external services | Application, Domain |
| Presentation | `SocAlytics.Platform.Api` | HTTP endpoints, OpenAPI, backend-for-frontend, composition root | Application, Infrastructure |

`SocAlytics.Platform.AppHost` composes the local development environment and
references only the API; `SocAlytics.Platform.ServiceDefaults` provides shared
hosting defaults. The functional areas of the platform (Club, Identity Access,
Recordings, Registry, Analysis, and Agent Orchestration) are folders and
namespaces inside the layers, for example
`SocAlytics.Platform.Application.Recordings`; they are not separate projects.
Application and Infrastructure each expose one public dependency-injection
composition method and keep their implementation types internal.

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
S3-compatible storage on durable stamp-dedicated logical resources. Production
topology, configuration, secrets, persistence, recovery, objectives, telemetry,
capacity, and promotion evidence remain unresolved and are governed by
[Production Deployment and Operations](production-operations.md); this profile
does not make Aspire a production dependency or select a cloud provider.

## Control Plane

The current control-plane evidence is a dependency-free ASP.NET Core host that
registers the Application and Infrastructure layers through their public
composition methods. It exposes only `/alive`, `/health`, and the built-in `v1`
OpenAPI document at `/openapi/v1.json`; it has no domain paths. The Aspire
AppHost composes only the API resource and uses `/health` for readiness.
Architecture tests enforce the layer dependency direction and the intended
public surface. This is development-host evidence, not an implemented domain
API, infrastructure topology, deployment mechanism, security posture, or
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
evidence.

### Planned Data Organization

Each stamp uses one PostgreSQL database with one application schema,
`socalytics`, for all functional areas. Table names are prefixed or grouped by
functional area where that improves readability, but areas do not have separate
schemas, roles, or access separation. Migration history is kept apart from
domain data in `socalytics_migrations.history`, which records the migration
sequence and checksum but contains no domain state.

Table definitions and SQL live in the Infrastructure layer. The API references
Application contracts rather than persistence types. Registry data includes the
queryable Analyst manifest cache keyed by immutable image digest. Analysis data
covers durable runs, DAG state, logical jobs, attempts, and accepted-result
references. Segment materialization runs on demand within the control-plane
deployment unless measured scaling later justifies extraction.

### Persistence And CQRS

This persistence and messaging baseline remains unimplemented; the current host
has no PostgreSQL, Npgsql, Dapper, DbUp, NATS JetStream, S3-compatible storage,
migrations, outbox, or infrastructure integration.

Each future deployment stamp uses one logical PostgreSQL database. Npgsql and Dapper
provide database access; Entity Framework Core is not part of the baseline.
DbUp applies one ordered sequence of versioned PostgreSQL SQL scripts for the
whole platform; the scripts live in the Infrastructure layer.

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

- every write that changes an aggregate, including changes to its child rows
  and data migrations, advances the root's `version` by exactly one, atomically
  with the change;
- a write that changes nothing does not advance it;
- a handler names the version it read in its change
  (`WHERE id = @Id AND version = @ExpectedVersion`), and zero affected rows is
  reported as a concurrency conflict instead of overwriting another change.

Immutable records (recording versions, timeline mappings, finalized recording
sets, accepted results, lineage) have no `version`; their identity or digest
identifies them. Views and projections are read-only and never carry their own
`version`; see
[Contracts and Compatibility](contracts-and-compatibility.md#representation-conventions)
for how versions appear as HTTP ETags.

PostgreSQL realizes the guarantee with triggers that the Infrastructure layer's
migrations create, so no handler can forget an increment:

- one shared `BEFORE UPDATE` trigger function, attached to every table with a
  `version` column, sets `version = OLD.version + 1` and fires only when the row
  actually changes (`WHEN (OLD.* IS DISTINCT FROM NEW.*)`); it does not check
  versions, which stays the handler's visible responsibility;
- `AFTER INSERT`, `UPDATE`, and `DELETE` triggers on an aggregate's child tables
  touch the aggregate root, which advances its `version` through the same
  increment trigger;
- a migration may suppress the increment for its own transaction with a
  transaction-local setting that only migration scripts use; by default,
  migrations and backfills advance versions like any other change.

If the platform moved to another database, the guarantee would stay and only
this mechanism would change.

Database changes and outgoing
events commit atomically through a PostgreSQL transactional outbox. A background
publisher delivers outbox records to NATS JetStream with retries; consumers and
completion handlers remain idempotent.

Encryption, retention, deletion, copy propagation, residency, and audit
requirements for the database, outbox, generated indexes, telemetry, replicas,
and backups are governed by
[Security and Data Governance](security-and-data-governance.md).

## API And Identity

The current dependency-free API implements only the operational and OpenAPI
surface described under [Control Plane](#control-plane). It implements no
accounts, authentication, authorization, BFF session, generated client, or
domain API behavior. Exposure and access policy for operational endpoints in a
production ingress remain unresolved.

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

The Analyst Manager uses a .NET 10 Generic Host for lifecycle, registration,
queue, hardware, and OCI-runtime responsibilities. Avalonia supplies its
cross-platform tray and status UI. The worker remains testable without the UI.

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

## Planned Acceptance Evidence

The initial platform-host change provides this executable evidence:

- `src/platform/SocAlytics.Platform.slnx` restores, builds, and tests on .NET 10;
- Aspire starts the API as its sole resource and reports `/health` readiness;
- host tests exercise `/alive`, `/health`, and `/openapi/v1.json` and verify the
  Application and Infrastructure registrations; and
- architecture tests enforce the layer dependency direction, API use of the
  public composition methods, and internal implementation visibility.

The following remain validation targets rather than claims of current evidence:

- PostgreSQL integration tests cover ordered checksum-aware migrations, Dapper
  mappings, explicit transactions, optimistic concurrency, the version
  guarantee (increments on change, none on no-op writes, root increments from
  child changes, migration behavior) and the presence of the version triggers
  on every versioned and child table, idempotency,
  authorization, immutable lineage, registry versions, analysis recovery, and
  state-plus-outbox atomicity;
- NATS recovery tests cover broker outage, retry, publisher restart,
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
  Layer dependency rules, host composition, and dependency-free startup
  are exercised. Transaction boundaries, duplicate delivery, restart recovery,
  local infrastructure failure, production database access control,
  backup/restore, lifecycle controls, service objectives, capacity, and complete
  workflow failure behavior remain unevidenced.
- **Prototype, measurement, or implementation evidence supports the choice —
  partially met.** The .NET 10 host, Aspire-only local composition, operational
  API surface, layer composition methods, and structural tests are
  implemented. Persistence, publication, secure BFF identity, both client
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
