# Scaffold Source Areas

## Why

SocAlytics needs stable source ownership boundaries before its first executable components are introduced. Establishing the first-level layout now also records the agreed platform and client release baseline without prematurely scaffolding projects or deeper implementation structure.

## What Changes

- Add `src/platform/`, `src/clients/`, `src/agents/`, and `src/analysts/` as the only first-level source areas, each tracked by an ownership README.
- Adopt ASP.NET Core on .NET 10 LTS as the platform baseline, with .NET Aspire for local composition and service defaults and Docker Compose for deployment.
- Keep the React/TypeScript Web UI source-owned with the Electron Coach Client under `src/clients/`, while coupling the Web UI release to the platform API and deploying it as a separate container.
- Define `src/analysts/` as the future parent of the Analyst Manager, Analyst SDK, and Analyst capability implementations; no nested source directories are created by this change.
- Synchronize repository guidance and the authoritative platform and client architecture narratives.
- Exclude executable projects, package manifests, tests, Dockerfiles, Compose files, Aspire AppHost files, CI, migrations, contracts, and product behavior.

## Capabilities

### New Capabilities

- `repository-foundation`: Defines the observable first-level source organization and the documentation that communicates ownership and implementation status.

### Modified Capabilities

None.

## Impact

- Adds four source-area README files under `src/`.
- Updates `README.md` and `AGENTS.md` to reflect the repository's new structural baseline without claiming executable build or test support.
- Updates `docs/architecture/client-applications.md` and `docs/architecture/platform-implementation.md` to record source ownership, release coupling, runtime composition, and the adopted platform technology baseline.
- Does not add runtime dependencies, APIs, product code, deployment manifests, or security and data-governance behavior.
- Detailed production topology, operational evidence, and implementation project structure remain unresolved and outside this change.
