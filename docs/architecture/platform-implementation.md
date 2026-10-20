# Platform Implementation Profile

This document defines the implementation baseline for SocAlytics.
It refines the logical components in the [architecture overview](overview.md)
without changing their domain responsibilities. The source and runtime choices
in this profile are adopted architecture; production readiness and operational
values remain **Provisional / Blocking production** as governed by
[Production Deployment and Operations](production-operations.md). The
executable platform provides the .NET 10 solution, local Aspire composition of
PostgreSQL, NATS JetStream, S3-compatible RustFS storage, the one-off Migrator,
and the API, the operational API surface, the layered project structure, and
the persistence, club and identity, recording lineage and upload, durable
analysis workflow, and Analyst Manager registration foundations described
below, with host, architecture, contract, and integration tests. Analyst
execution exists on the Docker Desktop runtime profile; the Web UI's
offline mode, deployment configuration, and production-readiness evidence do not exist yet.
External OpenID Connect sign-in with Club Admin linking exists as development
evidence against a test provider. The
Web UI's installable PWA shell and service worker exist as development evidence.

## Source And Runtime Baseline

SocAlytics uses one `src` root with these first-level ownership areas:

- `src/platform` owns the platform API, control plane, and supporting platform
  services; the API, Migrator, and their layers are implemented
- `src/clients` owns the Web UI, a Progressive Web App whose foundation,
  including its service worker and install and update flow, without offline mode, is implemented under `src/clients/web`
- `src/agents` owns future intelligence-agent runtimes and agent-specific
  integration code
- `src/analysts` owns the Analyst Manager, whose first desktop slice is
  executable under `src/analysts/manager`, and the future Analyst SDK and
  Analyst capability implementations across their required runtimes

The executable platform projects are peers under `src/platform`, except for the
test projects grouped under `src/platform/Tests`:

- `SocAlytics.Platform.Api`
- `SocAlytics.Platform.AppHost`
- `SocAlytics.Platform.Migrator`
- `SocAlytics.Platform.ServiceDefaults`
- `SocAlytics.Platform.Domain`
- `SocAlytics.Platform.Application`
- `SocAlytics.Platform.Infrastructure`
- `Tests/SocAlytics.Platform.Host.Tests`
- `Tests/SocAlytics.Platform.Architecture.Tests`
- `Tests/SocAlytics.Platform.Contracts.Tests`
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
failure outcome. It ships as its own OCI image and is the only
component that is meant to receive database access able to create or alter data
structures; the API image contains no migration execution path.

Executable components use or will use the native workspace and dependency tools
of each ecosystem:

- .NET solution and projects for C# components
- npm for the Web UI: `src/clients/web` is currently one npm project with a
  committed `package-lock.json`. The `pnpm` workspace for separately packaged
  TypeScript client applications and selected environment-neutral shared
  packages, as described by [Client Applications](client-applications.md),
  remains the planned design once such packages exist.
- `uv`, `pyproject.toml`, and committed lockfiles for Python projects
- versioned OpenAPI and JSON Schema documents for cross-language contracts: the
  platform's versioned OpenAPI document is committed as the snapshot
  `src/platform/SocAlytics.Platform.Api/openapi/v1.json`, from which the Web UI
  builds its generated client, while JSON Schema contract documents follow
  their own features

The platform API and control-plane baseline is ASP.NET Core on .NET 10 LTS; the
initial host implements that runtime choice. The Python ecosystem tooling is
future until its owning source area gains executable projects.
The platform API, the Migrator, and the Web UI are cloud-neutral OCI images;
the Analyst images and Hermes keep their own status and are not yet built.
.NET Aspire owns local
development composition, service discovery, health checks, OpenTelemetry
defaults, and developer dependencies. Aspire is not the production
orchestrator.

The images of the platform API, the Migrator, and the Web UI are implemented and
verified by Docker-backed tests in `SocAlytics.Platform.Images.Tests`; they are
neither deployment support nor production readiness, and no registry,
publication, or production profile value is decided (see the
[container-image contract](../../specs/archive/20261018-092503-oci-images/contracts/container-images.md)).
Each has one Dockerfile next to the project it packages
(`SocAlytics.Platform.Api`, `SocAlytics.Platform.Migrator`, and
`src/clients/web`) with a Dockerfile-specific allow-list ignore file, and is
built with the Docker CLI from the repository root. The multi-stage builds pin
every base image by digest: chiseled .NET runtime images for the API and the
Migrator and the unprivileged nginx image for the Web UI. The images run as
fixed non-root users (UID 1654 for the .NET images, UID 101 for the Web UI) on
a read-only root filesystem with `/tmp` as the only writable location, read
their configuration only from environment variables, and declare no
in-container health probe, so orchestrators probe `/alive` and `/health` over
HTTP. Every build requires the source revision and one release version in the
Web UI's `YYYY.MMDD.HHMMSS` shape and writes both to the standard OCI revision
and version labels, so the three images of a release carry the same values.
The API image contains neither the Migrator nor DbUp. The Web UI image itself
forwards `/api` and `/alive` to the API container, so every profile gets one
origin from the two images.

Docker Compose is the initial deployment mechanism. It will compose the
platform API and Web UI as separate OCI containers: the API image is built from
the platform source area, while the Web UI image is built from the client source
area and release-coupled to the platform API; only the Compose composition
itself is not implemented. Future
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

A self-hosted evaluation composition of one stamp is decided but not
implemented yet (see the
[Compose stamp contract](../../specs/20261018-094332-compose-stamp/contracts/compose-stamp.md)).
It lives in `src/platform/deploy/compose/` as one Compose file, an environment
file template, and its documentation, and is started under one Compose project
name per stamp; it fixes no container, network, or volume name. It runs the
platform API, Migrator, and Web UI images from image references that the
operator must supply, and PostgreSQL, NATS JetStream, and RustFS from images
pinned by digest, each with a health check and a stamp-scoped named volume.
The Migrator is a one-shot service, and the API depends on it with
`condition: service_completed_successfully`. Four project-scoped networks keep
PostgreSQL, NATS, storage, and the Migrator on internal networks, give the API
one egress network for its configured external endpoints such as the SMTP
relay, and publish only the Web UI, on the host's loopback interface. The API
runs in its production environment with NATS JWT authentication, the roles
of the committed initialization script, the bucket created by its existing
startup option, and an operator-supplied SMTP relay instead of a mail catcher.
A setup console, `SocAlytics.Platform.StampSetup`, writes every secret and the
NATS operator, stamp account, and system account trust material into a new
stamp directory outside the repository. The readiness rule is unchanged, so a
stamp reports healthy only once its Registry holds a resolvable default
analysis workflow, and the browser's direct recording uploads are unavailable
because object storage is not published. The composition is an evaluation
aid: it is not a production profile, passes no production preflight, and
does not replace the Aspire AppHost as the supported local entry point.

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

### Data Organization

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
covers durable runs, DAG state, logical jobs, attempts, accepted-result
references, and the indexed facts of accepted results (immutable rows written in
the acceptance transaction). Segment materialization runs on demand within the control-plane
deployment unless measured scaling later justifies extraction; it is decided,
not yet implemented, as a background worker of the API host that needs the
pinned FFmpeg and ffprobe toolchain on the host (see
[On-Demand Video Segmentation](match-data-pipeline.md#on-demand-video-segmentation)).

### Persistence And CQRS

The persistence foundation implements PostgreSQL access with Npgsql and Dapper,
DbUp migrations applied by the Migrator, explicit units of work, and
trigger-managed versions. The domain tables of the Club, Identity Access,
Recordings, Registry, and Analysis areas and the transactional outbox, published
to NATS JetStream, build on it.

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

Database changes and outgoing events commit atomically through a
PostgreSQL transactional outbox (`socalytics.outbox_messages`). The API process
hosts the background workers: an outbox publisher that delivers pending
records to NATS JetStream at least once with bounded retries and records each
outcome, a retention worker that removes only records whose publication
outcome is known, the Job Monitor that detects expired attempt leases from
durable state, the consumer of `matches.recordings-finalized`, and publication
reconciliation at startup and after transport reconnection. Workers claim rows
with `FOR UPDATE SKIP LOCKED`, so several API replicas run them safely
without leader election, and they add no deployable. Consumers and completion
handlers remain idempotent.

Encryption, retention, deletion, copy propagation, residency, and audit
requirements for the database, outbox, generated indexes, telemetry, replicas,
and backups are governed by
[Security and Data Governance](security-and-data-governance.md).

### Object Storage

The Recordings area adopts S3-compatible object storage through the S3
protocol only. Infrastructure uses `AWSSDK.S3` with path-style addressing
against a configured endpoint; Application and Domain depend only on a storage
abstraction. Every upload is a multipart upload with composite SHA-256
checksums: the API issues presigned per-part grants that sign each part's
length and checksum, assembles the declared parts itself, and verifies the
storage-reported composite digest and size; it never receives media bytes. A
hosted worker in the API host expires abandoned upload sessions and aborts
their multipart uploads. Locally the Aspire AppHost runs a pinned RustFS
container as a plain container resource, the API creates the development
bucket, and integration tests use the same image through Testcontainers
together with a store conformance probe that any production store must pass.
For the Web UI's direct part uploads in local development, the AppHost
allows the Web UI origin in the local store's CORS configuration
(`RUSTFS_CORS_ALLOWED_ORIGINS`) and passes the store's origin to the Web UI
(`SOCALYTICS_OBJECT_STORAGE_ORIGIN`) for its Content Security Policy.
Bucket provisioning, production storage selection, encryption, credentials,
production browser CORS and origins, public endpoints, grant and session
lifetimes, and lifecycle policy remain governed by
[Production Deployment and Operations](production-operations.md) and
[Security and Data Governance](security-and-data-governance.md).

## API And Identity

The current API implements local accounts, the server-validated BFF session,
anti-forgery protection, membership, club and team roles, team-scoped
authorization, security audit events, and the `Club > Season > Team > Match`
hierarchy, as specified in
`specs/archive/20261005-130701-club-identity-foundation`. It also implements
external OpenID Connect sign-in with linking, as development evidence against
a test provider, as described below. The Web UI's generated Kiota
TypeScript client exists and is used behind the adapters in
`src/clients/web/src/platform/` (see [Client Technology](#client-technology)).
Exposure and access policy for operational endpoints in a production ingress
remain unresolved.

REST with JSON is the primary platform protocol. ASP.NET Core publishes
a versioned OpenAPI description. Kiota generates the Web UI's TypeScript client
at build time from the committed OpenAPI snapshot, which the Host test
`OpenApiSnapshotTests` keeps current; the C# and Python clients remain planned.
Applications keep generated clients behind local adapter boundaries so
transport generation does not define their domain models.

The canonical artifacts, owner map, and compatibility policy are
governed by [Contracts and Compatibility](contracts-and-compatibility.md).

Platform-managed local accounts are always available. ASP.NET Core Identity
provides password hashing, security stamps, recovery tokens, and lockout;
its stores use Dapper and PostgreSQL. SocAlytics offers no MFA for local
accounts: a club that needs MFA signs in through an external OpenID Connect
provider that enforces it, and the platform does not evaluate MFA or
authentication-context claims. A stamp may additionally configure one or
more external OpenID Connect providers. The login UI displays external options
only when configured.

External sign-in is implemented: the API runs the
authorization code flow with PKCE, state, and nonce itself and keeps each
pending sign-in in PostgreSQL, bound to the starting browser by a short-lived
cookie and completable once, so the API still needs no shared key ring; it
validates ID tokens against the provider's published keys and keeps no
provider token after the sign-in. Membership stays platform data: a Club Admin
issues a single-use link credential for a member (a purpose of the existing
one-time credentials), the member redeems it by signing in at the provider,
and the provider's issuer and subject are linked to exactly that member. Only
a redeemed link credential creates a link; claims such as email or name never
find, create, or link members. Club Admins can unlink an identity, which ends
the member's sessions; a provider-side account change takes effect for
existing sessions only through the normal session limits, sign-out, unlinking,
or deactivation, since no refresh token is kept.

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
no API operation, authenticated or not, performs this break-glass recovery.
Members who sign in with a local password and have a verified email address
also recover a forgotten password themselves through a single-use
password-recovery account message (implemented as development evidence; see
[Email Notifications](email-notifications.md#password-recovery)). Recovery
never creates a local password, changes external identity links, grants roles,
or reactivates an account, and completing it ends every session of the member.
Club Admins will also be able to invite a person by email instead of handing
over a credential (decided, not yet implemented; see
[Email Notifications](email-notifications.md#member-invitations)): an
invitation is a pending record that reserves an account name and address, and
the member account, its verified address, and its team roles are created only
when the invitee accepts with a local password through the emailed
single-use link.

Members may register an optional email address, verified through a single-use
confirmation link before any message is sent, and choose which notification
types they receive. The API sends transactional notifications and account
messages through a dedicated transactional email outbox table and a delivery
worker that submits them with MailKit by authenticated, TLS-protected SMTP to
the stamp's configured relay; the Aspire AppHost and tests use a Mailpit
mail-catcher container. Self-service password recovery uses this channel;
invitations may use it through their own specification. The channel and
password recovery are implemented as development evidence against
Mailpit; see [Email Notifications](email-notifications.md).

Analyst Manager registration uses browser device-code pairing initiated by a
club `Registrar` and explicitly approved by a `Club Admin`. Club Admin inherits
the Registrar capability and may self-approve through a separate audited
operation. The AM activates only after proving possession of the protected
device key bound during pairing.

The pairing response's verification URL is built from an optional
verification base address, for stamps whose Web UI and API origins differ, as
in local development; the token, activation, and other machine-facing addresses
and the DPoP target stay on the stamp's public base address. The registration
operations declare typed OpenAPI request and response schemas, so the Web
UI's generated client covers them.

An active AM uses OAuth 2.0 client credentials with `private_key_jwt` and
short-lived DPoP-bound access tokens. API authorization also checks the
authoritative active registration and stamp binding. The durable registration
survives restart, while tokens and scoped NATS credentials expire and rotate.
Administrative revocation blocks API access and credential renewal immediately.
The complete workflow is defined in the
[Analyst Manager architecture](analyst-manager.md#registration-and-stamp-binding).

The Analyst Manager token endpoint is a minimal endpoint in the API's
Registry functional area that supports only this grant; the platform does not
run a general-purpose authorization server for machine clients. Agent workload
clients use a second minimal endpoint of the same kind in the Agent
Orchestration functional area (development evidence; see
[Agent Runtime](#agent-runtime)).

Until a dedicated stamp-operator role is defined, operational workflow
inspection and reconciliation operations under `/api/v1/operations` are
restricted to Club Admins and audited when they change state. Club Admins will
use them from the Web UI's Operations area (decided, not yet implemented; see
[Client Applications → Web UI](client-applications.md#web-ui)).

### Agent Runtime

Agent Orchestration uses the same Dapper, PostgreSQL, DbUp, logical CQRS,
optimistic-concurrency, and transactional-outbox baseline as the rest of the
platform. Its state remains in the stamp database; this profile introduces
neither a second agent database nor a special extraction rule. NATS JetStream
carries only minimal, at-least-once work notifications. Consumers claim durable
work through the Agent Orchestration application handlers and remain idempotent
under duplicate delivery.

The control-plane surface for direct invocations includes canonical
asynchronous submission, private conditional status/result/lineage reads,
workload claim, lease, checkpoint, completion, and failure operations, and
bounded recovery/reconciliation (development evidence). Consultation
operations, lifecycle suppression, and migration readiness remain planned.

Hermes is a planned separate Python OCI deployment with no direct database
access. It uses Microsoft Agent Framework behind local adapters, authenticates
as a stamp-local workload, and uses versioned REST
operations to claim, renew, checkpoint, delegate, and complete work. The
connected Web UI uses the API/BFF for asynchronous
submission and conditional status, result, and lineage polling; it never calls
Hermes directly. The complete persistence, transport, authorization, fencing,
and recovery contract is defined by
[Intelligence and Agents](intelligence-and-agents.md#agent-orchestration-authority).
The Web UI's direct Specialist Agent triage conversations exist as development
evidence. MCP tools, prompt/profile resolution, model execution, and the Web
UI's Coach Agent and consultation workflows remain future implementation work.

The first slice is implemented as development evidence: an `AgentOrchestration`
folder in each platform layer stores conversations, participants, invocations,
attempts, append-only lineage, accepted advice, and workload operation records
in the stamp database; publishes `agent-invocation-ready` notifications from
the transactional outbox to a JetStream work-queue stream; runs a monitor for
lease expiry, attempt deadlines, and scope loss and a reconciliation worker for
missing notifications; and authenticates configured workload clients through a
minimal workload token endpoint with `private_key_jwt` and DPoP. A simulated
agent workload under `src/platform/Tests`, which runs only in Development and
Test and is started by the AppHost, executes direct specialist invocations with
deterministic simulated advice in place of Hermes. See
[Intelligence and Agents](intelligence-and-agents.md#client-transport-and-runtime-protocol).

The agent catalog registry is decided and not yet implemented: a `Catalog`
folder inside the Agent Orchestration functional area of each layer stores
immutable agent catalog artifacts (`agent_catalog_artifacts`), agent versions
with their draft, approved, and retired lifecycle (`agent_versions`), one
enablement row per catalog agent identifier (`agent_enablements`), and the
references pinned into each invocation at submission (`agent_invocation_pins`).
Records are validated at runtime against the embedded `contracts/agent-catalog/`
schemas and addressed by the SHA-256 digest of their canonical JSON. The
catalog enablement replaces the configured list of enabled specialists, and in
Development and Test a seed registers and enables `specialist-tactics` and
`specialist-video-analysis` from the catalog's first-draft prompts with a
simulated model provider. Club Admins will operate the catalog from the Web
UI's Agent Catalog area (decided, not yet implemented; see
[Client Applications → Web UI](client-applications.md#web-ui)). See
[Intelligence Agent Catalog → Prompt And Advice Governance](intelligence-agent-catalog.md#prompt-and-advice-governance).

The MCP-facing application tools are decided and not yet implemented: a `Tools`
folder inside the Agent Orchestration functional area of each layer maps the
`/api/v1/agent-tools` route group, binds each call to the workload's current
attempt and the invocation's pinned tool policy, re-evaluates the initiating
user's authority, reserves the call in the immutable `agent_tool_calls` table
(call-identifier uniqueness and per-attempt limits), runs the read outside the
invocation lock through the same read cores as the member fact and
evidence-media operations, and appends a `tool-call` lineage entry. See
[Intelligence and Agents → MCP Tools](intelligence-and-agents.md#mcp-tools).

The MCP server is decided and not yet implemented: a separate Python OCI
deployment under `src/agents/mcp-server` with no database access, built on the
official MCP Python SDK, that authenticates as its own stamp-local workload
client with `private_key_jwt` and DPoP-bound tokens and exposes the
`/api/v1/agent-tools` operations to the agent runtime as MCP tools. See
[Intelligence and Agents → MCP Layer](intelligence-and-agents.md#mcp-layer).

Hermes' first slice is decided and not yet implemented: a separate Python OCI
deployment under `src/agents/hermes` on Microsoft Agent Framework with no
database access and no durable state, authenticated as its own stamp-local
workload client, that pulls agent work notifications, executes direct
Specialist Agent invocations with the pinned artifacts, reaches evidence only
through the MCP server, and reaches models through a provider-neutral adapter
for the OpenAI API, GitHub Models, and a Development and Test scripted
provider. The `attempt-report` contract's release 1.1.0 adds the pinned
model-selection reference and the resolved parameters to completions, which
Agent Orchestration checks against the pins. Its cross-process evidence runs
the API, MCP server, and Hermes images together with an HTTPS test
certificate authority. See
[Intelligence and Agents → Agent Orchestration Authority](intelligence-and-agents.md#agent-orchestration-authority).

Direct Coach Agent conversations are decided and not yet implemented: a
forward-only migration admits the `user-to-coach` route and stores the applied
philosophy profile on accepted advice; the `attempt-report` contract's release
1.2.0 adds the applied profile to profile checkpoints and completions, which
the Agent Orchestration layers validate against the pins and the attempt's
tool calls; the catalog seed registers, approves, and enables the three Coach
Agents in Development and Test; and the simulated agent workload and Hermes
execute Coach Agent invocations. See
[Intelligence and Agents → Agent Orchestration Authority](intelligence-and-agents.md#agent-orchestration-authority).

Delegated consultations are decided and not yet implemented: a forward-only
migration adds the versioned `agent_consultations` table, whose pending rows
are the parents' waiting edges, links child invocations to their parent and
consultation, admits the `coach-to-specialist` route and the
`waiting-for-consultations` and `synthesizing` states, and stores influence
declarations on accepted advice; the workload operations
`requestAgentConsultation` and `readAgentConsultationOutcomes` serve the
parent's current attempt; the `consultation-policy` catalog artifact, the
`consultation-request` contract, and new releases of the `agent-version`,
`work-notification`, `execution-envelope`, and `attempt-report` contracts
carry the change; the catalog seed adds consulting Coach and Specialist Agent
versions in Development and Test; and the simulated agent workload executes
children and, on request, consults. See
[Intelligence and Agents → Agent Orchestration Authority](intelligence-and-agents.md#agent-orchestration-authority).

## Client Technology

The Web UI is one React and TypeScript Progressive Web App built with Vite.
React Router handles navigation, TanStack Query manages remote server state, and
MUI supplies accessible components under a SocAlytics theme. A hand-written
service worker precaches the integrity-verified application shell of one
release, holds no user data, and is versioned with each release. The Web UI
release is coordinated with the platform API and is a separately
containerized deployment unit built from `src/clients`, served from one origin.
There is no second client application. See
[Client Applications](client-applications.md).

The online-only Web UI foundation is implemented as development evidence under
`src/clients/web`. Its Kiota client is generated at build time from the
committed OpenAPI snapshot `src/platform/SocAlytics.Platform.Api/openapi/v1.json`,
which belongs to the API project and is verified against the running API by a
Host test (`OpenApiSnapshotTests`), so the generated client cannot drift from
the API. The AppHost composes the Web UI with the API on one origin: the Vite
server proxies `/api` and `/alive` to the API, so the browser sends the BFF
session cookie without CORS. In end-to-end mode the AppHost serves the built
Web UI through `vite preview` on a fixed port. Its Content Security Policy
keeps `style-src 'unsafe-inline'`, an accepted and recorded trade-off because
MUI's Emotion runtime injects `<style>` elements and a nonce is not possible
for statically hosted pages; scripts stay locked down. See
[Security And Data Governance](security-and-data-governance.md). The service
worker is implemented as development evidence. The Web UI container image and
its production server configuration, which applies the same policy through one
policy module, are implemented; the production storage origin stays unresolved,
and offline mode is not implemented.

Future offline-mode implementation follows the decisions in
[Client Applications](client-applications.md): an encrypted replica owned by a
dedicated worker, opaque-cursor REST deltas, explicit clips, read-only first
offline delivery, a short-lived entitlement bound to a non-exportable device
key, a store key wrapped through WebAuthn PRF or a non-exportable WebCrypto
key, and a supported-browser matrix proven by a prototype gate.

## Analyst Technology

The Analyst Manager is a per-user application in a separate .NET solution
under `src/analysts/manager` with its own tests. A .NET 10 Generic Host worker
in a library without UI dependencies owns lifecycle, registration, key
providers, signed local state, queue, hardware, autostart, and OCI-runtime
responsibilities. In the tray profile, Avalonia supplies the cross-platform
tray and status UI in the same process; in the headless server profile, a
separate console executable (`socalytics-manager-server`) runs the worker
without Avalonia as a Linux systemd user service controlled by the
local-control CLI. Device keys use Windows CNG, a small Swift
bridge over CryptoKit and the Keychain on macOS, and PKCS#11 through
Pkcs11Interop on Linux. The worker and UI are tested without a display using
Avalonia headless tests; Linux CI uses a SoftHSM2 token, while the Windows and
macOS key stores are verified on those systems. Shared golden fixtures under
`contracts/analyst-manager/` are verified by both the platform and the Manager
test suites so the two implementations of the registration exchanges cannot
drift. Installers and updates are planned with Velopack, pending a spike that
runs unsigned first: per-user installers for Windows and macOS and one Linux
AppImage for both profiles, mounted through FUSE when available and otherwise
started in extract-and-run mode. Signing follows once the publisher is
decided. Each stamp serves the update feed to its Managers as a proxy for the
Manager's GitHub Releases, after verifying their build-provenance attestation;
it offers the releases of its chosen channel and may pin a version; the feed,
channels, and pinning stay planned after the Velopack spike. The minimum
Manager version is implemented through deployment configuration
(`Registry:ManagerVersion:Minimum`, empty by default): the status report
response carries it to each Manager, and the platform refuses queue credentials
and new claims below it. The fleet view, implemented in the Web UI and marking
Managers below the minimum, rests on a Manager status report,
sent with the periodic registration check and stored as the latest snapshot
only, and a fleet read operation for Club Admins.

Managers reach the work queue with NATS decentralized JWT authentication. The
stamp holds an operator key, a stamp account key, and a system account key; the
NATS server trusts the operator and runs the full account resolver. When an
active Manager asks for queue credentials, the platform issues a per-Manager
user JWT that expires after `AnalystWork:QueueCredentialLifetime` and permits
only pulling and acknowledging work on the per-capability consumers
(`analyst-<capability-id>`) that the platform provisions from the default
workflow definition resolved from the Analyst Registry once it resolves. A
Manager is assigned the consumer of an offered capability only when the offered
capability id and semantic version equal the resolved definition's, so a
Manager can neither publish work nor read other Managers' subjects. Revoking or
unregistering a Manager adds its issued user keys to the `revocations` of the
stamp account JWT; the platform re-signs that JWT with the operator key and
pushes it through the system account, which disconnects the Manager. Pending
pushes are retried after failures and restarts. `Nats:Authentication:Mode`
`None` (no authentication) is allowed only in the Development and Test
environments; every other environment requires `Jwt`. Production key custody,
credential lifetimes, rotation, and recovery remain unresolved under
`GOV-CRED-004`; the values above are development evidence. Manager work
execution on the Docker Desktop runtime profile runs real Analyst containers
and is verified end to end against a Linux engine; Docker Desktop on Windows
awaits manual evidence, and automated Windows verification is an open gap. The
simulated runtime and Analyst remain for Development and Test.

Analyst Containers use Python because PyTorch, ONNX Runtime, OpenCV, NumPy,
SciPy, scikit-learn, SAHI, and accelerator-vendor tooling provide the paved
model and computer-vision path. Each runtime image may pin a different Python
minor version when native or vendor compatibility requires it.

Versioned JSON Schema documents are canonical for Analyst job and result
contracts (`contracts/analysis/`); the platform and the Analyst Manager validate
result manifests against them. The planned internal Analyst SDK exposes Pydantic
models and validates input and output at the container boundary, and the
platform independently validates the same schemas. Runtime-specific OCI images use locked dependencies and record
their immutable image, model, and package provenance. Preprocessing and
postprocessing implementation is traced through the immutable OCI image rather
than selected as a separate job artifact.

## Test And Observability Baseline

The platform uses xUnit v3 and Shouldly for the Aspire host
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
Testcontainers runs disposable PostgreSQL, NATS, and RustFS instances for
`SocAlytics.Platform.Integration.Tests`.

## Acceptance Evidence

The platform provides this executable evidence:

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
current and non-current migration states; and
- integration tests cover the club hierarchy, local accounts, sessions, roles,
  team-scoped authorization, and audit evidence; idempotent recording upload,
  immutable recording lineage, and S3 conformance against RustFS; Analyst
  Manager pairing, approval, activation, revocation, and DPoP authentication;
  and analysis run creation, readiness, claims, fenced completion,
  state-plus-outbox atomicity, outbox publication and retention, and recovery
  from NATS outage, restart, and duplicate notifications; and
- integration, host, and browser tests cover member email addresses and their
  verification, notification preferences, transactional email delivery through
  the dedicated outbox including a relay outage, and the pending-request
  notification, using the Mailpit mail catcher; and
- integration and end-to-end tests cover per-object result write grants and
  validated result acceptance (eleven ordered manifest checks, storage-attested
  checksums, rejected manifests ending the attempt as failed, and immutable
  accepted-result manifest records) with a simulated Analyst and the Analyst
  Manager; and
- Docker-backed end-to-end tests run a test Analyst image through the real
  Analyst Manager core on the `docker-desktop` profile against a Linux engine's
  local socket: success with one accepted result, failing, missing-manifest,
  and out-of-memory Analysts, revocation during a hanging Analyst, and Manager
  restart with reconciliation that spares unlabelled containers, leaving
  nothing on the host; and
- integration and end-to-end tests cover the Manager-mediated upstream read
  (current-attempt and input-snapshot scoping, refusals without disclosure,
  per-object read grants, coverage), digest-verified staging with cleanup in the
  Analyst Manager, and simulated match-level lineage equal to the staged
  results; and
- integration and end-to-end tests cover partial-coverage match barriers
  (declaration import and seeding, the readiness rule with a minimum, snapshot-
  fixed coverage in the upstream read and staging, the coverage check of result
  acceptance, and the input coverage counts of the run status read) with a
  simulated Analyst that fails one segment; and
- integration and end-to-end tests cover fact validation (the twelfth ordered
  manifest check), atomic and idempotent fact indexing at acceptance, and the
  member reads `listAnalysisRunFacts` and `listAnalysisRunFactStreams`
  (authorization, keyset paging, range overlap, coverage, provenance, weak
  ETags), including an end-to-end read-back of the simulated Analyst's facts
  and a rejected out-of-window fact artifact; and
- Web UI browser tests cover Analyst Manager pairing, the Club Admin decision
  view, and the fleet view with revocation, and the match workflow and
  recording upload (match creation and editing, direct multipart upload with
  resume, timeline mapping, finalization, and the development CORS access to
  RustFS): the `simulated` project against a simulated API and a fake object
  store, and the `platform` project against the real platform with a scripted
  Manager and RustFS; and the match triage conversations (starting a
  conversation for a run, adding specialists, asking, polling, and reading
  advice with its disclosure and lineage) in the `simulated` project and in the
  `platform-triage` project against the real platform with the simulated agent
  workload; and the Analyst Registry administration for Club Admins (browsing
  the default workflow, images, profile versions, and workflow versions;
  registering images; creating, approving, and retiring profile versions;
  defining workflow versions; and changing the default workflow, with
  confirmations and refusals) in the `simulated` and `platform` projects; and
- a Host test verifies that every Registry operation carries OpenAPI schemas
  from which the Web UI client is generated (`AnalystRegistryOpenApiTests`); and
- a Host test verifies the development CORS allow-list and the storage origin
  the AppHost passes to the Web UI; and
- contract tests validate the canonical schemas, examples, and golden fixtures
  in `contracts/` and the OpenAPI document.

The following remain validation targets rather than claims of current evidence:

- PostgreSQL integration tests cover Dapper mappings and registry versions of
  capability, model, and Analyst-profile records;
- registry transport mapping, runtime conformance beyond the Docker Desktop
  profile on a Linux engine (Podman and Windows), and generated TypeScript,
  C#, and Python client smoke checks pass without introducing duplicate wire
  contracts; and
- telemetry tests cover correlation and operational signals across all areas
  while excluding recording, result, and outbox payload bodies.

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
  Layer dependency rules, host composition with PostgreSQL, NATS, RustFS, and
  the Migrator, local transaction boundaries, local database and broker
  unavailability, duplicate delivery, and analysis restart recovery are
  exercised. Production database access control, backup/restore, lifecycle
  controls, service objectives, and capacity remain unevidenced; workflow
  failure behavior with real Analyst execution is evidenced on the Docker
  Desktop profile against a Linux engine only.
- **Prototype, measurement, or implementation evidence supports the choice —
  partially met.** The .NET 10 host, Aspire local composition, operational
  API surface, layer composition methods, structural tests, the persistence
  foundation (migrations, Migrator, units of work, version triggers), the
  platform API, Migrator, and Web UI OCI images with their Docker-backed
  container tests (not deployment support or production readiness), domain
  persistence, outbox publication to NATS JetStream, local-account BFF
  sessions, S3-compatible upload, Analyst Manager registration, member email addresses with verified, outbox-backed transactional delivery to the Mailpit mail catcher, and result
  write grants with validated result acceptance against a simulated Analyst and a real Analyst container on the Docker Desktop profile (Linux engine), and the Manager-mediated
  read and staging of accepted upstream results, fact indexing, and the member
  fact reads are
  implemented, and the online-only Web UI foundation exists as development
  evidence, including the Analyst Manager pairing, approval, and fleet views,
  the Analyst Registry administration views, the match workflow with recording upload and finalization, and the match
  triage conversations with Specialist Agents. External
  OpenID Connect sign-in with Club Admin linking, unlinking, and coexistence
  with local accounts is evidenced by Integration and Playwright tests against
  a test provider (`mock-oauth2-server`), with production provider values,
  secret custody, and lifetimes (`GOV-CRED-006`) still unresolved. The
  production email relay, sender authentication, and bounce handling (Open / Blocking), the Web UI's offline mode (the PWA installs and
  updates through its service worker as development evidence on Chromium), the other
  Web UI client workflows (the analysis review and the Coach Agent and
  consultation conversations),
  cross-runtime schema agreement, reproducible Analyst
  images, and the Avalonia/OCI platform matrix beyond the Docker Desktop
  profile on a Linux engine (Windows automation, Podman, and the other
  profiles) remain unsupported by implementation evidence. The headless server profile adds automated
  evidence (an executable without Avalonia, the lingering check, the generated
  systemd user unit, the account rule against the simulated runtime, and the
  claimed profile and software key policy), but the manual validation on
  Ubuntu Server 22.04 and 24.04 LTS is open until a person performs it after
  the merge, and the matrix stays incomplete without Linux runtime adapters
  beyond the Docker Desktop adapter's development-only Linux socket.
- **Mature enough to govern subsequent implementation — not met for
  promotion.** The foundation can guide further reversible implementation, but
  the incomplete identity, client, Scheduler, Analyst runtime, production
  operations, and security/lifecycle evidence prevents the production profile
  from becoming production-ready or implementation-proven.

The implementation has demonstrated that Aspire starts the local platform with
health and telemetry defaults, that architecture tests enforce the layer
dependency and visibility boundaries, and that PostgreSQL integration tests
cover Dapper mappings, migrations, optimistic concurrency, outbox recovery, and
duplicate delivery. Before treating the remaining choices as
implementation-proven, evidence must demonstrate:

- optional OIDC login shares the local-account BFF session, with external
  identities linked only through Club Admin link credentials (evidenced by
  Integration and Playwright tests against a test provider; production
  providers and their values are not evidenced)
- Kiota-generated TypeScript, C#, and Python clients compile and pass contract
  compatibility tests
- the Web UI PWA meets the supported-browser evidence for offline mode
  (installation and service-worker updates are evidenced on Chromium; the
  offline-mode rows stay open)
- JSON Schema validation agrees at .NET and Python boundaries (the platform
  and the Analyst Manager already agree on result manifests at the .NET
  boundary)
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
