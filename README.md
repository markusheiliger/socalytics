# socAlytics

socAlytics is a planned polyglot monorepo. The repository currently contains
its governance and documentation foundation, a source-area scaffold, and the
first executable .NET platform host. Further product components will be added
through approved changes.

## Repository Structure

- [`src/platform/`](src/platform/README.md) contains the executable control-plane
    platform host and its ownership guidance.
- [`src/clients/`](src/clients/README.md) records the planned Web UI and Electron
    Coach Client ownership boundary.
- [`src/agents/`](src/agents/README.md) records the planned intelligence-agent
    runtime and tooling ownership boundary.
- [`src/analysts/`](src/analysts/README.md) records the planned Analyst Manager,
    Analyst SDK, and Analyst capability ownership boundary.
- [`docs/`](docs/README.md) indexes architecture, component, and operational documentation.
- [`.specify/`](.specify/memory/constitution.md) holds the Spec Kit project
    constitution, templates, and helper scripts; `.github/skills/speckit-*`
    holds the generated Spec Kit skills for GitHub Copilot.
- [`specs/`](specs/README.md) holds one UTC-timestamped folder per Spec Kit
    feature and an index of feature dependency order.
- [`AGENTS.md`](AGENTS.md) defines repository guidance for coding agents.
- [`LICENSE`](LICENSE) contains the project license.
- `.editorconfig` provides stack-neutral text-file defaults.

The clients, agents, and analysts source areas remain non-executable ownership
scaffolds. The platform area contains the repository's current application
projects and tests. No deployment manifests or product automation exist yet.

## Development

### Platform Host

The platform uses the .NET 10 SDK. Run these commands from the repository root:

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/SocAlytics.Platform.slnx --no-build
dotnet run --project src/platform/SocAlytics.Platform.AppHost
```

The current executable evidence covers the ASP.NET Core API and Aspire AppHost,
liveness at `/alive`, readiness at `/health`, the `v1` OpenAPI document at
`/openapi/v1.json`, six capability composition boundaries, and focused host and
architecture tests.

Domain behavior, PostgreSQL persistence with Dapper and DbUp, NATS messaging,
S3-compatible storage, identity and authentication, client applications,
Docker support, and production deployment remain deferred. This executable
host scaffold does not claim production readiness.

### Spec Kit

Features are developed with [GitHub Spec Kit](https://github.com/github/spec-kit)
`1.0.13`, set up for GitHub Copilot in skills mode with Python helper scripts.
The project constitution in
[`.specify/memory/constitution.md`](.specify/memory/constitution.md) governs
every specification, plan, and task list.

Using the committed skills in GitHub Copilot needs only Python 3.11+ on `PATH`
as `python`. The `specify` CLI is needed only to upgrade Spec Kit or add
extensions. It requires [uv](https://docs.astral.sh/uv/) and is installed from
the pinned release tag:

```powershell
uv tool install specify-cli --from git+https://github.com/github/spec-kit.git@v1.0.13
specify version
```

When PyPI is reachable only through a package proxy, pass it to uv with
`--index-url <proxy-simple-url>`, because uv does not read pip's configuration.

Run the skills in GitHub Copilot Chat, one at a time, reviewing each result:

1. `/speckit-specify <what and why>` creates `specs/<YYYYMMDD-HHMMSS>-<feature>/spec.md`,
    prefixed with the UTC creation time; see [specs/README.md](specs/README.md)
    for the naming rule and feature dependency order.
2. `/speckit-clarify` (optional) resolves ambiguities before planning.
3. `/speckit-plan <technical direction>` creates the plan and design artifacts
    and checks them against the constitution.
4. `/speckit-tasks` creates `tasks.md`; `/speckit-analyze` (optional) checks
    cross-artifact consistency.
5. `/speckit-implement` executes the tasks; `/speckit-converge` reports
    remaining gaps. Repeat both until convergence reports completion.

`/speckit-checklist` generates quality checklists, and `/speckit-constitution`
amends the constitution. Do not use `/speckit-taskstoissues`: this repository
mirrors features, not tasks, to GitHub through spec twins (see below).

The skills under `.github/skills/speckit-*` and the files under `.specify/`
other than the constitution are managed by the `specify` CLI. Do not edit them
by hand; run `specify self check` to look for a newer release and follow the
[Spec Kit upgrade guide](https://github.github.io/spec-kit/upgrade.html) to
refresh them.

### Spec Twins

The `Spec Kit prepare` workflow (`.github/workflows/speckit-prepare.yml`) keeps
one GitHub issue, a spec twin, per feature folder under `specs/` on `main`. It
runs on every push to `main` that changes `specs/**` and can be started
manually; manual runs are dry runs unless `dry_run` is unchecked.

- A twin is labelled `speckit:spec` and starts with a generated, clickable
    `**Spec**` line linking its folder. That line identifies the twin. The title
    and description are regenerated from the spec on every run, so edit the
    spec, not the issue.
- A twin is not used for tracking. The repository stays authoritative for what
    a feature specifies.
- When a folder disappears, its twin is closed as not planned and labelled
    `speckit:stage:discarded`; it is reopened if the folder returns. Twins closed
    as completed are never changed.
- Every twin carries exactly one generated stage label, derived from the files
    in its folder on `main`: `speckit:stage:specified` (only `spec.md`),
    `speckit:stage:planned` (`plan.md`), `speckit:stage:tasked` (`tasks.md`, no
    task checked), `speckit:stage:implementing` (some tasks checked), or
    `speckit:stage:implemented` (all tasks checked), or the human-requested
    `speckit:stage:implement` flag described below. Implementation happens on
    feature branches, so the implementation stages only reflect merged work. The
    `**Status**` line in `spec.md` is not used. A twin's stage, state, title, and
    description change in a single request, so no intermediate label state is
    ever visible; labels the workflow does not own are preserved.
- If any `speckit:spec` issue lacks a readable `**Spec**` line, the run creates
    no new twins and fails, listing the affected issues. Restore the line from
    the issue's edit history, or remove the label.
- New twins carry `speckit:deps-pending`. Copilot CLI then infers "blocked by"
    relationships between the new and the existing open twins, once. A script
    validates the result (known open twins only, no cycles), adds native GitHub
    issue dependencies, and comments the reasons. If inference fails, the label
    stays and the next run retries. After that, the dependencies belong to
    GitHub: adjust them on the issues. They define the order in which GitHub
    automation may implement features, not the order of local work.

### Requesting Implementation on GitHub

`speckit:stage:implement` sits between `tasked` and `implementing` and is a
"ready to act" flag that only people set; the sync never sets it. A flagged
twin may still be blocked: the `Spec Kit orchestrate` workflow
(`.github/workflows/speckit-orchestrate.yml`) picks up flagged twins once all
their "blocked by" issues are closed.

- When someone adds the label, the `validate-implement` job of
    `Spec Kit prepare` accepts it only if the person has at least write access,
    the twin is open and its folder exists on `main`, the computed stage is
    `tasked`, and every checkbox in `specs/<folder>/checklists/*.md` on `main`
    is checked. Otherwise the twin falls back to its computed stage, which may
    come before or after `implement`, and gets a comment with the reasons.
- Later syncs keep a valid flag and revoke it, with a comment, once the stage
    or checklist conditions no longer hold, for example when all tasks are
    merged and the twin becomes `implemented`.
- `Spec Kit orchestrate` is the orchestrator. It runs after every successful
    `Spec Kit prepare` run, when a worker run or its own merge jobs hand
    control back, when an issue is closed or reopened, when a `speckit/**` pull
    request is closed or a person pushes to it, hourly as a safety net, and on
    demand. Dependency edits on GitHub trigger nothing, so they apply with the
    next trigger. It only decides and dispatches; everything long runs in
    separate runs or jobs, each with its own time limit.
- For every ready twin it prepares an implementation workspace:
  - the branch `speckit/<folder>`, created as a linked branch so it appears in
      the twin's Development section, with an empty start commit;
  - a draft pull request `Implement: <spec title>` whose body contains
      `Closes #<twin>`, the spec link, and the tasks from `tasks.md` as
      checkboxes, assigned to the person who set the flag;
  - a `Spec Kit implementation` check run on the pull request, which tracks the
      implementation status, and a start comment.
- It then drives the implementation until it reaches `main`:

  | Step | Runs in | What it does |
  | --- | --- | --- |
  | Tasks | `Spec Kit implement` (`speckit-implement.yml`), one run per task | implements the next unchecked task with `/speckit-implement`, strictly in `tasks.md` order |
  | Acceptance gate | `Spec Kit converge` (`speckit-converge.yml`) | runs `/speckit-converge` once all tasks are checked; gaps are appended as a `Phase N: Convergence` section with new tasks, which the task chain implements before converge runs again (at most 3 rounds) |
  | Merge | merge jobs of `Spec Kit orchestrate` | merges `main` into the branch, runs the full verification on the result, and squash-merges the pull request into `main` |
  | Conflicts | `Spec Kit resolve` (`speckit-resolve.yml`) | resolves conflicts with `main` with Copilot CLI, verifies, and pushes the merge commit; then the merge step runs again |

  Each run is short, so specs of any size never hit the 6-hour job limit.
  Every worker run (implement, converge, resolve):
  1. checks that its step is still the next step of the open, flagged
      implementation; otherwise it does nothing;
  2. sets up the solution environment with the optional
      `.github/actions/environment-setup` action, then runs Copilot CLI
      (60 minutes at most) in a job with a read-only token, which reaches the
      CLI only as `COPILOT_GITHUB_TOKEN` and is hidden from the agent's shells;
      `git push`, `gh`, `curl`, and `wget` are denied;
  3. verifies the change in that job with the optional
      `.github/actions/environment-verify` action: the checks the changed paths
      call for after a task, every check after a conflict resolution;
  4. lands it in a separate job that never runs agent-written code: it
      re-validates the change (a task may only add its own `tasks.md` tick;
      convergence may only append a convergence section to `tasks.md`; a
      resolution may only change the conflicted files, none under `.github/`,
      `.specify/`, or `specs/`, and is replayed onto git's own merge), then
      commits, pushes, updates the pull request body and check run, and
      comments the result;
  5. hands control back, whatever the outcome, by starting `Spec Kit orchestrate`
      with its run id (`after_run`); the orchestrator waits until that run has
      finished and then decides the next step. An explicit dispatch is used
      because GitHub raises no `workflow_run` event for runs started by the
      orchestrator.

  The merge jobs run inside the orchestrate run: `merge-verify` (read-only
  token, outside the global orchestration concurrency group, so a long
  verification never delays other specs) merges `main` and runs every check of
  `environment-verify`; `merge-land` (write token, API only) squash-merges the
  pull request only if `main` still is the commit that was verified, sets the
  twin to `implemented` and closes it, deletes the branch, starts
  `Spec Kit prepare`, and comments "Merged into `main`" with an @mention of the
  person who set the flag. If `main` moved, the merge simply runs again. Set the
  repository variable `SPECKIT_AUTO_MERGE` to `false` to stop before merging:
  the pull request is then marked ready for review and a review is requested
  instead. Configure the Copilot credit cap per agent run with the repository
  variable `SPECKIT_TASK_AI_CREDITS` (default 1000).
- The two composite actions are the solution-specific extension points; the
  Spec Kit workflows and scripts know nothing about the solution. Both are
  optional: the workflow skips a missing action, and without
  `environment-verify` the pull request comments and check run say that no
  verification is configured. The workflow always loads them from the default
  branch, so an agent cannot change its own checks. In this repository:
  - `environment-setup` installs .NET from `src/platform/global.json` and the
      Markdown linters; Docker for Testcontainers is already on the runner;
  - `environment-verify` runs the platform restore, build, and tests when
      `src/platform/**` changed and the Markdown check when Markdown changed.
      Add a check there when another source area gets build or test commands.
- Every step gets at most three failed attempts: tasks, convergence, and
  conflict resolution are counted from the run names (`#<twin> <task> attempt
  <n>`, `#<twin> attempt <n>`), merges from their check runs, including crashed
  and timed-out runs. Failed attempts are commented on the pull request.
- People are involved only when automation cannot continue. Then the check run
  fails, the pull request is marked ready for review, and one comment
  @mentions the person who set the flag with the reason and the next step:

  | Case | What the person does |
  | --- | --- |
  | A step reached its attempt limit | push a fix to the implementation branch, or run `Spec Kit orchestrate` manually with the `twin` input |
  | Convergence still finds gaps after 3 rounds | implement the gaps on the branch, or adjust the spec and tasks |
  | Conflicts the agent could not resolve, or conflicts in protected paths | resolve them (GitHub's "Resolve conflicts" or locally) and push |

  A push by a person to the implementation branch triggers the orchestrator
  and starts a new attempt count, so the implementation continues and merges
  without further approval.
- An open implementation pull request marks the twin as in progress, so it is
    never started twice. Every step checks what already exists, so reruns after
    partial failures complete the work.
- Closing the pull request without merging removes the flag, and the twin
    falls back to its computed stage with a comment. GitHub keeps the closed
    pull request and its branch; flagging the twin again deletes and recreates
    the branch and opens a new draft pull request. Merging the pull request,
    automatically or by a person, closes the twin as completed.
- All Spec Kit workflows only run from `main`: manual runs started on another
    branch are refused, implementation-PR events use `pull_request_target`
    (always `main`'s workflow), the workflow token cannot push workflow files,
    and scripts always run from a `main` checkout. Pull requests and commits
    created with the workflow's `GITHUB_TOKEN` do not start other workflows, so
    CI on these pull requests needs a GitHub App or token later. Creating pull
    requests requires the repository setting "Allow GitHub Actions to create and
    approve pull requests".

The repository-local Spec Kit extension `gha`
(source in [`.specify/extension-src/gha/`](.specify/extension-src/gha/README.md))
makes this available from GitHub Copilot:

- `/speckit-implement` first runs `/speckit-gha-route`, a mandatory
    `before_implement` hook that asks whether to implement locally (the normal
    Spec Kit flow continues) or to request implementation on GitHub (the
    request is made and `/speckit-implement` stops). Set
    `SPECKIT_IMPLEMENT_MODE=local` or `SPECKIT_IMPLEMENT_MODE=remote` to skip the
    question; inside GitHub Actions it always implements locally.
- `/speckit-gha-request [folder]` requests implementation directly.

Both run `node .github/scripts/speckit-orchestrate.mjs request [--folder <folder>]`,
which checks the spec on `origin/main` with the same rules, finds its twin, and
adds the label with your own `gh` token, so the request is validated on GitHub.
Adding the label in the GitHub web interface or with
`gh issue edit <number> --add-label speckit:stage:implement` works the same way.

The tooling tests run with:

```powershell
node --test .github/scripts/speckit-*.test.mjs
```

A local dry run against the repository needs only read access:

```powershell
$env:GITHUB_REPOSITORY = 'markusheiliger/socalytics'; $env:GH_TOKEN = gh auth token
node .github/scripts/speckit-prepare.mjs sync --dry-run
```

### Markdown

Markdown validation uses pinned global development tools and does not require
an application `package.json`:

```powershell
npm install --global markdownlint-cli2@0.23.3 markdown-link-check@3.15.0
```

Run the supported Markdown validation command from the repository root:

```powershell
node .github/scripts/check-markdown.mjs
```

The command runs Markdown diagnostics on repository-authored files and checks
repository-relative links in all tracked or unignored Markdown files. Spec Kit
generated skills (`.github/skills/speckit-*`) and templates (`.specify/templates/`)
as well as installed extensions (`.specify/extensions/`) and extension command sources (`.specify/extension-src/*/commands/`) are excluded from style rules because Spec Kit defines their format;
their links are still checked. External URLs are ignored so
validation does not depend on network availability or third-party uptime.
Its own tests run with:

```powershell
node --test .github/scripts/check-markdown.test.mjs
```

## License

This project is licensed under the [MIT License](LICENSE).
