# Platform Implementation Profile

This document defines the implementation baseline for SocAlytics.
It refines the logical components in the [architecture overview](overview.md)
without changing their domain responsibilities. The source and runtime choices
in this profile are adopted architecture; production readiness and operational
values remain **Provisional / Blocking production** as governed by
[Production Deployment and Operations](production-operations.md). Only the
platform host and local persistence foundation are executable: they provide the
.NET 10 solution, Aspire composition with PostgreSQL, migration-gated readiness,
the operational API surface, capability composition boundaries, and host,
architecture, and PostgreSQL integration tests described below. They provide no
domain behavior, messaging or object-storage integration, identity or
authorization implementation, client runtime, production deployment
configuration, or production-readiness evidence.

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
- `SocAlytics.Platform.Persistence`
- `SocAlytics.Platform.Club`
- `SocAlytics.Platform.IdentityAccess`
- `SocAlytics.Platform.Recordings`
- `SocAlytics.Platform.Registry`
- `SocAlytics.Platform.Analysis`
- `SocAlytics.Platform.AgentOrchestration`
- `Tests/SocAlytics.Platform.Host.Tests`
- `Tests/SocAlytics.Platform.Architecture.Tests`
- `Tests/SocAlytics.Platform.Persistence.Tests`

`src/platform/SocAlytics.Platform.slnx` is the .NET 10 solution. The six
capability projects expose one public dependency-injection composition boundary
each and otherwise keep their initial marker types internal. They do not
reference one another. The naming and peer-layout refinement does not change
the capabilities' ownership or runtime boundaries and does not require an ADR.

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

The current control-plane evidence is an ASP.NET Core host that registers all
six capability projects through their public composition boundaries and runs
their registered migrations before reporting readiness. It exposes only
`/alive`, `/health`, and the built-in `v1` OpenAPI document at
`/openapi/v1.json`; it has no domain paths. The Aspire AppHost composes the API
with a PostgreSQL resource and uses `/health` for readiness. Host tests verify
successful migration startup, unhealthy readiness on migration failure, and
the unchanged operational and OpenAPI surface. Architecture tests enforce
capability isolation and the intended public surface. This is local-development
evidence, not a production deployment topology, security posture, or ingress
contract.

The target control plane starts as one ASP.NET Core modular-monolith deployment.
Its modules include identity and authorization; club, season, team, and match
management; recordings and uploads; segments; analysis scheduling and durable
job state; capability and model registration; result ingestion and indexing;
Analyst Manager registration; and MCP-facing application tools.
The Agent Orchestration module owns conversations, logical invocations,
delegated consultations, attempts, accepted advice, lineage projections,
lifecycle tombstones, and its outgoing events.

Module boundaries will be enforced in code and tests. A module owns its application
handlers, database objects, SQL, and migrations. Cross-module state changes use
the owning module's command boundary rather than direct table writes. A module
becomes a separate deployment only when measured scaling, fault-isolation, or
operational evidence justifies extraction.

### Current Module-Schema Ownership

The local persistence foundation creates only these module schemas and grants:

- `SocAlytics.Platform.Club` owns schema `club`;
- `SocAlytics.Platform.IdentityAccess` owns schema `identity_access`;
- `SocAlytics.Platform.Recordings` owns schema `recordings`;
- `SocAlytics.Platform.Registry` owns schema `registry`;
- `SocAlytics.Platform.Analysis` owns schema `analysis`;
- `SocAlytics.Platform.AgentOrchestration` owns schema `agent_orchestration`; and
- shared migration infrastructure owns `socalytics_migrations.history`, which
  records module, sequence, script identity, checksum, and successful
  application time but contains no domain state.

Each module owns its embedded schema-creation migration and its SQL; it cannot
register migrations for another module. For local and test databases, each
module has a schema owner role and a restricted runtime role. Runtime sessions
use only their module's schema, and PostgreSQL denies peer-schema access. The
bootstrap connection is restricted to migration orchestration. The API host
uses public module composition boundaries rather than module persistence types.
These initial migrations create schemas and grants only, not domain tables.
The planned Registry, Analysis, and segments data remains unimplemented.

### Persistence And CQRS

The module-neutral local persistence foundation is implemented. It provides
Npgsql connection boundaries, Dapper integration, explicit transaction
execution, a narrow optimistic-concurrency primitive, and DbUp migration
orchestration with deterministic ordering, SHA-256 checksum preflight,
transactional per-script history writes, repeat-run skipping, and sanitized
failures. Testcontainers-backed PostgreSQL tests verify migration order,
checksum conflicts, repeat startup, rollback, schema ownership and role
isolation, cross-schema denial, transaction and concurrency behavior, and the
absence of `club_id` from persistence artifacts and database catalogs.

Aspire composes one PostgreSQL database for local development. The API runs the
registered migrations during startup and remains unready when the database or
migrations are unavailable or fail. `/alive`, `/health`, and
`/openapi/v1.json` remain the only exposed operational routes. This local
foundation does not select production database names, hosts, ports, identities,
credentials, secret sources, or role-to-identity mappings.

The accepted architecture uses one logical PostgreSQL database per deployment
stamp, with module-owned schemas; this is not evidence of a production
deployment. Entity Framework Core is not part of the baseline.

CQRS is logical rather than physical:

- commands use plain typed C# handlers resolved through .NET dependency
  injection and own authorization, validation, transactions, and state changes
- queries use separate typed handlers and purpose-built SQL projections
- command and query paths share the stamp database
- event sourcing and separate read and write databases are not implied

Logical CQRS, domain persistence, and optimistic-concurrency use by domain
handlers remain future work. A PostgreSQL transactional outbox, NATS JetStream
publisher, retries, and idempotent consumers are also unimplemented.

Encryption, retention, deletion, copy propagation, residency, and audit
requirements for the database, outbox, generated indexes, telemetry, replicas,
and backups are governed by
[Security and Data Governance](security-and-data-governance.md).

### Persistence Foundation Decisions (ADR Candidates)

The active `add-platform-persistence-foundation` OpenSpec change is
implementing this baseline and carries two durable architecture decisions:

- **Shared peer persistence project.** `SocAlytics.Platform.Persistence` is a
  peer project under `src/platform` that owns only module-neutral connection
  creation, explicit transaction execution, optimistic-concurrency primitives,
  migration descriptors, checksum verification, and migration orchestration,
  including `socalytics_migrations.history`. It owns no capability schema or
  domain record. Each capability module keeps ownership of its own SQL,
  migrations, and schema, and references Persistence rather than another
  capability module.
- **PostgreSQL owner and runtime roles per module.** Local and test databases
  bootstrap a stable `NOLOGIN` owner role and a narrower runtime role for each
  adopted module schema. The owner role owns only its schema and migration
  objects; its runtime role receives only the schema usage and object
  privileges required by normal module access, with public and peer-module
  access revoked. Migration execution assumes the module's owner role, while
  normal module sessions assume its runtime role. This establishes role
  semantics only; production identity and secret mapping remain unresolved and
  are governed by
  [Production Deployment and Operations](production-operations.md).

Task 1.1 resolved the ADR candidate by recording its rationale in this
narrative rather than adding an ADR while the OpenSpec change is active. Under
the [architecture decision record threshold](decisions/README.md), the shared
boundary and role semantics remain candidates for reconsideration when the
change is ready to archive; an ADR will be added only if they still meet that
threshold. Production identity and secret mapping remain unresolved and are
governed by [Production Deployment and Operations](production-operations.md).

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

Agent Orchestration will use the same module-owned Dapper, PostgreSQL, DbUp,
logical CQRS, optimistic-concurrency, and transactional-outbox baseline. Its
state remains in the stamp database; this profile introduces neither a second
agent database nor a special extraction rule. NATS JetStream carries only
minimal, at-least-once work notifications. Consumers claim durable work through
the owning module and remain idempotent under duplicate delivery.

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

## Executable Evidence and Remaining Validation

The platform host and persistence foundation provide this executable evidence:

- `src/platform/SocAlytics.Platform.slnx` restores, builds, and tests on .NET 10;
- Aspire starts PostgreSQL and the API, and the API reports readiness only
  after successful migrations;
- host tests verify migration success and failure, `/alive`, `/health`,
  `/openapi/v1.json`, and all six capability registrations;
- PostgreSQL tests exercise ordered checksum-aware migrations and rollback,
  role-based schema ownership and cross-schema denial, explicit transactions,
  optimistic concurrency, and the absence of `club_id`; and
- architecture tests enforce capability project-reference isolation, API use of
  public composition boundaries, internal implementation visibility, module
  migration ownership, and persistence constraints.

The following remain unimplemented validation targets rather than claims of
current evidence:

- domain Dapper mappings, idempotency, authorization, immutable lineage,
  registry versions, analysis recovery, and state-plus-outbox atomicity;
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
  design compares modular-monolith, persistence, migration, CQRS, concurrency,
  outbox, lineage, workflow-record, and transport-adapter alternatives. It does
  not complete the existing comparative evidence for production orchestration,
  identity, client sharing, Analyst Manager UI/runtime, or Analyst
  image/runtime choices.
- **Consequences and operational tradeoffs understood — partially met.**
  Capability project isolation, PostgreSQL-backed local host composition,
  migration-gated readiness, transaction boundaries, and local/test
  database-role isolation are exercised. Duplicate delivery, restart recovery,
  production identity mapping, backup/restore, lifecycle controls, service
  objectives, capacity, and complete workflow failure behavior remain
  unevidenced.
- **Prototype, measurement, or implementation evidence supports the choice —
  partially met.** The .NET 10 host, Aspire PostgreSQL composition, operational
  API surface, shared persistence foundation, capability registration
  boundaries, and structural and PostgreSQL tests are implemented. Domain
  persistence, publication, secure BFF identity, both client shells,
  cross-runtime schema agreement, reproducible Analyst images, and the
  Avalonia/OCI platform matrix remain unsupported by implementation evidence.
- **Mature enough to govern subsequent implementation — not met for
  promotion.** The foundation can guide further reversible implementation, but
  the incomplete identity, client, Scheduler, Analyst runtime, production
  operations, and security/lifecycle evidence prevents the production profile
  from becoming production-ready or implementation-proven.

The platform foundation has demonstrated that Aspire starts the local API with
PostgreSQL, readiness follows migration success, and architecture tests enforce
the capability dependency and visibility boundaries. Before treating the
remaining choices as implementation-proven, evidence must demonstrate:

- PostgreSQL integration tests cover domain Dapper mappings, outbox recovery,
  and duplicate delivery in addition to the implemented foundation scenarios
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
