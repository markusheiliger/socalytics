# Repository Foundation Specification

## Purpose

Defines the observable source-area structure and ownership documentation that establish a truthful foundation for future SocAlytics implementation work.

## Requirements

### Requirement: Repository exposes the approved source areas

The repository SHALL contain a `src` directory whose immediate child directories are exactly `platform`, `clients`, `agents`, and `analysts` when this foundation change is applied.

#### Scenario: Source-area structure is inspected

- **WHEN** a contributor lists the immediate child directories of `src`
- **THEN** the list contains `platform`, `clients`, `agents`, and `analysts`
- **THEN** the list contains no other immediate child directory

### Requirement: Every source area communicates ownership

Each approved source area SHALL contain a README that identifies its purpose, planned ownership boundary, explicit exclusions, and authoritative architecture references.

#### Scenario: Contributor evaluates a source area

- **WHEN** a contributor opens the README in any approved source area
- **THEN** the contributor can determine what belongs in that area
- **THEN** the contributor can determine what is excluded from that area
- **THEN** the contributor can follow links to the architecture documents that govern it

### Requirement: Scaffold state remains truthful

The repository documentation SHALL distinguish the source-area scaffold from executable implementation and SHALL NOT claim that build, test, package, runtime, or deployment artifacts exist until those artifacts are introduced.

#### Scenario: Contributor reviews development guidance

- **WHEN** the repository contains only the first-level source-area scaffold
- **THEN** the repository guidance states that no executable product build or test command is available
- **THEN** planned nested components are described without presenting them as existing directories or implementations
