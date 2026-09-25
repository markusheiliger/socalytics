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

The repository also provides `/opsx-enqueue [<change-ref> ...]` to admit one or
more committed changes to the GitHub-backed OpenSpec queue. With no refs it
uses one true multi-select list of eligible changes; the client must support a
multi-select question tool. The command validates that selected change
directories are clean, strictly valid, and present on `origin/main`, expands
native GitHub blockers as one dependency-complete batch, asks for confirmation,
and adds `openspec:enqueued` to the matching issue twins.

`/opsx-enqueue` requires `git`, OpenSpec `1.13.0`, the GitHub CLI, and an
authenticated GitHub CLI session:

```powershell
gh auth status
```

The repository-owned enqueue skill is independent of the queue scripts under
`.github/scripts/`.

Useful OpenSpec repository checks are:

```powershell
openspec doctor --json
openspec schema validate spec-driven --json
openspec validate --all --json
openspec status --all --json
```

The repository-owned OpenSpec change queue has focused contract tests:

```powershell
node --test .github/scripts/*.test.mjs
gh aw validate .github/workflows/openspec-change-reconciliation.md
gh aw lint .github/workflows/openspec-change-reconciliation.lock.yml
node .github/scripts/openspec-change-workflow-names.mjs --check
```

The `gh aw` commands require the official `github/gh-aw` GitHub CLI extension.
The approved tooling combines deterministic issue synchronization and AI
dependency inference in one Agentic Workflow. The paths above are the expected
source and generated-lock names; if implementation settles different names,
use the committed pair. Edit only the Markdown source, regenerate its lock file
with `gh aw compile`, then apply the required generated-job display names with:

```powershell
node .github/scripts/openspec-change-workflow-names.mjs
```

The naming step is deterministic and must not be replaced with manual lock-file
edits.

See the [OpenSpec change queue operations guide](docs/operations/openspec-change-queue.md)
for its issue projection, reconciliation cadence, dependency checkpoint,
cloud-agent, recovery, labels, and human review contracts. This automation is
repository tooling rather than product CI.

Architecture narratives remain authoritative for current system design;
`openspec/specs/` is authoritative for accepted behavioral requirements.

## License

This project is licensed under the [MIT License](LICENSE).
