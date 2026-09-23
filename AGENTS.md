# Repository Guidelines

## Current State

- Treat this folder as the repository root.
- This is a planned polyglot monorepo in its governance-only phase. See [README.md](README.md) for the current structure and [docs/README.md](docs/README.md) for the documentation index.
- No application stack, component boundaries, build system, or test framework is selected yet. Do not invent them; establish requirements before adding product code.
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
- All `soca-*` agents are dispatch-only (`user-invocable: false`). OpenSpec dispatches the one owner declared by each task; specialists return results and never dispatch one another.
- Supported owners are `soca-strategist`, `soca-designer`, `soca-architect`, `soca-developer`, `soca-verifier`, and `soca-auditor`.
- Every task must declare exactly one `Owner: soca-*`. Split tasks when ownership or artifact category differs, and stop when ownership is missing, invalid, or conflicting.
- `soca-verifier` verifies independently and does not author implementation work. `soca-auditor` reports independent findings and does not silently remediate them.

## Repository Setup

- Use `main` as the default branch when initializing Git.
- Add only files justified by the selected stack. Generate `.gitignore` from the stack and local tooling rather than using a generic catch-all.
- Choose code-bearing top-level directories with the first components; do not impose a generic monorepo layout in advance.
- Do not add empty `src`, `tests`, or documentation directories; Git does not track empty directories. Create them with their first meaningful file.
- Add product CI workflows only after executable build, lint, or test commands exist. The OpenSpec Copilot setup workflow is repository-tooling setup, not product CI.
- Never commit, push, configure remotes, or publish without an explicit request.

## Changes and Validation

- Keep changes focused and avoid speculative abstractions or dependencies.
- Document every supported setup, build, test, and lint command in `README.md` when it becomes available.
- After each substantive edit, run the narrowest relevant check before widening scope.
- Validate OpenSpec with `openspec doctor --json`, `openspec schema validate spec-driven --json`, `openspec validate --all --json`, and `openspec status --json` as applicable.
- Validate customization frontmatter, Markdown diagnostics, and relative links when changing agents, prompts, skills, or documentation.
- Run `openspec update` after changing the selected workflow profile; do not customize OpenSpec-managed prompt or skill bodies.
- Preserve user changes in a dirty worktree and do not use destructive Git commands.
