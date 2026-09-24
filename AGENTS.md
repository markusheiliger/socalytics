# Repository Guidelines

## Current State

- Treat this folder as the repository root.
- This is a planned polyglot monorepo with a governance and documentation foundation plus an initial executable .NET 10 platform-host scaffold. See [README.md](README.md) for the current structure and [docs/README.md](docs/README.md) for the documentation index.
- The only first-level source areas are [platform](src/platform/README.md), [clients](src/clients/README.md), [agents](src/agents/README.md), and [analysts](src/analysts/README.md). The platform area is executable; the other source-area READMEs define non-executable planned ownership boundaries.
- `src/platform/SocAlytics.Platform.slnx` contains peer production projects for Api, AppHost, ServiceDefaults, Club, IdentityAccess, Recordings, Registry, Analysis, and AgentOrchestration. Host and architecture test projects are under `src/platform/Tests`.
- Current executable evidence is limited to the dependency-free API host, `/alive`, `/health`, the built-in `/openapi/v1.json` document, Aspire local composition of the API alone, six public capability DI boundaries with internal markers, and host and architecture tests. No domain behavior, PostgreSQL/Dapper/DbUp, NATS, S3, authentication or authorization, clients, deployment configuration, or production-readiness evidence exists.
- OpenSpec is the only user-facing change workflow. Use the generated `opsx-*` prompts for exploration, proposal, application, synchronization, verification, and archive.
- Keep this file current when the repository gains documented build, test, architecture, or contribution conventions.

## Authority Boundaries

- `docs/architecture/` is authoritative for the coherent current system design.
- `openspec/specs/` is authoritative for accepted behavioral requirements and scenarios.
- `openspec/changes/` contains active change state; `openspec/changes/archive/` preserves completed change history.
- Component-local executable contracts and implementation guidance belong with the component that owns and validates them.
- Preserve unresolved decisions explicitly. Do not present provisional values, target-state descriptions, or missing production evidence as adopted facts.

## OpenSpec and Specialists

- `openspec/config.yaml` contains project context, artifact rules, and advisory apply/archive routing policy. Do not duplicate that policy in generated prompt bodies.
- `.github/agents/openspec.agent.md` is OpenSpec-managed cloud-agent guidance. It is distinct from the SocAlytics specialists.
- `.github/agents/openspec-cloud.agent.md` is the repository-owned GitHub cloud orchestrator. It executes one controller-selected OpenSpec operation, follows the matching generated skill, and delegates apply tasks to their hidden declared owners through the `agent` tool.
- All `soca-*` agents are dispatch-only (`user-invocable: false`). OpenSpec dispatches the one owner declared by each task; specialists return results and never dispatch one another.
- Supported owners are `soca-strategist`, `soca-designer`, `soca-architect`, `soca-developer`, `soca-verifier`, and `soca-auditor`.
- Every task must declare exactly one `Owner: soca-*`. Split tasks when ownership or artifact category differs, and stop when ownership is missing, invalid, or conflicting.
- `soca-verifier` verifies independently and does not author implementation work. `soca-auditor` reports independent findings and does not silently remediate them.

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
- There is no supported product lint command yet. Do not infer deployment support or production readiness from successful local restore, build, test, or AppHost execution.
- Add only files justified by the adopted architecture and an approved change. Generate `.gitignore` from the actual stack and local tooling rather than using a generic catch-all.
- Preserve `src/platform/`, `src/clients/`, `src/agents/`, and `src/analysts/` as the approved first-level ownership areas. Do not add another immediate child of `src` without an accepted architecture change.
- Add nested source, test, or documentation directories only with their first meaningful artifacts; do not create empty placeholders or infer planned child paths from the source-area READMEs.
- Add product CI workflows only after executable build, lint, or test commands exist. The OpenSpec Copilot setup workflow is repository-tooling setup, not product CI.
- Never commit, push, configure remotes, or publish without an explicit request.

## OpenSpec Change Queue

- Issue twins are non-authoritative projections of active changes on `main`. The canonical change ref is stable identity; active and dated archive paths are mutable projections.
- `openspec:enqueued` records queue intent. Native GitHub issue dependencies are the only blocked-state authority.
- The controller uses the Agent Tasks API rather than native Copilot issue assignment because assignment immediately starts an uncontrolled duplicate session.
- The queue runs apply, verify, sync, and archive on one durable draft pull request. One Agent Task may run per change at a time.
- Reconciliation validates branch-visible evidence and append-only operation ledger entries. It does not treat Agent Task completion alone as success.
- Cross-change inference runs in the read-only `openspec-change-dependencies.md` Agentic Workflow. Only its typed custom safe-output job may invoke the privileged dependency reconciler; edit the source and regenerate the lock file with `gh aw compile`.
- Failed or timed-out work receives one retry. Cancellation and `waiting_for_user` always stop for human attention.
- Automation stops after validated archive. A human approves workflows, marks the pull request ready, reviews it, and enables auto-merge.
- Run `node --test .github/scripts/*.test.mjs` for the queue tooling tests.

## Changes and Validation

- Keep changes focused and avoid speculative abstractions or dependencies.
- Document every supported setup, build, test, and lint command in `README.md` when it becomes available.
- After each substantive edit, run the narrowest relevant check before widening scope.
- Validate OpenSpec with `openspec doctor --json`, `openspec schema validate spec-driven --json`, `openspec validate --all --json`, and `openspec status --all --json` as applicable.
- Validate customization frontmatter, Markdown diagnostics, and relative links when changing agents, prompts, skills, or documentation.
- Run `openspec update` after changing the selected workflow profile; do not customize OpenSpec-managed prompt or skill bodies.
- Preserve user changes in a dirty worktree and do not use destructive Git commands.
