# Contracts And Compatibility

This document defines the planned authority, ownership, versioning,
compatibility, and validation rules for future SocAlytics machine-readable
contracts. It refines the
[Platform Implementation Profile](platform-implementation.md) without changing
the domain responsibilities in the owning architecture topics.

## Contract Authority

No machine-readable contracts exist yet. Until implementation introduces them,
the architecture topics linked below are authoritative for behavior and
semantics. Future canonical machine-readable contracts will live under a
repository-root `contracts/` directory, whose index will identify every
published artifact, owner, version, example, and validation command.

OpenAPI 3.1 documents are authoritative for the REST transport surface.
JSON Schema draft 2020-12 documents are authoritative for reusable cross-language
payloads, including Analyst jobs and results. Generated source, Pydantic models,
C# types, TypeScript types, examples, and prose illustrations are consumers of
those artifacts and never replace them as contract authority.

## Semantic Ownership

Machine-readable representation does not move domain ownership:

| Contract family | Semantic owner | Owning architecture |
| --- | --- | --- |
| REST operations, shared API errors, and transport behavior | Control plane/API | [Platform Implementation Profile](platform-implementation.md) |
| Analysis runs, jobs, dependencies, and completion fencing | Scheduler and Job Registry | [Job Processing](job-processing.md) and [Analyst Runtime and Recovery](analyst-runtime-and-recovery.md) |
| Capability declarations | Model/Capability Registry | [Analysts, Models, and Hardware](analysts-models-and-hardware.md) |
| Logical-segment identity, boundaries, and manifests | Segment Service/data plane | [Match Data Pipeline](match-data-pipeline.md) |
| Analyst job and result payload semantics | Analyst capability owners | [Analysts, Models, and Hardware](analysts-models-and-hardware.md) |
| Contract syntax, reference closure, examples, compatibility, and generation evidence | Future contract pipeline | This document and the future contract index |

When a schema and its owning architecture disagree, the owner resolves the
semantic discrepancy before the schema is accepted. A schema must not silently
redefine domain behavior.

## Generated Client Boundary

Kiota is planned to generate TypeScript, C#, and Python transport clients from
the same bundled OpenAPI document. Generation will write only to ignored
temporary output. Generated clients will be compile-tested but will not replace
application domain models.

Each future application or SDK keeps generated transport code behind a local
adapter that owns mapping, workflow policy, and domain types. This rule does not
decide the Open / Blocking client code-sharing question in
[Client Applications](client-applications.md).

## Scope And Dependencies

The first future contract baseline will cover the documented Analyst workflow: shared
resources and errors, logical segments, Analyst jobs and result manifests,
capability declarations, accepted upstream facts, and fenced attempt
completion. It excludes service implementation, unrelated administration and
identity APIs, deployment contracts, production SDK adapters, and AsyncAPI.

The accepted logical-segment architecture is an input. Future
machine-readable schemas will encode its decisions without reopening identity,
boundaries, publication, retry, or migration behavior. Agent-specific schemas
and operations will follow the same conventions when their implementation
slice begins.

## Versioning And Compatibility

The initial REST major is `/api/v1`. OpenAPI documents and reusable schemas
declare exact semantic versions. Schemas use a stable major-version `$id` so
additive revisions describe one compatible contract family.

Additive changes are allowed within a major version. The following are breaking:

- removing or renaming an operation, property, parameter, or response
- adding a required input or making an optional value required
- narrowing a type, format, range, pattern, union, or accepted enum set
- tightening object openness for data previously accepted
- changing established status-code or structured-error semantics

A breaking change introduces side-by-side `/api/v2` and v2 schema identities;
the prior major remains addressable for its supported compatibility window.

## Representation Conventions

- Authoring references are repository-relative and must resolve offline.
- Schema `$id` values and OpenAPI `operationId` values are globally unique.
- JSON properties use lower camel case.
- Required and nullable are independent; null is accepted only when explicit.
- Timestamps use RFC 3339 UTC date-time values.
- Durations use nonnegative numeric seconds unless an owning contract states a
  stricter positive invariant.
- Domain identifiers are opaque nonempty strings and have no inferred format.
- Time windows are half-open: start is inclusive and end is exclusive.
- Paginated collections use an `items` array and an opaque `continuationToken`.
- Errors use one structured envelope with a stable code, message, correlation
  identifier, and optional field violations.
- Idempotency keys are opaque nonempty strings. Fencing tokens are positive
  integers whose ordering is interpreted only by the owning workflow.
- SHA-256 digests use `sha-256:<64 lowercase hexadecimal characters>`.
- Optimistic concurrency uses HTTP validators. A representation of one mutable
  aggregate returns its `version` as a strong ETag, and every edit of it (a
  change that replaces or modifies fields the client read) requires `If-Match`
  with that ETag: a missing `If-Match` is rejected with
  `428 Precondition Required`, a stale one with `412 Precondition Failed`.
  Lifecycle state-transition commands (for example approve, reject, revoke,
  complete, finalize, deactivate, or reactivate) do not require `If-Match`;
  they are guarded by the aggregate's current state and by retry keys, and a
  transition that the current state does not allow is rejected with
  `409 Conflict`. Transitions still advance the `version`, so a later edit
  based on an older ETag fails.
  Immutable resources use their identity or digest as a strong ETag. Views and
  projections that combine several aggregates return a weak ETag derived from
  the versions or digests of everything they contain; it serves caching
  (`If-None-Match`) only, and such a representation includes the `version` of
  each aggregate a client may change, because writes always target one
  aggregate, never a view. Non-HTTP consumers (events, agent claims, offline
  sync) use the `version` value directly. Clients compare versions and ETags
  only for equality; a version may advance by more than one between two reads.

## Validation Authority

Once contracts exist, one repository contract command will be the deterministic
authority for syntax, dialect and version metadata, offline references, unique
identifiers, OpenAPI linting and bundling, example outcomes, and same-major
compatibility. The same fixture corpus will also be validated independently at
.NET and Python boundaries. Kiota generation and compilation for TypeScript,
C#, and Python will provide transport-consumer evidence without making
generated code authoritative.

Contract completion additionally requires review by the API/control-plane,
Scheduler/Job Registry, Model/Capability Registry, Segment Service/data-plane,
and Analyst SDK/container owners.

---

Related architecture: [Index](README.md) |
[Platform Implementation Profile](platform-implementation.md) |
[Job Processing](job-processing.md) |
[Match Data Pipeline](match-data-pipeline.md) |
[Analysts, Models, and Hardware](analysts-models-and-hardware.md).
