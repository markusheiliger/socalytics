# Platform Host Specification

## Purpose

Defines the minimum executable platform-host behavior and verification evidence that contributors can rely on before domain and infrastructure capabilities are implemented.

## ADDED Requirements

### Requirement: Platform host has a reproducible development workflow

The repository SHALL provide documented commands that restore, build, and test the platform host from a clean checkout with the supported .NET 10 SDK.

#### Scenario: Contributor validates the platform host

- **WHEN** a contributor follows the documented restore, build, and test workflow from a clean checkout
- **THEN** dependency restoration completes successfully
- **THEN** the platform solution builds without errors
- **THEN** all platform tests pass

### Requirement: Local composition starts the platform API

The repository SHALL provide an Aspire local composition entry point that starts the platform API and reports its operational health without requiring PostgreSQL, NATS JetStream, S3-compatible storage, or other external product infrastructure.

#### Scenario: Contributor starts the local platform

- **WHEN** a contributor runs the documented local composition command
- **THEN** the platform API starts under the local composition environment
- **THEN** the composition reports whether the API is live and ready

### Requirement: Platform API exposes baseline discovery and health behavior

The running platform API SHALL expose liveness, readiness, and a versioned OpenAPI description while exposing no domain operation solely to demonstrate the scaffold.

#### Scenario: Runtime probes inspect the API

- **WHEN** a caller requests the documented liveness and readiness endpoints
- **THEN** each endpoint returns a successful response while the dependency-free host is healthy

#### Scenario: Contributor inspects the API description

- **WHEN** a contributor requests the documented OpenAPI endpoint
- **THEN** the response is a valid OpenAPI document identified as version `v1`
- **THEN** the document contains no scaffold-only domain operation

### Requirement: Adopted platform module boundaries are executable and verified

The platform host SHALL compose the Club, Identity Access, Recordings, Registry, Analysis, and Agent Orchestration modules through public module boundaries, and automated tests SHALL reject prohibited module coupling or host access to module-internal implementation types.

#### Scenario: Host composition is tested

- **WHEN** the platform host smoke test creates the application
- **THEN** all six adopted modules are registered through their public composition boundaries
- **THEN** the dependency-free application starts successfully

#### Scenario: Architecture boundaries are tested

- **WHEN** the platform architecture test suite evaluates project dependencies and type visibility
- **THEN** a module does not depend on another module's internal implementation
- **THEN** the API host does not depend on module-internal implementation types

### Requirement: Repository documentation reflects executable scope truthfully

Repository and platform development guidance SHALL document the supported host commands and SHALL distinguish the executable host scaffold from deferred domain, persistence, messaging, storage, identity, client, and production-deployment behavior.

#### Scenario: Contributor reviews platform guidance

- **WHEN** a contributor reads the root and platform development documentation
- **THEN** the contributor can identify the supported restore, build, test, and local-run commands
- **THEN** the contributor is not led to believe that deferred platform capabilities or production readiness are implemented
