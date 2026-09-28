# Deployment and Technology

This document summarizes the cross-cutting single-club stamp requirements and
the current technology selections. Detailed behavior remains in the linked
topic documents.

## Single-Club Deployment Stamp

Each SocAlytics deployment stamp represents exactly one club. The club is the
implicit domain root, so requests, jobs, manifests, and persistence records do
not carry a cross-club discriminator. Each stamp owns dedicated logical
database, object-storage, queue, credential, and configuration resources;
physical hosts and managed service instances may be shared between stamps.

The organizational hierarchy inside a stamp is `Club > Season > Team > Match`.
Users belong to the club, while non-admin access is granted at team level and
inherits to matches, recordings, analytics, and agent evidence. See
[Terminology and Principles](terminology-and-principles.md) for the canonical
ownership and lifecycle rules.

Logical isolation does not relax encryption, residency, privileged-access, or
copy-lifecycle obligations when physical infrastructure is shared. Those
requirements are defined by
[Security and Data Governance](security-and-data-governance.md).

```mermaid
flowchart TB

    subgraph Stamp[Single-Club Deployment Stamp]
        API[API]
        Queue[NATS Analysis Jobs]
        Storage[(Object Storage)]
        Database[(Club Database)]
    end

    Manager[Stamp-Registered Analyst Manager] --> Adapter[Runtime Adapter]
    Adapter --> Analyst[Analyst]

    Queue -->|Pull| Manager
    Analyst <--> Storage
    Manager -->|Completion callback| API
    API --> Database
```

Requirements:

- exactly one club per stamp
- dedicated logical data, messaging, credentials, and configuration per stamp
- team-scoped authorization within the club hierarchy
- stamp-specific analytics and model tracking
- stamp-registered Analyst Managers

### Implemented Local Persistence Foundation

Aspire local composition provisions one PostgreSQL database for the platform.
The shared `SocAlytics.Platform.Persistence` boundary owns module-neutral
connection, transaction, concurrency, and migration infrastructure, including
the `socalytics_migrations.history` journal; it owns no domain records. The six
modules own their schemas: `club`, `identity_access`, `recordings`, `registry`,
`analysis`, and `agent_orchestration`. Their initial embedded migrations create
only their schema and grants, not domain tables.

Local and test databases use a schema-owner role and a narrower runtime role
for each module. Runtime roles cannot read or write peer schemas, and the
bootstrap connection is restricted to migration orchestration. DbUp applies
registered migrations in deterministic module and sequence order after
preflighting applied SHA-256 checksums; each script and its history record are
committed together. API readiness depends on the database and successful
migrations, while `/alive`, `/health`, and `/openapi/v1.json` remain the only
operational routes.

PostgreSQL integration and architecture tests verify schema ownership,
cross-schema denial, migration behavior, and the absence of `club_id` in
product persistence artifacts and database catalogs. This foundation does not
implement a singleton Club row, hierarchy, membership, authorization, or other
domain tables. It preserves the single-club stamp invariant without introducing
a `club_id` discriminator. The local/test role semantics do not select
production identities or credentials, and these tests do not approve shared
physical production infrastructure, residency, encryption, retention, or
other production-policy values.

## Initial Production Profile

The initial production orchestrator remains **Provisional Docker Compose** and
is not implemented or validated by the local Aspire composition. The proposed
profile assigns one Compose project namespace to a deployment stamp and uses
Compose-managed PostgreSQL, NATS JetStream, and S3-compatible storage on
durable, stamp-dedicated logical volumes and networks. Physical hosts may be
shared only when database, storage, messaging, credentials, configuration,
network, volume, and resource isolation remain independently verifiable.

.NET Aspire remains the local-development composition and is not the
production orchestrator. Production database and service names, hosts, ports,
role-to-identity mapping, credentials, secret sources, and deployment values
remain unresolved. The complete environment inventory, persistence,
backup/restore, service-objective, telemetry, capacity, incident, upgrade, and
readiness contract is defined in
[Production Deployment and Operations](production-operations.md). Host,
provider, ports, secret source, resource values, and numeric objectives remain
profile-specific and Open / Blocking until approved with evidence.

The profile also adopts exact approved versions from the
[Security-Governance Decision Register](security-and-data-governance.md#security-governance-decision-register)
and a valid profile-scoped `GOV-SIGN-001`. This gate does not reopen the
single-club stamp: each stamp still has exactly one club and dedicated logical
data, messaging, credentials, configuration, networks, and volumes. PostgreSQL
remains domain and workflow authority, NATS remains transport only, and the
architecture remains cloud-neutral. Any unresolved or mismatched governance
entry keeps production promotion blocked.

---

## Technology Stack

The detailed planned implementation baseline and validation criteria are
defined in the
[Platform Implementation Profile](platform-implementation.md#executable-evidence-and-remaining-validation).

### Client Applications

- React and TypeScript web UI built with Vite
- React Router, TanStack Query, and MUI with a SocAlytics theme
- React, TypeScript, and Electron coach desktop client
- separately packaged application shells with selected environment-neutral
    packages shared through a `pnpm` workspace
- future offline boundaries governed by
    [Client Applications](client-applications.md)

### Control Plane

- .NET 10 LTS and C#
- .NET Aspire for development composition and service defaults
- ASP.NET Core modular monolith and backend-for-frontend
- Agent Orchestration module with module-owned PostgreSQL state, migrations,
  projections, and transactional outbox
- REST and JSON with OpenAPI; Kiota-generated clients
- PostgreSQL
- Npgsql, Dapper, logical CQRS, and plain typed handlers
- DbUp and module-owned versioned PostgreSQL SQL migrations
- ASP.NET Core Identity with Dapper stores, always-available local accounts,
    and optional external OpenID Connect providers

### Event Backbone

- NATS
- JetStream
- PostgreSQL transactional outbox and idempotent consumers

### Data Plane

- RustFS
- S3 Protocol

### Execution Plane

- .NET 10 Generic Host and Avalonia Analyst Manager
- OCI Runtime Adapter
- Externally managed runtime profiles (Docker Desktop first to validate)
- OCI Images

### Analytics

- Python projects managed by `uv` with `pyproject.toml` and committed lockfiles
- JSON Schema contracts with Pydantic models in the Analyst SDK
- RT-DETRv2-S baseline and YOLOX-S/M challenger
- PyTorch training and ONNX export
- ONNX Runtime CPU/CUDA inference
- Optional TensorRT optimization
- OpenCV and classical numerical and machine-learning libraries
- SAHI slicing and postprocessing after license and dependency audit
- HailoRT and Coral Edge TPU runtime-specific adapters with fixed-shape
    compiled artifacts

Exact versions and artifacts are controlled by the Model/Capability Registry
and remain Provisional pending license, provenance, quality, runtime, and cost
evidence. Coral profiles additionally require fully 8-bit-quantized TensorFlow
Lite artifacts and matching Edge TPU compiler and runtime versions.

### AI

- MCP with an explicit agent-safe read-only API allowlist
- Hermes as a separate Python OCI runtime with no database access
- Microsoft Agent Framework behind Hermes-owned adapters
- OpenAI API as the default model provider
- GitHub Models as a manually selected development alternative

For each invocation, Hermes resolves versioned prompt,
model/deployment, tool-policy, and safety-policy artifacts under the
authenticated user's team and resource authorization. Hermes claims durable
work through versioned Agent Orchestration API operations. PostgreSQL remains
authoritative; transactional-outbox notifications on JetStream are minimal and
at least once. A provider-neutral adapter prevents provider SDK types from
entering catalog or orchestration contracts, and no silent provider fallback is
allowed. Prompt policy describes agent behavior; MCP and the API remain the
enforcement boundary for platform access. See the
[Intelligence Agent Catalog](intelligence-agent-catalog.md).

### Engineering Baseline

- one monorepo using native .NET, `pnpm`, and `uv` workspaces
- cloud-neutral OCI production artifacts
- OpenTelemetry with Aspire ServiceDefaults for .NET components
- xUnit v3, Shouldly, NSubstitute, Vitest, Testing Library, pytest,
  Playwright, and Testcontainers

The planned .NET foundation, project and schema ownership, and validation
criteria are recorded in the
[Platform Implementation Profile](platform-implementation.md).

---

Related architecture: [Index](README.md) |
[Platform Implementation Profile](platform-implementation.md) |
[Client Applications](client-applications.md) |
[Job Processing](job-processing.md) | [Analyst Manager](analyst-manager.md) |
[Analyst Capability Catalog](analyst-capability-catalog.md) |
[Intelligence and Agents](intelligence-and-agents.md) |
[Intelligence Agent Catalog](intelligence-agent-catalog.md) |
[Production Deployment and Operations](production-operations.md)
