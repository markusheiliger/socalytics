# Club Domain Specification

## Purpose

Defines the observable single-club hierarchy, Team-scope resolution, concurrency, and domain API behavior for the first authenticated platform slice.

## ADDED Requirements

### Requirement: A deployment stamp has exactly one Club root

The platform SHALL maintain exactly one Club root per deployment stamp, SHALL reject creation of a second Club, and SHALL represent the Club as the implicit stamp root without a `club_id` discriminator on internal domain records, commands, or events.

#### Scenario: The stamp is initialized

- **WHEN** an authorized Club Admin initializes a stamp that has no Club
- **THEN** exactly one Club root is created
- **THEN** the created Club is returned with its current concurrency version

#### Scenario: A second Club is requested

- **WHEN** an authorized actor attempts to create another Club after the stamp has a Club
- **THEN** the request is rejected as a conflict
- **THEN** the existing Club remains unchanged

#### Scenario: Internal artifacts are inspected

- **WHEN** architecture and PostgreSQL tests inspect Club records, commands, events, tables, views, and function arguments
- **THEN** none carries a field or column named `club_id`

### Requirement: Seasons, Teams, and Matches form a required hierarchy

The platform SHALL require every Team to belong to one Season and every Match to belong to one Team. It SHALL prevent hierarchy mutations that would create an orphan or associate a Match with a Team outside the Match's resolved Team scope.

#### Scenario: An authorized hierarchy is created

- **WHEN** a Club Admin or Registrar creates a Season, creates a Team in that Season, and creates a Match for that Team with valid input
- **THEN** each resource is persisted under the required parent
- **THEN** each response identifies its parent and current concurrency version

#### Scenario: A required parent does not exist

- **WHEN** an authorized actor creates or moves a Team or Match using a missing parent
- **THEN** the request is rejected
- **THEN** no hierarchy record is created or changed

### Requirement: Every protected domain resource resolves to one Team scope

The platform SHALL resolve a protected Season, Team, or Match operation to an authoritative Team scope before authorization. A Team resolves to itself, a Match resolves through its owning Team, and a Season operation that exposes or mutates Team-protected data SHALL identify the affected Team or require Club-wide authority rather than treating the Season as an unscoped resource.

#### Scenario: A Match operation is authorized

- **WHEN** a member requests a protected Match operation
- **THEN** authorization is evaluated against the Match's current owning Team
- **THEN** the operation succeeds only when the member has a current grant for that Team or current Club-wide authority

#### Scenario: A Season operation has no single Team

- **WHEN** a member without Club-wide authority requests a Season operation that spans more than one Team
- **THEN** the request is denied
- **THEN** no Team's protected data is disclosed or changed

### Requirement: Contested Club hierarchy writes use optimistic concurrency

State-changing Club hierarchy operations SHALL require the caller's expected version, advance the version atomically on success, and reject stale writes without partial changes.

#### Scenario: Expected version is current

- **WHEN** an authorized actor updates a Club, Season, Team, or Match using its current version
- **THEN** the change and version advance commit atomically
- **THEN** the response contains the advanced version

#### Scenario: Expected version is stale

- **WHEN** an authorized actor updates a Club, Season, Team, or Match using a stale version
- **THEN** the request is rejected as a concurrency conflict
- **THEN** the current persisted state is unchanged

### Requirement: Club domain operations are represented in OpenAPI

The versioned OpenAPI document SHALL describe authenticated JSON operations for Club initialization and update, Season management, Team management, and Match management, including success, validation, unauthenticated, forbidden, not-found, and concurrency-conflict responses as applicable.

#### Scenario: The OpenAPI document is inspected

- **WHEN** a contributor retrieves the versioned OpenAPI document
- **THEN** every supported Club hierarchy operation is present with its authentication requirement and response outcomes
- **THEN** no optional external identity, Analyst Manager pairing, client, or production-ingress operation is introduced
