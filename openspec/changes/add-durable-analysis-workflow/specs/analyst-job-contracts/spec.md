# Analyst Job Contracts Specification

## Purpose

Define the first canonical, versioned machine-readable contracts for hardware-neutral Analyst work and attempt-fenced completion while preserving immutable workflow and result lineage.

## ADDED Requirements

### Requirement: Canonical schemas are versioned and validate offline

The repository SHALL publish canonical JSON Schema draft 2020-12 artifacts for Analyst job and completion contract families. Each schema SHALL declare a globally unique stable major-version `$id`, exact semantic version metadata, closed offline-resolvable references, and examples that are validated by the repository contract command.

#### Scenario: Contract artifacts are validated

- **WHEN** the repository contract validation command runs without network access
- **THEN** schema syntax, dialect, semantic versions, unique identifiers, reference closure, examples, and same-major compatibility all pass deterministically

#### Scenario: Breaking contract change is proposed

- **WHEN** a property is removed or renamed, a required input is added, an accepted value is narrowed, or established completion semantics change
- **THEN** compatibility validation rejects the change within the existing major and requires a side-by-side new major identity

### Requirement: Job contracts preserve hardware-neutral immutable snapshots

The job contract SHALL identify the contract version, message, stamp, Analysis Run, Workflow Node, Logical Job, readiness generation, capability and schema versions, execution scope, immutable source and accepted-upstream-result lineage, pinned Analyst profile, OCI image and model digests, resource requirements, attempt budget, lease and timeout policy, and correlation context. It SHALL NOT contain a selected Manager, host, runtime product, accelerator model, or container-private preprocessing choice.

#### Scenario: Segment-scoped job validates

- **WHEN** a segment-scoped job contains a finalized recording-set version and complete analysis-segment reference with all required pinned versions and digests
- **THEN** it validates as one hardware-neutral Logical Job snapshot

#### Scenario: Runtime-specific job is submitted

- **WHEN** a job attempts to select Docker, Podman, a host, Manager, accelerator model, or spatial inference tile
- **THEN** it is rejected by the canonical contract or semantic validation boundary

#### Scenario: Required lineage is incomplete

- **WHEN** a job omits a required source identity, accepted upstream-result reference, pinned version, or algorithm-qualified digest
- **THEN** validation fails before the payload is accepted for publication or execution

### Requirement: Completion contracts preserve attempt fencing and result lineage

The completion contract SHALL identify its contract version, idempotency key, Analysis Run, Workflow Node, Logical Job, positive attempt number, Execution Attempt, fencing token, expected entity version, result manifest and artifact references, algorithm-qualified digests, capability and schema versions, image and model digests, accepted upstream-result lineage, coverage, outcome, and correlation context.

#### Scenario: Current successful completion validates

- **WHEN** a completion supplies every required identity, pinned version, digest, lineage reference, attempt number, fencing token, expected entity version, and successful result manifest
- **THEN** it passes structural validation and remains subject to authoritative PostgreSQL state and semantic acceptance checks

#### Scenario: Completion omits fencing context

- **WHEN** a completion omits or invalidates its attempt number, Execution Attempt identity, fencing token, or expected entity version
- **THEN** contract validation fails and no result can become accepted

#### Scenario: Structurally valid completion is stale

- **WHEN** a completion passes schema validation but references a stale attempt or mismatched immutable job snapshot
- **THEN** semantic acceptance rejects it without changing the schema or accepted workflow state

### Requirement: Contract representation is deterministic and secret-free

Contract values SHALL follow the repository conventions for lower camel case properties, opaque identifiers, RFC 3339 UTC timestamps, nonnegative numeric-second durations, positive fencing tokens, and algorithm-qualified SHA-256 digests. Job and completion payloads SHALL exclude credentials, presigned URLs, broker connection details, video bytes, unrestricted logs, and mutable storage locations.

#### Scenario: Sensitive transport value is present

- **WHEN** a job or completion example contains a credential, presigned URL, connection string, or broker endpoint
- **THEN** contract validation or security fixtures reject it

#### Scenario: Equivalent payload is retried

- **WHEN** an equivalent semantic completion is serialized for idempotent replay
- **THEN** its canonical request representation produces the same request digest independent of JSON property ordering
