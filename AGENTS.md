# Repository Guidelines

## Current State

- Treat this folder as the repository root.
- This is a planned polyglot monorepo with a governance and documentation foundation plus an initial executable .NET 10 platform-host scaffold. See [README.md](README.md) for the current structure and [docs/README.md](docs/README.md) for the documentation index.
- The only first-level source areas are [platform](src/platform/README.md), [clients](src/clients/README.md), [agents](src/agents/README.md), and [analysts](src/analysts/README.md). The platform area is executable; the other source-area READMEs define non-executable planned ownership boundaries.
- `src/platform/SocAlytics.Platform.slnx` contains peer production projects for Api, AppHost, ServiceDefaults, Club, IdentityAccess, Recordings, Registry, Analysis, and AgentOrchestration. Host and architecture test projects are under `src/platform/Tests`.
- Current executable evidence is limited to the dependency-free API host, `/alive`, `/health`, the built-in `/openapi/v1.json` document, Aspire local composition of the API alone, six public capability DI boundaries with internal markers, and host and architecture tests. No domain behavior, PostgreSQL/Dapper/DbUp, NATS, S3, authentication or authorization, clients, deployment configuration, or production-readiness evidence exists.
- Keep this file current when the repository gains documented build, test, architecture, or contribution conventions.

## Authority Boundaries

- `docs/architecture/` is authoritative for the coherent current system design.
- Component-local executable contracts and implementation guidance belong with the component that owns and validates them.
- Preserve unresolved decisions explicitly. Do not present provisional values, target-state descriptions, or missing production evidence as adopted facts.

## Repository Setup

- Use `main` as the default branch when initializing Git.
- Use the .NET 10 SDK selected by `src/platform/global.json` for platform work.
- From the repository root, the supported platform commands are:

  ```powershell
  dotnet restore src/platform/SocAlytics.Platform.slnx
  dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
  dotnet test src/platform/SocAlytics.Platform.slnx --no-build
  dotnet run --project src/platform/SocAlytics.Platform.AppHost
  ```

- Restore before using the documented `--no-restore` build command, and build before using the documented `--no-build` test command. The AppHost is the supported local entry point. Stop it with `Ctrl+C`.
- There is no supported product lint command yet. Repository Markdown validation is available through `node .github/scripts/check-markdown.mjs`; do not treat it as product linting. Do not infer deployment support or production readiness from successful local restore, build, test, Markdown validation, or AppHost execution.
- Add only files justified by the adopted architecture and an approved change. Generate `.gitignore` from the actual stack and local tooling rather than using a generic catch-all.
- Preserve `src/platform/`, `src/clients/`, `src/agents/`, and `src/analysts/` as the approved first-level ownership areas. Do not add another immediate child of `src` without an accepted architecture change.
- Add nested source, test, or documentation directories only with their first meaningful artifacts; do not create empty placeholders or infer planned child paths from the source-area READMEs.
- Add product CI workflows only after executable build, lint, or test commands exist.
- Never commit, push, configure remotes, or publish without an explicit request.

## Changes and Validation

- Keep changes focused and avoid speculative abstractions or dependencies.
- Document every supported setup, build, test, and lint command in `README.md` when it becomes available.
- After each substantive edit, run the narrowest relevant check before widening scope.
- Run `node .github/scripts/check-markdown.mjs` to validate Markdown diagnostics and repository-relative links when changing documentation.
- Preserve user changes in a dirty worktree and do not use destructive Git commands.
