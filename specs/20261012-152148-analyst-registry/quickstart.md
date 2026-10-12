# Quickstart: Validate the Analyst Registry

Development evidence only.

## Prerequisites

- .NET SDK from `src/platform/global.json` and a running Docker-compatible
  container runtime (PostgreSQL and `registry:2` containers).

## Automated validation

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/SocAlytics.Platform.slnx --no-build
```

| Test area | Proves |
| --- | --- |
| `SocAlytics.Platform.Contracts.Tests` | The analyst-manifest schema and its valid and invalid examples |
| Integration `Registry/AnalystImageImportTests` | Registration by digest, idempotency, every refusal code, and that only the manifest, configuration, and last layer are fetched (SC-004) |
| Integration `Registry/AnalystProfileTests` | Creation, digest stability, contradiction refusals, approval (including self-approval marking), retirement rules, immutability triggers |
| Integration `Registry/WorkflowDefinitionTests` | Validation refusals, the default-workflow change with `If-Match`, atomic resolution |
| Integration `Analysis/*` (existing) | Run creation and snapshots unchanged, now resolved from the seeded Registry |
| Integration `Registry/RegistryResolutionTests` | Snapshots pin Registry profiles; existing runs unchanged after default or profile changes (SC-006) |
| Architecture tests | Registry infrastructure types stay internal |

## Manual exploration

1. `dotnet run --project src/platform/SocAlytics.Platform.AppHost`: in
   Development, the configured definition seeds the Registry.
2. As the Club Admin, call `GET /api/v1/registry/default-workflow` and
   `GET /api/v1/registry/analyst-profiles` to see the seeded entries.
