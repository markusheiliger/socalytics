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
- [`openspec/`](openspec/) contains accepted behavioral specifications, active changes, and archived change history.
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

### OpenSpec

The repository uses OpenSpec `1.13.0` as its user-facing change workflow. The
CLI is a global development tool and does not require an application
`package.json`:

```powershell
npm install --global @fission-ai/openspec@1.13.0
openspec --version
```

After cloning, run `openspec update` to refresh the generated GitHub Copilot
prompts and skills for the configured workflow profile. Restart VS Code after an
update so prompt discovery refreshes.

Start work through the generated GitHub Copilot prompts:

- `/opsx-explore` investigates an idea without creating change artifacts.
- `/opsx-propose` creates a proposal, behavioral specification delta, design,
  and owned task list.
- `/opsx-apply` implements approved tasks through their declared `soca-*` owner.
- `/opsx-update` continues or refreshes change artifacts.
- `/opsx-sync` synchronizes accepted behavioral requirements.
- `/opsx-verify` independently checks the completed change and evidence.
- `/opsx-archive` archives a verified change and updates accepted specs.

Useful OpenSpec repository checks are:

```powershell
openspec doctor --json
openspec schema validate spec-driven --json
openspec validate --all --json
openspec status --all --json
```

Architecture narratives remain authoritative for current system design;
`openspec/specs/` is authoritative for accepted behavioral requirements.

### OpenSpec Changesets

A changeset schedules multiple approved OpenSpec changes from one GitHub issue.
Its versioned JSON block is the dependency graph; the accompanying Mermaid
diagram is generated display only. Each runnable change starts with one branch-only
cloud-agent task. It opens the change's only pull request after Apply, Verify,
conditional Audit, Sync, and Archive all succeed. The controller enables squash auto-merge for a pull request only after
the cloud task declares its lifecycle complete; GitHub then waits for configured
checks and required reviews. The controller never merges directly or waits for
an agent.

Repository setup requires GitHub Actions, Copilot coding agent access, and an
Actions secret named `COPILOT_AGENT_TOKEN`. The secret must contain a
user-authorized token that can call the Agent Tasks API and access this
repository. The built-in `GITHUB_TOKEN` handles issue, label, comment, and pull
request metadata through the workflow's declared permissions. Enable **Allow
auto-merge** in the repository's pull request settings. Branch protection or
rulesets remain authoritative: required checks and reviews must pass before
GitHub merges, while repositories without required reviews can complete without
human interaction.

Ask GitHub Copilot to use the `soca-changeset` skill. It lists eligible active
changes, checks other open changesets, proposes dependency edges, requests
confirmation, and creates the issue. Adding `changeset:ready` activates the
controller. The controller then uses these mutually exclusive state labels:

- `changeset:ready` means the validated issue is ready for reconciliation.
- `changeset:running` means at least one change has active work.
- `changeset:attention` means an operator must resolve a graph or task outcome.
- `changeset:complete` means every change is archived on merged `main`.

Dependencies are released only when the prerequisite pull request is merged and
its dated archive exists on `main`. A closed pull request does not release
dependents. When human correction is required, the agent commits and pushes its
coherent work, asks through the native Agent Task session, and leaves the branch
without a pull request. Reply in that session; the same task continues on the
same branch. Cloud-agent sessions are limited to approximately 59 minutes, so
oversized changes should be split before adding them to a changeset.

Run the dependency-free tooling locally with Node.js 24 or later:

```powershell
node --test .github/scripts/openspec-changeset-core.test.mjs .github/scripts/openspec-changeset-controller.test.mjs .github/scripts/openspec-changeset-github.test.mjs
node .github/scripts/openspec-changeset-core.mjs validate <changeset-json-file>
node .github/scripts/openspec-changeset-core.mjs render <changeset-json-file>
node .github/scripts/openspec-changeset-controller.mjs dry-run --issue <issue-number>
```

The controller dry run needs `GITHUB_REPOSITORY` and `GITHUB_TOKEN` in the local
environment but does not mutate GitHub. For a technical dispatch failure before a
usable task branch exists, run the **OpenSpec Changeset Processing** workflow
manually with the affected issue and `retry`. If a task is terminal and cannot
continue through its native session, run the workflow with `recover`, the parent
changeset issue, and the affected change ref. Recovery validates the recorded
checkpoint and starts a replacement session on the same branch.
Use `accept-graph` only after intentionally reviewing a changed authoritative
JSON graph. Scheduled reconciliation runs twice per hour as a fallback.

## License

This project is licensed under the [MIT License](LICENSE).
