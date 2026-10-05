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

The command runs Markdown diagnostics and checks repository-relative links in
all tracked or unignored Markdown files. External URLs are ignored so
validation does not depend on network availability or third-party uptime.
Its own tests run with:

```powershell
node --test .github/scripts/check-markdown.test.mjs
```

## License

This project is licensed under the [MIT License](LICENSE).
