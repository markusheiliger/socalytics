# Data Model: Analyst and Media Verification Environment

These are runner-local verification records. There are no persistent domain entities, database migrations, or new root contract schemas.

## Pinned Tool

- Tool identity and exact expected version/build revision.
- Exact release archive URL and committed SHA-256.
- Expected executable paths within the verified archive.
- Publisher, release identity, license/provenance location, and supported host architecture.

Lifecycle: declared -> downloaded -> digest verified -> safely extracted -> executable version verified -> available. Any failure stops setup with guidance. An archive is never executed before verification. Installation belongs to a named action-owned runner directory, not an arbitrary path.

## Analyst Project

- Repository-relative root and manifest path.
- Normalized package identity, declared supported Python range, and independent lockfile path.
- Expected package build and test entry points.
- Declared local dependencies resolved to recognized project roots.
- Owned source scope, applicable mode/change triggers, and selected status.

Recognized roots are the SDK and immediate capability projects described in the [interface contract](contracts/environment-interface.md). Identities are unique; paths stay inside the repository and permitted ownership roots; cycles and missing dependencies are errors. Source selection uses path-component boundaries, not prefix similarity.

Lifecycle: absent -> recognized -> selected -> dependencies verified -> package built -> tests passed. A malformed or incompatible existing project fails verification; it does not become absent. A deleted manifest removes source coverage. No product state is persisted.

## Verification Selection

- Existing project graph and stable dependency order.
- Verification mode: `task`, `phase`, or `finalize`.
- Changed repository-relative paths from the established action input.
- Selected projects, with reason: direct change, local-dependency change, shared contract change, environment change, or finalize.
- Covered project scopes and uncovered changed paths.

Reverse dependency closure selects consumers of a changed local dependency. Shared analysis/common contracts and environment changes select all existing Python projects. Deduplicate selections by project root.

## Stage Outcome

- Tool or project identity.
- Stage: installation, integrity, version, discovery, compatibility, locked sync, package build, tests, or smoke check.
- Exit status and existing bounded log/summary evidence.
- One concrete recommended corrective action on failure.

Failure propagates through the existing action result and outputs. Coverage identifies the owner of an executable check; it never converts a failed check into evidence that a change passed.

## Relationships

The toolchain provisions the runtime used by all recognized projects and media smoke checks. Projects form an acyclic local-dependency graph. One invocation selects each applicable project at most once and records its ordered stage outcomes. Canonical contracts remain owned and covered by existing platform contract tests.
