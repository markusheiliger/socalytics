# socAlytics

socAlytics is a planned polyglot monorepo. The repository currently contains
its governance and documentation foundation, a source-area scaffold, and the
first executable .NET platform host. Further product components will be added
through approved changes.

## Repository Structure

- [`src/platform/`](src/platform/README.md) contains the executable control-plane
    platform host and its ownership guidance.
- [`src/clients/`](src/clients/README.md) contains the executable online-only
    Web UI foundation under `src/clients/web/`; the offline mode for coaches
    is planned.
- [`src/agents/`](src/agents/README.md) records the planned intelligence-agent
    runtime and tooling ownership boundary.
- [`src/analysts/`](src/analysts/README.md) contains the executable Analyst
    Manager desktop app under `src/analysts/manager/` and records the planned
    Analyst SDK and Analyst capability ownership boundary.
- [`docs/`](docs/README.md) indexes architecture, component, and operational documentation.
- [`contracts/`](contracts/README.md) holds the canonical machine-readable
    contracts (JSON Schema 2020-12), the golden-fixture set shared by the
    platform and Analyst Manager tests, and their index.
- [`.specify/`](.specify/memory/constitution.md) holds the Spec Kit project
    constitution, templates, and helper scripts; `.github/skills/speckit-*`
    holds the generated Spec Kit skills for GitHub Copilot.
- [`specs/`](specs/README.md) holds one UTC-timestamped folder per Spec Kit
    feature, implemented features under `specs/archive/`, and an index of
    feature dependency order.
- [`AGENTS.md`](AGENTS.md) defines repository guidance for coding agents.
- [`LICENSE`](LICENSE) contains the project license.
- `.editorconfig` provides stack-neutral text-file defaults.

The agents source area remains a non-executable ownership scaffold. The
platform, the Web UI, and the Analyst Manager contain the repository's current
application projects and tests. No deployment manifests or product automation exist yet.

## Development

Implementation workflows reuse trusted NuGet and npm download caches without skipping installs, builds, or tests.
See [dependency cache operation and measurements](.github/workflows/README.md#dependency-download-caches).
Run the workflow tooling regression tests from the repository root with
`node --test .github/scripts/*.test.mjs`.

### Platform Host

The platform uses the .NET 10 SDK. Run these commands from the repository root:

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/SocAlytics.Platform.slnx --no-build
dotnet run --project src/platform/SocAlytics.Platform.AppHost
```

Running the AppHost and running `dotnet test` (Host.Tests and
Integration.Tests) require a running Docker-compatible container runtime. The
AppHost starts PostgreSQL (`postgres:18`), then the one-off Migrator, then the
API. To run the Migrator on its own, set `ConnectionStrings__socalytics-migrator`
and run:

```powershell
dotnet run --project src/platform/SocAlytics.Platform.Migrator
```

To run only the persistence integration tests:

```powershell
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build
```

To run only the end-to-end tests (the platform with a real Analyst Manager and a
simulated runtime; they need Docker and, on Linux, SoftHSM2):

```powershell
dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests
```

The current executable evidence covers the ASP.NET Core API and Aspire AppHost,
liveness at `/alive`, readiness at `/health`, the `v1` OpenAPI document at
`/openapi/v1.json`, the layered project structure (Domain, Application,
Infrastructure, and Api with public Application and Infrastructure composition
methods), and focused host and architecture tests that enforce the layer
dependencies.

The persistence foundation (schema, migrations, Migrator, units of work,
optimistic concurrency, and database-aware readiness) is implemented as
development evidence. Multipart recording upload, recording lineage, and
recording-set finalization run against the local RustFS container started by
the AppHost; the opt-in above-5-GiB acceptance test
(`SOCALYTICS_RUN_LARGE_UPLOAD_TEST=true`) needs about 12 GB of free Docker
disk. Local evidence is not production readiness. Further domain behavior,
NATS messaging, the transactional outbox, client
applications, deployment images and configuration, production credentials, and
production readiness remain deferred. "Docker support" still means deployment
images and Compose files (deferred), not the local container runtime
prerequisite above.

The identity foundation is implemented as development evidence: local accounts,
a server-validated BFF session with anti-forgery protection, membership, club
and team roles, team-scoped authorization, security audit events, and the
`Club > Season > Team > Match` hierarchy under `/api/v1`. OIDC, MFA
enforcement, self-service recovery, clients,
production values for POL-001, POL-002, and POL-009, deployment, and
production readiness remain deferred.

Analyst Manager registration is implemented as development evidence: pairing,
approval, activation, DPoP machine tokens, and revocation, with golden
fixtures shared with the Analyst Manager tests.

Member email addresses and notifications are implemented as development
evidence: optional addresses verified through a single-use confirmation link,
notification preferences, transactional delivery through a dedicated email
outbox and MailKit, and the notification of Club Admins about pending Analyst
Manager registration requests. The AppHost starts a Mailpit mail catcher
(`axllent/mailpit`, pinned) as the SMTP relay; its web interface shows the
messages and no message leaves the machine. The production relay, sender
authentication, and bounce handling remain deferred.

The AppHost bootstraps the first club with these parameters:

- `first-club-admin-password`: generated and persisted in the AppHost user
  secrets. Read it with
  `dotnet user-secrets list --project src/platform/SocAlytics.Platform.AppHost`.
- `first-club-admin-account-name`: defaults to `club-admin`.
- `club-display-name`: defaults to `Development Club`.

Interactive sign-in uses the API HTTPS endpoint, which needs the ASP.NET Core
development certificate (`dotnet dev-certs https --trust`). The endpoint is
omitted when the AppHost runs with `--SocAlytics:ApiHttpsEndpoint=false`, as
the host smoke test does. The first club administrator must change the
password at first sign-in; afterwards remove
`ClubBootstrap__FirstClubAdmin__InitialPassword` from the configuration.
Without a valid bootstrap configuration, `/health` stays unhealthy
(`club-not-established` or `bootstrap-conflict`).

Operator break-glass recovery: set `BreakGlassRecovery__AccountName`,
`BreakGlassRecovery__RecoveryId`, and `BreakGlassRecovery__TemporaryCredential`
(from the secret store), start the API, confirm the
`break-glass-recovery.applied` audit event, then remove the directive.
`IdentityAccess` settings exist only in `appsettings.Development.json` and
test configuration.

### Web UI

The Web UI under `src/clients/web/` is a React and TypeScript single-page
application built with Vite, developed online-only as development evidence (no
service worker, offline mode, container image, or deployment configuration). It
needs Node.js 24, the Kiota 1.36.0 CLI on `PATH`
(`dotnet tool install --global Microsoft.OpenApi.Kiota --version 1.36.0`), and
for browser tests the Playwright Chromium browser
(`npx playwright@1.63.0 install chromium`). Run these commands from
`src/clients/web/`:

```powershell
npm ci
npm run build      # Kiota generation from the OpenAPI snapshot, type checking, production build
npm test           # Vitest unit tests
npm run test:e2e   # Playwright: simulated and platform projects (the platform project starts the AppHost and needs Docker)
```

The AppHost (`dotnet run --project src/platform/SocAlytics.Platform.AppHost`)
also starts the Web UI on one origin with the API. The Kiota client is
generated from the committed OpenAPI snapshot
`src/platform/SocAlytics.Platform.Api/openapi/v1.json`. After API changes,
regenerate it from the repository root with:

```powershell
$env:SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT='1'; dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests; Remove-Item Env:SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT
```

Then run the test again without the variable.

### Analyst Manager

The Analyst Manager is a per-user tray application for Windows, macOS, and
Linux in a separate .NET 10 solution that pins the same SDK as the platform in
its own `global.json`. A headless server executable for Linux (systemd user
service controlled by the CLI) exists; Velopack installers and updates are
planned, not implemented. Run these commands from the
repository root:

```powershell
dotnet restore src/analysts/manager/SocAlytics.Analysts.Manager.slnx
dotnet build src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-restore
dotnet test src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-build
dotnet run --project src/analysts/manager/SocAlytics.Analysts.Manager
dotnet run --project src/analysts/manager/SocAlytics.Analysts.Manager.Server  # Linux only (headless server profile)
```

The tray menu (or the status window where no tray area exists) offers
status, pause, timed pause, resume, safe exit, register, unregister, and
start at sign-in. `socalytics-manager status`, `pause`, `resume`, `exit`,
`register`, `unregister`, and `autostart` script a running Manager.
Device keys are non-exportable: Windows CNG (TPM), macOS Secure Enclave
(requires the Swift bridge built on macOS), and Linux PKCS#11
(tpm2-pkcs11); software-backed keys are used only where the stamp allows
them (development and test). Registration shows a device fingerprint that
the Registrar enters together with the pairing code. The Linux tests need
SoftHSM2 (`softhsm2`); Windows and macOS key-store tests are skipped elsewhere
and verified manually. No production container-runtime integration ships, so a
Manager stays Runtime unavailable. This is development evidence, not production
readiness.

### Contracts

Canonical machine-readable contracts live in [`contracts/`](contracts/README.md),
whose index lists every artifact with its owner, version, example, and
validation command. Validate them from the repository root with the
contract command:

```powershell
dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests
```

The platform `dotnet test` command above also runs these tests.

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
4. `/speckit-tasks` creates `tasks.md` and then runs `/speckit-gha-refine`, a
    mandatory `after_tasks` hook that makes the `[P]` groups safe to run in
    parallel; `/speckit-analyze` (optional) checks cross-artifact consistency.
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
one GitHub issue, a spec twin, per feature folder under `specs/` (or, once
implemented, `specs/archive/`) on the repository's default branch (`main` in this repository; the tooling reads the
name from GitHub and never assumes it). It runs on every push to the default
branch that changes `specs/**` and can be started manually; manual runs are
dry runs unless `dry_run` is unchecked.

- A twin is labelled `speckit:spec` and starts with a generated, clickable
    `**Spec**` line linking its folder. That line identifies the twin. The title
    and description are regenerated from the spec on every run, so edit the
    spec, not the issue.
- A twin is not used for tracking. The repository stays authoritative for what
    a feature specifies.
- When a folder disappears, its twin is closed as not planned and labelled
    `speckit:stage:discarded`; it is reopened if the folder returns. Twins closed
    as completed are not changed, except that twins of archived specs follow
    their folder.
- Implemented specs are archived: `specs/<folder>` moves to
    `specs/archive/<folder>` in the commit that merges the implementation (see
    below). An archived spec's twin is closed as completed, labelled
    `speckit:stage:implemented`, and its `**Spec**` line and links point to the
    archive; the folder name stays its identity. Archived specs get no new
    twins, take no part in dependency inference, and cannot be requested for
    implementation.
- Every twin carries exactly one generated stage label, derived from the files
    in its folder on the default branch: `speckit:stage:specified` (only `spec.md`),
    `speckit:stage:planned` (`plan.md`), `speckit:stage:tasked` (`tasks.md`, no
    task checked), `speckit:stage:implementing` (some tasks checked), or
    `speckit:stage:implemented` (all tasks checked), or, for a requested
    implementation, `speckit:stage:implement` and `speckit:stage:implementing`
    as described below. Implementation happens on
    feature branches, so the computed implementation stages only reflect merged work. The
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
their "blocked by" issues are closed. A requested twin moves through
`implement` (waiting to start) >> `implementing` (its implementation pull
request is open) >> `implemented` (merged): the scheduler sets
`speckit:stage:implementing` when it opens the pull request (or finds one
open), and the merge sets `speckit:stage:implemented`. Who requested the
implementation and when stays in the label history of `implement`.

- When someone adds the label, the `validate-implement` job of
    `Spec Kit prepare` accepts it only if the person has at least write access,
    the twin is open and its folder exists on the default branch, the computed
    stage is `tasked`, and every checkbox in `specs/<folder>/checklists/*.md`
    there is checked. Otherwise the twin falls back to its computed stage, which may
    come before or after `implement`, and gets a comment with the reasons.
- Later syncs keep a valid flag, and a valid `implementing` stage while the
    default branch has no task checked, and revoke either one, with a comment,
    once the stage or checklist conditions no longer hold, for example when all
    tasks are merged and the twin becomes `implemented`. A computed
    `implementing` stage (tasks partly checked on the default branch, as after
    a local implementation) is no implementation request.
- `Spec Kit orchestrate` is the scheduler. It runs after every successful
    `Spec Kit prepare` run, when an issue is closed or reopened, when a
    `speckit/**` pull request is closed or a person pushes to it, hourly as a
    safety net, and on demand. Dependency edits on GitHub trigger nothing, so
    they apply with the next trigger. It starts implementations while fewer
    than `SPECKIT_MAX_ACTIVE_SPECS` (default 3) are active, and continues an
    implementation whose chain broke (no run active, but something to do). It
    never drives tasks itself.
- For every ready twin it prepares an implementation workspace:
  - the branch `speckit/<folder>`, created from the default branch as a linked
      branch so it appears in the twin's Development section, with an empty
      start commit;
  - a draft pull request `Implement: <spec title>` against the default branch
      whose body contains
      `Closes #<twin>`, the spec link, and the tasks from `tasks.md` as
      checkboxes in short form (tags and first sentence, linked to the full
      `tasks.md`, so the body stays under GitHub's 65,536-character limit),
      assigned to the person who set the flag;
  - a `Spec Kit implementation` check run on the pull request, which tracks the
      implementation status, and a start comment.

  The pull request's base is the implementation's **base branch**: the branch
  it was created from and merges back into. From here on, every merge,
  comparison, conflict check, and link of the implementation uses the base
  branch named on the pull request, never an assumed `main`. The default
  branch only provides the trusted workflows and scripts.
- Then the spec's implementation chain takes over: runs of
  `Spec Kit implement spec` (`speckit-implement-spec.yml`), one at a time per
  spec, each started by the previous one and named after the spec, for example
  `Spec Kit implement spec #35 · Recording Lineage and Upload` (the spec title
  from the pull request title). Every run decides what comes next from the
  state on GitHub (job `decide`, shown as `Decide what comes next`),
  runs it, and starts the next run (job `continue`, shown as
  `Start the next run`), until the implementation is merged, waits for
  review, or needs a person. Of what a run can do next (a phase, a repair, the
  convergence, a conflict resolution, or the merge) only one runs, and the
  others show as skipped; job names say what a job does, and GitHub shows
  their order. Every run starts with the job `sync` (`Merge the base branch`),
  which merges the base branch when it is ahead and the next work is a phase
  that has not started, the convergence, the merge, or a resume; a conflict
  is resolved by the conflict resolution in the same run. Task groups,
  single-task implementation, guided repair, convergence, conflict resolution,
  and merge run in dedicated
  nested reusable workflows, which appear in the run as job groups. The run
  graph joins the names of nested jobs, for example
  `Phase 3: … / T013 / Implement T013`, so job names stay
  short; the job summaries carry the full titles, such as
  `Spec Kit implement task #35 · T013 [P] [US1] Create …`:

  | Next | Runs in | What it does |
  | --- | --- | --- |
  | A phase | job `phase`, which calls `Spec Kit implement phase` (`speckit-implement-phase.yml`), named after the phase heading of `tasks.md` as written | the phase planner forms ordered units. Its serialized `groups` matrix calls `Spec Kit implement group` once per unit. A singleton group calls `Spec Kit implement task` once; a consecutive `[P]` group fans out one task-workflow call per task and always verifies the combined result before the next unit starts |
  | Repair | job `repair`, which calls `Spec Kit implement repair` (`speckit-implement-repair.yml`) | after diagnosis and a writer-approved `/speckit retry <guidance>`, repairs an already-landed task, group, phase, or prospective merge verification failure and reverifies that scope before normal planning resumes |
  | Acceptance gate | job `converge`, which calls `Spec Kit implement convergence` (`speckit-implement-converge.yml`), named `Convergence` | once all tasks are checked, `/speckit-converge`; gaps are appended as a `Phase N: Convergence` section with new tasks, which the next runs implement before convergence runs again (at most 3 rounds) |
  | Merge | job `merge`, which calls `Spec Kit implement merge` (`speckit-implement-merge.yml`) | its nested `archive`, `verify`, and `land` jobs archive the spec on the branch, build and fully verify the prospective merged tree, and squash-merge the pull request into the base branch or hand it to a person for review |
  | Conflicts | job `resolve`, which calls `Spec Kit implement conflict resolution` (`speckit-implement-resolve.yml`), named `Conflict resolution` | resolves conflicts with the base branch with Copilot CLI, verifies, and pushes the merge commit, in the run whose `sync` job or merge found them; the next run continues the work on the merged branch |

  Each job is short, so specs of any size never hit the 6-hour job limit.
  Every call of `Spec Kit implement task` handles exactly one task:
  1. its `work` job sets up the solution
      environment with the optional `.github/actions/environment-setup`
      action, then runs Copilot CLI (60 minutes at most) with a read-only
      token, which reaches the CLI only as `COPILOT_GITHUB_TOKEN` and is
      hidden from the agent's shells; `git push`, `gh`, `curl`, and `wget` are
      denied;
  2. verifies each change in that job with the optional
      `.github/actions/environment-verify` action. After a task, mode `task`
      runs the checks the changed paths call for with only the tests the task
      touched (the agent is told to run only those as well, not the whole
      suite). After the phase's last task, the phase workflow's job "Verify
      the phase" runs mode `phase` (those checks with all their tests) for all
      changes of the implementation branch; if it fails, the implementation
      stops for a person (and a diagnosis) without using up the attempts of
      the task that came last. A conflict resolution and the merge run every
      check with all tests (mode `finalize`). Every verification runs once
      and its result counts: re-running unchanged code only delayed real
      failures, so flaky tests are fixed where they live instead;
  3. pushes the result in one "Commit and push" job (`land`) that never runs
      agent-written code. It re-validates the change (a task may only add its own
      `tasks.md` tick; convergence may only append a convergence section to
      `tasks.md`; a resolution may change the conflicted files, the files
      the implementation branch changed itself, and new files, so it can fix
      clashes git merges without a conflict, such as two migrations with the
      same number; none under `.github/`, `.specify/`, or `specs/`; it is
      replayed onto git's own merge), then commits, pushes, updates the pull request body and
      check run, and comments the result. The group workflow coordinates
      parallel calls: each task rebuilds safely when a sibling moved the branch,
      and after all tasks land the group always runs full combined verification.
      A singleton group skips group-level verification and reporting.

  GitHub documents the order in which matrix jobs are created, not that they
  run in that order; with one job at a time they run in order in practice.
  Every call starts from the tip of the implementation branch, which the chain
  alone writes while it runs.

  `Spec Kit implement merge` owns the complete merge alternative. Its `archive`
  job (write token, trusted tooling, never runs agent-written code)
  merges the base branch into the implementation branch, moves the spec folder
  to `specs/archive/<folder>`, rewrites every reference to it in the repository
  (relative Markdown links into and out of the folder, and plain-text
  `specs/<folder>` mentions such as code comments), commits, pushes, and moves
  the merge check run to the new head; on conflicts with the base branch it
  changes nothing, so the conflict resolution runs first. The archive thus
  lands in the merge commit itself, verified with everything else, and the base
  branch never has a broken link.
  Its `verify` job (read-only token) builds the prospective merged tree and runs
  every check of `environment-verify`; its `land` job (write token, API only) squash-merges
  the pull request into its base branch only if the base branch still is the
  commit that was verified, sets the twin to `implemented` and closes it,
  points the twin's links to the archive (when the base branch is the default
  branch, which the twin mirrors) and the pull request's links from the deleted
  implementation branch to the base branch, deletes the branch, starts
  `Spec Kit prepare` on the default branch, and comments "Merged into `<base branch>`" with an @mention of the person who set
  the flag. If the base branch moved, the merge simply runs again.
  Instead of merging, the pull request is marked ready for review and a review
  is requested when automatic merging is off, when it changes the environment
  actions, or when the verification reports changed files that no check
  covers. When a person merges such a pull request, the next scheduler run
  (it starts when the pull request or the twin closes) finds the flagged twin
  whose pull request was merged after the flag, sets it to `implemented`
  (closing it if it is still open), deletes the branch, and comments on the
  twin.- The two composite actions are the solution-specific extension points; the
  Spec Kit workflows and scripts know nothing about the solution. Both are
  optional: the workflow skips a missing action, and without
  `environment-verify` the pull request comments and check run say that no
  verification is configured. The workflow always loads them from the default
  branch, so an agent cannot change its own checks. In this repository:
  - `environment-setup` installs .NET from `src/platform/global.json`, the
      Markdown linters, and SoftHSM2 (refreshing the package lists only when
      the install fails); Docker for Testcontainers is already on the runner;
  - `environment-verify` runs the platform restore, build, and tests when
      `src/platform/**` or the repository-root `contracts/**` changed (the
      contract coverage is active because the platform solution exists, and
      the contract tests run as part of the platform tests), the Analyst
      Manager restore, build, and tests
      (`src/analysts/manager/SocAlytics.Analysts.Manager.slnx`) when
      `src/analysts/manager/**` or the shared fixtures under
      `contracts/analyst-manager/**` changed, and the Markdown
      check when Markdown changed.
      In mode `task` it runs the architecture tests, the test classes declared
      in changed test files, and a whole test project when other files of it
      changed; in modes `phase` and `finalize`, all tests.
      It lists the paths it covers in `COVERED`; changed files outside them
      are reported as not covered, including platform and contract files when
      the platform solution is missing.
- Specs extend these actions when they need a new framework, SDK, or tool, in
  a standalone environment spec:
  1. The environment spec's tasks change only
      `.github/actions/environment-setup/` and
      `.github/actions/environment-verify/`. These are the only paths under
      `.github/` that agents may change, and only without other changes on the
      same branch; mixing them fails the task.
  2. New checks add their paths to `COVERED` only when their project exists,
      run only when those paths changed (or before merging), and skip while
      their project does not exist yet, because the environment spec merges
      before the project is created.
  3. Before merging, the merge jobs also run the branch's own versions of both
      actions as a self-test; a failing self-test fails the merge attempt.
  4. The pull request always waits for a person's review, because these
      actions verify every later implementation.
  5. Specs that need the new tooling name the environment spec under
      Assumptions → Dependencies, so dependency inference blocks them until it
      is merged (or add the "blocked by" link on GitHub).
- Tasks marked `[P]` run through `Spec Kit implement group` in parallel, up to `SPECKIT_MAX_PARALLEL_TASKS`
  (default 3) at a time. Consecutive unchecked `[P]` tasks under the same
  heading run together; an unchecked task without `[P]` or a new heading
  ends them. Each task is verified and landed by its own task workflow. A task
  whose siblings landed first is rebuilt on the new head (its change without
  `tasks.md`, plus its own tick) and validated again. Files that a sibling
  (or a person) also changed since the task started are merged three-way;
  the group verification then runs mode `phase` on the combined result even
  when the changed files do not overlap. If it fails, the landed tasks remain
  checked and a durable repair blocks later units. Automatic diagnosis
  recommends `/speckit retry <guidance>`; after a writer posts it, the dedicated
  repair workflow changes no task checkbox and clears the repair only after
  reverification passes. Only when task edits conflict, or a change no longer
  applies, the task runs again from the new head in the next run; that does
  not count as a failed attempt, but every task has at most six attempts. A
  push that is refused although the branch did not move counts as a failed
  attempt.
  `SPECKIT_MAX_PARALLEL_TASKS=1` runs `[P]` tasks one after another.
  `/speckit-tasks` sets `[P]` from the task texts alone, so its
  `/speckit-gha-refine` hook checks each group against the files its tasks
  will really change (including registrations, project files, and shared
  configuration) and names the shared file, moves a shared edit into one task
  after the group, or ends the group. Groups that follow each other are
  separated with `###` headings. To see the task order exactly as the workflow
  plans it, run `node .github/scripts/speckit-tasks-groups.mjs <folder>`.
- Every task, repair, convergence, conflict resolution, and merge gets at most three
  failed attempts. Every outcome of a task, repair, convergence, or conflict
  resolution is commented on the pull request with
  a hidden attempt marker, and the next run counts the attempts from these
  comments (only those the workflow wrote); merges are counted from their
  check runs. An attempt whose check run is still in progress when the next
  run starts (a cancelled or crashed run) counts as failed.
- People are involved only when automation cannot continue. Then the check run
  fails, the pull request remains draft, and one comment
  @mentions the person who set the flag with the reason and a **Next steps**
  block with the pull request comment commands that apply (see
  [When an implementation stops](#when-an-implementation-stops)):

  | Case | What the person does |
  | --- | --- |
  | A task, the convergence, a conflict resolution, or the merge reached its attempt limit | read the automatic diagnosis and choose one of its options, or push a fix to the implementation branch |
  | Convergence still finds gaps after 3 rounds | implement the gaps on the branch, or adjust the spec and tasks (`/speckit diagnose` proposes how) |
  | Conflicts the agent could not resolve, or conflicts in protected paths | resolve them (GitHub's "Resolve conflicts" or locally) and push |

  A push by a person to the implementation branch triggers the scheduler,
  which continues the implementation with a new attempt count, so it merges
  without further approval.
- An open implementation pull request marks the twin as in progress: it is
    `speckit:stage:implementing`, and it is never started twice. Preparing the implementation checks what already exists, so reruns after
    partial failures complete the work.
- Closing the pull request without merging removes the implementation request
    (`implement` or `implementing`), and the twin
    falls back to its computed stage with a comment. GitHub keeps the closed
    pull request and its branch; flagging the twin again deletes and recreates
    the branch and opens a new draft pull request. Merging the pull request,
    automatically or by a person, closes the twin as completed.
- All Spec Kit workflows only run from the default branch: manual runs started
    on another branch are refused, implementation-PR events use
    `pull_request_target` (always the default branch's workflow), the workflow
    token cannot push workflow files, and scripts always run from a checkout of
    the default branch. Pull requests and commits
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
- `/speckit-gha-diagnose [pull request, twin, or folder] [guidance]` walks you
    through a stopped implementation locally; see
    [When an implementation stops](#when-an-implementation-stops).
- `/speckit-gha-fix [pull request] [guidance]` explains fix specs and offers to
    post `/speckit fix` with guidance from the latest diagnosis; see
    [Fix spec pull requests](#fix-spec-pull-requests).
- `/speckit-tasks` ends with `/speckit-gha-refine [folder]`, a mandatory
    `after_tasks` hook that refines the `[P]` groups of the new `tasks.md` (see
    the `[P]` paragraph under the implementation workflow). Run it on its own
    for a spec that already has tasks; for one being implemented it only fixes
    conflicting groups.
- `/speckit-gha-archive [folder]` archives an implemented spec before a local
    or manual merge (`/speckit-converge` suggests it as an optional
    `after_converge` hook): `node .github/scripts/speckit-archive.mjs <folder>
    [--check]` moves the folder and rewrites the references; commit the result
    with the implementation.

The route and request commands both run `node .github/scripts/speckit-orchestrate.mjs request [--folder <folder>]`,
which checks the spec on the remote default branch (asked from GitHub) with the
same rules, finds its twin, and
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

### When an Implementation Stops

An implementation stops for a person when a task, the convergence, a conflict
resolution, or the merge reaches its attempt limit,
convergence keeps finding gaps, conflicts need a person, or a merge fails. Not
every stop is a spec problem, so the next step starts with a diagnosis:

1. The stop comment on the implementation pull request ends with **Next
    steps**: the commands that apply now, each in a copyable code block. Unless
    the repository variable `SPECKIT_AUTO_DIAGNOSE` is `false`, a diagnosis
    starts automatically (at most twice for the same branch head), and the
    `Spec Kit implementation` check run shows `Diagnosis in progress`.
2. The `Spec Kit diagnose` agentic workflow
    ([`speckit-diagnose.md`](.github/workflows/speckit-diagnose.md), GitHub
    Agentic Workflows) collects the evidence (failure comments, logs of failed
    runs, check runs, branch state, your guidance), follows the method of the
    `/speckit-gha-diagnose` skill, and posts a **Diagnosis** comment: its
    category and what that means, the cause and the evidence, its confidence,
    and the next steps. Exactly one next step is recommended, and its command
    always fits the category; the other options are collapsed below it. Every
    option says **why** it should solve the problem and **what it does** (for
    example whether the base branch is merged first and what restarts), and
    every command can be posted as it is, with the guidance or answers
    from the findings already written out.

    | Category | Meaning | Recommended next step |
    | --- | --- | --- |
    | `artifacts` | the spec, plan, research, data model, contracts, or tasks are wrong or incomplete (also a code fix inside this feature, as a corrective task before the stuck one) | review and merge the linked **amendment pull request** (below) |
    | `retry` | a transient failure, or the agent took a poor approach while the artifacts are fine | `/speckit retry <guidance>` |
    | `outside` | another feature's code or tests on the base branch, the environment actions, tooling, permissions, quotas, or an external service | `/speckit fix <what to fix>` for code, tests, or the environment actions (a **fix spec**, below); `/speckit resume <guidance>` once a fix is on the base branch, or after an action the comment names |
    | `decision` | a trade-off only a person can make | `/speckit diagnose 1. … 2. …`, prefilled with the recommended answers |
    | `unknown` | not enough evidence | `/speckit diagnose <what to examine next>` |

    The diagnosis never weakens requirements, success criteria, or tests. The
    agent runs read-only behind gh-aw's network firewall.
3. On the implementation pull request you answer with a comment command. The
    command must be the first word of the comment; only people with write
    access can use them; a 👀 reaction confirms receipt, and a comment reports
    the result.

    | Command | Effect |
    | --- | --- |
    | `/speckit diagnose [guidance]` | start a diagnosis; guidance steers it or answers its questions (also while the implementation runs; it then waits). Nothing is retried meanwhile |
    | `/speckit retry [guidance]` | restart what stopped with a fresh attempt count, without merging the base branch for it (a phase that has not started, the convergence, and the merge always merge it first); the guidance goes into its prompt |
    | `/speckit resume [guidance]` | merge the base branch (the pull request's base; Copilot resolves conflicts), then restart what stopped with a fresh attempt count; the guidance goes into its prompt |
    | `/speckit fix [guidance]` | have a fix spec authored for a cause outside this spec (below); the implementation waits and resumes by itself once the fix has landed |
    | `/speckit help` | list the commands |

    Writer guidance is capped at 4,000 characters. Retry and resume guidance
    is persisted in a bot-authored pull request marker and reaches every task
    in the first retried parallel group, or the first convergence, repair, or
    conflict-resolution prompt. It expires after successful work or a newer
    person-authored push. Clean merges and verification-only repair resumes do
    not invoke an agent and therefore use no prompt guidance.

    What restarts is decided from the pull request's state: the stopped task,
    the convergence, the conflict resolution, or the merge. A failed phase
    verification is not repeated by itself; the next verification, at the
    latest the full verification before the merge, covers it. The next steps
    name what a restart would continue with. Pushing a fix to the
    implementation branch continues the implementation as well.

#### Amendment Pull Requests

For `artifacts`, the diagnosis amends the spec artifacts top-down (spec, then
plan, research, data model, contracts, and quickstart, then tasks) and opens a
draft pull request `speckit-amend/<folder>` → `speckit/<folder>`. It may only
change `specs/<folder>/`, never checks a task, and never removes a completed
one. Before anyone is asked to review it:

1. The `Spec Kit analyze` agentic workflow
    ([`speckit-analyze.md`](.github/workflows/speckit-analyze.md)) runs an
    independent, read-only `/speckit-analyze` of the whole spec folder on the
    amendment branch. The `Spec Kit amendment` check run shows the result.
2. CRITICAL, HIGH, or MEDIUM findings, or a broken amendment rule, start a
    correction round: the diagnose workflow fixes them on the same pull request,
    and the analysis runs again. Each finding gets an id (`F1`, `R1`, …), and
    the next analysis reports which of them the round resolved. Rounds continue
    while they make progress (at least one previous finding resolved); a round
    without progress, including one whose push fails, counts against a budget
    of 3, and 10 rounds are the limit in any case.
3. A consistent amendment becomes ready for review, assigned to the person who
    requested the implementation, with a review request and a status and
    **How to proceed** section in its description. An amendment that is still
    inconsistent when the budget or the limit is reached stays a draft with its
    findings, and you are asked to review it as well.

Then you handle it like any pull request:

| You do | Effect |
| --- | --- |
| Merge | the amendment lands on the implementation branch; a resume comment on the implementation pull request restarts the implementation with a fresh attempt count, starting with the next unchecked task (for example a new corrective task) |
| Comment, or submit a review with *Comment* or *Request changes* (line comments included) | a rework: the pull request goes back to draft, the diagnose workflow addresses all feedback since the last round, replies, pushes to the same pull request, and the consistency check runs again before it comes back to you |
| Close without merging | the amendment is discarded; the implementation pull request lists what you can do next |
| Push to the amendment branch yourself | the consistency check runs again, without correction rounds |
| Change its base branch, or add it (or the implementation pull request) to a pull request stack | undone right away: the base goes back to `speckit/<folder>` and the stack is dissolved, with a comment. A stack would merge the unfinished implementation into its base branch together with the amendment. Events reach the guard for a base changed to the default branch and for the implementation pull request; the scheduler (`Spec Kit orchestrate`) checks the rest on every run. An amendment merged into another branch anyway does not resume the implementation; you are asked to revert it there. |

Approving does not start anything; merge when you are satisfied. Feedback that
arrives while a rework or check runs is handled when it ends (the comment's job
waits up to 20 minutes, the scheduler handles it after that). A rework that
cannot push is reported on the amendment. A correction round that fails or
changes nothing (it judges the findings wrong), and the end of the correction
budget, hand the amendment to you as not consistent; a rework after your
feedback starts a fresh budget.
Reviews reach the automation through a small forwarder job that runs from the
pull request's merge ref, and merges and closes through a job that runs from the
implementation branch, so both need the current `speckit-commands.yml` there.
The hourly `Spec Kit orchestrate` run, which always runs from the default branch, settles
merges and closes that no event reported, picks up feedback that no event
delivered, checks pushes that no event reported, and closes checks that never
finished.

#### Fix Spec Pull Requests

When the cause lies outside the stopped spec (`outside`), for example a flaky
test of another feature on the base branch, `/speckit fix <what to fix>` keeps
the fix inside Spec Kit instead of patching around it:

1. The `Spec Kit fix` agentic workflow
    ([`speckit-fix.md`](.github/workflows/speckit-fix.md)) follows the skill
    `/speckit-gha-fix` and authors a **fix spec**: a small, complete spec
    (spec, plan, tasks, checklist, and its row in `specs/README.md`) whose
    `spec.md` names the implementation it unblocks (`**Fix for**: #<pull>`).
    It is proposed as a draft pull request `speckit-fix/<folder>` → the default
    branch that may only add that spec folder. When a fix spec cannot remove the
    cause (tooling under `.github/`, permissions, quotas, external services), it
    changes nothing and says why. A cause in the environment actions gets an
    environment spec, which is always held for review.
2. Like an amendment, it is checked by an independent `/speckit-analyze` with
    correction rounds and then handed to the person who requested the
    implementation; comments and reviews rework it, closing discards it.
3. Merging it adds the spec to the default branch. Its twin skips dependency
    inference; the scheduler makes the stopped spec's twin depend on it,
    requests its implementation on behalf of the person who asked for the fix,
    and starts it even when `SPECKIT_MAX_ACTIVE_SPECS` implementations are
    active. It is implemented, converged, and merged like any spec.
4. Once its twin is implemented, the stopped implementation resumes by itself:
    it merges the base branch, with the fix, and continues with a fresh attempt
    count. If the fix spec is closed or not implemented, the implementation
    pull request lists the next steps again.

The implementation pull request records every step in a comment.

These pull request comment commands are not skills. To walk through a stop
locally, run the skill `/speckit-gha-diagnose [pull request, twin, or folder]`
in GitHub Copilot: it uses the same method, asks you the decisions, and pushes
the result. The diagnose, analyze, and fix workflows are compiled with GitHub
Agentic Workflows; after editing `speckit-diagnose.md`, `speckit-analyze.md`, or
`speckit-fix.md`, install the extension (`gh extension install github/gh-aw`)
and run `gh aw compile`.
Do not edit the generated `.lock.yml` files. Their Copilot engine uses the
workflow token with the `copilot-requests` permission, so no secret is needed.

### Spec Kit Configuration

Everything that adapts the Spec Kit automation to this repository, in one
place. The workflows and scripts need no changes for any of it.

| Setting | Kind | Default | Effect |
| --- | --- | --- | --- |
| `SPECKIT_AUTO_MERGE` | repository variable | `true` | `false` holds every implementation for review instead of merging it automatically |
| `SPECKIT_TASK_AI_CREDITS` | repository variable | `1000` | Copilot CLI credit cap per agent run (tasks, convergence, conflict resolution) |
| `SPECKIT_MAX_PARALLEL_TASKS` | repository variable | `3` | how many consecutive `[P]` tasks run at the same time; `1` runs them one after another |
| `SPECKIT_MAX_ACTIVE_SPECS` | repository variable | `3` | how many specs the scheduler implements at the same time; a spec waiting for review or for a person does not count |
| `SPECKIT_AUTO_DIAGNOSE` | repository variable | `true` | `false` stops starting a diagnosis automatically when an implementation stops; `/speckit diagnose` still works |
| `.github/actions/environment-setup` | composite action, optional | not run | installs the SDKs and tools for building, testing, and verifying |
| `.github/actions/environment-verify` | composite action, optional | no verification | runs the checks for changed paths (modes `task`: only the touched tests; `phase` and `finalize`: all tests) and reports files no check covers |
| `SPECKIT_IMPLEMENT_MODE` | local environment variable | ask | `local` or `remote` answers the `/speckit-implement` routing question |

Set or remove repository variables in **Settings → Secrets and variables →
Actions → Variables**, or with `gh variable set <NAME> --body <value>` and
`gh variable delete <NAME>`; the next workflow run uses the new value. Change
the composite actions only through an environment spec, as described in
[Requesting Implementation on GitHub](#requesting-implementation-on-github).

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
