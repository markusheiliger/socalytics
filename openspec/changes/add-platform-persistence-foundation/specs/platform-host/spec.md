# Platform Host Specification Delta

## MODIFIED Requirements

### Requirement: Local composition starts the platform API

The repository SHALL provide an Aspire local composition entry point that provisions PostgreSQL for development, supplies the platform API with its database reference, waits for the database dependency, applies registered migrations, and reports the API's operational health without requiring NATS JetStream, S3-compatible storage, or other deferred product infrastructure.

#### Scenario: Contributor starts the local platform

- **WHEN** a contributor with the supported .NET SDK and container runtime runs the documented local composition command
- **THEN** Aspire starts PostgreSQL and the platform API
- **THEN** the API becomes ready only after PostgreSQL is available and registered migrations succeed
- **THEN** the composition reports whether PostgreSQL and the API are healthy

#### Scenario: Database migration blocks startup readiness

- **WHEN** PostgreSQL is unavailable or a registered migration cannot complete safely
- **THEN** the API does not report readiness
- **THEN** the local composition surfaces the dependency or migration failure

### Requirement: Adopted platform module boundaries are executable and verified

The platform host SHALL compose the Club, Identity Access, Recordings, Registry, Analysis, and Agent Orchestration modules through public module boundaries, SHALL supply their internal persistence registrations through the shared platform persistence boundary, and SHALL reject prohibited module coupling, public module persistence types, or host access to module-internal implementation types.

#### Scenario: Host composition is tested

- **WHEN** the platform host smoke test creates the application with its Aspire-managed PostgreSQL dependency
- **THEN** all six adopted modules are registered through their public composition boundaries
- **THEN** migrations complete and the application starts successfully

#### Scenario: Architecture boundaries are tested

- **WHEN** the platform architecture test suite evaluates project dependencies, type visibility, SQL ownership, and migration ownership
- **THEN** a module does not depend on another module's internal implementation
- **THEN** the API host does not depend on module-internal implementation types
- **THEN** a module exposes no persistence implementation type publicly

### Requirement: Repository documentation reflects executable scope truthfully

Repository and platform development guidance SHALL document the supported host and PostgreSQL integration-test prerequisites and commands, and SHALL distinguish the executable host and persistence foundation from deferred domain, messaging, storage, identity, client, and production-deployment behavior.

#### Scenario: Contributor reviews platform guidance

- **WHEN** a contributor reads the root and platform development documentation
- **THEN** the contributor can identify the supported restore, build, test, PostgreSQL integration-test, and local-run commands and their container-runtime prerequisite
- **THEN** the contributor is not led to believe that domain behavior, deferred infrastructure, production configuration, or production readiness is implemented
