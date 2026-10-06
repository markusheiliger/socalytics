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
    `speckit:stage:implemented` (all tasks checked). Implementation happens on
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

The workflow's tooling tests run with:

```powershell
node --test .github/scripts/speckit-prepare-core.test.mjs .github/scripts/speckit-prepare.test.mjs
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
are excluded from style rules because the `specify` CLI owns their formatting;
their links are still checked. External URLs are ignored so
validation does not depend on network availability or third-party uptime.
Its own tests run with:

```powershell
node --test .github/scripts/check-markdown.test.mjs
```

## License

This project is licensed under the [MIT License](LICENSE).
