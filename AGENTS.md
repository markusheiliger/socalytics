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

## OpenSpec and Execution Capabilities

- `openspec/config.yaml` contains project context, artifact rules, and apply/archive policy. Do not duplicate that policy in generated prompt or skill bodies.
- `.github/agents/openspec.agent.md` is OpenSpec-managed cloud-agent guidance and must remain generated.
- Repository execution contracts live under `openspec/capabilities/`. Capability ids resolve directly to same-named Markdown files; there is no registry or specialist-agent routing.
- Supported execution capabilities are `strategy`, `design`, `architecture`, `implementation`, `verification`, and `audit`.
- Every task must declare exactly one unordered plural `Capabilities:` set. Multiple capabilities are allowed only when every selected contract is composable and compatible.
- The `openspec` workflow starts one fresh agent session per capability-backed task and validates the pushed checkpoint commit before advancing.
- `verification` and `audit` are exclusive, isolated, checkbox-only capabilities. They report independently and do not author or remediate implementation work.

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
- Add product CI workflows only after executable build, lint, or test commands exist. The OpenSpec Copilot setup workflow is repository-tooling setup, not product CI.
- Never commit, push, configure remotes, or publish without an explicit request.

## OpenSpec Change Processing

- Explore and propose changes client-side. Implementation (apply, verify, sync, archive) runs server-side in the single `openspec` workflow (`.github/workflows/openspec.yml`) once a change is on `main`.
- Issue twins are non-authoritative projections of active changes on `main`. The canonical change ref is stable identity; active and dated archive paths are mutable projections.
- One combined issue reconciliation Agentic Workflow performs deterministic issue synchronization before AI dependency inference. Change-driven runs are incremental; weekly and manually requested full runs rebuild the complete inference view, and manual dry runs make no mutations.
- Dependency inference checkpoints are a rebuildable cache in `refs/notes/openspec-change-dependencies`. Fetch that ref explicitly when inspecting incremental behavior; the notes are not accepted state or processing authority.
- The issue holds queue state only. `openspec:change` classifies twins and `openspec:enqueued` is the one-shot processing request, honored only when a user with write access applied it. `openspec:processing`, `openspec:needs-attention`, and `openspec:awaiting-review` are workflow outputs. Native GitHub issue dependencies are the only blocked-state authority.
- Each admitted change gets a workflow-created `openspec/<change>` branch and draft pull request. Processing state lives only in the `OpenSpec lifecycle` check run on the pull request head; comments are a human-readable change log and are never read as state.
- The workflow is event-driven and short-lived: it never polls. Agent checkpoint pushes, `/openspec` pull-request comments, enqueue labels, merges, and a 15-minute watchdog wake it; every run reconciles every change under one repository-wide lock.
- Agent sessions run in the `openspec-agent` agentic workflow by default (`OPENSPEC_AGENT_RUNTIME=actions`: no personal access token, 60-minute budget, a separate job pushes, `.github/` blocked). The Copilot cloud agent through the Agent Tasks API with `head_ref` remains the `copilot` fallback. Never use native Copilot issue assignment, which starts an uncontrolled duplicate session.
- The agentic sandbox has no Docker: the agent runs Testcontainers tests through the host-side `run_platform_tests` tool, and "Check agent result" runs the platform tests at every apply checkpoint that changes `src/platform/`.
- Edit `openspec-agent.md` only, recompile it with `gh aw compile openspec-agent` (gh-aw v0.89.21), and run `node .github/scripts/openspec-change-workflow-names.mjs`; never hand-edit lock files.
- Every agent session ends with one pushed commit carrying an `OpenSpec-JSON:` checkpoint trailer (`complete`, `partial`, `needs_decision`, or `failed`). The workflow validates it at that exact commit before crediting; Agent Task status is supplemental.
- Failed, partial, or invalid sessions get one retry. Agent decisions, exhausted retries, and verify findings open human gates: any suggestion or warning opens a review gate, any critical finding opens a failure gate that cannot be approved, and a clean verify continues automatically. Humans resolve gates with `/openspec approve`, `retry`, `answer <text>`, or `abort`.
- Archive happens on the implementation branch. Automation stops at the merge gate; a human marks the pull request ready, reviews it, and merges.
- Cross-change inference is read-only until its typed custom safe output invokes the privileged validator and reconciler. Edit the combined Agentic Workflow source, regenerate its lock file with `gh aw compile`, and run `node .github/scripts/openspec-change-workflow-names.mjs` to apply generated-job display names; do not hand-edit the lock.
- Every job and step in `openspec.yml` and `openspec-agent.md` must keep a readable `name:`; `openspec-workflow.test.mjs` enforces it.
- Run `node --test .github/scripts/*.test.mjs` for the workflow tooling tests.

## Changes and Validation

- Keep changes focused and avoid speculative abstractions or dependencies.
- Document every supported setup, build, test, and lint command in `README.md` when it becomes available.
- After each substantive edit, run the narrowest relevant check before widening scope.
- Validate OpenSpec with `openspec doctor --json`, `openspec schema validate spec-driven --json`, `openspec validate --all --json`, and `openspec status --all --json` as applicable.
- Run `node .github/scripts/check-markdown.mjs` to validate Markdown diagnostics and repository-relative links when changing agents, prompts, skills, or documentation; validate customization frontmatter separately where applicable.
- Run `openspec update` after changing the selected workflow profile; do not customize OpenSpec-managed prompt or skill bodies.
- Preserve user changes in a dirty worktree and do not use destructive Git commands.
