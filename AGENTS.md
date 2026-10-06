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

## Spec Kit

- Feature work follows GitHub Spec Kit `1.0.13` in GitHub Copilot skills mode: `/speckit-specify`, optional `/speckit-clarify`, `/speckit-plan`, `/speckit-tasks`, optional `/speckit-analyze`, `/speckit-implement`, and `/speckit-converge`.
- [`.specify/memory/constitution.md`](.specify/memory/constitution.md) governs every spec, plan, and task list. Amend it with `/speckit-constitution` and keep it consistent with this file.
- Feature artifacts live in `specs/<YYYYMMDD-HHMMSS>-<short-name>/` folders created by `/speckit-specify` (`feature_numbering` is `timestamp` to avoid collisions between developers). The prefix MUST be the current UTC time; Spec Kit's helper scripts use local time, so run them with `TZ=UTC`. The prefix carries no dependency meaning: `specs/README.md` gives a human-readable overview, and spec twin issue dependencies define the automation order.
- The helper scripts are Python (`.specify/scripts/python/`) and run as `python` from the repository root.
- `.github/skills/speckit-*` and everything under `.specify/` except the constitution and `.specify/extension-src/` are managed by the `specify` CLI. Do not hand-edit them; refresh them through the `specify` CLI upgrade flow.
- `.specify/extension-src/gha/` is the source of the repository-local Spec Kit extension `gha`. After changing it, reinstall with `specify extension add .specify/extension-src/gha --dev --force`, replace the generated `.github/skills/speckit-gha-*/SKILL.md` symbolic links with regular files as described in its README, and commit the source together with the generated `.specify/extensions/`, `.specify/extensions.yml`, and skill files.
- Do not run `/speckit-taskstoissues`. Features, not tasks, are mirrored to GitHub as spec twins by `.github/workflows/speckit-prepare.yml`.

## Spec Twins

- Each feature folder under `specs/` on `main` has one twin issue labelled `speckit:spec`. Its generated `**Spec**` link line is its identity; the workflow regenerates the title and description, so change the spec, never the twin body.
- Twins are pointers, not trackers. The repository is authoritative for spec content; native GitHub issue dependencies between twins are authoritative for the order in which GitHub automation may implement features, and do not constrain local work.
- `speckit:deps-pending` marks twins whose dependencies Copilot CLI has not yet inferred. Inference runs once per new twin; after that, humans maintain the dependencies on GitHub.
- `speckit:stage:*` labels (`specified`, `planned`, `tasked`, `implementing`, `implemented`, `discarded`) are generated from the files on `main`; never set them by hand. The `**Status**` line in `spec.md` is not maintained or used.
- `speckit:stage:implement` is the only stage people set: a "ready to act" request for the `Spec Kit orchestrate` workflow. It is accepted only from someone with at least write access, for an open twin whose computed stage is `tasked` and whose checklists are all checked on `main`; otherwise the twin falls back to its computed stage. Request it with `/speckit-gha-request`, by choosing GitHub when `/speckit-implement` asks (the `gha` extension's `before_implement` hook), or by adding the label. Open blockers do not invalidate the flag; the workflow waits for them.
- When running `/speckit-implement` without a person to answer the routing question, set `SPECKIT_IMPLEMENT_MODE=local` or `SPECKIT_IMPLEMENT_MODE=remote`; inside GitHub Actions the hook always implements locally.
- `Spec Kit orchestrate` owns the `speckit/<folder>` branches and their draft pull requests. Each draft pull request closes its twin when merged, carries the task list as checkboxes, and reports status through the `Spec Kit implementation` check run and progress through comments. An open pull request means the spec is in progress; closing it without merging returns the twin to its computed stage. Do not push to `speckit/**` branches or edit those pull requests by hand unless you take over the implementation or resolve a problem the automation asked for; such a push continues the implementation automatically.
- `Spec Kit orchestrate` decides and dispatches, and its merge jobs merge implementations into `main`; `Spec Kit implement` implements exactly one task per run, `Spec Kit converge` runs `/speckit-converge` as the acceptance gate once all tasks are checked, and `Spec Kit resolve` resolves merge conflicts with `main`. Agents only run in read-only jobs; a separate job that never executes agent-written code validates, commits, and pushes. When working on tasks there, implement only the named task, check exactly that box in `tasks.md`, and do not change `.github/`, `.specify/`, or other files under `specs/` (the environment action folders are the only exception, and only in environment specs); convergence may only append a convergence section to `tasks.md`, and conflict resolution may only change the conflicted files. Other changes fail the attempt. Each step gets at most three failed attempts before the person who requested the implementation is asked.
- Verified, converged implementations are squash-merged into `main` without human review unless the repository variable `SPECKIT_AUTO_MERGE` is `false`; people are only asked when automation cannot continue, and a push by them to the implementation branch continues it.
- Solution-specific setup and verification for Spec Kit implementation belong in the optional composite actions `.github/actions/environment-setup` and `.github/actions/environment-verify`, not in the Spec Kit workflows or scripts. Keep both actions in line with the documented build and test commands.
- New frameworks, SDKs, or tools reach these actions only through a standalone environment spec: a spec whose implementation changes nothing but the two action folders (plus its own `tasks.md` ticks). Agents may change the action folders only in such a spec; mixing them with other changes fails the task. Environment specs are always held for a person's review, and the merge first self-tests the changed actions. Specs that need the new tooling must name the environment spec as a dependency, so they start only after it is merged. Checks added to `environment-verify` add their paths to `COVERED` only when their project exists and skip while it does not exist yet.
- Changed files that no check covers are listed in the task comments and hold the pull request for review instead of an automatic merge.
- All Spec Kit workflows refuse to run from branches other than `main`. Keep that guard, the `pull_request_target` trigger, and the separate tooling checkout when changing them, and never give a job that runs agent-written code a token that can write.
- Tooling lives in `.github/scripts/speckit-*.mjs`. Run `node --test .github/scripts/speckit-*.test.mjs` after changing it, and keep a readable `name:` on every workflow job and step.

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
- Add product CI workflows only after executable build, lint, or test commands exist. The Spec Kit workflows (`Spec Kit prepare`, `orchestrate`, `implement`, `converge`, and `resolve`) are repository tooling, not product CI.
- Never commit, push, configure remotes, or publish without an explicit request.

## Changes and Validation

- Keep changes focused and avoid speculative abstractions or dependencies.
- Document every supported setup, build, test, and lint command in `README.md` when it becomes available.
- After each substantive edit, run the narrowest relevant check before widening scope.
- Run `node .github/scripts/check-markdown.mjs` to validate Markdown diagnostics and repository-relative links when changing documentation or Spec Kit artifacts.
- Preserve user changes in a dirty worktree and do not use destructive Git commands.
