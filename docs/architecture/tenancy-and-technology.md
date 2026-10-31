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

### Stamp Persistence

The control-plane foundation implements the stamp's database side as
one PostgreSQL database with one application schema, `socalytics`, holding the
singleton Club root and the `Club > Season > Team > Match` hierarchy,
membership and Club/Team grants, immutable recording lineage, and durable run
and accepted-result lineage. The schema also holds the Registry's immutable
capability declarations keyed by image digest, content-addressed Analyst
profiles, immutable workflow definitions, and the stamp's default workflow.
Shared migration history is kept
apart in `socalytics_migrations`; it owns no domain records.

Architecture and PostgreSQL tests verify that the Club row is a singleton,
hierarchy parents are required, protected resources resolve to Team scope,
revoked and cross-Team grants do not authorize access, and internal tables,
commands, and events carry no `club_id`. These checks validate the
single-club invariant only. They do not approve shared physical production
infrastructure, credentials, residency, encryption, retention, or other
production-policy values.

## Initial Production Profile

The initial production orchestrator is **Provisional Docker Compose**. One
Compose project namespace represents one deployment stamp and uses
Compose-managed PostgreSQL, NATS JetStream, and S3-compatible storage on
durable, stamp-dedicated logical volumes and networks. Physical hosts may be
shared only when database, storage, messaging, credentials, configuration,
network, volume, and resource isolation remain independently verifiable. A
self-hosted evaluation composition implements one stamp per Compose project
namespace on stamp-scoped volumes and networks as development evidence; it is
not a production profile.

.NET Aspire remains the local-development composition and is not the
production orchestrator. The complete environment inventory, persistence,
backup/restore, service-objective, telemetry, capacity, incident, upgrade, and
readiness contract is defined in
[Production Deployment and Operations](production-operations.md). Host,
provider, ports, secret source, resource values, and numeric objectives remain
profile-specific and Open / Blocking until approved with evidence.

Azure Container Apps is the **Provisional target cloud profile**. It is
unverified and Open / Blocking until its own profile values and evidence exist.
Compose stays a supported self-hostable profile, so both profiles run the same
images and preserve the same single-club stamp invariants, and application code
takes no dependency that only one of them can satisfy.

The profile also adopts exact approved versions from the
[Security-Governance Decision Register](security-and-data-governance.md#security-governance-decision-register)
and a valid profile-scoped `GOV-SIGN-001`. This gate does not reopen the
single-club stamp: each stamp still has exactly one club and dedicated logical
data, messaging, credentials, configuration, networks, and volumes. PostgreSQL
remains domain and workflow authority, NATS remains transport only, and
application artifacts remain cloud-neutral. Any unresolved or mismatched governance
entry keeps production promotion blocked.

---

## Technology Stack

The detailed implementation baseline, its current evidence, and the remaining
validation criteria are defined in the
[Platform Implementation Profile](platform-implementation.md#acceptance-evidence).

### Client Applications

- React and TypeScript web UI built with Vite
- React Router, TanStack Query, and MUI with a SocAlytics theme
- React and TypeScript Web UI as a Progressive Web App with an offline mode for
  coaches
- separately packaged application shells with selected environment-neutral
    packages shared through a `pnpm` workspace
- future offline boundaries governed by
    [Client Applications](client-applications.md)

### Control Plane

- .NET 10 LTS and C#
- .NET Aspire for development composition and service defaults
- ASP.NET Core monolith structured in Domain, Application, Infrastructure, and
  API layers, including the backend-for-frontend
- Agent Orchestration functional area with PostgreSQL state, migrations,
  projections, and transactional outbox in the shared platform database
  (implemented for direct specialist invocations as development evidence)
- REST and JSON with OpenAPI; Kiota-generated clients
- PostgreSQL
- Npgsql, Dapper, logical CQRS, and plain typed handlers
- trigger-managed `version` columns for optimistic concurrency
- DbUp and one ordered sequence of versioned PostgreSQL SQL migrations,
  applied only by the one-off `SocAlytics.Platform.Migrator` host
- ASP.NET Core Identity with Dapper stores, always-available local accounts,
    and optional external OpenID Connect providers
- provider-neutral SMTP submission for transactional email with MailKit
    through a dedicated transactional email outbox table, with a Mailpit
    mail catcher locally (implemented as development evidence; the production
    relay stays Open / Blocking; see
    [Email Notifications](email-notifications.md))
- JSON Schema 2020-12 validation of contract payloads with JsonSchema.Net
    (MIT 8.x line)

### Event Backbone

- NATS
- JetStream
- NATS.Net v3 client (`NATS.Client.JetStream`)
- PostgreSQL transactional outbox and idempotent consumers

### Data Plane

- RustFS
- S3 Protocol

### Execution Plane

- .NET 10 Generic Host and Avalonia Analyst Manager
- OCI Runtime Adapter
- Externally managed runtime profiles (Docker Desktop implemented and verified
  against a Linux engine, awaiting manual Windows evidence)
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
- MCP server as a separate Python OCI service on the official MCP Python SDK
  (decided, not yet implemented)
- Hermes as a separate Python OCI runtime with no database access (its first
  slice for direct Specialist Agents in `src/agents/hermes` is decided, not yet
  implemented; direct Coach Agents and delegated consultations are decided,
  not yet implemented)
- Microsoft Agent Framework behind Hermes-owned adapters
- OpenAI API as the default model provider
- GitHub Models as a manually selected development alternative
- a deterministic scripted model provider for Development and Test only
  (decided, not yet implemented)

For each invocation, Hermes resolves versioned prompt,
model/deployment, tool-policy, and safety-policy artifacts, and for a Coach
Agent its dimension-set, philosophy-profile, and modifier-policy artifacts
and any consultation-policy artifact (decided, not yet implemented), under the
authenticated user's team and resource authorization; they are the artifacts
Agent Orchestration pinned from the platform's agent catalog at submission
(catalog pinning implemented as development evidence). Workloads claim durable
work through versioned Agent Orchestration API operations (implemented for
direct specialist invocations as development evidence; Hermes itself is not
implemented). PostgreSQL remains
authoritative; transactional-outbox notifications on JetStream are minimal and
at least once. A provider-neutral adapter prevents provider SDK types from
entering catalog or orchestration contracts, and no silent provider fallback is
allowed. Prompt policy describes agent behavior; MCP and the API remain the
enforcement boundary for platform access. See the
[Intelligence Agent Catalog](intelligence-agent-catalog.md).

### Engineering Baseline

- Visual Studio Code is the primary development IDE. Opening the repository
  root on the default branch and pressing F5 must run the full application in
  debug mode, initially on Windows. This is an adopted requirement awaiting
  implementation, not current executable evidence; see the
  [local development and debugging contract](platform-implementation.md#local-development-and-debugging).
- one monorepo using native .NET, `pnpm`, and `uv` workspaces
- cloud-neutral OCI production artifacts
- OpenTelemetry with Aspire ServiceDefaults for .NET components
- xUnit v3, Shouldly, NSubstitute, Vitest, Testing Library, pytest,
  Playwright, and Testcontainers

The planned .NET foundation, layered project structure, data organization, and
validation criteria are recorded in the
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
